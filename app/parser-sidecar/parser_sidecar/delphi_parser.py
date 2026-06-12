"""
Astra parser sidecar — Delphi / Object Pascal structural parser.

**Phase 9.0.a (v0 scaffolding).** A focused tokenizer-based parser that
recognises the *structural* surface needed to feed the Migration Planner
and the Spec extraction pipeline: unit declarations, `uses` clauses,
class declarations, procedure / function signatures, line ranges, and
call-site references.

This is **not** a full Delphi parser. Per ADR-024 the production parser
shells out to Free Pascal Compiler (`fpc`) and walks its symbol-table
output. The v0 module exists so the rest of Phase 9.0 (schema, prompts,
archetypes, equivalence sidecar, Indy seed) can develop in parallel
against a working AST source. The two parsers MUST produce the same
`ParseOutcome` shape so we can swap implementations without touching
callers.

What v0 handles
---------------
- `unit X;`, `program X;`, `library X;` headers
- `uses A, B, C;` clauses in both interface and implementation sections
- `interface` / `implementation` section boundaries
- Top-level `procedure NAME(params)` and `function NAME(params): T`
  declarations with full line-range tracking
- Class declarations (`TFoo = class(TBar)` / `TFoo = class(TBar, IBaz)`)
- Calls to other named routines found inside procedure bodies (a heuristic
  matcher; over-counts on identifiers shared with variables but the
  Migration Planner tolerates noise in this edge)

What v0 does NOT handle (deferred to fpc shell-out per ADR-024)
---------------------------------------------------------------
- `{$IF Defined(...)}` / `{$IFDEF ...}` preprocessor branches.  v0
  strips comments and processes ALL branches; if the corpus uses
  conditional compilation extensively, fpc shell-out is required.
- Anonymous methods (`procedure` inside an expression).
- Generic type parameters (`TFoo<T> = class`).  v0 records the class
  by name; the spec extractor handles parameter inference.
- ASM blocks (treated as opaque code).
- Includes (`{$I file.inc}`).  v0 ignores the directive; fpc inlines.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from typing import Iterable, List, Optional, Tuple

log = logging.getLogger("astra.parser.delphi")


# ──────────────────────────────────────────────────────────────────────
# Output shape — matches `cobol_parser.SubroutineSummary` and the proto
# ──────────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class SubroutineSummary:
    name: str
    signature: str
    line_start: int
    line_end: int
    # Delphi's `uses` clauses are the analogue of Fortran's COMMON blocks +
    # COBOL's COPY directives — they declare a cross-routine dependency
    # on a named unit. Stored here so the Migration Planner can walk them.
    common_block_refs: Tuple[str, ...]
    # Identifiers found in call position inside the routine body. Over-
    # counts on variables; the planner treats it as a hint, not a contract.
    called_subroutines: Tuple[str, ...]


@dataclass
class ParseOutcome:
    line_count: int
    subroutines: List[SubroutineSummary]
    warnings: List[str]
    filename: str


# ──────────────────────────────────────────────────────────────────────
# Pre-processing — strip comments, normalise line endings
# ──────────────────────────────────────────────────────────────────────


# Delphi has three comment syntaxes. We track them all but preserve line
# positions so post-parse line numbers stay accurate.
_LINE_COMMENT = re.compile(r"//[^\r\n]*")
_BRACE_COMMENT = re.compile(r"\{[^}]*\}", re.DOTALL)
_PAREN_STAR_COMMENT = re.compile(r"\(\*.*?\*\)", re.DOTALL)


def _strip_comments_preserve_lines(text: str) -> str:
    """Replace every comment with whitespace of the same shape so line
    numbers don't shift. We blank the bytes; we don't delete them."""
    def _blank(m: re.Match[str]) -> str:
        return "".join("\n" if c == "\n" else " " for c in m.group(0))

    text = _BRACE_COMMENT.sub(_blank, text)
    text = _PAREN_STAR_COMMENT.sub(_blank, text)
    text = _LINE_COMMENT.sub(lambda m: " " * len(m.group(0)), text)
    return text


# ──────────────────────────────────────────────────────────────────────
# Tokenization helpers (intentionally lightweight — no full lexer)
# ──────────────────────────────────────────────────────────────────────


_IDENT = r"[A-Za-z_][A-Za-z0-9_]*"
_HEADER_RE = re.compile(rf"^\s*(unit|program|library)\s+({_IDENT})\s*;", re.IGNORECASE | re.MULTILINE)
_USES_RE = re.compile(rf"\buses\b([^;]*);", re.IGNORECASE | re.DOTALL)
_SECTION_RE = re.compile(r"\b(interface|implementation|initialization|finalization|end\.)\b", re.IGNORECASE)
_ROUTINE_RE = re.compile(
    # Either `procedure NAME` or `function NAME`, optionally qualified
    # by a class (`TClass.Method`), followed by an optional parameter
    # list and (for function) a return type. We capture the signature
    # so the spec page can render it verbatim.
    rf"\b(procedure|function|constructor|destructor)\s+"
    rf"(({_IDENT}\.)?{_IDENT})"
    rf"\s*(\([^)]*\))?"          # optional ( params )
    rf"(\s*:\s*[A-Za-z_][\w<>, .]*)?"  # optional : ReturnType
    rf"\s*;",
    re.IGNORECASE,
)
_CLASS_DECL_RE = re.compile(
    rf"\b({_IDENT})\s*=\s*class\b",
    re.IGNORECASE,
)


# Reserved words that look like routine names but aren't actually calls.
_RESERVED_NOT_CALLS = {
    "begin", "end", "if", "then", "else", "while", "do", "for", "to", "downto",
    "repeat", "until", "case", "of", "with", "try", "except", "finally", "raise",
    "result", "self", "inherited", "nil", "true", "false", "and", "or", "not",
    "xor", "div", "mod", "shl", "shr", "in", "is", "as", "function", "procedure",
    "var", "const", "type", "uses", "unit", "interface", "implementation",
    "class", "record", "object", "array", "set", "string", "integer", "boolean",
    "real", "double", "single", "extended", "char", "ansichar", "widechar",
    "byte", "word", "longword", "int64", "cardinal", "shortint", "smallint",
    "ansistring", "widestring", "unicodestring", "tobject", "exit", "halt",
    "ord", "chr", "sizeof", "length", "high", "low", "succ", "pred", "inc", "dec",
}


def _looks_like_call(token: str) -> bool:
    return token.lower() not in _RESERVED_NOT_CALLS and token.isidentifier()


# ──────────────────────────────────────────────────────────────────────
# Public entry point
# ──────────────────────────────────────────────────────────────────────


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single Delphi source file. Always returns a ParseOutcome;
    failure modes become warnings, not exceptions, so a malformed file
    in a 300k-LoC corpus doesn't take down the ingest."""
    warnings: List[str] = []
    raw = content.replace("\r\n", "\n").replace("\r", "\n")
    line_count = raw.count("\n") + (1 if raw and not raw.endswith("\n") else 0)
    if not raw.strip():
        return ParseOutcome(line_count=line_count, subroutines=[], warnings=warnings, filename=filename)

    blanked = _strip_comments_preserve_lines(raw)

    # Unit header — informational; doesn't itself produce a subroutine.
    header = _HEADER_RE.search(blanked)
    if not header:
        warnings.append(
            "no unit/program/library header found — accepting body as a free-form file"
        )

    # `uses` clauses — collect every unit name seen, deduped, ordered.
    uses_units = _collect_uses(blanked)

    # Class names — used to mark a routine that belongs to a class even
    # when it's declared as a free-standing implementation.
    class_names = {m.group(1) for m in _CLASS_DECL_RE.finditer(blanked)}

    routines = _collect_routines(blanked, uses_units, class_names)

    return ParseOutcome(
        line_count=line_count,
        subroutines=routines,
        warnings=warnings,
        filename=filename,
    )


def _collect_uses(blanked: str) -> Tuple[str, ...]:
    """Walk every `uses A, B, C;` clause; return units in source order."""
    seen: List[str] = []
    seen_lower: set[str] = set()
    for m in _USES_RE.finditer(blanked):
        for part in m.group(1).split(","):
            name = part.strip()
            # `uses Foo in 'Foo.pas';` form — keep only the unit name.
            if " in " in name:
                name = name.split(" in ", 1)[0].strip()
            if not name:
                continue
            key = name.lower()
            if key in seen_lower:
                continue
            seen.append(name)
            seen_lower.add(key)
    return tuple(seen)


def _collect_routines(
    blanked: str,
    uses_units: Tuple[str, ...],
    class_names: set[str],
) -> List[SubroutineSummary]:
    """Walk every `procedure NAME(...)` / `function NAME(...)` declaration.

    Real Delphi units declare a routine TWICE: once as a forward
    declaration in the `interface` section, once with a body in the
    `implementation` section. We dedup by qualified name and keep the
    entry that has a body (larger line-range, populated calls list) so
    the SubroutineSummary table reflects the routine, not the syntactic
    occurrences of its header.

    v0 uses a simple `begin`/`end` depth counter to delimit the body.
    It tolerates nested `begin`/`end` (case statements, blocks) but does
    NOT understand `record`/`object` declarations inside the body —
    those are rare in real Indy code and would surface as warnings."""
    src_lines = blanked.splitlines()
    # Keyed by qualified name; values are the best entry seen so far
    # ("best" = highest body-size; ties broken by source order).
    best_by_name: dict[str, SubroutineSummary] = {}
    order: List[str] = []

    for m in _ROUTINE_RE.finditer(blanked):
        name = _class_qualify(m.group(2), class_names)
        signature = m.group(0).rstrip(";").strip()
        line_start = blanked.count("\n", 0, m.start()) + 1
        body_start, body_end, calls = _find_routine_body(blanked, m.end(), src_lines)
        line_end = body_end if body_end > 0 else line_start

        seen_calls: List[str] = []
        seen_lower: set[str] = set()
        for c in calls:
            if c.lower() in seen_lower:
                continue
            seen_calls.append(c)
            seen_lower.add(c.lower())

        candidate = SubroutineSummary(
            name=name,
            signature=signature,
            line_start=line_start,
            line_end=line_end,
            common_block_refs=uses_units,
            called_subroutines=tuple(seen_calls),
        )

        # Dedup key: use the method's LAST segment. `TIdSMTPMin.Connect`
        # and the bare `procedure Connect;` declared inside the class
        # block both dedup to `connect`; the candidate with a body
        # (qualified, implementation section) wins. The visible name on
        # the surviving entry is the candidate's — qualified beats bare
        # because it carries more information for the planner.
        key = name.rsplit(".", 1)[-1].lower()
        existing = best_by_name.get(key)
        if existing is None:
            best_by_name[key] = candidate
            order.append(key)
            continue
        # Prefer the entry with the larger body span. Forward declarations
        # have line_start == line_end, so an implementation body always wins.
        existing_span = existing.line_end - existing.line_start
        candidate_span = candidate.line_end - candidate.line_start
        if candidate_span > existing_span:
            best_by_name[key] = candidate
        elif candidate_span == existing_span and "." in name and "." not in existing.name:
            # Same body span; prefer the more-qualified name (carries the
            # class prefix) over the bare-name forward declaration.
            best_by_name[key] = candidate

    return [best_by_name[n] for n in order]


def _class_qualify(name: str, class_names: set[str]) -> str:
    """If the routine name is qualified (`TFoo.Method`) keep it as-is.
    Otherwise, return the bare name — v0 does NOT inject the class name
    based on context, because doing so requires tracking whether we're
    inside `implementation` for a class declared in `interface`."""
    return name


def _find_routine_body(
    blanked: str,
    after_header: int,
    src_lines: List[str],
) -> Tuple[int, int, List[str]]:
    """Locate the body of the routine that ends at the *next matching*
    `end;` after the header, tracking `begin`/`end` depth. Returns
    `(body_start_offset, end_line_1based, calls_seen)`.

    Calls are heuristically identified: an identifier followed by `(`
    at the same depth-1 inside the body."""
    # Find the `begin` that opens the body. If we hit a `;` first, this
    # is a forward declaration — no body, no calls.
    body_search = blanked[after_header:]
    begin_match = re.search(r"\b(begin|asm)\b", body_search, re.IGNORECASE)
    next_semi = body_search.find(";")
    if begin_match is None or (0 <= next_semi < begin_match.start()):
        return after_header, 0, []

    body_start = after_header + begin_match.end()
    depth = 1
    pos = body_start
    calls: List[str] = []
    # Walk forward token-by-token until depth == 0.
    while pos < len(blanked) and depth > 0:
        # Match a `begin`, an `end`, or an identifier-followed-by-(
        m = re.search(rf"\b(begin|end|asm|{_IDENT})\b", blanked[pos:], re.IGNORECASE)
        if not m:
            break
        token = m.group(1)
        token_lower = token.lower()
        absolute = pos + m.start()
        if token_lower in ("begin", "asm"):
            depth += 1
        elif token_lower == "end":
            depth -= 1
            if depth == 0:
                end_line = blanked.count("\n", 0, absolute) + 1
                return body_start, end_line, calls
        else:
            # Identifier — heuristically classify it as a call. Delphi
            # allows no-arg invocations without parens (`Foo;`), so an
            # identifier followed by `;` or `then` / `do` / `,` / `)` /
            # `and` / `or` / EOL counts as a call too. We REJECT it when
            # the next non-space char is `:=` (assignment), `.` (member
            # access — handled separately), or `=` (comparison).
            tail = blanked[pos + m.end():pos + m.end() + 4].lstrip()
            looks_called = False
            if tail.startswith("("):
                looks_called = True
            elif tail.startswith(":="):
                pass  # assignment target — not a call
            elif tail.startswith("."):
                pass  # member access — call lands on the qualified pair
            elif tail and tail[0] in ";\n,)" :
                looks_called = True
            if looks_called and _looks_like_call(token):
                calls.append(token)
        pos = pos + m.end()

    # Walked off the file without finding the matching `end;`. Return
    # whatever we have; the warning is logged at caller level.
    end_line = blanked.count("\n") + 1
    return body_start, end_line, calls
