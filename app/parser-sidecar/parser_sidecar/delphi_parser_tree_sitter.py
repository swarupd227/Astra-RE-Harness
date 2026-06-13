"""
Astra parser sidecar — Delphi / Object Pascal production parser.

**Phase 9.4.a (production scaffolding).** Walks the concrete syntax tree
emitted by Isopod/tree-sitter-pascal v0.10.2 and produces the existing
`ParseOutcome` shape so dispatch can swap between this module and the
v0 tokenizer-based `delphi_parser` without touching callers.

Strategy per ADR-031. The walker is calibrated against the actual node
vocabulary the grammar emits — confirmed by probing v0.10.2 inside the
sidecar container — not against the names ADR-024 speculated about.

Node-type cheat-sheet (v0.10.2 vocabulary)
------------------------------------------
- `unit` / `program` / `library`        — top-level module wrappers
- `moduleName > identifier`             — the unit name (may be dotted)
- `interface` / `implementation`        — section markers
- `declUses`                            — a `uses` clause; identifiers
                                          are `moduleName > identifier`
                                          children
- `declProc`                            — routine DECLARATION (forward
                                          decl OR the head of a body
                                          definition; carries the name
                                          + args + return type)
- `defProc`                             — routine DEFINITION (wraps a
                                          `declProc` head + a `block`)
- `kProcedure`/`kFunction`/`kConstructor`/`kDestructor` — keyword tokens
- `identifier` / `genericDot`           — routine name (the latter wraps
                                          a qualified `Class.Method`)
- `block`                               — body (kBegin ... kEnd)
- `statement`                           — single statement node; bare-
                                          identifier-followed-by-`;`
                                          inside one is a Delphi paren-
                                          less call
- `exprCall`                            — explicit `Foo(arg, ...)` call
- `pp_if` / `pp_ifdef` / `pp_ifndef`    — preprocessor conditionals
- `defAnonProc` / anonymous method      — function expression (skipped
                                          when collecting top-level
                                          routines)

What this parser handles better than the v0 tokenizer
-----------------------------------------------------
- Qualified method names (`TIdSMTPMin.Connect`) via `genericDot`
- Real call detection from `exprCall` nodes; bare-name calls
  (`Connect;`) recognised via `statement > identifier > ;`
- Multiple `uses` clauses (interface + implementation) collapsed into
  one ordered `common_block_refs` tuple
- Preprocessor branches walked through `pp_if` arms; routines inside
  conditional blocks still surface

What this parser does NOT handle
--------------------------------
- mORMot's heavy RTTI macros — the grammar may panic on some
  Embarcadero XE-era compiler-magic constructs (per ADR-031 OQ-031-1).
  Mitigated by the v0 fallback in `server.py` dispatch.
- Cross-unit semantic resolution — calls to identifiers declared in
  other `uses` clauses are recorded as-is; the Migration Planner does
  the cross-unit join.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import List, Optional, Tuple

import tree_sitter_pascal
from tree_sitter import Language, Parser

log = logging.getLogger("astra.parser.delphi.tree_sitter")


# ──────────────────────────────────────────────────────────────────────
# Output shape — matches `delphi_parser.SubroutineSummary` + the proto
# ──────────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class SubroutineSummary:
    name: str
    signature: str
    line_start: int
    line_end: int
    common_block_refs: Tuple[str, ...]
    called_subroutines: Tuple[str, ...]


@dataclass
class ParseOutcome:
    line_count: int
    subroutines: List[SubroutineSummary]
    warnings: List[str]
    filename: str


# ──────────────────────────────────────────────────────────────────────
# Lazy parser singleton
# ──────────────────────────────────────────────────────────────────────

_PARSER: Optional[Parser] = None


def _get_parser() -> Parser:
    global _PARSER
    if _PARSER is None:
        lang = Language(tree_sitter_pascal.language(), "pascal")
        p = Parser()
        p.set_language(lang)
        _PARSER = p
    return _PARSER


# ──────────────────────────────────────────────────────────────────────
# Node-type predicates (from v0.10.2 vocabulary)
# ──────────────────────────────────────────────────────────────────────

# Routine declarations (forward, header) AND definitions (with body).
# `defProc` wraps a `declProc` + `block`; we collect both and let the
# line-span dedup keep the larger entry (the definition) per ADR-024
# behavioural compatibility.
_DEF_ROUTINE = "defProc"
_DECL_ROUTINE = "declProc"

# Keyword tokens we skip past when extracting the routine name.
_ROUTINE_KEYWORDS = frozenset({
    "kProcedure", "kFunction", "kConstructor", "kDestructor",
    "kClass",  # `class procedure` modifier
})

# `uses` clause node + the per-name child shapes.
_USES_NODE_TYPE = "declUses"
_MODULE_NAME_NODE = "moduleName"

# Body node (kBegin ... kEnd).
_BODY_NODE = "block"

# Conditional-compilation nodes — walked transparently (we recurse into
# both arms; routines inside conditional blocks still surface in the
# output, with no special tagging in v1).
_CONDITIONAL_NODE_TYPES = frozenset({
    "pp_if", "pp_ifdef", "pp_ifndef", "pp_else", "pp_endif",
})

# Anonymous-method nodes — skipped at the top-level routine collection
# step (their body is still walked when computing the parent routine's
# call list).
_ANONYMOUS_ROUTINE_NODE_TYPES = frozenset({
    "defAnonProc",
})


# ──────────────────────────────────────────────────────────────────────
# Public entry point
# ──────────────────────────────────────────────────────────────────────


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single Delphi source file via tree-sitter-pascal."""
    warnings: List[str] = []
    raw = content.replace("\r\n", "\n").replace("\r", "\n")
    if not raw:
        return ParseOutcome(line_count=0, subroutines=[], warnings=[], filename=filename)
    line_count = raw.count("\n") + (0 if raw.endswith("\n") else 1)

    parser = _get_parser()
    src_bytes = raw.encode("utf-8")
    tree = parser.parse(src_bytes)
    if tree.root_node.type == "ERROR":
        raise RuntimeError(
            f"tree-sitter-pascal returned ERROR root for {filename}; falling back to v0"
        )

    uses_clauses = _collect_uses(tree.root_node, src_bytes)
    uses_tuple = tuple(uses_clauses)

    routines = _collect_routines(tree.root_node, src_bytes, uses_tuple)
    return ParseOutcome(
        line_count=line_count,
        subroutines=routines,
        warnings=warnings,
        filename=filename,
    )


# ──────────────────────────────────────────────────────────────────────
# `uses` walker
# ──────────────────────────────────────────────────────────────────────


def _collect_uses(root, src_bytes: bytes) -> List[str]:
    """Walk every `declUses` node + collect the `moduleName` children in
    source order, de-duplicated."""
    seen: List[str] = []
    seen_set = set()

    def visit(node):
        if node.type == _USES_NODE_TYPE:
            for child in node.children:
                if child.type == _MODULE_NAME_NODE:
                    name = src_bytes[child.start_byte:child.end_byte].decode("utf-8", errors="replace").strip()
                    if name and name not in seen_set:
                        seen.append(name)
                        seen_set.add(name)
            return
        for c in node.children:
            visit(c)

    visit(root)
    return seen


# ──────────────────────────────────────────────────────────────────────
# Routine walker
# ──────────────────────────────────────────────────────────────────────


def _collect_routines(
    root,
    src_bytes: bytes,
    common_block_refs: Tuple[str, ...],
) -> List[SubroutineSummary]:
    """Walk every routine-shaped node, build a `SubroutineSummary` per
    entry, then dedup by qualified name preferring the entry with the
    larger line span (definition wins over forward declaration)."""
    raw_entries: List[SubroutineSummary] = []

    def visit(node):
        if node.type in _ANONYMOUS_ROUTINE_NODE_TYPES:
            # Don't surface anonymous methods as routines, but walk their
            # body so any calls inside them count toward the parent's call
            # list. The parent walker collects calls via _collect_calls;
            # we don't need to do anything here — just return.
            return
        if node.type == _DEF_ROUTINE:
            # `defProc` wraps a `declProc` head and a `block` body. Build
            # one entry from the whole node (largest span); skip walking
            # its inner `declProc` so we don't double-emit.
            summary = _build_summary_from_def(node, src_bytes, common_block_refs)
            if summary is not None:
                raw_entries.append(summary)
            return
        if node.type == _DECL_ROUTINE:
            # `declProc` standalone (NOT inside a `defProc`) is a
            # forward-only declaration. Emit it with a small span so the
            # dedup picks the definition over it later.
            summary = _build_summary_from_decl(node, src_bytes, common_block_refs)
            if summary is not None:
                raw_entries.append(summary)
            return
        for c in node.children:
            visit(c)

    visit(root)
    return _dedup_by_name(raw_entries)


def _build_summary_from_def(
    def_node,
    src_bytes: bytes,
    common_block_refs: Tuple[str, ...],
) -> Optional[SubroutineSummary]:
    """Build a summary for a `defProc` node. Name + signature come from
    its inner `declProc`; body span + call list come from its `block`."""
    decl = _first_child_of_type(def_node, _DECL_ROUTINE)
    body = _first_child_of_type(def_node, _BODY_NODE)
    if decl is None:
        return None
    name = _extract_name(decl, src_bytes)
    if not name:
        return None
    signature = _signature_text(decl, src_bytes)
    called = _collect_calls(body, src_bytes) if body is not None else ()
    bare_self = name.rsplit(".", 1)[-1]
    called = tuple(c for c in called if c != bare_self)
    return SubroutineSummary(
        name=name,
        signature=signature,
        line_start=def_node.start_point[0] + 1,
        line_end=def_node.end_point[0] + 1,
        common_block_refs=common_block_refs,
        called_subroutines=called,
    )


def _build_summary_from_decl(
    decl_node,
    src_bytes: bytes,
    common_block_refs: Tuple[str, ...],
) -> Optional[SubroutineSummary]:
    """Build a summary for a stand-alone `declProc` (forward declaration
    or class-method declaration). No body, no call list."""
    name = _extract_name(decl_node, src_bytes)
    if not name:
        return None
    signature = _signature_text(decl_node, src_bytes)
    return SubroutineSummary(
        name=name,
        signature=signature,
        line_start=decl_node.start_point[0] + 1,
        line_end=decl_node.end_point[0] + 1,
        common_block_refs=common_block_refs,
        called_subroutines=(),
    )


def _extract_name(decl_node, src_bytes: bytes) -> Optional[str]:
    """Walk the `declProc` children: skip the leading routine keyword
    (`kProcedure`/`kFunction`/`kConstructor`/`kDestructor`), then return
    the first identifier-or-genericDot value."""
    seen_keyword = False
    for c in decl_node.children:
        if not seen_keyword:
            if c.type in _ROUTINE_KEYWORDS:
                seen_keyword = True
            continue
        if c.type in ("identifier", "genericDot", "qualifiedIdentifier"):
            return src_bytes[c.start_byte:c.end_byte].decode("utf-8", errors="replace").strip()
    # Fallback: first identifier-shaped descendant.
    return _first_identifier_value(decl_node, src_bytes)


def _signature_text(decl_node, src_bytes: bytes) -> str:
    """Return the `declProc` source verbatim, whitespace-collapsed and
    capped at 320 chars so a pathological generic-bounded signature
    doesn't blow out the spec page."""
    text = src_bytes[decl_node.start_byte:decl_node.end_byte].decode("utf-8", errors="replace")
    return " ".join(text.split())[:320]


# ──────────────────────────────────────────────────────────────────────
# Call walker
# ──────────────────────────────────────────────────────────────────────


def _collect_calls(body_node, src_bytes: bytes) -> Tuple[str, ...]:
    """Walk a routine body and emit every callee identifier, preserving
    discovery order + de-duplicated.

    Handles two call shapes:
      1. `exprCall` — explicit parameterized call (`Foo(a, b)`).
      2. `statement > identifier > ;` — Delphi's parenless statement
         call (`Foo;`). The grammar emits a bare identifier inside the
         statement node; we detect it by scanning the statement's
         immediate identifier children that are followed by `;`.
    """
    seen: List[str] = []
    seen_set = set()

    def emit(name: str) -> None:
        if not name:
            return
        bare = name.rsplit(".", 1)[-1]
        if bare in seen_set or _looks_like_keyword(bare):
            return
        seen.append(bare)
        seen_set.add(bare)

    def visit(node):
        if node.type == "exprCall":
            callee = _first_identifier_value(node, src_bytes)
            if callee:
                emit(callee)
            for c in node.children:
                visit(c)
            return
        if node.type == "statement":
            # Bare-name call: the statement has an `identifier` as its
            # first non-keyword child. Skip statements that are
            # assignments / control flow.
            kid_types = [c.type for c in node.children if c.type != ";"]
            if kid_types and kid_types[0] == "identifier":
                callee = src_bytes[node.children[0].start_byte:node.children[0].end_byte] \
                    .decode("utf-8", errors="replace").strip()
                emit(callee)
        for c in node.children:
            visit(c)

    visit(body_node)
    return tuple(seen)


# Common Delphi reserved words that an `identifier`-shaped node might
# erroneously surface. Filter them out so we don't add `if`/`else`/etc.
# to the call list.
_RESERVED_NOT_CALLS = frozenset({
    "begin", "end", "if", "then", "else", "while", "do", "for", "to", "downto",
    "repeat", "until", "case", "of", "with", "try", "except", "finally", "raise",
    "result", "self", "inherited", "nil", "true", "false", "and", "or", "not",
    "xor", "div", "mod", "shl", "shr", "in", "is", "as", "function", "procedure",
    "var", "const", "type", "uses", "unit", "interface", "implementation",
    "class", "record", "object", "array", "set", "string", "integer", "boolean",
    "exit", "halt",
})


def _looks_like_keyword(token: str) -> bool:
    return token.lower() in _RESERVED_NOT_CALLS


# ──────────────────────────────────────────────────────────────────────
# Helpers
# ──────────────────────────────────────────────────────────────────────


def _first_child_of_type(node, type_name: str):
    for c in node.children:
        if c.type == type_name:
            return c
    return None


def _first_identifier_value(node, src_bytes: bytes) -> Optional[str]:
    if node.type in ("identifier", "genericDot", "qualifiedIdentifier"):
        return src_bytes[node.start_byte:node.end_byte].decode("utf-8", errors="replace").strip()
    for c in node.children:
        found = _first_identifier_value(c, src_bytes)
        if found:
            return found
    return None


def _dedup_by_name(routines: List[SubroutineSummary]) -> List[SubroutineSummary]:
    """Mirror v0's dedup: when the same qualified name appears multiple
    times (forward declaration + implementation), keep the one with the
    larger body span. Preserve discovery order among the survivors.

    Qualified-name handling: when an unqualified declaration (`Connect`)
    coexists with a qualified definition (`TFoo.Connect`), they are
    DIFFERENT names. We preserve both. This matches the v0 tokenizer's
    behaviour when the same unit has e.g. `class procedure Foo` declared
    inside a class plus a global `procedure Foo`.

    We also collapse a class-internal declaration ending in `.X` with
    its corresponding qualified definition `T.X` by keying on the bare
    last segment when one of the two is unqualified — that's the v0
    dedup heuristic re-implemented faithfully.
    """
    best: dict[str, SubroutineSummary] = {}
    keys_by_routine: List[str] = []
    bare_to_qualified: dict[str, str] = {}

    # First pass: collect unique keys, remembering bare→qualified mapping.
    for r in routines:
        bare = r.name.rsplit(".", 1)[-1].lower()
        if "." in r.name and bare not in bare_to_qualified:
            bare_to_qualified[bare] = r.name

    for r in routines:
        bare = r.name.rsplit(".", 1)[-1].lower()
        # When this is an unqualified routine but a qualified version
        # exists, dedup to the qualified key (v0 behaviour).
        key = bare_to_qualified.get(bare, r.name) if "." not in r.name else r.name
        keys_by_routine.append(key)
        cur = best.get(key)
        if cur is None or (r.line_end - r.line_start) > (cur.line_end - cur.line_start):
            best[key] = r

    seen: set[str] = set()
    result: List[SubroutineSummary] = []
    for r, key in zip(routines, keys_by_routine):
        if key in seen:
            continue
        seen.add(key)
        result.append(best[key])
    return result
