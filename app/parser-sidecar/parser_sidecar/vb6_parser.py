"""
Astra parser sidecar — VB6 (Visual Basic 6) structural parser.

**Phase 10.0.a (v0 scaffolding).** A focused tokenizer-based parser that
recognises the *structural* surface needed to feed the Migration Planner
and the Spec extraction pipeline: module declarations, Sub/Function
signatures, On Error handlers, COM interop call sites, and module-level
references.

This is **not** a full VB6 parser. Per ADR-035 the production parser
loads an ANTLR4 grammar (sourced from Rubberduck VBA) via the ANTLR4
Python runtime. The v0 module exists so the rest of Phase 10.0 (schema,
prompts, archetypes, equivalence sidecar, seed corpus) can develop in
parallel against a working AST source. The two parsers MUST produce the
same `ParseOutcome` shape so we can swap implementations without
touching callers.

What v0 handles
---------------
- `.bas` modules, `.cls` class modules, `.frm` forms (code-block only;
  the property-bag header is parsed separately by `parse_frm_layout`).
- Top-level `[Public|Private|Friend] Sub NAME(...)` and
  `[Public|Private|Friend] Function NAME(...) [As Type]` declarations
  with full line-range tracking.
- `Public Property Get|Let|Set NAME(...)` accessors (recorded as routines
  with `signature` carrying the accessor flavour).
- `Attribute VB_Name = "ModuleName"` declarations.
- `On Error Goto <label>` and `On Error Resume Next` blocks (recorded as
  warnings on the containing routine so the SME sees them).
- `CreateObject("...")` and `GetObject(...)` call sites (recorded as
  `called_subroutines` with a `com:` prefix so the planner can surface
  them as `com_interop_contract` claims downstream).
- `Call ROUTINE(...)` and bare `ROUTINE(...)` call sites for routines
  declared in the same file (heuristic; over-counts on identifiers shared
  with variables — matches the Delphi v0 behaviour).

What v0 does NOT handle (deferred to ANTLR4 per ADR-035)
--------------------------------------------------------
- `#If VBA7 Then` / `#If Win64 Then` preprocessor branches. v0 strips
  comments and processes ALL branches; if the corpus uses conditional
  compilation extensively, ANTLR4 (production path) is required.
- `Declare Sub|Function NAME Lib "kernel32" ...` P/Invoke declarations
  (recorded as warnings, not as routines — they're external bindings).
- Implicit line continuations (`_` at end of line). v0 doesn't physically
  join continued lines; the production parser does.
- Default-property access (`rs!FieldName`, `rs("col")`). v0 does not
  resolve these; the spec extractor surfaces them based on context.
- Late-bound `Set obj = ...` against `Variant` — v0 records the Set; the
  production parser will tag it with the static type if resolvable.

.frm layout block
-----------------
The `.frm` form-file format has a binary-ish property bag at the top
followed by regular VB6 code. `parse_frm_layout` walks the property bag
and returns a structured `FormLayout` describing controls + their
properties. The code-block after the property bag is then passed to
`parse_source` like any other VB6 source.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

log = logging.getLogger("astra.parser.vb6")


# ──────────────────────────────────────────────────────────────────────
# Output shape — matches `delphi_parser.SubroutineSummary` and the proto
# ──────────────────────────────────────────────────────────────────────


@dataclass(frozen=True)
class SubroutineSummary:
    name: str
    signature: str
    line_start: int
    line_end: int
    # VB6's analogue of Delphi's `uses` clauses: module-level imports +
    # `Tools → References` COM type-library bindings. Surfaced here so the
    # Migration Planner can walk them as dependency edges.
    common_block_refs: Tuple[str, ...]
    # Identifiers found in call position inside the routine body. COM
    # call sites are prefixed `com:` (e.g. `com:Excel.Application`) so the
    # downstream pipeline can route them to the `com_interop_contract`
    # claim kind without re-parsing.
    called_subroutines: Tuple[str, ...]


@dataclass
class ParseOutcome:
    line_count: int
    subroutines: List[SubroutineSummary]
    warnings: List[str]
    filename: str


# Phase 10.0.a — .frm form-layout container. Hand-rolled parse target;
# not exposed via the gRPC proto in v0 (the proto already carries the
# routine catalogue; layout is queried separately by the IngestPipeline).
@dataclass
class FormControl:
    kind: str        # e.g. "VB.CommandButton", "VB.TextBox"
    name: str        # `Name = ...` property
    properties: Dict[str, str] = field(default_factory=dict)
    children: List["FormControl"] = field(default_factory=list)


@dataclass
class FormLayout:
    root: Optional[FormControl]
    attributes: Dict[str, str] = field(default_factory=dict)
    code_block_start_line: int = 0     # 1-based; first line AFTER the property bag


# ──────────────────────────────────────────────────────────────────────
# Pre-processing — strip comments, normalise line endings
# ──────────────────────────────────────────────────────────────────────


_COMMENT_RE = re.compile(r"(?:'|\bREM\b).*$", re.IGNORECASE)


def _strip_comments(line: str) -> str:
    """Strip VB6 line comments (`'` or `REM `). Conservative — does not
    parse strings, so a `'` inside a `"..."` string is treated as the
    start of a comment. v0 accepts this over-strip; ANTLR4 (production)
    handles it correctly."""
    return _COMMENT_RE.sub("", line).rstrip()


def _normalise(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


# ──────────────────────────────────────────────────────────────────────
# Routine declaration patterns
# ──────────────────────────────────────────────────────────────────────


_VISIBILITY = r"(?:Public\s+|Private\s+|Friend\s+)?"
_STATIC = r"(?:Static\s+)?"

# Sub NAME(...) → group 1 = name, group 2 = params (everything inside parens)
_SUB_DECL_RE = re.compile(
    rf"^\s*{_VISIBILITY}{_STATIC}Sub\s+(\w+)\s*\(([^)]*)\)",
    re.IGNORECASE,
)
_END_SUB_RE = re.compile(r"^\s*End\s+Sub\b", re.IGNORECASE)

# Function NAME(...) [As Type] → group 1 = name, group 2 = params, group 3 = return type or empty
_FUNCTION_DECL_RE = re.compile(
    rf"^\s*{_VISIBILITY}{_STATIC}Function\s+(\w+)\s*\(([^)]*)\)\s*(?:As\s+([\w\.\(\)]+))?",
    re.IGNORECASE,
)
_END_FUNCTION_RE = re.compile(r"^\s*End\s+Function\b", re.IGNORECASE)

# Property Get|Let|Set NAME(...) [As Type]
_PROPERTY_DECL_RE = re.compile(
    rf"^\s*{_VISIBILITY}Property\s+(Get|Let|Set)\s+(\w+)\s*\(([^)]*)\)\s*(?:As\s+([\w\.\(\)]+))?",
    re.IGNORECASE,
)
_END_PROPERTY_RE = re.compile(r"^\s*End\s+Property\b", re.IGNORECASE)

# Module-level attributes (Attribute VB_Name = "Foo")
_ATTRIBUTE_RE = re.compile(r"^\s*Attribute\s+(\w+)\s*=\s*\"([^\"]*)\"", re.IGNORECASE)

# Declare Sub|Function NAME Lib "..." [Alias "..."]
_DECLARE_RE = re.compile(
    rf"^\s*{_VISIBILITY}Declare\s+(?:PtrSafe\s+)?(Sub|Function)\s+(\w+)\s+Lib\s+\"([^\"]+)\"",
    re.IGNORECASE,
)

# On Error variants
_ON_ERROR_GOTO_RE = re.compile(r"^\s*On\s+Error\s+Goto\s+([\w\-]+)", re.IGNORECASE)
_ON_ERROR_RESUME_RE = re.compile(r"^\s*On\s+Error\s+Resume\s+Next\b", re.IGNORECASE)

# COM interop
_CREATE_OBJECT_RE = re.compile(r"\bCreateObject\s*\(\s*\"([^\"]+)\"", re.IGNORECASE)
_GET_OBJECT_RE = re.compile(r"\bGetObject\s*\(", re.IGNORECASE)

# Generic call site — `Call NAME(...)` or bare `NAME(...)` at statement start.
# Conservative; misses dotted calls (`obj.method()`). v0 deliberately under-
# counts here; the planner treats `called_subroutines` as a hint, not a
# contract — same posture as the Delphi v0.
_CALL_KEYWORD_RE = re.compile(r"^\s*Call\s+(\w+)\s*\(", re.IGNORECASE)
_BARE_CALL_RE = re.compile(r"^\s*(\w+)\s*\(")  # over-counts; filtered post-hoc

# Reserved tokens that should never be treated as call sites even when they
# match the bare-call pattern.
_RESERVED = frozenset({
    "if", "for", "while", "do", "select", "with", "set", "let",
    "dim", "redim", "const", "public", "private", "friend",
    "type", "enum", "function", "sub", "property", "end",
    "next", "case", "loop", "wend", "until", "to",
    "open", "close", "input", "print", "write", "kill", "rmdir",
    "createobject", "getobject", "callbyname",
})


# ──────────────────────────────────────────────────────────────────────
# Main entry — parse_source
# ──────────────────────────────────────────────────────────────────────


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single VB6 source file (`.bas`, `.cls`, or `.frm`) and
    return a `ParseOutcome` matching the canonical shape.

    For `.frm` files, the binary property bag at the top is skipped
    (it's parsed separately by `parse_frm_layout`); only the code block
    after `Attribute VB_Name = "..."` is consumed here.
    """
    content = _normalise(content)
    lines = content.split("\n")
    line_count = len(lines)
    warnings: List[str] = []

    # For .frm files, skip the property bag — find the line after the
    # last `Attribute VB_*` declaration in the header.
    code_block_start = 0
    if filename.lower().endswith(".frm"):
        code_block_start = _find_frm_code_start(lines)
        if code_block_start == 0:
            warnings.append("vb6: could not locate end of .frm property bag; parsing entire file")

    # Collect module-level attributes (Attribute VB_Name = "Foo")
    module_name = _extract_module_name(lines[code_block_start:])

    # Walk the code block for routine declarations.
    subroutines: List[SubroutineSummary] = []
    declare_routines: List[str] = []      # P/Invoke declarations — flagged as warnings
    i = code_block_start
    while i < len(lines):
        raw = lines[i]
        stripped = _strip_comments(raw)

        # P/Invoke declarations — single-line, no body to walk into.
        declare_match = _DECLARE_RE.match(stripped)
        if declare_match:
            kind, name, lib = declare_match.groups()
            declare_routines.append(f"{name} (Lib \"{lib}\")")
            i += 1
            continue

        sub_match = _SUB_DECL_RE.match(stripped)
        func_match = _FUNCTION_DECL_RE.match(stripped) if not sub_match else None
        prop_match = _PROPERTY_DECL_RE.match(stripped) if not sub_match and not func_match else None

        if sub_match or func_match or prop_match:
            line_start = i + 1     # 1-based
            if sub_match:
                name = sub_match.group(1)
                signature = _trim_signature(raw)
                end_re = _END_SUB_RE
            elif func_match:
                name = func_match.group(1)
                signature = _trim_signature(raw)
                end_re = _END_FUNCTION_RE
            else:
                accessor = prop_match.group(1)
                base_name = prop_match.group(2)
                name = f"{base_name} ({accessor})"
                signature = _trim_signature(raw)
                end_re = _END_PROPERTY_RE

            # Walk forward until the matching End block.
            line_end, body = _consume_routine_body(lines, i + 1, end_re)
            calls = _collect_calls(body)
            # Emit a warning for routines that use On Error Resume Next.
            for body_line in body:
                if _ON_ERROR_RESUME_RE.match(_strip_comments(body_line)):
                    warnings.append(
                        f"vb6: routine '{name}' uses On Error Resume Next "
                        f"(line {line_start}); surface as on_error_handler claim"
                    )
                    break
                goto_match = _ON_ERROR_GOTO_RE.match(_strip_comments(body_line))
                if goto_match:
                    warnings.append(
                        f"vb6: routine '{name}' uses On Error Goto {goto_match.group(1)} "
                        f"(line {line_start}); surface as on_error_handler claim"
                    )
                    break

            subroutines.append(SubroutineSummary(
                name=name,
                signature=signature,
                line_start=line_start,
                line_end=line_end,
                common_block_refs=(module_name,) if module_name else tuple(),
                called_subroutines=tuple(sorted(calls)),
            ))
            i = line_end
            continue
        i += 1

    if declare_routines:
        warnings.append(
            f"vb6: file declares {len(declare_routines)} Win32 P/Invoke binding(s): "
            + ", ".join(declare_routines[:5])
            + (" ..." if len(declare_routines) > 5 else "")
        )

    return ParseOutcome(
        line_count=line_count,
        subroutines=subroutines,
        warnings=warnings,
        filename=filename,
    )


def _trim_signature(raw: str) -> str:
    """Return the declaration line as written, stripped of leading whitespace
    and trailing comments. Preserves the full Sub/Function/Property prefix
    for display in the routine catalogue."""
    return _strip_comments(raw).strip()


def _consume_routine_body(
    lines: List[str], start: int, end_re: re.Pattern
) -> Tuple[int, List[str]]:
    """Walk forward from `start` (0-based) until we hit the matching
    End block. Returns (1-based line_end, body_lines)."""
    body: List[str] = []
    i = start
    while i < len(lines):
        raw = lines[i]
        if end_re.match(_strip_comments(raw)):
            return i + 1, body  # 1-based, inclusive of the End line
        body.append(raw)
        i += 1
    # Unterminated routine — return EOF as the end. Production parser will
    # surface this as a syntax warning; v0 silently runs to EOF.
    return len(lines), body


def _collect_calls(body: List[str]) -> List[str]:
    """Heuristically collect call-site identifiers from a routine body.

    Returns a list with COM call sites prefixed `com:` (e.g.
    `com:Excel.Application`) so the downstream pipeline can route them
    to the `com_interop_contract` claim kind."""
    calls: List[str] = []
    for raw in body:
        line = _strip_comments(raw)
        if not line:
            continue
        for m in _CREATE_OBJECT_RE.finditer(line):
            calls.append(f"com:{m.group(1)}")
        if _GET_OBJECT_RE.search(line):
            calls.append("com:<GetObject>")
        call_match = _CALL_KEYWORD_RE.match(line)
        if call_match:
            calls.append(call_match.group(1))
            continue
        bare_match = _BARE_CALL_RE.match(line)
        if bare_match:
            name = bare_match.group(1)
            if name.lower() not in _RESERVED:
                calls.append(name)
    return calls


def _extract_module_name(lines: List[str]) -> str:
    """Find `Attribute VB_Name = "..."` and return the value. Returns
    empty string if absent (`.bas` modules typically have this; `.frm`
    forms always do; standalone `.cls` modules sometimes don't)."""
    for line in lines[:50]:  # property attributes always sit in the first ~30 lines
        m = _ATTRIBUTE_RE.match(line)
        if m and m.group(1).lower() == "vb_name":
            return m.group(2)
    return ""


# ──────────────────────────────────────────────────────────────────────
# .frm property-bag parser
# ──────────────────────────────────────────────────────────────────────


_FRM_BEGIN_RE = re.compile(r"^\s*Begin\s+(\S+)\s+(\w+)\s*$", re.IGNORECASE)
_FRM_END_RE = re.compile(r"^\s*End\s*$", re.IGNORECASE)
_FRM_PROPERTY_RE = re.compile(r"^\s*(\w+)\s*=\s*(.+?)\s*$")
_FRM_ATTRIBUTE_RE = _ATTRIBUTE_RE   # alias — same shape as module attributes


def parse_frm_layout(content: str) -> FormLayout:
    """Parse the property-bag header of a `.frm` file and return a
    structured `FormLayout`. The code block that follows the property
    bag is NOT returned here — pass the same content to `parse_source`
    for the code surface.

    Format (simplified):

        VERSION 5.00
        Begin VB.Form Form1
           Caption         =   "Form1"
           ClientHeight    =   3015
           Begin VB.CommandButton btnOK
              Caption         =   "OK"
           End
        End
        Attribute VB_Name = "Form1"
        Attribute VB_GlobalNameSpace = False
        ...code...
    """
    content = _normalise(content)
    lines = content.split("\n")
    stack: List[FormControl] = []
    root: Optional[FormControl] = None
    attributes: Dict[str, str] = {}
    code_block_start_line = 0

    for idx, raw in enumerate(lines):
        line = raw.rstrip()
        if line.startswith("VERSION "):
            continue
        begin_match = _FRM_BEGIN_RE.match(line)
        if begin_match:
            kind, name = begin_match.group(1), begin_match.group(2)
            ctrl = FormControl(kind=kind, name=name)
            if stack:
                stack[-1].children.append(ctrl)
            else:
                root = ctrl
            stack.append(ctrl)
            continue
        if _FRM_END_RE.match(line):
            if stack:
                stack.pop()
            continue
        if stack:
            prop_match = _FRM_PROPERTY_RE.match(line)
            if prop_match:
                key, val = prop_match.group(1), prop_match.group(2)
                stack[-1].properties[key] = val
                continue
        # After the property bag — look for top-level Attribute lines.
        attr_match = _FRM_ATTRIBUTE_RE.match(line)
        if attr_match:
            attributes[attr_match.group(1)] = attr_match.group(2)
            # The first Attribute line marks the start of the code block.
            if code_block_start_line == 0:
                code_block_start_line = idx + 1   # 1-based
            continue

    return FormLayout(
        root=root,
        attributes=attributes,
        code_block_start_line=code_block_start_line,
    )


def _find_frm_code_start(lines: List[str]) -> int:
    """Return the 0-based line index of the first line after the .frm
    property bag (i.e., after the last `Attribute VB_*` at the top)."""
    last_attribute_idx = 0
    seen_attribute = False
    for idx, line in enumerate(lines):
        if _FRM_ATTRIBUTE_RE.match(line):
            last_attribute_idx = idx
            seen_attribute = True
        elif seen_attribute and line.strip() and not _FRM_ATTRIBUTE_RE.match(line):
            # First non-attribute line after the Attribute block.
            return idx
    return last_attribute_idx + 1 if seen_attribute else 0
