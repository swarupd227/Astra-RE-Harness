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

- VBA as exported from Office documents (see `Office/OfficeVba` in the
  API): ` _` line continuations are joined into logical lines; `:`
  statement separators are split (so one-line routines and `x = 1: End
  Sub` work); `'` inside a string is not a comment; `#If/#ElseIf/#Else/
  #End If` and `#Const` are evaluated for 64-bit Windows Office (VBA7 and
  Win64 true, Mac false, undefined constants false) and the skipped
  branches are blanked, so a routine defined in both arms appears once
  (with a warning saying which environment was assumed); a routine whose
  End is missing or mismatched ends where the next routine begins.
  Measured on 109 real modules (2,325 routines): every routine found,
  no duplicates.

What v0 does NOT handle (deferred to ANTLR4 per ADR-035)
--------------------------------------------------------
- `Declare Sub|Function NAME Lib "kernel32" ...` P/Invoke declarations
  (recorded as warnings, not as routines — they're external bindings).
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


def _strip_comments(line: str) -> str:
    """Strip a VB line comment (`'`, or `REM` at the start of a statement),
    leaving `'` inside a `"..."` string alone (`""` is an escaped quote,
    which toggles twice and so needs no special case)."""
    in_str = False
    for i, c in enumerate(line):
        if c == '"':
            in_str = not in_str
        elif not in_str:
            if c == "'":
                return line[:i].rstrip()
            if (c in "Rr" and line[i:i + 3].upper() == "REM"
                    and (i + 3 == len(line) or line[i + 3] in " \t:")
                    and (line[:i].strip() == "" or line[:i].rstrip().endswith(":"))):
                return line[:i].rstrip()
    return line.rstrip()


def _statements(code: str) -> List[str]:
    """Split a logical line on `:` statement separators outside strings
    (`Dim x: x = 1`, `Property Get W(): W = 1: End Property`). A named
    argument's `:=` is not a separator."""
    parts: List[str] = []
    cur: List[str] = []
    in_str = False
    for i, c in enumerate(code):
        if c == '"':
            in_str = not in_str
        if c == ":" and not in_str and not code.startswith(":=", i):
            parts.append("".join(cur))
            cur = []
            continue
        cur.append(c)
    parts.append("".join(cur))
    return [p.strip() for p in parts if p.strip()]


@dataclass(frozen=True)
class _Logical:
    """One logical line: physical lines joined across ` _` continuations,
    comments stripped. `first`/`last` are 0-based physical line indexes."""
    text: str
    first: int
    last: int


_CONTINUATION_RE = re.compile(r"(?:^|\s)_$")


def _logical_lines(lines: List[str], start: int = 0) -> List[_Logical]:
    out: List[_Logical] = []
    i = start
    while i < len(lines):
        first = i
        parts: List[str] = []
        while True:
            code = _strip_comments(lines[i])
            if _CONTINUATION_RE.search(code) and i + 1 < len(lines):
                parts.append(code[:-1].strip())
                i += 1
                continue
            parts.append(code.strip())
            break
        out.append(_Logical(" ".join(p for p in parts if p), first, i))
        i += 1
    return out


# ──────────────────────────────────────────────────────────────────────
# Conditional compilation — #If / #ElseIf / #Else / #End If / #Const
# ──────────────────────────────────────────────────────────────────────

# The environment the branches are evaluated for: 64-bit Windows Office,
# which is where a migrated workbook was last running. A constant the code
# never defines is Empty in VBA, i.e. False.
_TARGET_CONSTANTS = {"vba7": True, "vba6": True, "win64": True, "win32": True, "mac": False, "win16": False}
_DIRECTIVE_RE = re.compile(r"^\s*#\s*(If|ElseIf|Else|End\s*If|Const)\b(.*)$", re.IGNORECASE)
_CC_TOKEN_RE = re.compile(r"\s*(<>|<=|>=|[()=<>]|&H[0-9A-Fa-f]+|\d+|\w+)")


def _cc_eval(expr: str, consts: Dict[str, object], unknown: set) -> object:
    """Evaluate a compiler-constant expression: True/False, numbers,
    constants, Not/And/Or/Xor, comparisons and parentheses."""
    expr = re.sub(r"\bThen\s*$", "", expr.strip(), flags=re.IGNORECASE)
    toks = [m.group(1) for m in _CC_TOKEN_RE.finditer(expr)]
    pos = 0

    def peek() -> str:
        return toks[pos].lower() if pos < len(toks) else ""

    def take() -> str:
        nonlocal pos
        pos += 1
        return toks[pos - 1]

    def atom() -> object:
        t = take() if pos < len(toks) else "0"
        lt = t.lower()
        if lt == "(":
            v = disj()
            if peek() == ")":
                take()
            return v
        if lt == "not":
            return not bool(atom())
        if lt in ("true", "false"):
            return lt == "true"
        if lt.startswith("&h"):
            return int(lt[2:], 16)
        if t.isdigit():
            return int(t)
        if lt in consts:
            return consts[lt]
        unknown.add(t)
        return False

    def comparison() -> object:
        v = atom()
        while peek() in ("=", "<>", "<", ">", "<=", ">="):
            op = take()
            r = atom()
            v = {"=": v == r, "<>": v != r, "<": v < r, ">": v > r, "<=": v <= r, ">=": v >= r}[op]  # type: ignore[operator]
        return v

    def conj() -> object:
        v = comparison()
        while peek() == "and":
            take()
            r = comparison()
            v = bool(v) and bool(r)
        return v

    def disj() -> object:
        v = conj()
        while peek() in ("or", "xor"):
            op = take().lower()
            r = conj()
            v = (bool(v) or bool(r)) if op == "or" else (bool(v) != bool(r))
        return v

    return disj()


def _apply_conditional_compilation(lines: List[str], warnings: List[str]) -> List[str]:
    """Blank out the lines of every branch the compiler would skip (and the
    directives themselves), keeping line numbers. Without this, both arms of
    `#If VBA7 Then ... #Else ... #End If` are parsed and a routine defined in
    each comes out twice."""
    consts: Dict[str, object] = dict(_TARGET_CONSTANTS)
    unknown: set = set()
    stack: List[List[bool]] = []      # [parent_active, branch_taken]
    active = True
    seen = False
    out: List[str] = []
    for raw in lines:
        m = _DIRECTIVE_RE.match(_strip_comments(raw))
        if not m:
            out.append(raw if active else "")
            continue
        seen = True
        out.append("")
        kw = re.sub(r"\s+", "", m.group(1)).lower()
        rest = m.group(2)
        if kw == "const":
            if active and "=" in rest:
                name, value = rest.split("=", 1)
                consts[name.strip().lower()] = _cc_eval(value, consts, unknown)
        elif kw == "if":
            cond = bool(_cc_eval(rest, consts, unknown))
            stack.append([active, cond])
            active = active and cond
        elif kw == "elseif" and stack:
            parent, taken = stack[-1]
            if taken:
                active = False
            else:
                cond = bool(_cc_eval(rest, consts, unknown))
                stack[-1][1] = cond
                active = parent and cond
        elif kw == "else" and stack:
            parent, taken = stack[-1]
            active = parent and not taken
            stack[-1][1] = True
        elif kw == "endif" and stack:
            active = stack.pop()[0]
    if seen:
        warnings.append("vb6: conditional compilation evaluated for 64-bit Windows Office "
                        "(VBA7, Win64 true; Mac false); the other branches were skipped")
    if unknown:
        warnings.append("vb6: undefined compiler constant(s) treated as False: " + ", ".join(sorted(unknown)))
    if stack:
        warnings.append("vb6: #If without a matching #End If")
    return out


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

# A sub call written without brackets: `Name` or `Name arg, arg` as a whole statement. Not an assignment
# (`x = 1`, `Name = result` inside a function), a member access (`ws.Range`), a label (`Done:`) or `Name(…)`
# (the bracketed form is handled above).
_BARE_SUB_CALL_RE = re.compile(r"^\s*([A-Za-z_]\w*)(?:\s*$|\s+(?![=\s])(?!(?:As|To|Then)\b)[^=]*$)")
# Statements and runtime procedures that look like a bare call but are not a routine of the project.
_STATEMENT_WORDS = frozenset({
    "exit", "goto", "gosub", "on", "resume", "return", "stop", "else", "elseif", "option", "attribute",
    "call", "static", "global", "implements", "event", "raiseevent", "declare", "deftype", "erase", "beep",
    "randomize", "msgbox", "debug", "load", "unload", "doevents", "line", "get", "put", "seek", "lock",
    "unlock", "reset", "chdir", "chdrive", "mkdir", "name", "filecopy", "date", "time", "sendkeys", "appactivate",
    "savesetting", "deletesetting", "mid", "lset", "rset", "width", "not", "and", "or", "new", "each", "in",
    "error", "private", "optional", "byval", "byref", "paramarray",
})
_WORD_RE = re.compile(r"[A-Za-z_]\w*")


def _strip_strings(line: str) -> str:
    """Blank out `"..."` string literals so a name inside a message is not read as a call."""
    return re.sub(r'"(?:[^"]|"")*"', '""', line)


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

    # Walk the code block for routine declarations, one logical line at a
    # time (continuations joined, skipped #If branches blanked).
    code_lines = _apply_conditional_compilation(lines, warnings)
    logical = _logical_lines(code_lines, code_block_start)
    subroutines: List[SubroutineSummary] = []
    pending: List[Tuple[str, str, int, int, List[str], List[str]]] = []
    declare_routines: List[str] = []      # P/Invoke declarations — flagged as warnings
    j = 0
    while j < len(logical):
        stmts = _statements(logical[j].text)
        stripped = stmts[0] if stmts else ""

        # P/Invoke declarations — no body to walk into.
        declare_match = _DECLARE_RE.match(stripped)
        if declare_match:
            kind, name, lib = declare_match.groups()
            declare_routines.append(f"{name} (Lib \"{lib}\")")
            j += 1
            continue

        sub_match = _SUB_DECL_RE.match(stripped)
        func_match = _FUNCTION_DECL_RE.match(stripped) if not sub_match else None
        prop_match = _PROPERTY_DECL_RE.match(stripped) if not sub_match and not func_match else None

        if sub_match or func_match or prop_match:
            line_start = logical[j].first + 1     # 1-based physical line of the declaration
            signature = stripped
            if sub_match:
                name = sub_match.group(1)
                end_re = _END_SUB_RE
            elif func_match:
                name = func_match.group(1)
                end_re = _END_FUNCTION_RE
            else:
                accessor = prop_match.group(1)
                base_name = prop_match.group(2)
                name = f"{base_name} ({accessor})"
                end_re = _END_PROPERTY_RE

            # A one-line routine ends on its own line (`...: End Property`);
            # otherwise walk forward to the matching End statement.
            rest = stmts[1:]
            end_at = next((k for k, s in enumerate(rest) if end_re.match(s)), None)
            if end_at is not None:
                line_end, body, next_j = logical[j].last + 1, rest[:end_at], j + 1
            else:
                line_end, body, next_j, closed = _consume_routine_body(logical, j + 1, end_re, rest)
                if not closed:
                    warnings.append(f"vb6: routine '{name}' (line {line_start}) has no matching End statement; "
                                    f"it was ended where the next routine begins (or at the end of the file)")
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

            pending.append((name, signature, line_start, line_end, calls, body))
            j = next_j
            continue
        j += 1

    # VBA calls a routine without brackets (`ValidateInputs ws`) and uses a function inside an expression
    # (`s = BuildRequestJson(ws)`, `If Not IsYes(v) Then`); the start-of-statement patterns above see neither.
    # A routine declared in this module that another routine's code names (outside strings and comments) is a
    # call — the name is known to be a routine, so no builtin or worksheet member is ever mistaken for one.
    local_names = {p[0] for p in pending if " (" not in p[0]}
    for name, signature, line_start, line_end, calls, body in pending:
        found = set(calls)
        for raw in body:
            code = _strip_strings(_strip_comments(raw))
            if not code:
                continue
            for word in _WORD_RE.findall(code):
                if word in local_names and word != name:
                    found.add(word)
            bare = _BARE_SUB_CALL_RE.match(code)
            # A lone word may be a line label (`Done:` is split off its colon), so it counts only when it
            # names a routine of this module; a word followed by arguments cannot be a label.
            if (bare and bare.group(1).lower() not in _RESERVED and bare.group(1).lower() not in _STATEMENT_WORDS
                    and (code.strip() != bare.group(1) or bare.group(1) in local_names)):
                found.add(bare.group(1))
        found = {c for c in found if c.lower() != name.lower()}
        subroutines.append(SubroutineSummary(
            name=name,
            signature=signature,
            line_start=line_start,
            line_end=line_end,
            common_block_refs=(module_name,) if module_name else tuple(),
            called_subroutines=tuple(sorted(found)),
        ))

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


def _consume_routine_body(
    logical: List[_Logical], start: int, end_re: re.Pattern, carried: List[str]
) -> Tuple[int, List[str], int, bool]:
    """Walk forward from logical line `start` until a statement matches the
    End statement (it may follow a colon: `x = 1: End Sub`). Returns the
    1-based physical line of the End, the body statements, the index of the
    next logical line, and whether a matching End was found."""
    body: List[str] = list(carried)
    j = start
    while j < len(logical):
        stmts = _statements(logical[j].text)
        # Routines do not nest: a new declaration means this one's End was
        # missing or mismatched (`Property Get … End Function`), so it ends
        # on the line before rather than swallowing the routines after it.
        if stmts and j > start and (_SUB_DECL_RE.match(stmts[0]) or _FUNCTION_DECL_RE.match(stmts[0])
                                    or _PROPERTY_DECL_RE.match(stmts[0])):
            return logical[j - 1].last + 1, body, j, False
        for s in stmts:
            if end_re.match(s):
                return logical[j].last + 1, body, j + 1, True
            body.append(s)
        j += 1
    # Unterminated routine — run to EOF, as before.
    last = logical[-1].last + 1 if logical else 0
    return last, body, len(logical), False


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
