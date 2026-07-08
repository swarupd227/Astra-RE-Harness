"""
Astra parser sidecar — VB.NET (.vb) structural parser.

**Phase 12.2 (v0).** A focused tokenizer-based parser that recognises the
*structural* surface a VB.NET line-of-business app exposes: namespace /
class / module / structure containers, the full VB.NET modifier grammar on
`Sub` / `Function` / `Property` declarations, auto-properties, and
`Imports` / `New` / `Call` reference sites. Produces the canonical
`ParseOutcome` shape shared with every other language parser so the API
sees one unified `ParseResult` regardless of source language.

Why not just reuse the VB6 tokenizer
------------------------------------
VB6 and VB.NET share the `Sub … End Sub` block shape, so the VB6 parser
*appears* to work on `.vb` files — which is why the sidecar shipped a
`_vbnet_parse` stub that delegated to it. But it silently drops a large
fraction of a real VB.NET codebase:

- **Modifiers.** VB6 visibility is only `Public|Private|Friend`. VB.NET adds
  `Protected`, `Shared`, `Overrides`, `Overridable`, `MustOverride`,
  `Overloads`, `Shadows`, `Partial`, `Async`, `Iterator`, `ReadOnly`,
  `WriteOnly`, `Default`, `Protected Friend`, `Private Protected`. A
  `Protected Overrides Sub OnStart()` or `Public Shared Async Function` does
  not match the VB6 pattern → the routine is missed entirely.
- **Properties.** VB6 only has `Property Get|Let|Set`. VB.NET's dominant form
  is the auto-property (`Public Property Name As Type`) with no body and no
  `End Property`, plus expanded `ReadOnly`/`WriteOnly` properties. The VB6
  regex matches none of these.
- **Containers.** VB.NET groups routines under `Namespace` / `Class` /
  `Module` / `Structure`, not `Attribute VB_Name`. The enclosing type is the
  natural "module" grouping for the documentation roll-up.

What v0 handles
---------------
- `.vb` files. Comment stripping for `'`, `'''` (XML doc), and `REM`.
- `Namespace` / `Class` / `Module` / `Structure` / `Interface` container
  tracking (a stack); a routine's `common_block_refs` carries its enclosing
  type name so the module roll-up groups correctly.
- `[modifiers] Sub NAME[(Of …)](…)` and
  `[modifiers] Function NAME[(Of …)](…) [As Type]` with full line ranges.
- `[modifiers] Property NAME …` — both full (walks to `End Property`) and
  auto (single line, no body).
- `Imports X.Y` at file scope (recorded as a file-level warning listing the
  dependency surface).
- `Call NAME(…)`, bare `NAME(…)`, and `New TypeName(…)` call sites for the
  dependency graph. Conservative — dotted `obj.Method()` calls are NOT
  collected (they'd over-count wildly); the planner treats
  `called_subroutines` as a hint, not a contract, matching the VB6/Delphi v0.

What v0 does NOT handle (deferred to a Roslyn-backed parser)
-----------------------------------------------------------
- `#If … Then` conditional compilation — all branches are processed.
- Physical joining of `_` line continuations — a declaration split across
  lines with a trailing `_` is matched on its first physical line only.
- Generic parameter/return types with nested parens are captured only up to
  the first `)`; sufficient for name + line-range detection.
- Lambdas / multiline `Function()` expressions are not treated as routines.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from typing import List, Optional, Tuple

log = logging.getLogger("astra.parser.vbnet")


# ──────────────────────────────────────────────────────────────────────
# Output shape — identical to vb6_parser.SubroutineSummary / the proto
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
# Pre-processing
# ──────────────────────────────────────────────────────────────────────

# `'` line comments (covers `'''` XML doc) and `REM `. Conservative — does
# not parse strings, so a `'` inside a literal is treated as a comment start.
_COMMENT_RE = re.compile(r"(?:'|\bREM\b).*$", re.IGNORECASE)


def _strip_comments(line: str) -> str:
    return _COMMENT_RE.sub("", line).rstrip()


def _normalise(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


# ──────────────────────────────────────────────────────────────────────
# Declaration grammar
# ──────────────────────────────────────────────────────────────────────

# The full VB.NET modifier set that may precede Sub/Function/Property, in any
# order and any number. Kept as a non-capturing repeated alternation so a
# declaration like `Protected Overrides Async Function` matches cleanly.
_MODIFIER = (
    r"(?:(?:Public|Private|Protected|Friend|Shared|Shadows|Overloads|"
    r"Overrides|Overridable|MustOverride|NotOverridable|MustInherit|"
    r"NotInheritable|Partial|Default|ReadOnly|WriteOnly|Iterator|Async|"
    r"Widening|Narrowing)\s+)*"
)
_OF_CLAUSE = r"(?:\(\s*Of\s+[^)]*\)\s*)?"     # generic type params: (Of T, U)

_SUB_DECL_RE = re.compile(
    rf"^\s*{_MODIFIER}Sub\s+(\w+)\s*{_OF_CLAUSE}\(([^)]*)\)?",
    re.IGNORECASE,
)
_END_SUB_RE = re.compile(r"^\s*End\s+Sub\b", re.IGNORECASE)

_FUNCTION_DECL_RE = re.compile(
    rf"^\s*{_MODIFIER}Function\s+(\w+)\s*{_OF_CLAUSE}\(([^)]*)\)?\s*(?:As\s+([\w\.\(\)]+))?",
    re.IGNORECASE,
)
_END_FUNCTION_RE = re.compile(r"^\s*End\s+Function\b", re.IGNORECASE)

# Property — modifiers then `Property NAME`. Params (indexed properties) and
# `As Type` are optional. Whether it is an auto-property or a full property is
# resolved by look-ahead in the walker (auto-properties have no End Property).
_PROPERTY_DECL_RE = re.compile(
    rf"^\s*{_MODIFIER}Property\s+(\w+)\s*(?:\(([^)]*)\)?)?\s*(?:As\s+([\w\.\(\)]+))?",
    re.IGNORECASE,
)
_END_PROPERTY_RE = re.compile(r"^\s*End\s+Property\b", re.IGNORECASE)

# Container declarations — modifiers then the container keyword + name.
_CONTAINER_DECL_RE = re.compile(
    rf"^\s*{_MODIFIER}(Class|Module|Structure|Interface)\s+(\w+)",
    re.IGNORECASE,
)
_CONTAINER_END_RE = re.compile(
    r"^\s*End\s+(Class|Module|Structure|Interface)\b", re.IGNORECASE
)
_NAMESPACE_DECL_RE = re.compile(r"^\s*Namespace\s+([\w\.]+)", re.IGNORECASE)
_NAMESPACE_END_RE = re.compile(r"^\s*End\s+Namespace\b", re.IGNORECASE)

_IMPORTS_RE = re.compile(r"^\s*Imports\s+([\w\.]+)", re.IGNORECASE)

# Call sites.
_CALL_KEYWORD_RE = re.compile(r"^\s*Call\s+(\w+)\s*\(", re.IGNORECASE)
_NEW_RE = re.compile(r"\bNew\s+([\w\.]+)\s*\(", re.IGNORECASE)
_BARE_CALL_RE = re.compile(r"^\s*(?:Await\s+)?(\w+)\s*\(")

# Any declaration that terminates an auto-property look-ahead.
_ANY_DECL_RE = re.compile(
    rf"^\s*{_MODIFIER}(?:Sub|Function|Property|Class|Module|Structure|Interface)\b",
    re.IGNORECASE,
)

_RESERVED = frozenset({
    "if", "for", "while", "do", "select", "with", "set", "let", "get",
    "dim", "redim", "const", "public", "private", "friend", "protected",
    "type", "enum", "function", "sub", "property", "end", "return",
    "next", "case", "loop", "wend", "until", "to", "each", "in", "then",
    "else", "elseif", "try", "catch", "finally", "throw", "using",
    "new", "await", "async", "and", "or", "not", "is", "isnot", "andalso",
    "orelse", "nothing", "me", "mybase", "myclass", "true", "false",
    "byval", "byref", "optional", "as", "of", "call", "exit", "continue",
    "addhandler", "removehandler", "raiseevent", "gettype", "typeof",
    "print", "write", "open", "close", "cint", "clng", "cstr", "cdbl",
    "cbool", "cdate", "cdec", "csng", "cobj", "ctype", "directcast",
})


# ──────────────────────────────────────────────────────────────────────
# Main entry
# ──────────────────────────────────────────────────────────────────────


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single VB.NET `.vb` file and return a `ParseOutcome`."""
    content = _normalise(content)
    lines = content.split("\n")
    line_count = len(lines)
    warnings: List[str] = []

    imports: List[str] = []
    container_stack: List[str] = []
    subroutines: List[SubroutineSummary] = []

    i = 0
    while i < len(lines):
        raw = lines[i]
        stripped = _strip_comments(raw)
        if not stripped:
            i += 1
            continue

        # Imports (file scope) — record for the dependency-surface warning.
        imp = _IMPORTS_RE.match(stripped)
        if imp:
            imports.append(imp.group(1))
            i += 1
            continue

        # Namespace push/pop.
        ns = _NAMESPACE_DECL_RE.match(stripped)
        if ns:
            container_stack.append(ns.group(1))
            i += 1
            continue
        if _NAMESPACE_END_RE.match(stripped):
            if container_stack:
                container_stack.pop()
            i += 1
            continue

        # Class / Module / Structure / Interface push/pop.
        cont = _CONTAINER_DECL_RE.match(stripped)
        if cont:
            container_stack.append(cont.group(2))
            i += 1
            continue
        if _CONTAINER_END_RE.match(stripped):
            if container_stack:
                container_stack.pop()
            i += 1
            continue

        enclosing = container_stack[-1] if container_stack else ""

        sub_match = _SUB_DECL_RE.match(stripped)
        func_match = _FUNCTION_DECL_RE.match(stripped) if not sub_match else None
        prop_match = (
            _PROPERTY_DECL_RE.match(stripped)
            if not sub_match and not func_match else None
        )

        if sub_match or func_match:
            name = (sub_match or func_match).group(1)
            end_re = _END_SUB_RE if sub_match else _END_FUNCTION_RE
            line_start = i + 1
            line_end, body = _consume_routine_body(lines, i + 1, end_re)
            subroutines.append(SubroutineSummary(
                name=name,
                signature=_trim_signature(raw),
                line_start=line_start,
                line_end=line_end,
                common_block_refs=(enclosing,) if enclosing else tuple(),
                called_subroutines=tuple(sorted(set(_collect_calls(body)))),
            ))
            i = line_end
            continue

        if prop_match:
            name = prop_match.group(1)
            line_start = i + 1
            is_full, line_end, body = _resolve_property(lines, i)
            subroutines.append(SubroutineSummary(
                name=f"{name} (Property)",
                signature=_trim_signature(raw),
                line_start=line_start,
                line_end=line_end,
                common_block_refs=(enclosing,) if enclosing else tuple(),
                called_subroutines=tuple(sorted(set(_collect_calls(body)))),
            ))
            i = line_end if is_full else line_start
            continue

        i += 1

    if imports:
        shown = ", ".join(imports[:8])
        warnings.append(
            f"vbnet: file imports {len(imports)} namespace(s): {shown}"
            + (" ..." if len(imports) > 8 else "")
        )

    return ParseOutcome(
        line_count=line_count,
        subroutines=subroutines,
        warnings=warnings,
        filename=filename,
    )


def _trim_signature(raw: str) -> str:
    return _strip_comments(raw).strip()


def _consume_routine_body(
    lines: List[str], start: int, end_re: re.Pattern
) -> Tuple[int, List[str]]:
    """Walk forward from `start` (0-based) to the matching End block.
    Returns (1-based inclusive line_end, body_lines)."""
    body: List[str] = []
    i = start
    while i < len(lines):
        if end_re.match(_strip_comments(lines[i])):
            return i + 1, body
        body.append(lines[i])
        i += 1
    return len(lines), body


def _resolve_property(lines: List[str], decl_idx: int) -> Tuple[bool, int, List[str]]:
    """Decide whether the property at `decl_idx` (0-based) is a full property
    (has a Get/Set body closed by `End Property`) or an auto-property (no body).

    Returns (is_full, 1-based line_end, body_lines). For an auto-property the
    body is empty and line_end == decl line. The look-ahead stops at the next
    declaration or container-end so a bodyless auto-property is never misread
    as swallowing the routines that follow it."""
    body: List[str] = []
    i = decl_idx + 1
    while i < len(lines):
        stripped = _strip_comments(lines[i])
        if _END_PROPERTY_RE.match(stripped):
            return True, i + 1, body
        # Hit the next routine/container boundary before any End Property →
        # the declaration was an auto-property with no body.
        if stripped and (_ANY_DECL_RE.match(stripped) or _CONTAINER_END_RE.match(stripped)):
            return False, decl_idx + 1, []
        if stripped:
            body.append(lines[i])
        i += 1
    return False, decl_idx + 1, []


def _collect_calls(body: List[str]) -> List[str]:
    """Heuristically collect call-site identifiers. `New TypeName(` sites are
    prefixed `new:` so the downstream pipeline can distinguish instantiation
    from invocation."""
    calls: List[str] = []
    for raw in body:
        line = _strip_comments(raw)
        if not line:
            continue
        for m in _NEW_RE.finditer(line):
            calls.append(f"new:{m.group(1)}")
        call_match = _CALL_KEYWORD_RE.match(line)
        if call_match:
            calls.append(call_match.group(1))
            continue
        bare = _BARE_CALL_RE.match(line)
        if bare:
            name = bare.group(1)
            if name.lower() not in _RESERVED:
                calls.append(name)
    return calls
