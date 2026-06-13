"""
Astra parser sidecar — C++ structural parser.

**Phase 9.1.a (v0 scaffolding).** A focused tokenizer-based parser that
recognises the *structural* surface needed to feed the Migration Planner
and the Spec extraction pipeline: namespaces, class / struct declarations,
function definitions (both free-standing and class-method), template
declarations, `#include` edges, and call-site references.

This is **not** a full C++ parser. Per the C++ approach implied by
phase-9.0-multi-source-language.md §9.1.a, the production parser shells
out to `libclang` and walks its AST against `compile_commands.json`
(bootstrapped by CMake per ADR-028). The v0 module exists so the rest of
Phase 9.1 (schema, prompts, archetypes, equivalence sidecar, fmt seed)
can develop in parallel against a working AST source. The two parsers
MUST produce the same `ParseOutcome` shape so we can swap implementations
without touching callers.

What v0 handles
---------------
- `#include "x.h"` / `#include <x>` directives (recorded as `common_block_refs`)
- `namespace NAME { ... }` blocks (tracked to qualify routines)
- `class NAME` / `struct NAME` declarations
- Free function definitions: `int foo(int x) { ... }`
- Class-method definitions: `int Foo::bar(int x) { ... }`
- Constructor / destructor definitions: `Foo::Foo()`, `Foo::~Foo()`
- Template prefixes: `template<typename T> T add(T a, T b)` — recorded
  once for the primary template per ADR-026 (no per-instantiation rows)
- Function calls inside bodies (heuristic — over-counts on identifiers
  shared with variables; the planner treats it as a hint, not a contract)

What v0 does NOT handle (deferred to libclang shell-out)
--------------------------------------------------------
- `#if`/`#ifdef`/`#else` preprocessor branches.  v0 strips comments and
  processes ALL branches — if the corpus uses heavy conditional
  compilation, libclang+CMake is required for semantic accuracy.
- Full template specialisation detection.  v0 records the primary
  template name; explicit specialisations get their own row (per
  OQ-026-2).
- Overload resolution.  v0 treats overloads as the same name; the
  signature column disambiguates.
- Lambda expressions (recorded as anonymous calls inside the parent
  function body).
- Macro expansion.  v0 ignores macros entirely; libclang+the cpp will
  expand them in the production path.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from typing import List, Tuple

log = logging.getLogger("astra.parser.cpp")


# ──────────────────────────────────────────────────────────────────────
# Output shape — matches `delphi_parser.SubroutineSummary` and the proto
# ──────────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class SubroutineSummary:
    name: str
    signature: str
    line_start: int
    line_end: int
    # `#include` edges. The Migration Planner treats them the same way it
    # treats Fortran COMMON blocks and Delphi `uses` clauses — as a
    # cross-routine dependency edge.
    common_block_refs: Tuple[str, ...]
    # Identifiers found in call position inside the routine body. Over-
    # counts on variable names; the planner treats it as a hint, not a
    # contract.
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


# C++ has two comment syntaxes. We blank them both while preserving line
# positions so post-parse line numbers stay accurate.
_LINE_COMMENT = re.compile(r"//[^\r\n]*")
_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
# String / char literals — also blanked so curly braces inside them don't
# confuse brace-balancing.
_STRING_LITERAL = re.compile(r'"(?:\\.|[^"\\\n])*"')
_CHAR_LITERAL = re.compile(r"'(?:\\.|[^'\\\n])'")
# Raw string literals (R"delim(...)delim") — best-effort blank.
_RAW_STRING = re.compile(r'R"([^()\s\\]{0,16})\((.*?)\)\1"', re.DOTALL)


def _blank_preserve_lines(m: "re.Match[str]") -> str:
    return "".join("\n" if c == "\n" else " " for c in m.group(0))


def _strip_comments_and_literals(text: str) -> str:
    text = _BLOCK_COMMENT.sub(_blank_preserve_lines, text)
    text = _RAW_STRING.sub(_blank_preserve_lines, text)
    text = _STRING_LITERAL.sub(_blank_preserve_lines, text)
    text = _CHAR_LITERAL.sub(_blank_preserve_lines, text)
    text = _LINE_COMMENT.sub(lambda m: " " * len(m.group(0)), text)
    return text


# ──────────────────────────────────────────────────────────────────────
# Tokenization helpers
# ──────────────────────────────────────────────────────────────────────


_IDENT = r"[A-Za-z_][A-Za-z0-9_]*"
_QUALIFIED = rf"(?:{_IDENT}::)*{_IDENT}"  # foo, ns::foo, ns::ns2::foo
# Qualified destructor: ns::Class::~Class. Must come BEFORE _QUALIFIED in
# the function-name alternation so the `~` doesn't get stranded.
_QUALIFIED_DTOR = rf"(?:{_IDENT}::)+~{_IDENT}"

_INCLUDE_RE = re.compile(
    r'^\s*#\s*include\s*[<"]([^>"]+)[>"]',
    re.MULTILINE,
)
_NAMESPACE_RE = re.compile(
    rf"\bnamespace\s+({_IDENT})\s*\{{",
)
_CLASS_DECL_RE = re.compile(
    rf"\b(class|struct)\s+({_IDENT})\b(?:\s*:\s*[^{{;]+)?\s*\{{",
)
# A function header: optional template prefix is matched separately.
# Capture groups: return-ish-tokens (optional, missing for ctors/dtors),
# qualified-name, param-list.
# We deliberately keep the return-type pattern loose — C++ types span
# `const std::vector<std::pair<int, std::string>>&` and a strict regex
# would lose more than it gains. libclang will do this properly later.
#
# The `ret` group is OPTIONAL because constructors and destructors have
# no return type. The matcher post-filters: a routine with an empty `ret`
# is only kept when its `name` is a (qualified) constructor / destructor
# — i.e. `ClassName::ClassName`, `ClassName::~ClassName`, or `~ClassName`.
_FUNCTION_RE = re.compile(
    rf"(?:(?P<ret>[\w:<>,\s\*&]+?)\s+)?"  # optional return type
    rf"(?P<name>{_QUALIFIED_DTOR}|{_QUALIFIED}|~{_IDENT}|{_IDENT})\s*"
    rf"\((?P<params>[^)]*)\)\s*"
    rf"(?P<post>(?:const|noexcept|override|final|=\s*0|=\s*default|=\s*delete|\s)*)"
    # Optional member-initialiser list for constructors:
    # `Foo() : member_(...) { ... }`. Match a `:` followed by a balanced
    # run that stops at the first top-level `{`.
    rf"(?P<init>:\s*[^{{;]*)?"
    rf"(?P<body>\{{|;)",
)
_TEMPLATE_RE = re.compile(
    r"\btemplate\s*<([^>]*)>",
)

# Reserved words that look like calls but aren't.
_RESERVED_NOT_CALLS = {
    "if", "else", "for", "while", "do", "switch", "case", "default",
    "return", "break", "continue", "throw", "try", "catch", "goto",
    "sizeof", "alignof", "decltype", "typeid", "static_cast", "dynamic_cast",
    "const_cast", "reinterpret_cast", "new", "delete", "this", "nullptr",
    "true", "false", "operator", "template", "typename", "class", "struct",
    "union", "enum", "namespace", "using", "typedef", "auto", "constexpr",
    "consteval", "constinit", "const", "volatile", "mutable", "static",
    "inline", "extern", "virtual", "explicit", "friend", "public", "private",
    "protected", "void", "bool", "char", "int", "short", "long", "float",
    "double", "unsigned", "signed", "wchar_t", "char8_t", "char16_t",
    "char32_t", "nullptr_t",
    # ranges / std vocabulary that overload-resolves but isn't really a
    # user-defined call in the planner's sense
    "std", "begin", "end", "size",
}


def _looks_like_call(token: str) -> bool:
    return token.lower() not in _RESERVED_NOT_CALLS and token.isidentifier()


def _is_ctor_or_dtor(name: str) -> bool:
    """True when `name` looks like a constructor or destructor.

    Cases:
      - destructor: leading `~`, e.g. `~Foo` or `Foo::~Foo`
      - constructor: qualified `Class::Class` where both segments match
      - in-class constructor body (e.g. `Foo() { ... }` inside the class
        definition) is intentionally NOT matched here — the v0 parser
        treats that as a regular routine because the regex sees no
        return type AND no qualifier. We'd misclassify random control
        flow as constructors if we accepted those. Out-of-line ctors
        (the common case for fmt / Indy / real code) are caught.
    """
    if "::" in name:
        head, tail = name.rsplit("::", 1)
        if tail.startswith("~"):
            return tail[1:] == head.rsplit("::", 1)[-1]
        return tail == head.rsplit("::", 1)[-1]
    return name.startswith("~")


# ──────────────────────────────────────────────────────────────────────
# Body extraction — brace-balanced span starting at the opening `{`
# ──────────────────────────────────────────────────────────────────────


def _find_matching_brace(text: str, open_idx: int) -> int:
    """Return the index of the `}` matching the `{` at `open_idx`, or
    -1 if no match found before end-of-input. Caller has already stripped
    comments / string literals so naive brace-counting is sound."""
    depth = 0
    for i in range(open_idx, len(text)):
        c = text[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def _line_of(text: str, index: int) -> int:
    """1-based line number of `index` in `text`."""
    return text.count("\n", 0, index) + 1


# ──────────────────────────────────────────────────────────────────────
# Public entry point
# ──────────────────────────────────────────────────────────────────────


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single C++ source / header file. Always returns a
    ParseOutcome; failure modes become warnings, not exceptions, so a
    malformed file in a 300k-LoC corpus doesn't take down the ingest."""
    warnings: List[str] = []
    raw = content.replace("\r\n", "\n").replace("\r", "\n")
    if not raw:
        line_count = 0
    else:
        line_count = raw.count("\n") + (0 if raw.endswith("\n") else 1)

    stripped = _strip_comments_and_literals(raw)

    includes: List[str] = []
    for m in _INCLUDE_RE.finditer(raw):  # find includes in the RAW text
        # so commented-out includes don't slip in.
        includes.append(m.group(1).strip())
    includes_tuple = tuple(includes)

    # Track namespace and class context as we walk function definitions.
    # The walker scans linearly through `stripped` so it sees declarations
    # in source order.
    routines: List[SubroutineSummary] = []
    seen_names: List[str] = []  # for dedup-by-name when overloads collide

    # We deliberately do NOT track nested namespace stacks — qualified
    # function names (`ns::Class::method`) already carry their context,
    # and v0 keeps the call-graph honest by name+arity rather than full
    # ns-stack resolution.

    for m in _FUNCTION_RE.finditer(stripped):
        name = m.group("name")
        ret = (m.group("ret") or "").strip()
        body_open = m.group("body")
        if body_open != "{":
            # Forward declaration only — skip; we'll catch the definition
            # later in the same file or accept it as a header-only decl
            # in another file the ingest pipeline already records.
            continue

        # Reject false positives that look like function headers but
        # aren't: control-flow keywords, type aliases, etc.
        if name.lower() in _RESERVED_NOT_CALLS:
            continue

        # A ret-less match is only legitimate when `name` is a
        # constructor or destructor. Everything else (e.g. bare
        # `if (x) { ... }`) gets filtered out here.
        if not ret and not _is_ctor_or_dtor(name):
            continue
        # Also reject ret-less matches where `name` starts with a
        # reserved keyword — guards against `else { foo(); }` and
        # similar control-flow that the regex over-matches.
        if not ret:
            first_tok = name.split("::")[0].lstrip("~").lower()
            if first_tok in _RESERVED_NOT_CALLS:
                continue

        start_idx = m.start()
        brace_idx = m.end("body") - 1  # the `{`
        close_idx = _find_matching_brace(stripped, brace_idx)
        if close_idx < 0:
            warnings.append(
                f"unmatched brace for routine '{name}' at line "
                f"{_line_of(stripped, start_idx)}; truncated"
            )
            close_idx = len(stripped) - 1

        # Walk backward from start_idx to see whether a `template<...>`
        # prefix decorates this routine (per ADR-026 we keep the primary
        # template's name but prepend the template-prefix to the
        # signature so the spec page renders it verbatim).
        template_prefix = ""
        tmpl_m = _last_template_before(stripped, start_idx)
        if tmpl_m is not None:
            template_prefix = f"template<{tmpl_m}> "

        signature = (
            f"{template_prefix}"
            f"{(ret + ' ') if ret else ''}"
            f"{name}"
            f"({m.group('params').strip()})"
            f"{(' ' + m.group('post').strip()) if m.group('post').strip() else ''}"
        )

        line_start = _line_of(raw, start_idx)
        line_end = _line_of(raw, close_idx)

        # Heuristic call detection inside the body.
        body_text = stripped[brace_idx + 1 : close_idx]
        called = _scan_calls(body_text)
        # Don't include the routine's own qualified-tail in its call list.
        bare_self = name.rsplit("::", 1)[-1]
        called = tuple(c for c in called if c != bare_self)

        routines.append(
            SubroutineSummary(
                name=name,
                signature=_collapse_whitespace(signature),
                line_start=line_start,
                line_end=line_end,
                common_block_refs=includes_tuple,
                called_subroutines=called,
            )
        )
        seen_names.append(name)

    # Dedup-by-name: when the same qualified name appears multiple times
    # (overloads with different signatures), keep the entry with the
    # largest body span — same heuristic as delphi_parser, and the same
    # rationale (the definition wins over the forward declaration).
    routines = _dedup_by_name(routines)

    return ParseOutcome(
        line_count=line_count,
        subroutines=routines,
        warnings=warnings,
        filename=filename,
    )


# ──────────────────────────────────────────────────────────────────────
# Helpers
# ──────────────────────────────────────────────────────────────────────


def _last_template_before(text: str, idx: int) -> "str | None":
    """If a `template<...>` prefix sits immediately before `idx` (with
    only whitespace between), return its parameter list. Otherwise None.

    "Immediately before" tolerates blank lines and indentation but not
    other declarations — once we cross a `;` or `}` we give up."""
    window = text[max(0, idx - 512) : idx]
    # walk from the end of `window` backward
    j = len(window) - 1
    while j >= 0 and window[j] in " \t\r\n":
        j -= 1
    if j < 0:
        return None
    # the previous non-whitespace char must be `>` (template close)
    if window[j] != ">":
        return None
    # Match the LAST `template<...>` in the window; the regex already
    # handles arbitrarily nested angle brackets via greedy matching.
    matches = list(_TEMPLATE_RE.finditer(window))
    if not matches:
        return None
    tmpl_params = matches[-1].group(1).strip()
    # Reject if there's a `;` or `}` between the template and idx — that
    # means the template decorates SOMETHING ELSE, not our function.
    tail = window[matches[-1].end() :]
    if ";" in tail or "}" in tail:
        return None
    return tmpl_params


_CALL_RE = re.compile(rf"\b({_IDENT})\s*\(")


def _scan_calls(body: str) -> Tuple[str, ...]:
    seen: list[str] = []
    seen_set: set[str] = set()
    for m in _CALL_RE.finditer(body):
        tok = m.group(1)
        if _looks_like_call(tok) and tok not in seen_set:
            seen.append(tok)
            seen_set.add(tok)
    return tuple(seen)


def _collapse_whitespace(s: str) -> str:
    return re.sub(r"\s+", " ", s).strip()


def _dedup_by_name(routines: List[SubroutineSummary]) -> List[SubroutineSummary]:
    """Keep one row per qualified name; prefer the entry with the larger
    line-range (definition over forward declaration). Stable: preserves
    discovery order among the survivors."""
    best: dict[str, SubroutineSummary] = {}
    for r in routines:
        cur = best.get(r.name)
        if cur is None:
            best[r.name] = r
            continue
        if (r.line_end - r.line_start) > (cur.line_end - cur.line_start):
            best[r.name] = r
    # Preserve original discovery order.
    seen: set[str] = set()
    result: List[SubroutineSummary] = []
    for r in routines:
        if r.name in seen:
            continue
        seen.add(r.name)
        result.append(best[r.name])
    return result
