<?php
/*
 * Astra PHP equivalence driver.
 *
 * Runs ONE PHP function or method with ONE input vector and writes what it observed (return value, echoed output,
 * response code, headers, by-reference outputs, $_SESSION after the call, exception, warnings) to a result file.
 * The Python sidecar starts a fresh `php` process per vector, under resource limits, with networking and process
 * functions disabled and open_basedir set to the run directory.
 *
 * Nothing from the request is ever passed to eval(). The source under test is loaded as DECLARATIONS ONLY: each
 * supplied file is tokenized and only its namespace/use/const/declare statements, function, class, interface,
 * trait and enum declarations, `if (!function_exists(...))` guards and literal define() calls are kept; top-level
 * statements (a script's own dispatch, includes, database connections, output) are dropped. Every class is written
 * to its own snippet next to its source file (so __DIR__ keeps its meaning) and loaded on demand by an autoloader;
 * functions are loaded eagerly behind function_exists() guards.
 *
 * Usage:  php run_routine.php prepare <request.json> <result.json>   (index the files; writes the snippets)
 *         php run_routine.php inspect <request.json> <result.json>   (describe the routine's parameters)
 *         php run_routine.php run     <request.json> <result.json>   (run one vector)
 */

namespace AstraEquivalenceDriver;

final class State
{
    public static string $resultPath = '';
    public static array $result = [];
    public static bool $written = false;
    public static array $warnings = [];
    public static array $index = ['classes' => [], 'functionFiles' => [], 'routines' => []];
    public static string $root = '';
    public static ?float $started = null;
    public static bool $calling = false;
    public static array $stubs = [];
    public static array $loaded = [];
}

function write_result(): void
{
    if (State::$written) return;
    State::$written = true;
    $r = State::$result;
    $r['warnings'] = array_slice(State::$warnings, 0, 50);
    $json = json_encode($r, JSON_PRESERVE_ZERO_FRACTION | JSON_PARTIAL_OUTPUT_ON_ERROR | JSON_INVALID_UTF8_SUBSTITUTE | JSON_UNESCAPED_SLASHES);
    file_put_contents(State::$resultPath, $json === false ? '{"ok":false,"error":"result not encodable"}' : $json);
}

/** exit()/die() inside the routine, or a fatal error, still produces a result. */
function on_shutdown(): void
{
    if (State::$written) return;
    if (State::$calling) {
        $out = '';
        while (ob_get_level() > 0) $out = ob_get_clean() . $out;
        State::$result['ok'] = true;   // the routine ran; it ended the request itself
        State::$result['output'] = $out;
        State::$result['exited'] = true;
        State::$result['returnValue'] = null;
        State::$result['responseCode'] = response_code();
        State::$result['headers'] = headers();
        State::$result['session'] = isset($_SESSION) && is_array($_SESSION) ? normalize($_SESSION) : null;
        if (State::$started !== null) State::$result['durationMs'] = (int) round((hrtime(true) - State::$started) / 1e6);
    }
    $err = error_get_last();
    if ($err !== null && in_array($err['type'], [E_ERROR, E_PARSE, E_CORE_ERROR, E_COMPILE_ERROR, E_USER_ERROR], true)) {
        State::$result['ok'] = false;
        State::$result['fatal'] = ['message' => $err['message'], 'line' => $err['line']];
    }
    write_result();
}

function response_code(): int
{
    $c = http_response_code();
    return is_int($c) && $c > 0 ? $c : 200;
}

function headers(): array
{
    $h = function_exists('headers_list') ? headers_list() : [];
    return array_values(array_filter($h, fn($l) => stripos($l, 'X-Powered-By:') !== 0));
}

// ─── Declarations-only loading ─────────────────────────────────────────────

function sig(array $toks, int $i, int $dir): int
{
    $n = count($toks);
    for ($j = $i + $dir; $j >= 0 && $j < $n; $j += $dir) {
        if (!$toks[$j]->isIgnorable()) return $j;
    }
    return -1;
}

/** Index of the token that closes the brace block opened at or after $from (returns the '}' index). */
function block_end(array $toks, int $from): int
{
    $n = count($toks);
    $i = $from;
    while ($i < $n && $toks[$i]->text !== '{') {
        if ($toks[$i]->text === ';') return $i;   // a body-less declaration
        $i++;
    }
    $depth = 0;
    for (; $i < $n; $i++) {
        $t = $toks[$i];
        if ($t->text === '{' || $t->id === T_CURLY_OPEN || $t->id === T_DOLLAR_OPEN_CURLY_BRACES) $depth++;
        elseif ($t->text === '}') {
            $depth--;
            if ($depth === 0) return $i;
        }
    }
    return $n - 1;
}

function statement_end(array $toks, int $from): int
{
    $n = count($toks);
    $depth = 0;
    for ($i = $from; $i < $n; $i++) {
        $x = $toks[$i]->text;
        if ($x === '(' || $x === '[' || $x === '{') $depth++;
        elseif ($x === ')' || $x === ']' || $x === '}') $depth--;
        elseif ($x === ';' && $depth <= 0) return $i;
    }
    return $n - 1;
}

function text(array $toks, int $a, int $b): string
{
    $s = '';
    for ($i = $a; $i <= $b; $i++) $s .= $toks[$i]->text;
    return $s;
}

/** Walk back over modifiers, attributes and the doc comment that belong to the declaration starting at $i. */
function decl_start(array $toks, int $i): int
{
    $start = $i;
    $j = sig($toks, $i, -1);
    while ($j >= 0) {
        $t = $toks[$j];
        if (in_array($t->id, [T_ABSTRACT, T_FINAL, T_READONLY], true)) { $start = $j; $j = sig($toks, $j, -1); continue; }
        if ($t->text === ']') {
            // an attribute group #[...]
            $depth = 0;
            for ($k = $j; $k >= 0; $k--) {
                if ($toks[$k]->text === ']') $depth++;
                elseif ($toks[$k]->text === '[') $depth--;
                elseif ($toks[$k]->id === T_ATTRIBUTE) { $depth--; }
                if ($depth === 0) break;
            }
            if ($k >= 0 && $toks[$k]->id === T_ATTRIBUTE) { $start = $k; $j = sig($toks, $k, -1); continue; }
        }
        break;
    }
    return $start;
}

/**
 * Splits one file into: header lines (namespace + use + declare), function declarations, class-like declarations
 * (each with its short name and line range), and literal constants. Handles braced namespaces.
 */
function extract_declarations(string $code): array
{
    $toks = \PhpToken::tokenize($code);
    $n = count($toks);
    $out = ['blocks' => []];   // blocks: list of ['ns' => string, 'uses' => [], 'items' => [...]]
    $ns = '';
    $uses = [];
    $items = [];
    $nsBraceDepth = null;     // brace depth at which a braced namespace's body lives
    $depth = 0;
    $flush = function () use (&$out, &$ns, &$uses, &$items) {
        if ($items) $out['blocks'][] = ['ns' => $ns, 'uses' => $uses, 'items' => $items];
        $items = [];
    };
    for ($i = 0; $i < $n; $i++) {
        $t = $toks[$i];
        $x = $t->text;
        if ($t->isIgnorable() || $t->id === T_INLINE_HTML || $t->id === T_OPEN_TAG || $t->id === T_CLOSE_TAG || $t->id === T_OPEN_TAG_WITH_ECHO) continue;
        $top = ($nsBraceDepth === null) ? $depth === 0 : $depth === $nsBraceDepth;
        if ($x === '{' || $t->id === T_CURLY_OPEN || $t->id === T_DOLLAR_OPEN_CURLY_BRACES) { $depth++; continue; }
        if ($x === '}') {
            $depth--;
            if ($nsBraceDepth !== null && $depth === $nsBraceDepth - 1) { $flush(); $nsBraceDepth = null; $ns = ''; $uses = []; }
            continue;
        }
        if (!$top) continue;

        if ($t->id === T_NAMESPACE) {
            $j = sig($toks, $i, 1);
            $name = '';
            while ($j >= 0 && ($toks[$j]->id === T_STRING || $toks[$j]->id === T_NAME_QUALIFIED)) { $name .= $toks[$j]->text; $j = sig($toks, $j, 1); }
            if ($j < 0) break;
            $flush();
            $ns = $name;
            $uses = [];
            if ($toks[$j]->text === '{') { $depth++; $nsBraceDepth = $depth; $i = $j; }
            else $i = $j; // the ';'
            continue;
        }
        if ($t->id === T_USE) {
            $p = sig($toks, $i, -1);
            if ($p >= 0 && $toks[$p]->text === ')') continue;   // a closure's use (...)
            $e = statement_end($toks, $i);
            $uses[] = text($toks, $i, $e);
            $i = $e;
            continue;
        }
        if ($t->id === T_DECLARE) {
            $e = statement_end($toks, $i);
            $i = $e;   // strict_types applies to calls made FROM a file; the driver makes the calls, so it is not kept
            continue;
        }
        if ($t->id === T_CONST) {
            $e = statement_end($toks, $i);
            $items[] = ['kind' => 'const', 'code' => text($toks, $i, $e), 'line' => $t->line];
            $i = $e;
            continue;
        }
        if ($t->id === T_FUNCTION) {
            $j = sig($toks, $i, 1);
            if ($j >= 0 && $toks[$j]->text === '&') $j = sig($toks, $j, 1);
            if ($j < 0 || $toks[$j]->id !== T_STRING) continue;   // a closure
            $s = decl_start($toks, $i);
            $e = block_end($toks, $j);
            $items[] = ['kind' => 'function', 'name' => $toks[$j]->text, 'code' => text($toks, $s, $e),
                        'line' => $toks[$s]->line, 'endLine' => $toks[$e]->line];
            $i = $e;
            continue;
        }
        if (in_array($t->id, [T_CLASS, T_INTERFACE, T_TRAIT, T_ENUM], true)) {
            $p = sig($toks, $i, -1);
            if ($p >= 0 && ($toks[$p]->id === T_DOUBLE_COLON || $toks[$p]->id === T_NEW)) continue;  // Foo::class, new class
            $j = sig($toks, $i, 1);
            if ($j < 0 || $toks[$j]->id !== T_STRING) continue;
            $s = decl_start($toks, $i);
            $e = block_end($toks, $j);
            $items[] = ['kind' => 'class', 'name' => $toks[$j]->text, 'code' => text($toks, $s, $e),
                        'line' => $toks[$s]->line, 'endLine' => $toks[$e]->line,
                        'methods' => methods_of($toks, $j, $e)];
            $i = $e;
            continue;
        }
        if ($t->id === T_IF) {
            $j = sig($toks, $i, 1);
            if ($j < 0 || $toks[$j]->text !== '(') continue;
            $close = $j; $d = 0;
            for ($k = $j; $k < $n; $k++) {
                if ($toks[$k]->text === '(') $d++;
                elseif ($toks[$k]->text === ')') { $d--; if ($d === 0) { $close = $k; break; } }
            }
            $cond = text($toks, $j, $close);
            if (preg_match('/^\(\s*!\s*\\\\?(function_exists|class_exists|interface_exists|trait_exists|enum_exists|defined)\s*\(/i', $cond)) {
                $b = sig($toks, $close, 1);
                if ($b >= 0 && $toks[$b]->text === '{') {
                    $e = block_end($toks, $b);
                    $inner = text($toks, $b + 1, $e - 1);
                    $items[] = ['kind' => 'guarded', 'code' => text($toks, $i, $e), 'line' => $t->line,
                                'functions' => preg_match_all('/\bfunction\s+&?\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(/', $inner, $m) ? $m[1] : []];
                    $i = $e;
                }
            }
            continue;
        }
        if ($t->id === T_STRING && strcasecmp($x, 'define') === 0) {
            $j = sig($toks, $i, 1);
            if ($j < 0 || $toks[$j]->text !== '(') continue;
            $e = statement_end($toks, $i);
            $hasVar = false;
            for ($k = $i; $k <= $e; $k++) {
                if (in_array($toks[$k]->id, [T_VARIABLE, T_STRING_VARNAME, T_DOLLAR_OPEN_CURLY_BRACES], true)) { $hasVar = true; break; }
                if ($toks[$k]->id === T_STRING && $k > $j && sig($toks, $k, 1) >= 0 && $toks[sig($toks, $k, 1)]->text === '(') { $hasVar = true; break; }
            }
            if (!$hasVar) $items[] = ['kind' => 'define', 'code' => text($toks, $i, $e), 'line' => $t->line];
            $i = $e;
            continue;
        }
    }
    $flush();
    return $out;
}

/** Method names declared directly in a class body, with their line numbers. */
function methods_of(array $toks, int $nameIdx, int $end): array
{
    $methods = [];
    $depth = 0;
    $open = false;
    for ($i = $nameIdx; $i <= $end; $i++) {
        $t = $toks[$i];
        if ($t->text === '{' || $t->id === T_CURLY_OPEN || $t->id === T_DOLLAR_OPEN_CURLY_BRACES) { $depth++; $open = true; continue; }
        if ($t->text === '}') { $depth--; continue; }
        if ($open && $depth === 1 && $t->id === T_FUNCTION) {
            $j = sig($toks, $i, 1);
            if ($j >= 0 && $toks[$j]->text === '&') $j = sig($toks, $j, 1);
            if ($j >= 0 && $toks[$j]->id === T_STRING) {
                $s = decl_start($toks, $i);
                // walk back over visibility/static keywords too
                $k = sig($toks, $i, -1);
                while ($k >= 0 && in_array($toks[$k]->id, [T_PUBLIC, T_PROTECTED, T_PRIVATE, T_STATIC, T_ABSTRACT, T_FINAL], true)) { $s = min($s, $k); $k = sig($toks, $k, -1); }
                $methods[] = ['name' => $toks[$j]->text, 'line' => $toks[$s]->line, 'endLine' => $toks[block_end($toks, $j)]->line];
            }
        }
    }
    return $methods;
}

function header_for(string $ns, array $uses): string
{
    $h = "<?php\n";
    if ($ns !== '') $h .= "namespace $ns;\n";
    foreach ($uses as $u) $h .= $u . "\n";
    return $h;
}

function fqn(string $ns, string $name): string
{
    return $ns === '' ? $name : $ns . '\\' . $name;
}

/** prepare: tokenize every supplied file once, write the snippets and an index the per-vector runs load. */
function prepare(array $req): array
{
    $root = State::$root;
    $index = ['classes' => [], 'functionFiles' => [], 'routines' => [], 'skippedFiles' => []];
    $n = 0;
    foreach ($req['files'] as $rel) {
        $abs = $root . '/' . $rel;
        $code = @file_get_contents($abs);
        if ($code === false) continue;
        try {
            $decl = extract_declarations($code);
        } catch (\Throwable $e) {
            $index['skippedFiles'][] = ['path' => $rel, 'reason' => $e->getMessage()];
            continue;
        }
        $dir = dirname($abs);
        $fnCode = '';
        foreach ($decl['blocks'] as $block) {
            $head = header_for($block['ns'], $block['uses']);
            $fnBody = '';
            foreach ($block['items'] as $item) {
                if ($item['kind'] === 'class') {
                    $snippet = $dir . '/.astra-decl-' . (++$n) . '.php';
                    file_put_contents($snippet, $head . $item['code'] . "\n");
                    $key = strtolower(fqn($block['ns'], $item['name']));
                    if (!isset($index['classes'][$key])) $index['classes'][$key] = $snippet;
                    $index['routines'][] = ['kind' => 'class', 'path' => $rel, 'fqn' => fqn($block['ns'], $item['name']),
                                            'line' => $item['line'], 'endLine' => $item['endLine'], 'methods' => $item['methods']];
                } elseif ($item['kind'] === 'function') {
                    $f = fqn($block['ns'], $item['name']);
                    $fnBody .= 'if (!\\function_exists(' . var_export($f, true) . ")) {\n" . $item['code'] . "\n}\n";
                    $index['routines'][] = ['kind' => 'function', 'path' => $rel, 'fqn' => $f, 'name' => $item['name'],
                                            'line' => $item['line'], 'endLine' => $item['endLine']];
                } elseif ($item['kind'] === 'const' || $item['kind'] === 'define') {
                    $fnBody .= $item['code'] . "\n";
                } elseif ($item['kind'] === 'guarded') {
                    $fnBody .= $item['code'] . "\n";
                    foreach ($item['functions'] as $g) {
                        $index['routines'][] = ['kind' => 'function', 'path' => $rel, 'fqn' => fqn($block['ns'], $g), 'name' => $g,
                                                'line' => $item['line'], 'endLine' => $item['line']];
                    }
                }
            }
            if ($fnBody !== '') $fnCode .= ($block['ns'] !== '' ? "namespace {$block['ns']} {\n" : "namespace {\n")
                . implode("\n", $block['uses']) . "\n" . $fnBody . "}\n";
        }
        if ($fnCode !== '') {
            $snippet = $dir . '/.astra-decl-' . (++$n) . '.php';
            file_put_contents($snippet, "<?php\n" . $fnCode);
            $index['functionFiles'][] = ['path' => $rel, 'snippet' => $snippet];
        }
    }
    file_put_contents($root . '/.astra-index.json', json_encode($index, JSON_UNESCAPED_SLASHES));
    $lint = [];
    return ['ok' => true, 'classes' => count($index['classes']), 'functionFiles' => count($index['functionFiles']),
            'routines' => count($index['routines']), 'skippedFiles' => $index['skippedFiles'], 'lint' => $lint];
}

function load_index(): void
{
    State::$index = json_decode((string) file_get_contents(State::$root . '/.astra-index.json'), true) ?: State::$index;
    spl_autoload_register(function (string $class) {
        $key = strtolower(ltrim($class, '\\'));
        $snippet = State::$index['classes'][$key] ?? null;
        if ($snippet === null) {
            // a class referenced by its short name from another namespace's snippet: one unambiguous match wins
            $short = strtolower(substr($key, strrpos($key, '\\') === false ? 0 : strrpos($key, '\\') + 1));
            $hits = array_values(array_filter(array_keys(State::$index['classes']), fn($k) => $k === $short || str_ends_with($k, '\\' . $short)));
            if (count($hits) === 1 && $hits[0] !== $key) {
                // load it and alias it under the requested name
                $real = State::$index['classes'][$hits[0]];
                require_once $real;
                if (class_exists($hits[0], false) || interface_exists($hits[0], false) || trait_exists($hits[0], false))
                    class_alias($hits[0], $class);
            }
            return;
        }
        // Not require_once: a snippet whose declaration failed (a parent not yet stubbed) must load again on retry.
        if (isset(State::$loaded[$snippet])) return;
        require $snippet;
        State::$loaded[$snippet] = true;
    });
    // functions first (they may name classes, which autoload)
    foreach (State::$index['functionFiles'] as $f) {
        try {
            require_once $f['snippet'];
        } catch (\Throwable $e) {
            State::$warnings[] = ['type' => 'load', 'message' => 'functions of ' . $f['path'] . ' not loaded: ' . $e->getMessage()];
        }
    }
}

/** Finds the routine: a function or a method by name, preferring the one declared at `line` in `path`. */
function locate(array $entry): array
{
    $name = $entry['name'];
    $path = $entry['path'] ?? null;
    $line = isset($entry['line']) ? (int) $entry['line'] : null;
    $class = $entry['className'] ?? null;
    $cands = [];
    foreach (State::$index['routines'] as $r) {
        if ($r['kind'] === 'function' && strcasecmp($r['name'], $name) === 0 && $class === null) {
            $score = ($path !== null && $r['path'] === $path ? 2 : 0) + ($line !== null && $line >= $r['line'] && $line <= $r['endLine'] ? 4 : 0);
            $cands[] = [$score, ['kind' => 'function', 'fqn' => $r['fqn']]];
        }
        if ($r['kind'] === 'class') {
            if ($class !== null && strcasecmp(substr($r['fqn'], strrpos('\\' . $r['fqn'], '\\')), $class) !== 0
                && strcasecmp($r['fqn'], ltrim($class, '\\')) !== 0) continue;
            foreach ($r['methods'] as $m) {
                if (strcasecmp($m['name'], $name) !== 0) continue;
                $score = 1 + ($path !== null && $r['path'] === $path ? 2 : 0) + ($line !== null && $line >= $m['line'] && $line <= $m['endLine'] ? 4 : 0);
                $cands[] = [$score, ['kind' => 'method', 'class' => $r['fqn'], 'name' => $m['name']]];
            }
        }
    }
    // A class hint that matches nothing (the parser names the enclosing type its own way) is dropped: the name and
    // the line still say which routine it is.
    if (!$cands && $class !== null) {
        $entry['className'] = null;
        return locate($entry);
    }
    if (!$cands) throw new \RuntimeException("routine '$name' is not declared in the supplied files");
    usort($cands, fn($a, $b) => $b[0] <=> $a[0]);
    return $cands[0][1];
}

function reflect(array $loc): \ReflectionFunctionAbstract
{
    if ($loc['kind'] === 'function') return new \ReflectionFunction($loc['fqn']);
    // The method's class may extend a class (or implement an interface, use a trait) declared in a file that was not
    // supplied — a framework base class such as Dolibarr's CommonObject. That type is stubbed as an empty declaration
    // written by the driver (its name checked to be an identifier; nothing from the request is evaluated) and noted;
    // a routine that then relies on the stub's behaviour fails visibly and the vector is skipped by the comparer.
    for ($attempt = 0; $attempt < 24; $attempt++) {
        try {
            $rc = new \ReflectionClass($loc['class']);
            // Constant expressions in defaults are evaluated lazily; evaluate them here, inside the retry.
            for ($c = $rc; $c !== false; $c = $c->getParentClass()) {
                $c->getConstants();
                $c->getDefaultProperties();
            }
            return $rc->getMethod($loc['name']);
        } catch (\Error | \ReflectionException $e) {
            // A class declaration that names a constant the application defines while it boots (osTicket's
            // TICKET_TABLE, Dolibarr's MAIN_DB_PREFIX): the constant stands in as its own name, and that is noted.
            if (preg_match('/^Undefined constant "([A-Za-z_][A-Za-z0-9_]*)"$/', $e->getMessage(), $c) && !defined($c[1])) {
                define($c[1], $c[1]);
                State::$stubs[] = 'constant ' . $c[1];
                continue;
            }
            if (!preg_match('/^(Class|Interface|Trait) "([A-Za-z_\\\\][A-Za-z0-9_\\\\]*)" not found/', $e->getMessage(), $m)) throw $e;
            if (strcasecmp(ltrim($m[2], '\\'), ltrim($loc['class'], '\\')) === 0) throw $e;   // never stub the routine's own class
            stub_type(strtolower($m[1]), $m[2]);
        }
    }
    throw new \RuntimeException('too many missing types around ' . $loc['class']);
}

function stub_type(string $kind, string $fqn): void
{
    $fqn = ltrim($fqn, '\\');
    $pos = strrpos($fqn, '\\');
    $ns = $pos === false ? '' : substr($fqn, 0, $pos);
    $short = $pos === false ? $fqn : substr($fqn, $pos + 1);
    $word = $kind === 'interface' ? 'interface' : ($kind === 'trait' ? 'trait' : 'class');
    // A stubbed base class accepts any constructor arguments and any property, so the subclass can be built.
    $decl = $word === 'class' ? "#[\\AllowDynamicProperties]\nclass $short { public function __construct(...\$a) {} }" : "$word $short {}";
    $snippet = State::$root . '/.astra-stub-' . md5($fqn) . '.php';
    file_put_contents($snippet, "<?php\n" . ($ns !== '' ? "namespace $ns;\n" : '') . $decl . "\n");
    require_once $snippet;
    State::$stubs[] = $word . ' ' . $fqn;
}

function describe(\ReflectionFunctionAbstract $f): array
{
    $params = [];
    foreach ($f->getParameters() as $p) {
        $d = null;
        if ($p->isDefaultValueAvailable()) {
            try { $d = normalize($p->getDefaultValue()); } catch (\Throwable) { $d = null; }
        }
        $params[] = ['name' => $p->getName(), 'type' => $p->hasType() ? (string) $p->getType() : null,
                     'optional' => $p->isOptional(), 'byRef' => $p->isPassedByReference(), 'variadic' => $p->isVariadic(),
                     'default' => $d];
    }
    $out = ['kind' => $f instanceof \ReflectionMethod ? 'method' : 'function', 'name' => $f->getName(), 'params' => $params,
            'returnType' => $f->hasReturnType() ? (string) $f->getReturnType() : null];
    if ($f instanceof \ReflectionMethod) {
        $out['class'] = $f->getDeclaringClass()->getName();
        $out['static'] = $f->isStatic();
        $out['visibility'] = $f->isPrivate() ? 'private' : ($f->isProtected() ? 'protected' : 'public');
    }
    return $out;
}

function norm_name(string $s): string
{
    return strtolower(preg_replace('/[^A-Za-z0-9]/', '', $s));
}

/** JSON-safe picture of a PHP value: assoc arrays become objects, objects their public properties. */
function normalize(mixed $v, int $depth = 0, array &$seen = []): mixed
{
    if ($depth > 10) return '…';
    if ($v === null || is_bool($v) || is_int($v) || is_string($v)) return $v;
    if (is_float($v)) {
        if (is_nan($v)) return 'NaN';
        if (is_infinite($v)) return $v > 0 ? 'Infinity' : '-Infinity';
        return $v;
    }
    if (is_array($v)) {
        if (array_is_list($v)) return array_map(fn($x) => normalize($x, $depth + 1, $seen), $v);
        $o = [];
        foreach ($v as $k => $x) $o[(string) $k] = normalize($x, $depth + 1, $seen);
        return (object) $o;
    }
    if ($v instanceof \UnitEnum) return $v instanceof \BackedEnum ? $v->value : $v->name;
    if ($v instanceof \DateTimeInterface) return $v->format(DATE_ATOM);
    if (is_object($v)) {
        $id = spl_object_id($v);
        if (isset($seen[$id])) return '(cycle)';
        $seen[$id] = true;
        if ($v instanceof \JsonSerializable) {
            try { return normalize($v->jsonSerialize(), $depth + 1, $seen); } catch (\Throwable) {}
        }
        $o = ['__class' => (new \ReflectionClass($v))->getShortName()];
        foreach (get_object_vars($v) as $k => $x) $o[$k] = normalize($x, $depth + 1, $seen);
        return (object) $o;
    }
    if (is_resource($v)) return '(resource ' . get_resource_type($v) . ')';
    return (string) $v;
}

/** JSON input → PHP value: objects become associative arrays (the PHP way). */
function denormalize(mixed $v): mixed
{
    if (is_array($v)) return array_map(__NAMESPACE__ . '\denormalize', $v);
    if (is_object($v)) {
        $a = [];
        foreach (get_object_vars($v) as $k => $x) $a[$k] = denormalize($x);
        return $a;
    }
    return $v;
}

function apply_superglobals(array $sg): void
{
    $get = isset($sg['get']) ? denormalize($sg['get']) : [];
    $post = isset($sg['post']) ? denormalize($sg['post']) : [];
    $cookie = isset($sg['cookie']) ? denormalize($sg['cookie']) : [];
    $_GET = is_array($get) ? $get : [];
    $_POST = is_array($post) ? $post : [];
    $_COOKIE = is_array($cookie) ? $cookie : [];
    $_REQUEST = isset($sg['request']) ? (array) denormalize($sg['request']) : array_merge($_GET, $_POST);
    $_FILES = [];
    $server = isset($sg['server']) ? (array) denormalize($sg['server']) : [];
    $_SERVER = array_merge(['REQUEST_METHOD' => $_POST ? 'POST' : 'GET', 'REQUEST_URI' => '/', 'SCRIPT_NAME' => '/index.php',
                            'HTTP_HOST' => 'localhost', 'SERVER_NAME' => 'localhost', 'REMOTE_ADDR' => '127.0.0.1'], $server);
    if (array_key_exists('session', $sg)) $_SESSION = (array) denormalize($sg['session']);
    else $_SESSION = [];
}

/** Builds the call's argument list. Returns [args, notes] or throws a MissingInput. */
function bind_args(\ReflectionFunctionAbstract $f, array $vector, array &$refs): array
{
    $params = $f->getParameters();
    $notes = [];
    $args = [];
    if (array_key_exists('args', $vector) && is_array($vector['args'])) {
        foreach ($vector['args'] as $i => $a) {
            $refs[$i] = denormalize($a);
            $args[$i] = &$refs[$i];
        }
        return [$args, $notes];
    }
    $named = isset($vector['named']) ? (array) $vector['named'] : [];
    $byName = [];
    foreach ($named as $k => $v) $byName[norm_name((string) $k)] = [$k, $v];
    $matched = 0;
    foreach ($params as $p) if (isset($byName[norm_name($p->getName())])) $matched++;
    // The "lifted request" shape: none of the inputs is a parameter and the routine takes exactly one (or one
    // required) parameter — the inputs are that parameter's keys, e.g. resolveQty($input) reading $input['qty'].
    $required = array_values(array_filter($params, fn($p) => !$p->isOptional()));
    if ($matched === 0 && (count($params) === 1 || count($required) === 1)) {
        $target = count($params) === 1 ? $params[0] : $required[0];
        $type = $target->hasType() ? strtolower((string) $target->getType()) : 'array';
        if (str_contains($type, 'array') || $type === 'mixed' || $type === '?array') {
            $tp = $target->getPosition();
            for ($i = 0; $i < $tp; $i++) { $refs[$i] = $params[$i]->getDefaultValue(); $args[$i] = &$refs[$i]; }
            $refs[$tp] = denormalize((object) $named);
            $args[$tp] = &$refs[$tp];
            $notes[] = 'inputs passed as the keys of $' . $target->getName();
            return [$args, $notes];
        }
    }
    foreach ($params as $p) {
        $pos = $p->getPosition();
        $key = norm_name($p->getName());
        if (isset($byName[$key])) {
            $refs[$pos] = denormalize($byName[$key][1]);
        } elseif ($p->isDefaultValueAvailable()) {
            $refs[$pos] = $p->getDefaultValue();
        } elseif ($p->isVariadic()) {
            break;
        } elseif ($p->allowsNull() && $p->hasType()) {
            $refs[$pos] = null;
            $notes[] = '$' . $p->getName() . ' not supplied; passed null';
        } else {
            throw new MissingInput('$' . $p->getName());
        }
        $args[$pos] = &$refs[$pos];
    }
    return [$args, $notes];
}

final class MissingInput extends \RuntimeException {}

function instance_for(\ReflectionMethod $m, array $vector, array &$notes): ?object
{
    if ($m->isStatic()) return null;
    $rc = $m->getDeclaringClass();
    if ($rc->isAbstract() || $rc->isInterface()) throw new \RuntimeException('method of an abstract type: ' . $rc->getName());
    $ctor = $rc->getConstructor();
    if (isset($vector['ctorArgs']) && is_array($vector['ctorArgs'])) return $rc->newInstanceArgs(array_map(__NAMESPACE__ . '\denormalize', $vector['ctorArgs']));
    if ($ctor === null || $ctor->getNumberOfRequiredParameters() === 0) return $rc->newInstance();
    $notes[] = $rc->getShortName() . ' built without running its constructor (it needs ' . $ctor->getNumberOfRequiredParameters() . ' argument(s))';
    return $rc->newInstanceWithoutConstructor();
}

function run_vector(array $req): void
{
    $vector = $req['vector'] ?? [];
    State::$result = ['id' => $vector['id'] ?? null, 'ok' => false];
    load_index();
    try {
        $loc = locate($req['entry']);
        $f = reflect($loc);
    } catch (\Throwable $e) {
        State::$result['loadError'] = get_class($e) . ': ' . $e->getMessage();
        return;
    }
    State::$result['routine'] = describe($f);
    State::$result['stubs'] = State::$stubs;
    apply_superglobals(isset($vector['superglobals']) ? (array) $vector['superglobals'] : []);
    $refs = [];
    $notes = [];
    try {
        [$args, $bindNotes] = bind_args($f, $vector, $refs);
        $notes = array_merge($notes, $bindNotes);
        $obj = $f instanceof \ReflectionMethod ? instance_for($f, $vector, $notes) : null;
    } catch (MissingInput $e) {
        State::$result['missingInput'] = $e->getMessage();
        State::$result['notes'] = $notes;
        return;
    } catch (\Throwable $e) {
        State::$result['loadError'] = get_class($e) . ': ' . $e->getMessage();
        return;
    }
    if ($f instanceof \ReflectionMethod) $f->setAccessible(true);
    foreach (State::$stubs as $s)
        $notes[] = str_starts_with($s, 'constant ')
            ? "$s is defined when the application boots; it stands in as its own name here"
            : "$s is not in the supplied files; an empty stand-in was declared";
    State::$result['notes'] = $notes;
    $sessionBefore = json_encode(normalize($_SESSION));
    State::$calling = true;
    State::$started = hrtime(true);
    ob_start();
    $ret = null;
    try {
        $ret = $f instanceof \ReflectionMethod ? $f->invokeArgs($obj, $args) : $f->invokeArgs($args);
        State::$result['ok'] = true;
    } catch (\Throwable $e) {
        State::$result['ok'] = true;   // the routine ran; it threw
        State::$result['exception'] = ['class' => (new \ReflectionClass($e))->getShortName(), 'message' => $e->getMessage(),
                                       'code' => $e->getCode()];
    }
    $out = '';
    while (ob_get_level() > 0) $out = ob_get_clean() . $out;
    State::$calling = false;
    State::$result['durationMs'] = (int) round((hrtime(true) - State::$started) / 1e6);
    State::$result['returnValue'] = normalize($ret);
    State::$result['returnType'] = get_debug_type($ret);
    State::$result['output'] = $out;
    State::$result['responseCode'] = response_code();
    State::$result['headers'] = headers();
    $byRef = [];
    foreach ($f->getParameters() as $p) if ($p->isPassedByReference() && array_key_exists($p->getPosition(), $refs)) $byRef[$p->getName()] = normalize($refs[$p->getPosition()]);
    State::$result['byRef'] = (object) $byRef;
    $sessionAfter = normalize($_SESSION ?? []);
    State::$result['session'] = $sessionAfter;
    State::$result['sessionChanged'] = json_encode($sessionAfter) !== $sessionBefore;
}

function inspect(array $req): void
{
    State::$result = ['ok' => false];
    load_index();
    try {
        State::$result['routine'] = describe(reflect(locate($req['entry'])));
        State::$result['stubs'] = State::$stubs;
        State::$result['ok'] = true;
    } catch (\Throwable $e) {
        State::$result['loadError'] = get_class($e) . ': ' . $e->getMessage();
    }
}

// ─── main ────────────────────────────────────────────────────────────────

$mode = $argv[1] ?? (getenv('ASTRA_MODE') ?: '');
$reqPath = $argv[2] ?? (getenv('ASTRA_REQUEST') ?: '');
State::$resultPath = $argv[3] ?? (getenv('ASTRA_RESULT') ?: '');
State::$root = getenv('ASTRA_ROOT') ?: dirname($reqPath);
register_shutdown_function(__NAMESPACE__ . '\on_shutdown');
set_error_handler(function (int $no, string $msg, string $file, int $line) {
    if (count(State::$warnings) < 50) State::$warnings[] = ['type' => match ($no) {
        E_WARNING, E_USER_WARNING => 'warning', E_NOTICE, E_USER_NOTICE => 'notice', E_DEPRECATED, E_USER_DEPRECATED => 'deprecated', default => 'error',
    }, 'message' => $msg, 'line' => $line];
    return true;   // PHP carries on as it would in production (display_errors off)
});
$req = json_decode((string) file_get_contents($reqPath), true);
if (!is_array($req)) { State::$result = ['ok' => false, 'error' => 'request is not JSON']; write_result(); exit(0); }
switch ($mode) {
    case 'prepare': State::$result = prepare($req); break;
    case 'inspect': inspect($req); break;
    case 'run': run_vector($req); break;
    default: State::$result = ['ok' => false, 'error' => "unknown mode '$mode'"];
}
write_result();
