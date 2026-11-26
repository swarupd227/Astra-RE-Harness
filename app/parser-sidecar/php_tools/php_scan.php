<?php
/**
 * Astra parser sidecar — PHP structural scanner (nikic/PHP-Parser).
 *
 * Reads {"files":[{"filename":"a.php","content":"<?php ..."}]} on stdin and writes
 * {"files":[ <summary per file> ]} on stdout. It states what the code SAYS and nothing else: routines with
 * their line ranges, resolved call targets, and neutral "sites" (calls with their literal arguments, SQL
 * strings, superglobal reads, includes, switch dispatch). Interpreting those — which call is a route, which
 * string is a table — is the job of small framework detectors on the Python side, so a new framework needs no
 * change here. Nothing is executed; the file is only parsed.
 *
 * Never throws on bad PHP: a file that does not parse is reported with errors and whatever was recovered.
 */
declare(strict_types=1);

$vendor = getenv('ASTRA_PHP_VENDOR') ?: '/opt/php-parser/vendor/autoload.php';
require $vendor;

use PhpParser\Error;
use PhpParser\ErrorHandler;
use PhpParser\Node;
use PhpParser\Node\Expr;
use PhpParser\Node\Name;
use PhpParser\Node\Stmt;
use PhpParser\NodeTraverser;
use PhpParser\NodeVisitor\NameResolver;
use PhpParser\NodeVisitorAbstract;
use PhpParser\ParserFactory;
use PhpParser\PrettyPrinter\Standard;

const MAX_SITES_PER_FILE = 4000;
const MAX_ARG_TEXT = 160;
const SQL_START = '/^\s*\(?\s*(SELECT|INSERT|UPDATE|DELETE|REPLACE|CALL|EXEC|WITH|MERGE|TRUNCATE|CREATE|ALTER|DROP)\b/i';

function short_name(?string $fqn): ?string
{
    if ($fqn === null) return null;
    $i = strrpos($fqn, '\\');
    return $i === false ? $fqn : substr($fqn, $i + 1);
}

/** Resolved fully-qualified name of a Name node, or null for self/static/parent. */
function resolved(Name $n): ?string
{
    $low = $n->toLowerString();
    if ($low === 'self' || $low === 'static' || $low === 'parent') return null;
    $r = $n->getAttribute('resolvedName');
    return $r instanceof Name ? $r->toString() : $n->toString();
}

final class Scanner extends NodeVisitorAbstract
{
    public array $routines = [];
    public array $classes = [];
    public array $sites = [];
    public array $includes = [];
    public array $topCalls = [];
    public array $topSites = [];
    public array $topLevelStmtLines = [];

    /** @var array<int, array> class context stack */
    private array $classStack = [];
    /** @var array<int, array> routine context stack (index into $this->routines + local var types) */
    private array $routineStack = [];
    private Standard $printer;
    private array $internal;

    public function __construct()
    {
        $this->printer = new Standard();
        $this->internal = array_flip(get_defined_functions()['internal']);
    }

    public function enterNode(Node $node)
    {
        if ($node instanceof Stmt\Class_ || $node instanceof Stmt\Interface_ || $node instanceof Stmt\Trait_ || $node instanceof Stmt\Enum_) {
            $fqn = isset($node->namespacedName) ? $node->namespacedName->toString() : ($node->name ? $node->name->toString() : null);
            if ($fqn === null) { // anonymous class: its methods belong to the enclosing routine
                $this->classStack[] = ['fqn' => null, 'parent' => null, 'props' => []];
                return null;
            }
            $kind = $node instanceof Stmt\Interface_ ? 'interface' : ($node instanceof Stmt\Trait_ ? 'trait' : ($node instanceof Stmt\Enum_ ? 'enum' : 'class'));
            $parent = ($node instanceof Stmt\Class_ && $node->extends) ? resolved($node->extends) : null;
            $implements = [];
            $impl = $node instanceof Stmt\Class_ || $node instanceof Stmt\Enum_ ? $node->implements : [];
            foreach ($impl as $i) $implements[] = resolved($i);
            if ($node instanceof Stmt\Interface_) foreach ($node->extends as $i) $implements[] = resolved($i);
            $traits = [];
            $props = [];
            foreach ($node->stmts as $s) {
                if ($s instanceof Stmt\TraitUse) foreach ($s->traits as $t) $traits[] = resolved($t);
                if ($s instanceof Stmt\Property && $s->type !== null) {
                    $t = $this->typeName($s->type);
                    if ($t !== null) foreach ($s->props as $p) $props[$p->name->toString()] = $t;
                }
                if ($s instanceof Stmt\ClassMethod && strtolower($s->name->toString()) === '__construct') {
                    foreach ($s->params as $p) {
                        if ($p->flags !== 0 && $p->type !== null && $p->var instanceof Expr\Variable && is_string($p->var->name)) {
                            $t = $this->typeName($p->type);
                            if ($t !== null) $props[$p->var->name] = $t;
                        }
                    }
                }
            }
            $this->classStack[] = ['fqn' => $fqn, 'parent' => $parent, 'props' => $props];
            $this->classes[] = [
                'name' => $fqn, 'kind' => $kind, 'extends' => $parent, 'implements' => array_values(array_filter($implements)),
                'traits' => array_values(array_filter($traits)),
                'lineStart' => $node->getStartLine(), 'lineEnd' => $node->getEndLine(),
                'attributes' => $this->attributes($node->attrGroups),
            ];
            return null;
        }

        if ($node instanceof Stmt\ClassMethod || $node instanceof Stmt\Function_) {
            $cls = $this->classStack ? end($this->classStack) : null;
            $isMethod = $node instanceof Stmt\ClassMethod;
            $name = $node->name->toString();
            $vars = [];
            $params = [];
            foreach ($node->params as $p) {
                if ($p->var instanceof Expr\Variable && is_string($p->var->name)) {
                    $t = $p->type !== null ? $this->typeName($p->type) : null;
                    if ($t !== null) $vars[$p->var->name] = $t;
                    $params[] = ['name' => $p->var->name, 'type' => $t !== null ? $t : ($p->type !== null ? $this->printer->prettyPrint([$p->type]) : null)];
                }
            }
            $idx = count($this->routines);
            $this->routines[] = [
                'name' => $name,
                'class' => $cls['fqn'] ?? null,
                'kind' => $isMethod ? 'method' : 'function',
                'visibility' => $isMethod ? ($node->isPrivate() ? 'private' : ($node->isProtected() ? 'protected' : 'public')) : null,
                'static' => $isMethod ? $node->isStatic() : false,
                'abstract' => $isMethod ? ($node->stmts === null) : false,
                'lineStart' => $node->getStartLine(), 'lineEnd' => $node->getEndLine(),
                'params' => $params,
                'returnType' => $node->returnType !== null ? $this->printer->prettyPrint([$node->returnType]) : null,
                'attributes' => $this->attributes($node->attrGroups),
                'calls' => [], 'sites' => [], 'superglobals' => [],
            ];
            $this->routineStack[] = ['idx' => $idx, 'vars' => $vars, 'cls' => $cls];
            return null;
        }

        if ($node instanceof Stmt\Switch_) {
            $this->recordSwitch($node);
        }

        $ctx = $this->routineStack ? end($this->routineStack) : null;
        $cls = $ctx['cls'] ?? ($this->classStack ? end($this->classStack) : null);
        $this->visitExpr($node, $ctx, $cls);
        return null;
    }

    public function leaveNode(Node $node)
    {
        if ($node instanceof Stmt\ClassMethod || $node instanceof Stmt\Function_) {
            array_pop($this->routineStack);
        } elseif ($node instanceof Stmt\Class_ || $node instanceof Stmt\Interface_ || $node instanceof Stmt\Trait_ || $node instanceof Stmt\Enum_) {
            array_pop($this->classStack);
        }
        return null;
    }

    // ── expressions ────────────────────────────────────────────────────

    private function visitExpr(Node $node, ?array $ctx, ?array $cls): void
    {
        $line = $node->getStartLine();

        if ($node instanceof Expr\Assign && $ctx !== null && $node->var instanceof Expr\Variable && is_string($node->var->name)) {
            // $x = new C(...)  teaches the receiver type of later $x->m() calls in this routine.
            if ($node->expr instanceof Expr\New_ && $node->expr->class instanceof Name) {
                $c = $this->className($node->expr->class, $cls);
                if ($c !== null) $this->routineStack[array_key_last($this->routineStack)]['vars'][$node->var->name] = $c;
            }
        }

        if ($node instanceof Expr\New_) {
            if ($node->class instanceof Name) {
                $c = $this->className($node->class, $cls);
                $this->call($ctx, ['kind' => 'new', 'class' => $c, 'method' => '__construct', 'line' => $line]);
                $this->site($ctx, $node->class->toString(), 'new', $node->args, $line, $c);
            }
            return;
        }

        if ($node instanceof Expr\StaticCall && $node->class instanceof Name && $node->name instanceof Node\Identifier) {
            $c = $this->className($node->class, $cls);
            $this->call($ctx, ['kind' => 'static', 'class' => $c, 'method' => $node->name->toString(), 'line' => $line]);
            $this->site($ctx, ($c !== null ? $c : $node->class->toString()) . '::' . $node->name->toString(), 'static', $node->args, $line, $c);
            return;
        }

        if ($node instanceof Expr\MethodCall || $node instanceof Expr\NullsafeMethodCall) {
            if ($node->name instanceof Node\Identifier) {
                $recv = $this->receiverClass($node->var, $ctx, $cls);
                $this->call($ctx, ['kind' => 'method', 'class' => $recv, 'method' => $node->name->toString(), 'line' => $line]);
                $this->site($ctx, '->' . $node->name->toString(), 'method', $node->args, $line, $recv, $this->chainRoot($node->var));
            }
            return;
        }

        if ($node instanceof Expr\FuncCall && $node->name instanceof Name) {
            $fq = resolved($node->name) ?? $node->name->toString();
            $short = short_name($fq);
            $lower = strtolower($short);
            $builtin = isset($this->internal[$lower]);
            $this->call($ctx, ['kind' => 'function', 'name' => $short, 'fqn' => $fq, 'builtin' => $builtin, 'line' => $line]);
            $this->site($ctx, $short, 'function', $node->args, $line, null);
            return;
        }

        if ($node instanceof Expr\Include_) {
            $path = $node->expr instanceof Node\Scalar\String_ ? $node->expr->value : null;
            $this->includes[] = ['path' => $path, 'expr' => $path === null ? $this->text($node->expr) : null, 'line' => $line,
                'kind' => ['1' => 'include', '2' => 'include_once', '3' => 'require', '4' => 'require_once'][(string) $node->type] ?? 'include'];
            if ($ctx !== null) $this->routines[$ctx['idx']]['calls'][] = ['kind' => 'include', 'path' => $path, 'line' => $line];
            else $this->topCalls[] = ['kind' => 'include', 'path' => $path, 'line' => $line];
            return;
        }

        if ($node instanceof Expr\ArrayDimFetch && $node->var instanceof Expr\Variable && is_string($node->var->name)
            && in_array($node->var->name, ['_GET', '_POST', '_REQUEST', '_SESSION', '_COOKIE', '_FILES', '_SERVER', '_ENV'], true)) {
            $key = $node->dim instanceof Node\Scalar\String_ ? $node->dim->value : null;
            $entry = ['name' => '$' . $node->var->name, 'key' => $key, 'line' => $line];
            if ($ctx !== null) $this->routines[$ctx['idx']]['superglobals'][] = $entry;
            else $this->topSites[] = ['callee' => '$' . $node->var->name, 'type' => 'superglobal', 'args' => [['s' => $key]], 'line' => $line];
            return;
        }

        if ($node instanceof Expr\BinaryOp\Concat) {
            // "SELECT ... " . $x . " WHERE ..." — the literal head is enough to see the verb and tables. The whole
            // chain is one site: its inner concatenations and its head string are marked so they are not recorded again.
            if ($node->getAttribute('astraSeen') === true) return;
            $inner = $node;
            while ($inner instanceof Expr\BinaryOp\Concat) { $inner->setAttribute('astraSeen', true); $inner = $inner->left; }
            $head = $this->concatHead($node);
            if ($head !== null && strlen($head) > 6 && preg_match(SQL_START, $head) === 1) {
                $inner->setAttribute('astraSeen', true);
                $this->pushSite($ctx, ['callee' => 'sql', 'type' => 'sql', 'text' => substr($head, 0, 2000), 'line' => $line, 'dynamic' => true]);
            }
            return;
        }

        if ($node instanceof Node\Scalar\String_ || $node instanceof Node\Scalar\Encapsed || $node instanceof Node\Scalar\InterpolatedString) {
            if ($node->getAttribute('astraSeen') === true) return;
            $text = $node instanceof Node\Scalar\String_ ? $node->value : $this->encapsedText($node);
            if (strlen($text) > 6 && preg_match(SQL_START, $text) === 1) {
                $this->pushSite($ctx, ['callee' => 'sql', 'type' => 'sql', 'text' => substr($text, 0, 2000), 'line' => $line]);
            }
            return;
        }
    }

    private function recordSwitch(Stmt\Switch_ $sw): void
    {
        $cond = $sw->cond;
        $src = null;
        if ($cond instanceof Expr\ArrayDimFetch && $cond->var instanceof Expr\Variable && is_string($cond->var->name)
            && in_array($cond->var->name, ['_GET', '_POST', '_REQUEST'], true)) {
            $src = ['var' => '$' . $cond->var->name, 'key' => $cond->dim instanceof Node\Scalar\String_ ? $cond->dim->value : null];
        } elseif ($cond instanceof Expr\Variable && is_string($cond->name)) {
            $src = ['var' => '$' . $cond->name, 'key' => null];
        }
        $cases = [];
        foreach ($sw->cases as $c) {
            if ($c->cond instanceof Node\Scalar\String_) {
                $calls = [];
                $finder = new NodeTraverser();
                $collector = new class($calls, $this) extends NodeVisitorAbstract {
                    public function __construct(public array &$calls, private $owner) {}
                    public function enterNode(Node $n)
                    {
                        if ($n instanceof Expr\FuncCall && $n->name instanceof Name) $this->calls[] = short_name($n->name->toString());
                        if ($n instanceof Expr\StaticCall && $n->class instanceof Name && $n->name instanceof Node\Identifier) $this->calls[] = short_name($n->class->toString()) . '::' . $n->name->toString();
                        if ($n instanceof Expr\MethodCall && $n->name instanceof Node\Identifier) $this->calls[] = '->' . $n->name->toString();
                        return null;
                    }
                };
                $finder->addVisitor($collector);
                $finder->traverse($c->stmts);
                $cases[] = ['label' => $c->cond->value, 'line' => $c->getStartLine(), 'lineEnd' => $c->getEndLine(), 'calls' => array_values(array_unique($calls))];
            }
        }
        if ($src !== null && count($cases) > 0) {
            $ctx = $this->routineStack ? end($this->routineStack) : null;
            $entry = ['callee' => 'switch', 'type' => 'dispatch', 'on' => $src, 'cases' => $cases, 'line' => $sw->getStartLine(), 'lineEnd' => $sw->getEndLine()];
            $this->pushSite($ctx, $entry);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────

    private function call(?array $ctx, array $c): void
    {
        if ($ctx !== null) $this->routines[$ctx['idx']]['calls'][] = $c;
        else $this->topCalls[] = $c;
    }

    private function site(?array $ctx, string $callee, string $type, array $args, int $line, ?string $class, ?string $root = null): void
    {
        $summary = [];
        $literal = false;
        foreach (array_slice($args, 0, 6) as $a) {
            if ($a instanceof Node\Arg) {
                $s = $this->argSummary($a->value);
                $summary[] = $s;
                if (isset($s['s']) || isset($s['cls']) || isset($s['arr']) || isset($s['n'])) $literal = true;
            }
        }
        // A site is a call a detector might read: one that passes something literal (a path, a class, a table name, a map).
        if (!$literal && $type !== 'new') return;
        $entry = ['callee' => $callee, 'type' => $type, 'args' => $summary, 'line' => $line];
        if ($class !== null) $entry['class'] = $class;
        if ($root !== null) $entry['root'] = $root;
        $this->pushSite($ctx, $entry);
    }

    private function pushSite(?array $ctx, array $entry): void
    {
        if (count($this->sites) + count($this->topSites) >= MAX_SITES_PER_FILE) return;
        if ($ctx !== null) $this->routines[$ctx['idx']]['sites'][] = $entry;
        else $this->topSites[] = $entry;
    }

    private function argSummary(Node $v): array
    {
        if ($v instanceof Node\Scalar\String_) return ['s' => $v->value];
        if ($v instanceof Node\Scalar\LNumber || $v instanceof Node\Scalar\Int_) return ['n' => $v->value];
        if ($v instanceof Node\Scalar\Float_ || $v instanceof Node\Scalar\DNumber) return ['n' => $v->value];
        if ($v instanceof Expr\ClassConstFetch && $v->class instanceof Name && $v->name instanceof Node\Identifier && strtolower($v->name->toString()) === 'class') {
            return ['cls' => resolved($v->class) ?? $v->class->toString()];
        }
        if ($v instanceof Expr\ConstFetch) return ['const' => $v->name->toString()];
        if ($v instanceof Expr\Array_) {
            $items = [];
            foreach (array_slice($v->items, 0, 24) as $it) {
                if ($it === null) continue;
                $row = ['v' => $this->argSummary($it->value)];
                if ($it->key !== null) $row['k'] = $this->argSummary($it->key);
                $items[] = $row;
            }
            return ['arr' => $items];
        }
        if ($v instanceof Expr\Closure || $v instanceof Expr\ArrowFunction) return ['closure' => true];
        return ['expr' => substr($this->text($v), 0, MAX_ARG_TEXT)];
    }

    private function text(Node $n): string
    {
        try { return $n instanceof Expr ? $this->printer->prettyPrintExpr($n) : $this->printer->prettyPrint([$n]); }
        catch (\Throwable $e) { return ''; }
    }

    private function encapsedText(Node\Scalar\Encapsed $n): string
    {
        $out = '';
        foreach ($n->parts as $p) $out .= $p instanceof Node\InterpolatedStringPart || $p instanceof Node\Scalar\EncapsedStringPart ? $p->value : '?';
        return $out;
    }

    /** Literal text at the head of a concatenation chain ("SELECT * FROM t WHERE id = " . $id). */
    private function concatHead(Expr $e): ?string
    {
        while ($e instanceof Expr\BinaryOp\Concat) $e = $e->left;
        if ($e instanceof Node\Scalar\String_) return $e->value;
        if ($e instanceof Node\Scalar\Encapsed) return $this->encapsedText($e);
        return null;
    }

    private function className(Name $n, ?array $cls): ?string
    {
        $low = $n->toLowerString();
        if ($low === 'self' || $low === 'static') return $cls['fqn'] ?? null;
        if ($low === 'parent') return $cls['parent'] ?? null;
        return resolved($n);
    }

    private function typeName(Node $t): ?string
    {
        if ($t instanceof Node\NullableType) return $this->typeName($t->type);
        if ($t instanceof Name) {
            $low = $t->toLowerString();
            if (in_array($low, ['self', 'static', 'parent'], true)) return null;
            return resolved($t);
        }
        return null; // scalar types and unions are not class receivers
    }

    private function receiverClass(Expr $var, ?array $ctx, ?array $cls): ?string
    {
        if ($var instanceof Expr\Variable && is_string($var->name)) {
            if ($var->name === 'this') return $cls['fqn'] ?? null;
            return $ctx['vars'][$var->name] ?? null;
        }
        if ($var instanceof Expr\PropertyFetch && $var->var instanceof Expr\Variable && $var->var->name === 'this'
            && $var->name instanceof Node\Identifier) {
            return $cls['props'][$var->name->toString()] ?? null;
        }
        if ($var instanceof Expr\New_ && $var->class instanceof Name) return $this->className($var->class, $cls);
        return null;
    }

    private function chainRoot(Expr $e): ?string
    {
        while ($e instanceof Expr\MethodCall || $e instanceof Expr\NullsafeMethodCall || $e instanceof Expr\PropertyFetch || $e instanceof Expr\StaticCall) {
            if ($e instanceof Expr\StaticCall) return $e->class instanceof Name ? ($e->class->toString() . '::' . ($e->name instanceof Node\Identifier ? $e->name->toString() : '')) : null;
            $e = $e->var;
        }
        if ($e instanceof Expr\Variable && is_string($e->name)) return '$' . $e->name;
        return null;
    }

    private function attributes(array $groups): array
    {
        $out = [];
        foreach ($groups as $g) {
            foreach ($g->attrs as $a) {
                $args = [];
                foreach (array_slice($a->args, 0, 8) as $arg) {
                    $row = ['v' => $this->argSummary($arg->value)];
                    if ($arg->name !== null) $row['name'] = $arg->name->toString();
                    $args[] = $row;
                }
                $out[] = ['name' => resolved($a->name) ?? $a->name->toString(), 'args' => $args];
            }
        }
        return $out;
    }

    public function finishTopLevel(array $stmts): array
    {
        // Executable statements outside any class or function: a script's own flow (a front controller, a route file).
        $lines = [];
        foreach ($stmts as $s) $this->topStmtRange($s, $lines);
        return $lines;
    }

    private function topStmtRange(Node $s, array &$lines): void
    {
        if ($s instanceof Stmt\Namespace_) {
            foreach ($s->stmts as $c) $this->topStmtRange($c, $lines);
            return;
        }
        if ($s instanceof Stmt\Declare_ && $s->stmts === null) return;
        if ($s instanceof Stmt\Class_ || $s instanceof Stmt\Interface_ || $s instanceof Stmt\Trait_ || $s instanceof Stmt\Enum_
            || $s instanceof Stmt\Function_ || $s instanceof Stmt\Use_ || $s instanceof Stmt\GroupUse || $s instanceof Stmt\InlineHTML
            || $s instanceof Stmt\Nop || $s instanceof Stmt\Declare_ || $s instanceof Stmt\HaltCompiler) return;
        $lines[] = [$s->getStartLine(), $s->getEndLine()];
    }
}

function scan_file(string $filename, string $content, \PhpParser\Parser $parser): array
{
    $lineCount = substr_count(str_replace("\r\n", "\n", $content), "\n") + 1;
    $errors = new ErrorHandler\Collecting();
    $stmts = null;
    try {
        $stmts = $parser->parse($content, $errors);
    } catch (\Throwable $e) {
        return ['filename' => $filename, 'lineCount' => $lineCount, 'ok' => false, 'errors' => [$e->getMessage()],
            'routines' => [], 'classes' => [], 'includes' => [], 'top' => null];
    }
    $errs = array_map(fn($e) => $e->getMessage(), $errors->getErrors());
    if ($stmts === null) {
        return ['filename' => $filename, 'lineCount' => $lineCount, 'ok' => false, 'errors' => $errs ?: ['could not parse'],
            'routines' => [], 'classes' => [], 'includes' => [], 'top' => null];
    }

    // Names are resolved in a pass of their own first: the scanner reads a node's children (parameter types,
    // `Foo::class` arguments) when it enters the node, before a shared pass would have resolved them.
    $resolver = new NodeTraverser();
    $resolver->addVisitor(new NameResolver(null, ['replaceNodes' => false, 'preserveOriginalNames' => false]));
    $stmts = $resolver->traverse($stmts);
    $scanner = new Scanner();
    $traverser = new NodeTraverser();
    $traverser->addVisitor($scanner);
    $traverser->traverse($stmts);

    $ranges = $scanner->finishTopLevel($stmts);
    $top = null;
    if (count($ranges) > 0) {
        $from = min(array_map(fn($r) => $r[0], $ranges));
        $to = max(array_map(fn($r) => $r[1], $ranges));
        $top = ['lineStart' => $from, 'lineEnd' => $to, 'calls' => $scanner->topCalls, 'sites' => $scanner->topSites, 'statements' => count($ranges)];
    }
    return [
        'filename' => $filename, 'lineCount' => $lineCount, 'ok' => count($errs) === 0, 'errors' => $errs,
        'routines' => $scanner->routines, 'classes' => $scanner->classes, 'includes' => $scanner->includes, 'top' => $top,
    ];
}

$raw = stream_get_contents(STDIN);
$in = json_decode($raw === false ? '' : $raw, true);
if (!is_array($in) || !isset($in['files']) || !is_array($in['files'])) {
    fwrite(STDERR, "expected {\"files\":[...]} on stdin\n");
    echo json_encode(['files' => [], 'error' => 'bad input']);
    exit(2);
}
$parser = (new ParserFactory())->createForNewestSupportedVersion();
$out = [];
foreach ($in['files'] as $f) {
    $name = (string) ($f['filename'] ?? '');
    $content = (string) ($f['content'] ?? '');
    try {
        $out[] = scan_file($name, $content, $parser);
    } catch (\Throwable $e) {
        $out[] = ['filename' => $name, 'lineCount' => substr_count($content, "\n") + 1, 'ok' => false,
            'errors' => ['scanner failed: ' . $e->getMessage()], 'routines' => [], 'classes' => [], 'includes' => [], 'top' => null];
    }
}
echo json_encode(['files' => $out], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_INVALID_UTF8_SUBSTITUTE | JSON_PARTIAL_OUTPUT_ON_ERROR);
