"""
Astra parser sidecar — Progress OpenEdge ABL (.p / .w / .i) structural parser.

**Phase 13.0 (v0).** A focused tokenizer-based parser for Progress OpenEdge
ABL (the 4GL, formerly Progress 4GL). Recognises the structural surface a
legacy Progress line-of-business app exposes: internal PROCEDUREs, FUNCTIONs,
OO METHODs / CONSTRUCTORs, CLASS containers, the file-level main block, and
RUN / {include} reference sites. Produces the canonical `ParseOutcome` shape
shared with every other language parser.

Why this matters
-----------------
OpenEdge is the client's Tier-2 "do-urgently" technology: the pool of ABL
developers is retiring faster than it is being replaced, so the immediate
goal is to *document* these systems before the institutional knowledge walks
out the door. That means the parser only has to surface the routine
catalogue + call graph the documentation pipeline reads — not a
migration-grade AST.

What v0 handles
---------------
- `.p` (procedure/program), `.w` (SmartWindow), `.i` (include) files.
- Nestable `/* ... */` block comments (ABL genuinely nests them) and `//`
  line comments, with string-literal awareness so a `/*` inside `"..."` is
  not treated as a comment.
- `PROCEDURE name:` … `END [PROCEDURE].` internal procedures.
- `FUNCTION name RETURNS type (…):` … `END [FUNCTION].` (FORWARD
  declarations are skipped — they have no body).
- `METHOD [access] [mods] type name(…):` … `END [METHOD].`,
  `CONSTRUCTOR`/`DESTRUCTOR` blocks.
- `CLASS name:` / `INTERFACE name:` container tracking (a routine's
  `common_block_refs` carries its enclosing class).
- The file-level **main block** — for a `.p`/`.w` whose top-level code sits
  outside any named routine, a synthetic routine covering that span is
  emitted so the file is never documented as empty.
- `RUN target[.p] [IN h] [PERSISTENT]`, `DYNAMIC-FUNCTION("name")` call
  sites and `{include.i}` references for the dependency graph.

What v0 does NOT handle (deferred)
----------------------------------
- OO `.cls` class files — the `.cls` extension collides with VB6 class
  modules; disambiguation needs content sniffing, tracked separately. The
  bulk of the talent-extinction risk is procedural `.p`/`.w`, which this
  parser covers.
- Multi-line routine headers (a PROCEDURE/FUNCTION header split across
  physical lines). v0 keys off the first line of the header.
- Preprocessor `{&NAME}` expansion and `&IF … &THEN` conditional blocks —
  processed literally, all branches kept.
- Block-depth tracking uses a heuristic (a header line ends in `:`, every
  `END.` closes one level); deeply pathological nesting may mis-range a
  routine end, which the SME corrects at review.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import List, Tuple

log = logging.getLogger("astra.parser.abl")


# ──────────────────────────────────────────────────────────────────────
# Output shape — identical to every other parser / the proto
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
# Comment stripping — nestable /* */, // line, string-aware.
# Replaces comment characters with spaces so line numbers are preserved.
# ──────────────────────────────────────────────────────────────────────


def _normalise(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


def _strip_comments(content: str) -> str:
    out: List[str] = []
    i, n = 0, len(content)
    depth = 0          # nested /* */ depth
    in_str: str | None = None
    while i < n:
        c = content[i]
        nxt = content[i + 1] if i + 1 < n else ""
        if depth > 0:
            if c == "/" and nxt == "*":
                depth += 1; out.append("  "); i += 2; continue
            if c == "*" and nxt == "/":
                depth -= 1; out.append("  "); i += 2; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if in_str is not None:
            out.append(c)
            if c == in_str:
                in_str = None
            i += 1; continue
        if c in ('"', "'"):
            in_str = c; out.append(c); i += 1; continue
        if c == "/" and nxt == "*":
            depth += 1; out.append("  "); i += 2; continue
        if c == "/" and nxt == "/":
            while i < n and content[i] != "\n":
                out.append(" "); i += 1
            continue
        out.append(c); i += 1
    return "".join(out)


# ──────────────────────────────────────────────────────────────────────
# Declaration grammar (ABL keywords are case-insensitive)
# ──────────────────────────────────────────────────────────────────────

_PROC_RE = re.compile(r"^\s*PROCEDURE\s+([\w\-]+)", re.IGNORECASE)
_FUNC_RE = re.compile(r"^\s*FUNCTION\s+([\w\-]+)\s+RETURNS?\b", re.IGNORECASE)
_METHOD_RE = re.compile(r"^\s*METHOD\s+.*?\b([\w\-]+)\s*\(", re.IGNORECASE)
_CTOR_RE = re.compile(
    r"^\s*(CONSTRUCTOR|DESTRUCTOR)\s+(?:(?:PUBLIC|PROTECTED|PRIVATE|STATIC)\s+)*([\w\-]+)\s*\(",
    re.IGNORECASE,
)
_CLASS_RE = re.compile(r"^\s*(?:CLASS|INTERFACE)\s+([\w\-\.]+)", re.IGNORECASE)
_END_CLASS_RE = re.compile(r"^\s*END\s+(?:CLASS|INTERFACE)\b", re.IGNORECASE)
_END_RE = re.compile(r"^\s*END\b", re.IGNORECASE)
_FORWARD_RE = re.compile(r"\bFORWARD\s*\.", re.IGNORECASE)

_RUN_RE = re.compile(r"\bRUN\s+(?:VALUE\s*\(\s*[\"']?)?([\w\-][\w\-\.\/\\]*)", re.IGNORECASE)
_DYNFUNC_RE = re.compile(r"\bDYNAMIC-FUNCTION\s*\(\s*[\"']([\w\-]+)", re.IGNORECASE)
_INCLUDE_RE = re.compile(r"\{([\w\-][\w\-\.\/\\]*\.i)\b", re.IGNORECASE)

# Keywords whose presence in the pre-block region signals real top-level
# logic worth emitting as a main-block routine.
_MAIN_BLOCK_HINT = re.compile(
    r"\b(RUN|FIND|FOR\s+EACH|DISPLAY|MESSAGE|ASSIGN|CREATE|UPDATE|DELETE|"
    r"OUTPUT\s+TO|INPUT\s+FROM|REPEAT|DO\b|IF\b)\b",
    re.IGNORECASE,
)


def _module_of(filename: str) -> str:
    return PurePosixPath(filename.replace("\\", "/")).stem


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single ABL source file and return a `ParseOutcome`."""
    content = _normalise(content)
    raw_lines = content.split("\n")
    cleaned = _strip_comments(content).split("\n")
    line_count = len(raw_lines)
    warnings: List[str] = []

    subroutines: List[SubroutineSummary] = []
    class_stack: List[str] = []
    includes_seen: List[str] = []
    first_named_start: int | None = None

    i = 0
    while i < len(cleaned):
        line = cleaned[i]

        # Class / interface container push & pop.
        cls = _CLASS_RE.match(line)
        if cls and line.rstrip().endswith(":"):
            class_stack.append(cls.group(1))
            i += 1
            continue
        if _END_CLASS_RE.match(line):
            if class_stack:
                class_stack.pop()
            i += 1
            continue

        enclosing = class_stack[-1] if class_stack else ""

        proc = _PROC_RE.match(line)
        func = _FUNC_RE.match(line) if not proc else None
        meth = _METHOD_RE.match(line) if not proc and not func else None
        ctor = _CTOR_RE.match(line) if not proc and not func and not meth else None

        if proc or func or meth or ctor:
            # FUNCTION … FORWARD. is a prototype with no body — skip it.
            if func and _FORWARD_RE.search(line):
                i += 1
                continue

            if proc:
                name, kind = proc.group(1), "PROCEDURE"
            elif func:
                name, kind = func.group(1), "FUNCTION"
            elif meth:
                name, kind = meth.group(1), "METHOD"
            else:
                name, kind = ctor.group(2), ctor.group(1).upper()

            line_start = i + 1
            if first_named_start is None:
                first_named_start = line_start
            end_idx = _consume_block(cleaned, i)
            body = cleaned[i + 1:end_idx]
            calls, incs = _collect_refs(body)
            includes_seen.extend(incs)

            refs: List[str] = []
            if enclosing:
                refs.append(enclosing)
            refs.extend(incs)

            subroutines.append(SubroutineSummary(
                name=name if kind != "DESTRUCTOR" else f"{name} (destructor)",
                signature=_trim(raw_lines[i]),
                line_start=line_start,
                line_end=end_idx + 1,
                common_block_refs=tuple(dict.fromkeys(refs)),
                called_subroutines=tuple(sorted(set(calls))),
            ))
            i = end_idx + 1
            continue

        i += 1

    # File-level main block. If the file has no named routine, the whole file
    # IS the external procedure. If it has named routines but real top-level
    # logic precedes the first one, emit that span as the main block.
    module = _module_of(filename)
    if not subroutines:
        pre_calls, pre_incs = _collect_refs(cleaned)
        subroutines.append(SubroutineSummary(
            name=f"{module} (main block)",
            signature=f"External procedure {module}",
            line_start=1,
            line_end=line_count,
            common_block_refs=tuple(dict.fromkeys(pre_incs)),
            called_subroutines=tuple(sorted(set(pre_calls))),
        ))
    elif first_named_start and first_named_start > 1:
        head = cleaned[: first_named_start - 1]
        if any(_MAIN_BLOCK_HINT.search(l) for l in head):
            pre_calls, pre_incs = _collect_refs(head)
            subroutines.insert(0, SubroutineSummary(
                name=f"{module} (main block)",
                signature=f"Main block of {module}",
                line_start=1,
                line_end=first_named_start - 1,
                common_block_refs=tuple(dict.fromkeys(pre_incs)),
                called_subroutines=tuple(sorted(set(pre_calls))),
            ))

    if includes_seen:
        uniq = list(dict.fromkeys(includes_seen))
        shown = ", ".join(uniq[:8])
        warnings.append(
            f"abl: file references {len(uniq)} include(s): {shown}"
            + (" ..." if len(uniq) > 8 else "")
        )

    return ParseOutcome(
        line_count=line_count,
        subroutines=subroutines,
        warnings=warnings,
        filename=filename,
    )


def _trim(raw: str) -> str:
    return raw.strip()


def _consume_block(cleaned: List[str], header_idx: int) -> int:
    """Walk from the header line (0-based `header_idx`) to the `END` that
    closes it. Returns the 0-based index of that END line.

    Depth heuristic: the routine header opens one block (depth 1); every
    subsequent line whose last non-blank character is `:` opens a nested
    block, and every `END` statement closes one. When depth returns to 0 the
    matching END has been found."""
    depth = 1
    i = header_idx + 1
    while i < len(cleaned):
        line = cleaned[i]
        if _END_RE.match(line):
            depth -= 1
            if depth == 0:
                return i
            i += 1
            continue
        if line.rstrip().endswith(":"):
            depth += 1
        i += 1
    return len(cleaned) - 1


def _collect_refs(body: List[str]) -> Tuple[List[str], List[str]]:
    """Collect (called routines/programs, include references) from a body."""
    calls: List[str] = []
    includes: List[str] = []
    for line in body:
        for m in _RUN_RE.finditer(line):
            target = m.group(1).rstrip(".")
            # Skip the ABL control-flow verbs that begin with RUN-* internally
            # (there are none) — but drop empty captures defensively.
            if target and target.upper() not in ("VALUE",):
                calls.append(target)
        for m in _DYNFUNC_RE.finditer(line):
            calls.append(m.group(1))
        for m in _INCLUDE_RE.finditer(line):
            includes.append(m.group(1))
    return calls, includes
