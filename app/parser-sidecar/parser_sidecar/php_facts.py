"""
Astra parser sidecar — facts a PHP code base states, for the entity graph.

The PHP scan (`php_native`) says what the code literally contains: routines, resolved calls and neutral "sites"
(calls with their literal arguments, SQL strings, superglobal reads, switch dispatch). This module reads those
sites with small, independent **detectors** and states facts in the API's FactSet shape:

    {"entities": [{"type", "name", "locator", "attrs"}],
     "links":    [{"relation", "from": {"type","name"}, "to": {"type","name"}, "locator", "confidence", "attrs"}]}

Every fact points at the lines that show it (`L12-12`), so the API can cite it through the routine that contains
those lines. Detectors are pure functions over one file's scan plus a class index of the whole corpus; adding a
framework is adding a detector to `DETECTORS`, nothing else. Nothing is guessed silently: a fact read by a
heuristic is stated with `confidence: "medium"`.

What is stated
- `route`     an HTTP route the application serves: `GET /invoices/{id}`, or `ANY /api.php?action=getCustomer` for a
              front controller; it `triggers` the class (or script) that handles it.
- `class`     a PHP class/interface/trait/enum that is an actor in some fact.
- `script`    a PHP file whose own statements act (a front controller, a routes file).
- `table`     a database table named in SQL or by a query builder; read, written or referenced by the actor.
- `config-key` a setting the code reads (`env('DB_HOST')`, `getenv`, `config('app.timezone')`, `$_ENV['X']`).
- `file`      a file included by a literal path.
"""
from __future__ import annotations

import re
from dataclasses import dataclass, field
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Tuple

ROUTE = "route"
CLASS = "class"
SCRIPT = "script"
TABLE = "table"
CONFIG_KEY = "config-key"
FILE = "file"

READS, WRITES, REFERENCES, TRIGGERS, CONTAINS = "reads", "writes", "references", "triggers", "contains"
HIGH, MEDIUM = "high", "medium"

HTTP_VERBS = ("get", "post", "put", "patch", "delete", "options", "head")
_ROUTER_ROOT = re.compile(r"^\$(app|router|routes|route|r|group|api|web|map|collector|routeCollector)$", re.IGNORECASE)
_ROUTER_CLASSES = {"route", "router"}
_DISPATCH_VARS = {"action", "act", "op", "cmd", "command", "task", "do", "mode", "page", "route", "r", "a", "method", "func", "fn"}
_CONFIG_FUNCTIONS = {"env": "env", "getenv": "getenv", "config": "config", "ini_get": "ini"}
_CONFIG_STATICS = {"config::get", "config::string", "config::integer", "config::boolean", "env::get", "configure::read"}
_QUERY_BUILDER_TABLE = {"table", "from", "into"}
# Active-record style database objects (CodeIgniter 3 and alike): the first argument names the table.
_DB_ROOTS = {"$this->db", "$db", "$this->database", "$database", "$this->connection", "$connection", "$conn", "$this->conn"}
_ACTIVE_RECORD_READS = {"get", "get_where", "getwhere", "from", "join", "count_all"}
_ACTIVE_RECORD_WRITES = {"insert", "insert_batch", "update", "update_batch", "delete", "replace", "empty_table", "truncate", "upsert"}


def short(fqn: Optional[str]) -> Optional[str]:
    if not fqn:
        return None
    return fqn.rsplit("\\", 1)[-1]


def loc(line: Optional[int], end: Optional[int] = None) -> Optional[str]:
    if not line:
        return None
    return f"L{int(line)}-{int(end or line)}"


# ──────────────────────────────────────────────────────────────────────
# Output
# ──────────────────────────────────────────────────────────────────────


@dataclass
class Facts:
    entities: Dict[Tuple[str, str], dict] = field(default_factory=dict)
    links: Dict[Tuple[str, str, str, str, str], dict] = field(default_factory=dict)

    def entity(self, type_: str, name: str, locator: Optional[str] = None, **attrs) -> None:
        key = (type_, name)
        clean = {k: v for k, v in attrs.items() if v not in (None, "", [], {})}
        if key in self.entities:
            existing = self.entities[key]
            if existing.get("locator") is None and locator:
                existing["locator"] = locator
            for k, v in clean.items():
                existing.setdefault("attrs", {}).setdefault(k, v)
            return
        e = {"type": type_, "name": name}
        if locator:
            e["locator"] = locator
        if clean:
            e["attrs"] = clean
        self.entities[key] = e

    def link(self, relation: str, from_: Tuple[str, str], to: Tuple[str, str], locator: Optional[str],
             confidence: str = HIGH, **attrs) -> None:
        key = (relation, from_[0], from_[1], to[0], to[1])
        clean = {k: v for k, v in attrs.items() if v not in (None, "", [], {})}
        if key in self.links:
            existing = self.links[key]
            # The strongest statement wins; the first place that shows it stays its evidence.
            if confidence == HIGH:
                existing["confidence"] = HIGH
            return
        l = {"relation": relation, "from": {"type": from_[0], "name": from_[1]}, "to": {"type": to[0], "name": to[1]},
             "confidence": confidence}
        if locator:
            l["locator"] = locator
        if clean:
            l["attrs"] = clean
        self.links[key] = l

    def to_json(self) -> dict:
        return {"entities": list(self.entities.values()), "links": list(self.links.values())}


# ──────────────────────────────────────────────────────────────────────
# Reading argument summaries (see php_scan.php::argSummary)
# ──────────────────────────────────────────────────────────────────────


def s(arg: Optional[dict]) -> Optional[str]:
    return arg.get("s") if isinstance(arg, dict) and isinstance(arg.get("s"), str) else None


def cls(arg: Optional[dict]) -> Optional[str]:
    return arg.get("cls") if isinstance(arg, dict) and isinstance(arg.get("cls"), str) else None


def arr(arg: Optional[dict]) -> Optional[List[dict]]:
    return arg.get("arr") if isinstance(arg, dict) and isinstance(arg.get("arr"), list) else None


def strings_of(arg: Optional[dict]) -> List[str]:
    items = arr(arg)
    if items is None:
        one = s(arg)
        return [one] if one else []
    return [x for x in (s(i.get("v")) for i in items) if x]


def named(args: Sequence[dict], name: str) -> Optional[dict]:
    for a in args:
        if a.get("name") == name:
            return a.get("v")
    return None


def positional(args: Sequence[dict], i: int) -> Optional[dict]:
    pos = [a.get("v") for a in args if "name" not in a]
    return pos[i] if i < len(pos) else None


@dataclass(frozen=True)
class Handler:
    cls: Optional[str]      # fully qualified when the code names it with ::class; else as written
    method: Optional[str]
    closure: bool = False

    @property
    def label(self) -> str:
        if self.closure:
            return "closure"
        c = short(self.cls) or "?"
        return f"{c}::{self.method}" if self.method else c


_HANDLER_STRING = re.compile(r"^\\?(?P<cls>[A-Za-z_\\][\w\\]*)(?:@|::|:)(?P<m>[A-Za-z_]\w*)$")


def handler_of(arg: Optional[dict]) -> Optional[Handler]:
    """`[Ctrl::class, 'm']`, `'Ctrl@m'` (Laravel), `'Ctrl::m'` (CodeIgniter 4), `'Ctrl:m'` (Slim), `Ctrl::class` (invokable), a closure."""
    if not isinstance(arg, dict):
        return None
    if arg.get("closure"):
        return Handler(None, None, closure=True)
    items = arr(arg)
    if items is not None and len(items) >= 2:
        c = cls(items[0].get("v")) or s(items[0].get("v"))
        m = s(items[1].get("v"))
        if c and m:
            return Handler(c, m)
    c = cls(arg)
    if c:
        return Handler(c, "__invoke")
    text = s(arg)
    if text:
        m = _HANDLER_STRING.match(text.strip())
        if m:
            return Handler(m.group("cls"), m.group("m"))
    expr = arg.get("expr") if isinstance(arg.get("expr"), str) else None
    if expr:
        # Slim: Ctrl::class . ':method'
        m = re.match(r"^\\?(?P<cls>[\w\\]+)::class\s*\.\s*'[:@](?P<m>\w+)'$", expr.strip())
        if m:
            return Handler(m.group("cls"), m.group("m"))
    return None


def normalise_path(path: str) -> str:
    p = path.strip()
    if not p.startswith("/"):
        p = "/" + p
    p = re.sub(r"/{2,}", "/", p)
    return p if p == "/" else p.rstrip("/")


def looks_like_path(text: Optional[str]) -> bool:
    if not text or len(text) > 300 or re.search(r"\s", text):
        return False
    if re.match(r"^[a-z]+://", text, re.IGNORECASE):
        return False
    return True


# ──────────────────────────────────────────────────────────────────────
# Context passed to detectors
# ──────────────────────────────────────────────────────────────────────


@dataclass
class Actor:
    type: str
    name: str

    @property
    def key(self) -> Tuple[str, str]:
        return (self.type, self.name)


@dataclass
class FileContext:
    filename: str
    scan: dict
    facts: Facts
    classes: Dict[str, dict]           # FQN → class summary, across the corpus

    def actor_for(self, routine: Optional[dict]) -> Actor:
        if routine is not None and routine.get("class"):
            name = short(routine["class"]) or routine["class"]
            return Actor(CLASS, name)
        return Actor(SCRIPT, self.filename)

    def declare_actor(self, actor: Actor) -> None:
        if actor.type == SCRIPT:
            top = self.scan.get("top") or {}
            self.facts.entity(SCRIPT, actor.name, loc(top.get("lineStart"), top.get("lineStart")), language="php")
        else:
            fqn = next((c["name"] for c in self.scan.get("classes") or [] if short(c.get("name")) == actor.name), None)
            info = self.classes.get(fqn) if fqn else None
            if info is not None:
                self.facts.entity(CLASS, actor.name, loc(info.get("lineStart")), fqn=fqn, kind=info.get("kind"),
                                  extends=short(info.get("extends")), language="php")
            else:
                self.facts.entity(CLASS, actor.name)

    def handler_target(self, h: Handler) -> Optional[Tuple[str, str]]:
        if h.closure or not h.cls:
            return None
        return (CLASS, short(h.cls) or h.cls)

    def units(self) -> Iterable[Tuple[Optional[dict], List[dict]]]:
        """(routine or None for the file's own statements, its sites)."""
        for r in self.scan.get("routines") or []:
            yield r, r.get("sites") or []
        top = self.scan.get("top")
        if top:
            yield None, top.get("sites") or []


Detector = Callable[[FileContext], None]


# ──────────────────────────────────────────────────────────────────────
# Detectors — routes
# ──────────────────────────────────────────────────────────────────────


def _route(ctx: FileContext, verbs: Sequence[str], path: str, handler: Optional[Handler], line: int,
           detector: str, confidence: str = HIGH, **attrs) -> None:
    verb = "|".join(v.upper() for v in verbs) if verbs else "ANY"
    path = normalise_path(path)
    name = f"{verb} {path}"
    ctx.facts.entity(ROUTE, name, loc(line), method=verb, path=path, handler=handler.label if handler else None,
                     detector=detector, file=ctx.filename, **attrs)
    target = ctx.handler_target(handler) if handler else None
    if target is not None:
        ctx.facts.entity(*target)
        ctx.facts.link(TRIGGERS, (ROUTE, name), target, loc(line), confidence,
                       method=handler.method if handler else None)


_LARAVEL_RESOURCE = [
    ("index", "GET", ""), ("create", "GET", "/create"), ("store", "POST", ""), ("show", "GET", "/{id}"),
    ("edit", "GET", "/{id}/edit"), ("update", "PUT|PATCH", "/{id}"), ("destroy", "DELETE", "/{id}"),
]


def detect_router_calls(ctx: FileContext) -> None:
    """
    Route tables declared as calls: `Route::get('/x', [Ctrl::class, 'm'])` (Laravel), `$router->post(...)` (Lumen),
    `$app->get('/x', Ctrl::class . ':m')` (Slim), `$routes->get('x', 'Ctrl::m')` (CodeIgniter 4), plus `match`,
    `any`, `map`, and Laravel's `resource` / `apiResource`.
    """
    for _routine, sites in ctx.units():
        for site in sites:
            callee = (site.get("callee") or "")
            args = site.get("args") or []
            verb: Optional[str] = None
            router = False
            if site.get("type") == "static" and "::" in callee:
                klass, verb = callee.rsplit("::", 1)
                router = (short(klass) or "").lower() in _ROUTER_CLASSES
            elif site.get("type") == "method" and callee.startswith("->"):
                verb = callee[2:]
                root = site.get("root") or ""
                router = bool(_ROUTER_ROOT.match(root)) or (short(root.split("::")[0]) or "").lower() in _ROUTER_CLASSES
            if not router or not verb:
                continue
            v = verb.lower()
            line = int(site.get("line") or 0)
            if v in HTTP_VERBS:
                path = s(positional_args(args, 0))
                if looks_like_path(path):
                    _route(ctx, [v], path, handler_of(positional_args(args, 1)), line, "router-call")
            elif v == "any":
                path = s(positional_args(args, 0))
                if looks_like_path(path):
                    _route(ctx, [], path, handler_of(positional_args(args, 1)), line, "router-call")
            elif v in ("match", "map"):
                verbs = [x.lower() for x in strings_of(positional_args(args, 0))]
                path = s(positional_args(args, 1))
                if verbs and looks_like_path(path):
                    _route(ctx, verbs, path, handler_of(positional_args(args, 2)), line, "router-call")
            elif v in ("resource", "apiresource"):
                base = s(positional_args(args, 0))
                ctrl = cls(positional_args(args, 1)) or s(positional_args(args, 1))
                if looks_like_path(base) and ctrl:
                    for action, method, suffix in _LARAVEL_RESOURCE:
                        if v == "apiresource" and action in ("create", "edit"):
                            continue
                        _route(ctx, method.split("|"), base + suffix, Handler(ctrl, action), line, "router-resource")


def positional_args(args: Sequence[dict], i: int) -> Optional[dict]:
    return args[i] if i < len(args) else None


_ROUTE_ATTRIBUTE = re.compile(r"(^|\\)Route$")


def detect_route_attributes(ctx: FileContext) -> None:
    """`#[Route('/path', methods: ['GET'])]` on a method, prefixed by a `#[Route('/prefix')]` on its class (Symfony and others)."""
    prefixes: Dict[str, str] = {}
    for c in ctx.scan.get("classes") or []:
        for a in c.get("attributes") or []:
            if _ROUTE_ATTRIBUTE.search(a.get("name") or ""):
                p = s(positional(a.get("args") or [], 0)) or s(named(a.get("args") or [], "path"))
                if p is not None:
                    prefixes[c.get("name")] = p
    for r in ctx.scan.get("routines") or []:
        for a in r.get("attributes") or []:
            if not _ROUTE_ATTRIBUTE.search(a.get("name") or ""):
                continue
            args = a.get("args") or []
            path = s(positional(args, 0))
            if path is None:
                path = s(named(args, "path"))
            if path is None:      # '' is a real path: the class prefix itself
                continue
            prefix = prefixes.get(r.get("class") or "", "")
            methods = strings_of(named(args, "methods")) or strings_of(positional(args, 2))
            route_name = s(named(args, "name")) or s(positional(args, 1))
            full = (prefix.rstrip("/") + "/" + path.lstrip("/")) if prefix else (path or "/")
            _route(ctx, [m.lower() for m in methods], full, Handler(r.get("class"), r.get("name")),
                   int(r.get("lineStart") or 0), "route-attribute", routeName=route_name)


def detect_front_controller(ctx: FileContext) -> None:
    """
    A script that dispatches on a request parameter — `switch ($_REQUEST['action'])`, or `switch ($action)` where the
    variable's name says what it is — serves one route per case: `ANY /api.php?action=getCustomer`.
    """
    for routine, sites in ctx.units():
        for site in sites:
            if site.get("type") != "dispatch":
                continue
            on = site.get("on") or {}
            var, key = on.get("var") or "", on.get("key")
            confidence = HIGH
            if var in ("$_GET", "$_POST", "$_REQUEST"):
                if not key:
                    continue
                param = key
            elif var.lstrip("$").lower() in _DISPATCH_VARS:
                param = var.lstrip("$")
                confidence = MEDIUM   # a variable's name is a hint, not proof, that it came from the request
            else:
                continue
            actor = ctx.actor_for(routine)
            ctx.declare_actor(actor)
            for case in site.get("cases") or []:
                label = case.get("label")
                if not label:
                    continue
                name = f"ANY {normalise_path(ctx.filename)}?{param}={label}"
                calls = [c for c in case.get("calls") or [] if c]
                ctx.facts.entity(ROUTE, name, loc(case.get("line"), case.get("lineEnd")), method="ANY",
                                 path=normalise_path(ctx.filename), parameter=param, value=label,
                                 handler=calls[0] if calls else None, calls=calls[:8],
                                 detector="front-controller", file=ctx.filename)
                ctx.facts.link(TRIGGERS, (ROUTE, name), actor.key, loc(case.get("line"), case.get("lineEnd")), confidence,
                               calls=calls[:8])


# ──────────────────────────────────────────────────────────────────────
# Detectors — data and settings
# ──────────────────────────────────────────────────────────────────────

_IDENT = r"[`\"\[]?(?P<t{n}>[A-Za-z_][\w$]*(?:[`\"\]]?\.[`\"\[]?[A-Za-z_][\w$]*)?)[`\"\]]?"
_SQL_PATTERNS = [
    (re.compile(r"\bFROM\s+" + _IDENT.format(n=1), re.IGNORECASE), READS),
    (re.compile(r"\bJOIN\s+" + _IDENT.format(n=1), re.IGNORECASE), READS),
    (re.compile(r"\bINSERT\s+(?:IGNORE\s+)?INTO\s+" + _IDENT.format(n=1), re.IGNORECASE), WRITES),
    (re.compile(r"\bREPLACE\s+INTO\s+" + _IDENT.format(n=1), re.IGNORECASE), WRITES),
    (re.compile(r"^\s*\(?\s*UPDATE\s+(?:LOW_PRIORITY\s+|IGNORE\s+)*" + _IDENT.format(n=1), re.IGNORECASE), WRITES),
    (re.compile(r"\bDELETE\s+(?:\w+\s+)?FROM\s+" + _IDENT.format(n=1), re.IGNORECASE), WRITES),
    (re.compile(r"\bMERGE\s+(?:INTO\s+)?" + _IDENT.format(n=1), re.IGNORECASE), WRITES),
    (re.compile(r"\bTRUNCATE\s+(?:TABLE\s+)?" + _IDENT.format(n=1), re.IGNORECASE), WRITES),
]
_NOT_TABLES = {"dual", "select", "where", "set", "values", "lateral", "unnest", "information_schema", "the", "a"}


def tables_in_sql(sql: str) -> List[Tuple[str, str]]:
    """(table, reads|writes) for each table a statement names; a table both read and written is written."""
    found: Dict[str, str] = {}
    for pattern, relation in _SQL_PATTERNS:
        for m in pattern.finditer(sql):
            name = re.sub(r"[`\"\[\]]", "", m.group("t1"))
            if not name or name.lower() in _NOT_TABLES or name.startswith("$"):
                continue
            if found.get(name) == WRITES:
                continue
            found[name] = relation if relation == WRITES else found.get(name, READS)
    # DELETE FROM x is matched by both the FROM and the DELETE patterns; the write stands.
    return list(found.items())


def detect_sql(ctx: FileContext) -> None:
    for routine, sites in ctx.units():
        actor = None
        for site in sites:
            if site.get("type") != "sql":
                continue
            text = site.get("text") or ""
            pairs = tables_in_sql(text)
            if not pairs:
                continue
            if actor is None:
                actor = ctx.actor_for(routine)
                ctx.declare_actor(actor)
            confidence = MEDIUM if site.get("dynamic") else HIGH
            for table, relation in pairs:
                ctx.facts.entity(TABLE, table)
                ctx.facts.link(relation, actor.key, (TABLE, table), loc(site.get("line")), confidence,
                               statement=text[:200])


def detect_query_builder(ctx: FileContext) -> None:
    """`DB::table('invoices')`, `->from('x')`, Laravel `Schema::create('x', ...)` (declares the table)."""
    for routine, sites in ctx.units():
        for site in sites:
            callee = (site.get("callee") or "")
            args = site.get("args") or []
            first = s(positional_args(args, 0))
            if not first or not re.match(r"^[A-Za-z_][\w.]*$", first):
                continue
            low = callee.lower()
            kind = None
            if site.get("type") == "static":
                klass, _, method = low.rpartition("::")
                if (short(klass) or "") == "schema" and method == "create":
                    kind = "declare"
                elif (short(klass) or "") == "schema" and method in ("table", "drop", "dropifexists"):
                    kind = "alter"
                elif (short(klass) or "") in ("db", "capsule") and method in _QUERY_BUILDER_TABLE:
                    kind = "use"
            elif site.get("type") == "method":
                method = low[2:]
                root = (site.get("root") or "").lower()
                db_root = root in _DB_ROOTS or root.startswith("db::")
                if db_root and method in _ACTIVE_RECORD_READS:
                    kind = "read"
                elif db_root and method in _ACTIVE_RECORD_WRITES:
                    kind = "write"
                elif method in _QUERY_BUILDER_TABLE and (db_root or root.startswith(("$query", "$qb", "$builder"))):
                    kind = "use"
            if kind is None:
                continue
            actor = ctx.actor_for(routine)
            ctx.declare_actor(actor)
            line = loc(site.get("line"))
            if kind == "declare":
                ctx.facts.entity(TABLE, first, line, definedBy="schema-builder", file=ctx.filename)
                ctx.facts.link(CONTAINS, actor.key, (TABLE, first), line, HIGH)
            else:
                relation = {"alter": WRITES, "write": WRITES, "read": READS}.get(kind, REFERENCES)
                ctx.facts.entity(TABLE, first)
                ctx.facts.link(relation, actor.key, (TABLE, first), line, MEDIUM, via=callee)


def detect_config(ctx: FileContext) -> None:
    """Settings read: `env('X')`, `getenv('X')`, `config('app.x')`, `ini_get`, `Config::get('x')`, `$_ENV['X']`."""
    for routine, sites in ctx.units():
        for site in sites:
            callee = (site.get("callee") or "")
            key = s(positional_args(site.get("args") or [], 0))
            source = None
            if site.get("type") == "function" and callee.lower() in _CONFIG_FUNCTIONS:
                source = _CONFIG_FUNCTIONS[callee.lower()]
            elif site.get("type") == "static" and f"{(short(callee.split('::')[0]) or '').lower()}::{callee.rsplit('::', 1)[-1].lower()}" in _CONFIG_STATICS:
                source = "config"
            elif site.get("type") == "superglobal" and callee == "$_ENV":
                source = "env"
            if not source or not key or not re.match(r"^[A-Za-z_][\w.\-:]*$", key):
                continue
            actor = ctx.actor_for(routine)
            ctx.declare_actor(actor)
            ctx.facts.entity(CONFIG_KEY, key, None, source=source)
            ctx.facts.link(READS, actor.key, (CONFIG_KEY, key), loc(site.get("line")), HIGH, via=callee)
        if routine is not None:
            for sg in routine.get("superglobals") or []:
                if sg.get("name") == "$_ENV" and sg.get("key"):
                    actor = ctx.actor_for(routine)
                    ctx.declare_actor(actor)
                    ctx.facts.entity(CONFIG_KEY, sg["key"], None, source="env")
                    ctx.facts.link(READS, actor.key, (CONFIG_KEY, sg["key"]), loc(sg.get("line")), HIGH, via="$_ENV")


def detect_includes(ctx: FileContext) -> None:
    for inc in ctx.scan.get("includes") or []:
        path = inc.get("path")
        if not path:
            continue
        line = int(inc.get("line") or 0)
        routine = next((r for r in ctx.scan.get("routines") or []
                        if int(r.get("lineStart") or 0) <= line <= int(r.get("lineEnd") or 0)), None)
        actor = ctx.actor_for(routine)
        ctx.declare_actor(actor)
        ctx.facts.entity(FILE, path)
        ctx.facts.link(REFERENCES, actor.key, (FILE, path), loc(line), HIGH, via=inc.get("kind"))


DETECTORS: List[Tuple[str, Detector]] = [
    ("router-calls", detect_router_calls),
    ("route-attributes", detect_route_attributes),
    ("front-controller", detect_front_controller),
    ("sql", detect_sql),
    ("query-builder", detect_query_builder),
    ("config", detect_config),
    ("includes", detect_includes),
]


def facts_for(files: Sequence[Tuple[str, dict]]) -> Tuple[Dict[str, dict], List[str]]:
    """FactSet JSON per file, from each file's scan. A detector that fails on a file is reported, never fatal."""
    classes: Dict[str, dict] = {}
    for _, scan in files:
        for c in (scan or {}).get("classes") or []:
            if c.get("name"):
                classes[c["name"]] = c
    out: Dict[str, dict] = {}
    warnings: List[str] = []
    for filename, scan in files:
        if not scan:
            out[filename] = {"entities": [], "links": []}
            continue
        ctx = FileContext(filename=filename, scan=scan, facts=Facts(), classes=classes)
        for name, detector in DETECTORS:
            try:
                detector(ctx)
            except Exception as e:  # noqa: BLE001 — one detector's bug must not lose the file's other facts
                warnings.append(f"{filename}: the {name} detector failed ({type(e).__name__}: {e})")
        out[filename] = ctx.facts.to_json()
    return out, warnings
