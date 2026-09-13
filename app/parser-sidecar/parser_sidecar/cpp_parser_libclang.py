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
  - `CALL_EXPR`               call sites, resolved through `cursor.referenced`
                              to the callee's *qualified* name so the
                              dependency graph can join them to definitions
  - `DECL_REF_EXPR` /
    `MEMBER_REF_EXPR`         references to shared mutable state
                              (namespace-scope non-const variables, static
                              data members) — the C++ analogue of a COMMON
                              block, recorded as `common_block_refs`

`#include` directives are deliberately NOT recorded as `common_block_refs`
any more: every routine in a file shares the same headers, so the graph
builder paired every routine with every other one (785k "shared-storage"
edges on fmt, 309k on oatpp) and the call graph — which resolved only bare
`CALL_EXPR` spellings — was nearly empty. Both are fixed here.

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
- Per-file parsing of a `.cpp` whose class lives in a header it cannot
  see. clang drops `void Store::save(int)` entirely when `Store` was never
  declared, so a per-file parse of a real corpus yields almost nothing
  from its `.cpp` files. `parse_corpus` fixes that: the whole corpus is
  written to a temporary root, every directory holding a header becomes
  an include path, and each file is parsed on disk so definitions,
  qualified call targets and shared-state refs resolve across files.
"""
from __future__ import annotations

import logging
import os
import shutil
import tempfile
import threading
from concurrent import futures
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Sequence, Tuple

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
    # True for the cursor that carries the body. Dedup prefers it over an
    # in-class declaration of the same qualified name, whatever the spans.
    is_definition: bool = False


@dataclass
class ParseOutcome:
    line_count: int
    subroutines: List[SubroutineSummary]
    warnings: List[str]
    filename: str


# ──────────────────────────────────────────────────────────────────────
# One lazily created Index per thread — creating a clang Index allocates
# an LLVM context, so reuse it, but an Index is NOT thread-safe and
# `parse_corpus` parses files on a small thread pool.
# ──────────────────────────────────────────────────────────────────────

_INDEX_LOCAL = threading.local()


def _get_index() -> cindex.Index:
    index = getattr(_INDEX_LOCAL, "index", None)
    if index is None:
        index = cindex.Index.create()
        _INDEX_LOCAL.index = index
    return index


# CXTranslationUnit_KeepGoing: do not stop at a fatal diagnostic. Without
# it a single missing `#include` ends the parse at that line and every
# routine below it vanishes. Not exposed as a constant by the Python
# bindings, so spelled as the C API value.
_PARSE_KEEP_GOING = 0x200


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
    # Exceptions and RTTI stay ON: with them off clang rejects every
    # `throw` / `try` / `typeid` / `dynamic_cast` and drops the enclosing
    # statement, and the calls inside it, from the AST.
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
    raw = _normalise(content)
    if not raw:
        return ParseOutcome(line_count=0, subroutines=[], warnings=[], filename=filename)
    args = list(compile_args) if compile_args else list(_DEFAULT_CXX_FLAGS)
    return _parse_tu(
        path=filename,
        display_name=filename,
        args=args,
        unsaved=[(filename, raw)],
        line_count=_line_count(raw),
    )


def _normalise(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


def _line_count(raw: str) -> int:
    return raw.count("\n") + (0 if raw.endswith("\n") else 1)


def _parse_tu(
    path: str,
    display_name: str,
    args: List[str],
    unsaved: Optional[List[Tuple[str, str]]],
    line_count: int,
) -> ParseOutcome:
    """Parse one translation unit — from memory (`unsaved`) or from disk —
    and collect the routines that live in `path` itself."""
    index = _get_index()
    tu = index.parse(path, args=args, unsaved_files=unsaved, options=_PARSE_OPTIONS)
    routines = _collect_routines(tu.cursor, path)
    return ParseOutcome(
        line_count=line_count,
        subroutines=routines,
        warnings=_error_diagnostics(tu),
        filename=display_name,
    )


# No PARSE_DETAILED_PROCESSING_RECORD: it records every macro expansion
# and instantiation, which on template-heavy code costs more than the
# parse itself, and nothing here reads preprocessor cursors any more.
_PARSE_OPTIONS = cindex.TranslationUnit.PARSE_INCOMPLETE | _PARSE_KEEP_GOING


def _error_diagnostics(tu) -> List[str]:
    """Error-or-worse diagnostics as outcome warnings. Compile warnings
    are not surfaced (too noisy on cross-TU symbol failures); the
    production parser is best-effort."""
    out: List[str] = []
    for diag in tu.diagnostics:
        if diag.severity >= cindex.Diagnostic.Error:
            out.append(f"libclang: {diag.spelling}")
    return out


# ──────────────────────────────────────────────────────────────────────
# Corpus mode — the files on disk, so includes resolve
# ──────────────────────────────────────────────────────────────────────

_HEADER_SUFFIXES = (".h", ".hpp", ".hxx", ".h++", ".ipp", ".inl", ".tcc", ".inc")
_MAX_INCLUDE_DIRS = 500


@dataclass
class _TuResult:
    """What one translation unit yielded: routines for every corpus file
    it pulled in (keyed by display name), the main file's diagnostics, and
    the display names of every corpus file it covered."""
    display: str
    routines: Dict[str, List[SubroutineSummary]]
    warnings: List[str]
    covered: List[str]


ProgressCallback = Callable[[int, int, str], None]


def parse_corpus(
    files: Sequence[Tuple[str, str]],
    compile_args: Optional[Sequence[str]] = None,
    max_workers: Optional[int] = None,
    on_progress: Optional[ProgressCallback] = None,
) -> List[ParseOutcome]:
    """Parse every (relative path, content) pair as one corpus.

    The files are written under a temporary root exactly as named and
    every directory that holds a header (and each of its ancestors)
    becomes an `-I` path, so `#include "a/b.hpp"` resolves, the class
    behind `void Store::save(int)` is known, and the definition, its
    qualified callees and its shared-state refs all survive. A missing
    third-party header is still reported but no longer ends the parse.

    Each translation unit (every non-header file) is parsed once, and the
    routines of every corpus header it pulls in are harvested from that
    same parse — the headers are already in the AST, so parsing them
    again on their own would only repeat the work. Headers no unit reaches
    (header-only corpora, orphans) are parsed standalone afterwards. Units
    run in a process pool: the cursor walk is Python and holds the GIL,
    so threads alone gain little.

    A declaration is dropped when some other file in the corpus holds the
    definition of the same qualified name: one routine, one row, the one
    with the body. Declarations nothing defines (pure virtuals, symbols
    from libraries outside the corpus) stay, exactly as in per-file mode.

    Outcomes come back in input order. Paths that try to escape the root
    are parsed in memory instead, as `parse_source` would.

    `on_progress(parsed_files, total_files, display)` is called from the
    calling thread after every translation unit, with the number of files
    whose routines are known so far (a header harvested from a unit counts
    as soon as that unit is done).
    """
    outcomes: List[Optional[ParseOutcome]] = [None] * len(files)
    on_disk: Dict[str, Tuple[int, str, int]] = {}  # normalised path -> (index, display, line count)
    root = tempfile.mkdtemp(prefix="astra-corpus-")
    try:
        for i, (display, content) in enumerate(files):
            raw = _normalise(content or "")
            if not raw:
                outcomes[i] = ParseOutcome(line_count=0, subroutines=[], warnings=[], filename=display)
                continue
            rel = _safe_relative(display)
            if rel is None:
                outcomes[i] = parse_source(display, raw, compile_args)
                continue
            abs_path = os.path.join(root, rel)
            os.makedirs(os.path.dirname(abs_path), exist_ok=True)
            with open(abs_path, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(raw)
            on_disk[os.path.normpath(abs_path)] = (i, display, _line_count(raw))

        args = list(compile_args) if compile_args else list(_DEFAULT_CXX_FLAGS)
        args += [f"-I{d}" for d in _include_dirs(root, list(on_disk))]
        wanted = {path: meta[1] for path, meta in on_disk.items()}

        harvested: Dict[str, List[SubroutineSummary]] = {}
        file_warnings: Dict[str, List[str]] = {}
        covered: set = set()
        total_files = len(on_disk) + sum(1 for o in outcomes if o is not None)
        known = sum(1 for o in outcomes if o is not None)

        def absorb(result: _TuResult) -> None:
            nonlocal known
            covered.update(result.covered)
            file_warnings[result.display] = result.warnings
            for display, routines in result.routines.items():
                harvested.setdefault(display, routines)
            known = len(covered | set(harvested)) + sum(1 for o in outcomes if o is not None)
            if on_progress is not None:
                on_progress(min(known, total_files), total_files, result.display)

        units = [p for p in on_disk if not p.lower().endswith(_HEADER_SUFFIXES)]
        for result in _run_units(units, args, wanted, max_workers):
            absorb(result)
        orphans = [
            p for p in on_disk
            if p.lower().endswith(_HEADER_SUFFIXES) and wanted[p] not in covered
        ]
        for result in _run_units(orphans, args, wanted, max_workers):
            absorb(result)

        for path, (i, display, line_count) in on_disk.items():
            outcomes[i] = ParseOutcome(
                line_count=line_count,
                subroutines=harvested.get(display, []),
                warnings=file_warnings.get(display, []),
                filename=display,
            )
    finally:
        shutil.rmtree(root, ignore_errors=True)

    filled = [o for o in outcomes if o is not None]
    _drop_declarations_defined_elsewhere(filled)
    return filled


def _run_units(
    paths: List[str],
    args: List[str],
    wanted: Dict[str, str],
    max_workers: Optional[int],
):
    """Parse the given translation units, in a spawn-based process pool when
    there is enough work to share. Spawn, not fork: the caller is a gRPC
    server whose C core does not survive being forked mid-flight. Work is
    handed out in small batches and yielded as each batch completes, so a
    caller reporting progress hears from us every few seconds rather than
    once per worker."""
    if not paths:
        return
    workers = max_workers or int(os.environ.get("ASTRA_CORPUS_WORKERS", "0") or 0) or max(1, min(4, os.cpu_count() or 1))
    workers = max(1, min(workers, len(paths)))
    if workers == 1:
        yield from _parse_units(paths, args, wanted)
        return
    import multiprocessing

    batch = _UNIT_BATCH
    batches = [paths[k:k + batch] for k in range(0, len(paths), batch)]
    ctx = multiprocessing.get_context("spawn")
    with futures.ProcessPoolExecutor(max_workers=workers, mp_context=ctx) as pool:
        pending = [pool.submit(_parse_units_list, b, args, wanted) for b in batches]
        for done in futures.as_completed(pending):
            yield from done.result()


# Translation units per pool task: small enough that progress ticks every
# few seconds on a real corpus, large enough that pickling `wanted` per
# task (one entry per corpus file) stays negligible.
_UNIT_BATCH = 4


def _parse_units_list(paths: List[str], args: List[str], wanted: Dict[str, str]) -> List[_TuResult]:
    return list(_parse_units(paths, args, wanted))


def _parse_units(paths: List[str], args: List[str], wanted: Dict[str, str]):
    index = _get_index()
    for path in paths:
        display = wanted[path]
        try:
            tu = index.parse(path, args=args, options=_PARSE_OPTIONS)
            routines = _collect_routines_multi(tu.cursor, wanted)
            covered = [display]
            try:
                for inc in tu.get_includes():
                    hit = wanted.get(os.path.normpath(inc.include.name))
                    if hit is not None:
                        covered.append(hit)
            except Exception:  # noqa: BLE001 — inclusion listing is best-effort
                pass
            yield _TuResult(display=display, routines=routines, warnings=_error_diagnostics(tu), covered=covered)
            del tu
        except Exception as e:  # noqa: BLE001 — one file must not sink the corpus
            log.info("libclang failed on %s in corpus mode (%s)", display, e)
            yield _TuResult(display=display, routines={}, warnings=[f"libclang: {e}"], covered=[display])


def _safe_relative(display: str) -> Optional[str]:
    """Turn a corpus-relative path into a path safe to create under the
    temporary root, or None when it cannot be trusted."""
    p = (display or "").replace("\\", "/").strip()
    while p.startswith("./"):
        p = p[2:]
    p = p.lstrip("/")
    if not p or ":" in p.split("/")[0]:
        return None
    parts = [seg for seg in p.split("/") if seg != ""]
    if not parts or any(seg in (".", "..") for seg in parts):
        return None
    return os.path.join(*parts)


def _include_dirs(root: str, paths: Sequence[str]) -> List[str]:
    """Root plus every directory holding a header and each ancestor up to
    the root, shallowest first — so `#include "oatpp/core/Types.hpp"`
    resolves from `-I<root>/src` and `#include "Types.hpp"` from the
    header's own directory."""
    dirs = {root}
    for p in paths:
        if not p.lower().endswith(_HEADER_SUFFIXES):
            continue
        d = os.path.dirname(p)
        while d.startswith(root) and d != root:
            dirs.add(d)
            d = os.path.dirname(d)
    ordered = sorted(dirs, key=lambda d: (d.count(os.sep), d))
    return ordered[:_MAX_INCLUDE_DIRS]


def _drop_declarations_defined_elsewhere(outcomes: List[ParseOutcome]) -> None:
    defined = {s.name for o in outcomes for s in o.subroutines if s.is_definition}
    for o in outcomes:
        o.subroutines = [s for s in o.subroutines if s.is_definition or s.name not in defined]


# ──────────────────────────────────────────────────────────────────────
# Routine walker
# ──────────────────────────────────────────────────────────────────────


def _collect_routines(
    root_cursor,
    filename: str,
) -> List[SubroutineSummary]:
    """Recursively walk the cursor tree, collecting routine summaries.

    Namespaces are traversed transparently — routines inside
    `namespace fmt::detail` get the qualified name from the cursor's
    fully-qualified spelling.

    Class declarations are walked too; nested class methods surface
    with the same qualified-name handling.
    """
    wanted = {filename: filename, os.path.normpath(filename): filename}
    return _collect_routines_multi(root_cursor, wanted).get(filename, [])


def _collect_routines_multi(
    root_cursor,
    wanted: Dict[str, str],
) -> Dict[str, List[SubroutineSummary]]:
    """Walk the cursor tree once and bucket routine summaries by the
    corpus file they live in. `wanted` maps a normalised on-disk path (or
    the exact spelling libclang was given) to the file's display name;
    cursors from any other file — system headers, third-party code — are
    skipped without descending, which is also what keeps the walk cheap.
    Each bucket is deduplicated by qualified name (definition first)."""
    buckets: Dict[str, List[SubroutineSummary]] = {}
    memo: Dict[str, Optional[str]] = {}

    def display_for(name: str) -> Optional[str]:
        hit = memo.get(name, memo)
        if hit is memo:
            hit = wanted.get(name)
            if hit is None:
                hit = wanted.get(os.path.normpath(name))
            memo[name] = hit
        return hit

    def visit(cursor):
        loc = cursor.location.file
        display: Optional[str] = None
        if loc is not None:
            display = display_for(loc.name)
            if display is None:
                return
        if cursor.kind in _ROUTINE_CURSOR_KINDS:
            if display is not None:
                summary = _build_summary(cursor)
                if summary is not None:
                    buckets.setdefault(display, []).append(summary)
            # Calls and shared-state refs were collected inside
            # _build_summary; nothing below a routine is a routine we want.
            return
        # Namespaces, classes, the TU root and anything else: descend.
        for child in cursor.get_children():
            visit(child)

    visit(root_cursor)
    return {display: _dedup_by_name(entries) for display, entries in buckets.items()}


def _build_summary(cursor) -> Optional[SubroutineSummary]:
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
    # Strip self-recursion (qualified or bare spelling) from the call list.
    bare_self = name.rsplit("::", 1)[-1]
    called = tuple(c for c in called if c != name and c != bare_self)
    shared = _collect_shared_state(cursor)

    try:
        is_definition = bool(cursor.is_definition())
    except Exception:  # noqa: BLE001
        is_definition = False

    return SubroutineSummary(
        name=name,
        signature=signature,
        line_start=line_start,
        line_end=line_end,
        common_block_refs=shared,
        called_subroutines=called,
        is_definition=is_definition,
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


_SYSTEM_PATH_MARKERS = ("/usr/include", "/usr/lib/gcc", "/usr/local/include", "include/c++")


def _is_system_location(location) -> bool:
    """True when a cursor lives in a compiler / libstdc++ header. Calls
    into `std::` are not part of the corpus's own call graph; recording
    them only inflates the "external callees" list with `std::basic_string::c_str`."""
    f = location.file if location is not None else None
    if f is None:
        return False
    name = (f.name or "").replace("\\", "/")
    return any(marker in name for marker in _SYSTEM_PATH_MARKERS)


def _collect_calls(routine_cursor) -> Tuple[str, ...]:
    """Walk the routine body recursively and collect the callee of every
    CALL_EXPR, in discovery order, de-duplicated.

    The callee is the *qualified* name of the declaration libclang
    resolved (`cursor.referenced`) — `fmt::detail::format_context::parse_arg`,
    not `parse_arg` — which is exactly how this parser names definitions,
    so `DependencyGraphBuilder` can join call → definition by name even
    when the bare method name (`write`, `on_x`) is ambiguous corpus-wide.
    Calls into system headers are skipped; unresolved calls (no
    `referenced`, e.g. a symbol from a header libclang could not find)
    fall back to the bare spelling so a unique bare name can still match.
    """
    seen: List[str] = []
    seen_set: set[str] = set()

    def add(name: str) -> None:
        if name and name not in seen_set:
            seen.append(name)
            seen_set.add(name)

    def visit(cursor):
        if cursor.kind in _CALL_CURSOR_KINDS:
            ref = None
            try:
                ref = cursor.referenced
            except Exception:  # noqa: BLE001 — libclang can throw on odd cursors
                ref = None
            if ref is not None and ref.kind in _ROUTINE_CURSOR_KINDS:
                if not _is_system_location(ref.location):
                    add(_qualified_name(ref))
            else:
                callee = cursor.spelling
                if callee:
                    # Unresolved: the spelling may carry template args
                    # (`Func<int>`); strip them and keep the bare name.
                    add(callee.split("<")[0])
        for child in cursor.get_children():
            visit(child)

    for child in routine_cursor.get_children():
        visit(child)
    return tuple(seen)


_CLASS_CURSOR_KINDS = frozenset({
    cindex.CursorKind.CLASS_DECL,
    cindex.CursorKind.STRUCT_DECL,
    cindex.CursorKind.CLASS_TEMPLATE,
    cindex.CursorKind.CLASS_TEMPLATE_PARTIAL_SPECIALIZATION,
    cindex.CursorKind.UNION_DECL,
})

_SHARED_SCOPE_KINDS = frozenset({
    cindex.CursorKind.NAMESPACE,
    cindex.CursorKind.TRANSLATION_UNIT,
})


def _collect_shared_state(routine_cursor) -> Tuple[str, ...]:
    """The C++ analogue of COMMON-block references: qualified names of the
    genuinely shared mutable state this routine touches — namespace-scope
    (or global) non-const variables and static data members. Locals,
    parameters, non-static fields (per-instance) and const/constexpr
    values are not shared state and are skipped, as is anything that
    lives in a system header.
    """
    seen: List[str] = []
    seen_set: set[str] = set()

    def visit(cursor):
        if cursor.kind in (cindex.CursorKind.DECL_REF_EXPR, cindex.CursorKind.MEMBER_REF_EXPR):
            ref = None
            try:
                ref = cursor.referenced
            except Exception:  # noqa: BLE001
                ref = None
            if ref is not None and ref.kind == cindex.CursorKind.VAR_DECL and not _is_system_location(ref.location):
                parent = ref.semantic_parent
                parent_kind = parent.kind if parent is not None else None
                is_shared_scope = parent_kind in _SHARED_SCOPE_KINDS or parent_kind in _CLASS_CURSOR_KINDS
                if is_shared_scope:
                    try:
                        is_const = ref.type.is_const_qualified()
                    except Exception:  # noqa: BLE001
                        is_const = False
                    if not is_const:
                        qn = _qualified_name(ref)
                        if qn and qn not in seen_set:
                            seen.append(qn)
                            seen_set.add(qn)
        for child in cursor.get_children():
            visit(child)

    for child in routine_cursor.get_children():
        visit(child)
    return tuple(seen)


# ──────────────────────────────────────────────────────────────────────
# Dedup
# ──────────────────────────────────────────────────────────────────────


def _dedup_by_name(routines: List[SubroutineSummary]) -> List[SubroutineSummary]:
    """When the same qualified name appears more than once (in-class
    declaration + out-of-line definition, forward declaration + body), keep
    the DEFINITION — it is the cursor whose body yielded the calls and the
    shared-state refs. Only among equals (two declarations, two overload
    bodies) does the larger source span win. Preserve discovery order.

    Span alone was the old rule; a one-line `int load() const;` and a
    one-line `int Store::load() const { return helper(counter); }` tie on
    span, so the declaration — first in file order — silently won and the
    routine lost every call edge and every ref."""
    best: dict[str, SubroutineSummary] = {}

    def rank(r: SubroutineSummary) -> Tuple[int, int]:
        return (1 if r.is_definition else 0, r.line_end - r.line_start)

    for r in routines:
        cur = best.get(r.name)
        if cur is None or rank(r) > rank(cur):
            best[r.name] = r
    seen: set[str] = set()
    result: List[SubroutineSummary] = []
    for r in routines:
        if r.name in seen:
            continue
        seen.add(r.name)
        result.append(best[r.name])
    return result
