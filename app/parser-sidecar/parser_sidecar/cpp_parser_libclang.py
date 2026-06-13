"""
Astra parser sidecar — C++ production parser.

**Phase 9.4.b (production scaffolding).** Walks libclang's translation
unit AST and produces the existing `ParseOutcome` shape so dispatch
can swap between this module and the v0 tokenizer-based `cpp_parser`
without touching callers.

Strategy per ADR-028. libclang gives us full preprocessor expansion,
semantic resolution, and real call detection — everything the v0
tokenizer approximated structurally. The walker hooks the following
cursor kinds:

  - `FUNCTION_DECL`           free-standing function definitions
  - `CXX_METHOD`              class methods (out-of-line + in-class)
  - `CONSTRUCTOR` /
    `DESTRUCTOR`              C++ class lifetime hooks
  - `FUNCTION_TEMPLATE`       template function declarations
  - `NAMESPACE`               traversed transparently
  - `CALL_EXPR` /
    `CXX_NEW_EXPR`            call-site identifier collection
  - `INCLUSION_DIRECTIVE`     `#include` edges (analogue of `uses`)

Compile-flag handling
---------------------
When a `compile_commands.json` is present (per ADR-028's CMake
auto-bootstrap), the caller threads the file's flags into `parse_source`
via the optional `compile_args` parameter. Otherwise we fall back to a
best-effort C++20 flag set with the supplied include directories — the
grammar is permissive enough that most header-only fmt-style code
parses, even if some symbol lookups fail. libclang's `parse` returns a
`TranslationUnit` regardless; we record any diagnostic >= ERROR on the
outcome's `warnings` list.

What this parser handles better than the v0 tokenizer
-----------------------------------------------------
- Preprocessor branches: `#ifdef`-conditional code is expanded against
  the active macro set; only the live branch's routines surface.
- Templates and SFINAE: function templates surface via the
  `FUNCTION_TEMPLATE` cursor kind with their parameter list and
  `requires` clause readable from the cursor's spelling.
- Namespaces: routines inside `namespace fmt::detail` carry the
  qualified name verbatim (`fmt::detail::format_int`), not just the
  bare last segment.
- Real call detection: walking `CALL_EXPR` cursors gives us the actual
  callee identifier — no more heuristic identifier-followed-by-paren
  guessing.

What this parser does NOT handle
--------------------------------
- Heavy template metaprogramming with `consteval` recursion — libclang
  parses it but the cursor walk surfaces only the primary template;
  per-instantiation analysis is out of scope (per ADR-026).
- Cross-translation-unit symbol resolution. A `CALL_EXPR` to a routine
  defined in a different `.cpp` file is recorded by name; the Migration
  Planner does the cross-TU join later.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import List, Optional, Sequence, Tuple

from clang import cindex

log = logging.getLogger("astra.parser.cpp.libclang")


# ──────────────────────────────────────────────────────────────────────
# Output shape — matches `cpp_parser.SubroutineSummary` + the proto
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
# Lazy Index singleton — creating a clang Index allocates an LLVM
# context, so reuse across calls. Indexes are NOT thread-safe; the
# parser-sidecar serialises Parse RPCs at the server layer.
# ──────────────────────────────────────────────────────────────────────

_INDEX: Optional[cindex.Index] = None


def _get_index() -> cindex.Index:
    global _INDEX
    if _INDEX is None:
        _INDEX = cindex.Index.create()
    return _INDEX


# ──────────────────────────────────────────────────────────────────────
# Cursor-kind predicates
# ──────────────────────────────────────────────────────────────────────


# Cursor kinds we treat as "a C++ routine the spec extractor cares about".
# In-class declarations (without a body) and out-of-line definitions both
# match; the dedup at the end keeps the entry with the larger source span
# so the definition wins over the declaration.
_ROUTINE_CURSOR_KINDS = frozenset({
    cindex.CursorKind.FUNCTION_DECL,
    cindex.CursorKind.CXX_METHOD,
    cindex.CursorKind.CONSTRUCTOR,
    cindex.CursorKind.DESTRUCTOR,
    cindex.CursorKind.FUNCTION_TEMPLATE,
    cindex.CursorKind.CONVERSION_FUNCTION,
})

_CALL_CURSOR_KINDS = frozenset({
    cindex.CursorKind.CALL_EXPR,
})

_NAMESPACE_CURSOR_KINDS = frozenset({
    cindex.CursorKind.NAMESPACE,
})


# ──────────────────────────────────────────────────────────────────────
# Default compile flags for the "no compile_commands.json" path
# ──────────────────────────────────────────────────────────────────────

_DEFAULT_CXX_FLAGS: Tuple[str, ...] = (
    "-std=c++20",
    "-x", "c++",
    "-fno-exceptions",
    "-fno-rtti",
    "-Wno-everything",
    "-Wno-deprecated",
    # Phase 9.4.b: when no compile_commands.json is available, point
    # libclang at the libstdc++-N-dev system headers shipped in the
    # parser-sidecar container so canonical `<string>` / `<vector>` /
    # `<type_traits>` includes resolve. Without these flags libclang's
    # parse silently degrades — typed function bodies aren't analyzed
    # and CALL_EXPR cursors don't fire. Paths target Debian trixie's
    # g++-14 layout (python:3.12-slim base); bump if the base image
    # ships a different g++ major.
    "-I/usr/include/c++/14",
    "-I/usr/include/x86_64-linux-gnu/c++/14",
    "-I/usr/include/c++/14/backward",
    # gcc's internal headers (stddef.h, stdarg.h, ...) — without these
    # libclang fires "stddef.h not found" warnings on every parse and
    # CALL_EXPR detection degrades inside routines that touch size_t
    # / NULL. The exact path tracks gcc's major version on the base
    # image (currently gcc-14 on python:3.12-slim = Debian trixie).
    "-I/usr/lib/gcc/x86_64-linux-gnu/14/include",
)


# ──────────────────────────────────────────────────────────────────────
# Public entry point
# ──────────────────────────────────────────────────────────────────────


def parse_source(
    filename: str,
    content: str,
    compile_args: Optional[Sequence[str]] = None,
) -> ParseOutcome:
    """Parse a single C++ source file via libclang.

    `compile_args` is the per-TU flag list from `compile_commands.json`
    (per ADR-028). When None we fall back to `_DEFAULT_CXX_FLAGS` — the
    grammar is permissive enough that most header-only code still
    parses. Caller raises on `cindex.LibclangError` so the dispatcher
    in `server.py` can fall through to the v0 tokenizer.
    """
    warnings: List[str] = []
    raw = content.replace("\r\n", "\n").replace("\r", "\n")
    if not raw:
        return ParseOutcome(line_count=0, subroutines=[], warnings=[], filename=filename)
    line_count = raw.count("\n") + (0 if raw.endswith("\n") else 1)

    index = _get_index()
    args = list(compile_args) if compile_args else list(_DEFAULT_CXX_FLAGS)
    tu = index.parse(
        filename,
        args=args,
        unsaved_files=[(filename, raw)],
        options=(
            cindex.TranslationUnit.PARSE_DETAILED_PROCESSING_RECORD
            | cindex.TranslationUnit.PARSE_INCOMPLETE
        ),
    )

    # Collect fatal-or-error diagnostics as warnings on the outcome.
    # Non-fatal warnings from the compile aren't surfaced (too noisy on
    # cross-TU symbol failures); the production parser is best-effort.
    for diag in tu.diagnostics:
        if diag.severity >= cindex.Diagnostic.Error:
            warnings.append(f"libclang: {diag.spelling}")

    includes = _collect_includes(tu, filename)
    includes_tuple = tuple(includes)

    routines = _collect_routines(tu.cursor, filename, includes_tuple)
    return ParseOutcome(
        line_count=line_count,
        subroutines=routines,
        warnings=warnings,
        filename=filename,
    )


# ──────────────────────────────────────────────────────────────────────
# Include walker
# ──────────────────────────────────────────────────────────────────────


def _collect_includes(tu, filename: str) -> List[str]:
    """Walk the TU for INCLUSION_DIRECTIVE cursors and return the include
    names (without angle-brackets / quotes) in source-order, de-duplicated.

    Using cursors instead of `tu.get_includes()` because the latter only
    returns includes that successfully resolved — when libclang can't
    find `<fmt/core.h>` because the corpus didn't supply -I flags, the
    directive itself is still present as a cursor but won't appear in
    `get_includes()`. The harness's `common_block_refs` should reflect
    what the SOURCE asked for, not what libclang found.
    """
    seen: List[str] = []
    seen_set: set[str] = set()

    def visit(cursor):
        loc = cursor.location.file
        if loc is not None and loc.name != filename:
            return
        if cursor.kind == cindex.CursorKind.INCLUSION_DIRECTIVE:
            name = cursor.spelling or ""
            if name:
                # `name` may include a directory prefix (`fmt/core.h`);
                # keep it as-is — the planner treats the qualified name
                # as the include identity.
                if name not in seen_set:
                    seen.append(name)
                    seen_set.add(name)
            return
        for child in cursor.get_children():
            visit(child)

    for child in tu.cursor.get_children():
        visit(child)
    return seen


# ──────────────────────────────────────────────────────────────────────
# Routine walker
# ──────────────────────────────────────────────────────────────────────


def _collect_routines(
    root_cursor,
    filename: str,
    common_block_refs: Tuple[str, ...],
) -> List[SubroutineSummary]:
    """Recursively walk the cursor tree, collecting routine summaries.

    Namespaces are traversed transparently — routines inside
    `namespace fmt::detail` get the qualified name from the cursor's
    fully-qualified spelling.

    Class declarations are walked too; nested class methods surface
    with the same qualified-name handling.
    """
    raw_entries: List[SubroutineSummary] = []

    def visit(cursor):
        # Skip cursors that live in files other than the one we're
        # parsing — `#include`d headers parade through here otherwise.
        loc = cursor.location.file
        if loc is not None and loc.name != filename:
            return
        if cursor.kind in _ROUTINE_CURSOR_KINDS:
            summary = _build_summary(cursor, common_block_refs)
            if summary is not None:
                raw_entries.append(summary)
            # Recurse into the routine to pick up nested routines and
            # call-expressions used by _collect_calls (already counted
            # within _build_summary).
            return
        if cursor.kind in _NAMESPACE_CURSOR_KINDS or cursor.kind == cindex.CursorKind.TRANSLATION_UNIT:
            for child in cursor.get_children():
                visit(child)
            return
        # Class / struct declarations: walk children to surface methods.
        if cursor.kind in (
            cindex.CursorKind.CLASS_DECL,
            cindex.CursorKind.STRUCT_DECL,
            cindex.CursorKind.CLASS_TEMPLATE,
            cindex.CursorKind.CLASS_TEMPLATE_PARTIAL_SPECIALIZATION,
        ):
            for child in cursor.get_children():
                visit(child)
            return
        # Other cursor kinds we don't care about for routine extraction.
        for child in cursor.get_children():
            visit(child)

    visit(root_cursor)
    return _dedup_by_name(raw_entries)


def _build_summary(
    cursor,
    common_block_refs: Tuple[str, ...],
) -> Optional[SubroutineSummary]:
    """Build a `SubroutineSummary` from a routine cursor.

    The qualified name comes from `_qualified_name(cursor)` which walks
    up parent cursors collecting the namespace / class chain. The
    signature uses `cursor.displayname` (libclang's pretty-print) for
    brevity — it includes the parameter list but elides the return
    type, so we prepend the `cursor.type.spelling.split('(')[0]` to
    recover it.
    """
    name = _qualified_name(cursor)
    if not name:
        return None

    return_type = ""
    try:
        # FUNCTION_TEMPLATE cursors lack a well-formed type; guard.
        rtype = cursor.result_type.spelling if cursor.result_type is not None else ""
        return_type = rtype if rtype else ""
    except Exception:  # noqa: BLE001 — libclang exposes broad exceptions
        return_type = ""

    display = cursor.displayname or name
    if return_type and not display.startswith(return_type):
        signature = f"{return_type} {display}"
    else:
        signature = display

    # Template prefix: walk the FUNCTION_TEMPLATE wrapper to capture the
    # `<typename T, ...>` segment when present.
    if cursor.kind == cindex.CursorKind.FUNCTION_TEMPLATE:
        params = []
        for c in cursor.get_children():
            if c.kind in (
                cindex.CursorKind.TEMPLATE_TYPE_PARAMETER,
                cindex.CursorKind.TEMPLATE_NON_TYPE_PARAMETER,
                cindex.CursorKind.TEMPLATE_TEMPLATE_PARAMETER,
            ):
                params.append(c.spelling or "?")
        if params:
            signature = f"template<{', '.join(params)}> {signature}"

    # Cap at 320 chars (mirror cpp_parser v0; signature column is
    # varchar(2048) but we keep it tight for UI legibility).
    signature = " ".join(signature.split())[:320]

    # The DB's `name` column is varchar(255). libclang's qualified names
    # for fmt's heavily-template-meta-programmed routines can exceed
    # that — `fmt::detail::format_args<...>::stored<T,U,...>` chains
    # routinely hit 400+ chars. Cap to 255 with an ellipsis suffix so
    # the truncation is visible and IngestPipeline's varchar insert
    # doesn't trip Postgres error 22001 (string_data_right_truncation).
    if len(name) > 255:
        name = name[:252] + "..."

    # Source range — libclang exposes line + column directly.
    line_start = cursor.extent.start.line
    line_end = cursor.extent.end.line
    if line_end < line_start:
        line_end = line_start

    called = _collect_calls(cursor)
    # Strip the routine's own bare-name from its call list (matches v0).
    bare_self = name.rsplit("::", 1)[-1]
    called = tuple(c for c in called if c != bare_self)

    return SubroutineSummary(
        name=name,
        signature=signature,
        line_start=line_start,
        line_end=line_end,
        common_block_refs=common_block_refs,
        called_subroutines=called,
    )


def _qualified_name(cursor) -> str:
    """Walk up the cursor tree and collect namespace / class qualifiers."""
    segments: List[str] = []
    if cursor.spelling:
        segments.append(cursor.spelling)
    parent = cursor.semantic_parent
    while parent is not None and parent.kind != cindex.CursorKind.TRANSLATION_UNIT:
        if parent.kind in (
            cindex.CursorKind.NAMESPACE,
            cindex.CursorKind.CLASS_DECL,
            cindex.CursorKind.STRUCT_DECL,
            cindex.CursorKind.CLASS_TEMPLATE,
            cindex.CursorKind.CLASS_TEMPLATE_PARTIAL_SPECIALIZATION,
        ):
            if parent.spelling:
                segments.append(parent.spelling)
        parent = parent.semantic_parent
    return "::".join(reversed(segments))


# ──────────────────────────────────────────────────────────────────────
# Call walker
# ──────────────────────────────────────────────────────────────────────


def _collect_calls(routine_cursor) -> Tuple[str, ...]:
    """Walk the routine body recursively, collecting every CALL_EXPR's
    callee identifier in discovery order, de-duplicated."""
    seen: List[str] = []
    seen_set: set[str] = set()

    def visit(cursor):
        if cursor.kind in _CALL_CURSOR_KINDS:
            callee = cursor.spelling
            if callee:
                # The callee spelling may include template args for
                # function-template calls (`Func<int>`); strip them.
                bare = callee.split("<")[0]
                if bare and bare not in seen_set:
                    seen.append(bare)
                    seen_set.add(bare)
        for child in cursor.get_children():
            visit(child)

    for child in routine_cursor.get_children():
        visit(child)
    return tuple(seen)


# ──────────────────────────────────────────────────────────────────────
# Dedup
# ──────────────────────────────────────────────────────────────────────


def _dedup_by_name(routines: List[SubroutineSummary]) -> List[SubroutineSummary]:
    """Mirror v0's dedup: when the same qualified name appears multiple
    times (e.g. in-class declaration + out-of-line definition), keep
    the one with the larger source span. Preserve discovery order."""
    best: dict[str, SubroutineSummary] = {}
    for r in routines:
        cur = best.get(r.name)
        if cur is None or (r.line_end - r.line_start) > (cur.line_end - cur.line_start):
            best[r.name] = r
    seen: set[str] = set()
    result: List[SubroutineSummary] = []
    for r in routines:
        if r.name in seen:
            continue
        seen.add(r.name)
        result.append(best[r.name])
    return result
