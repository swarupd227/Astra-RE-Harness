"""
Astra parser sidecar — UniBasic (Rocket UniData / UniVerse "Pick BASIC") structural parser.

**Feasibility POC (2026-07).** A focused structural parser for UniBasic, the
procedural language that runs *inside* Rocket's MultiValue databases (UniData
and its sibling UniVerse). Built and calibrated against six real, public
UniBasic/Pick-BASIC source files (github.com/zelenko/pick — a UniVerse-backed
Eclipse ERP customization) rather than from memory, so the grammar below
reflects genuinely observed shop conventions, not textbook syntax.

Why one file = one routine
---------------------------
Unlike languages with a `FUNCTION name(...)` header, UniBasic's `SUBROUTINE
(params)` declaration carries NO NAME — only a parameter list. A cataloged
BASIC program's identity comes from its catalog/file name, not a keyword in
its own source (confirmed against the one real `SUBROUTINE (...)` example in
the calibration corpus, VZ.SALES.BR.pick — no name follows the keyword). Every
one of the six real files is a single cataloged program: either the formal
`SUBROUTINE (params)` form (externally callable, returns via output params) or
— far more common in the wild — a bare top-level script with no header at all,
executed as the program's implicit MAIN block. This mirrors exactly how this
platform already treats Fortran subroutines, COBOL PROGRAM-IDs, and ABL's
main-block synthesis: one file, one routine. So this parser ALWAYS emits
exactly one `SubroutineSummary` per file, named from the filename (the
catalog-name convention), with the `PGM = "..."` assignment (a shop
convention, not a language keyword) captured into the signature when present.

What this parser handles (calibrated against real code)
---------------------------------------------------------
- Comment stripping: full-line `*`/`***`/`!` comments (the calibration shop
  uses `!` as a de-facto comment prefix for both commented-out code and a
  trailing `!user~date~time` check-in stamp — NOT a universal UniBasic
  standard, a shop convention; flagged as a dialect item to verify against the
  client's actual source) and inline `;*` trailing comments.
  String-literal-aware (both `'` and `"` quoting are used, including one
  quote style nested inside the other in `EXECUTE`-bound query strings).
- `SUBROUTINE (params)` — the formal, externally-callable declaration.
- `FUNCTION name (params)` — the named-function form documented for the
  UniBasic dialect family (not observed in the small calibration sample, kept
  for robustness since it's real and well-documented in the language).
- Internal `LABEL:` GOSUB paragraphs — collected as informational internal
  sections (surfaced via a warning), not separate top-level routines — the
  same treatment this platform gives COBOL paragraphs/sections.
- Real inter-program call forms, captured as `called_subroutines`:
    `CALL name(...)` / `CALL @var(...)` (the `@`-prefixed indirect/dynamic
    form can't be statically resolved to a target — captured as `@var` and
    flagged, since dataflow analysis would be needed to resolve it for real)
    `SUBR('name', ...)` — the function-call-style invocation form.
  `GOSUB label` is explicitly EXCLUDED from called_subroutines (internal
  control flow within the same program, like a COBOL PERFORM to a local
  paragraph) — collected as an internal-paragraph warning instead.

What this parser does NOT handle (deferred — the honest gap)
---------------------------------------------------------------
- No dataflow resolution of `@var`-indirect CALL targets or non-literal
  SUBR(...) targets — captured as-observed, not resolved.
- No data-dictionary (DICT) lookups to translate a numbered field position
  (`READV x FROM file,id,9`) to a business-meaningful attribute name — the
  numbered position is exactly what's captured; resolving it to a real name
  needs the client's actual DICT definitions.
- Record-locking verbs (READU/LOCK/RELEASE) are not exercised by the small
  calibration sample and so are not specially recognised here — a documented
  gap a real client engagement would need to validate against.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import List, Tuple

log = logging.getLogger("astra.parser.unibasic")


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
# Comment stripping — full-line */!, inline ;* , string-aware ('/").
# Replaces comment/string characters with spaces so line numbers and
# column positions of remaining tokens are preserved.
# ──────────────────────────────────────────────────────────────────────


def _normalise(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


def _strip_comments(content: str) -> str:
    lines = content.split("\n")
    out_lines: List[str] = []
    for line in lines:
        stripped = line.lstrip()
        # Full-line comment: leading * (any run of *) or ! (shop convention;
        # see module docstring) — blank it entirely, preserving the line.
        if stripped.startswith("*") or stripped.startswith("!"):
            out_lines.append("")
            continue
        out_lines.append(_strip_inline(line))
    return "\n".join(out_lines)


def _strip_inline(line: str) -> str:
    """Strip a trailing `;*` inline comment, respecting string literals."""
    i, n = 0, len(line)
    in_str: str | None = None
    while i < n:
        c = line[i]
        if in_str is not None:
            if c == in_str:
                in_str = None
            i += 1
            continue
        if c in ("'", '"'):
            in_str = c
            i += 1
            continue
        if c == ";" and i + 1 < n and line[i + 1] == "*":
            return line[:i]
        i += 1
    return line


# ──────────────────────────────────────────────────────────────────────
# Declaration / call grammar
# ──────────────────────────────────────────────────────────────────────

_SUBROUTINE_HDR_RE = re.compile(r"^\s*SUBROUTINE\s*\(([^)]*)\)", re.IGNORECASE)
_FUNCTION_HDR_RE = re.compile(r"^\s*FUNCTION\s+([\w.\-]+)\s*\(([^)]*)\)", re.IGNORECASE)
_PGM_RE = re.compile(r"^\s*PGM\s*=\s*['\"]([\w.\-]+)['\"]", re.IGNORECASE)
_LABEL_RE = re.compile(r"^\s*([A-Za-z][\w.\-]*)\s*:(?!=)")

_CALL_RE = re.compile(r"\bCALL\s+(@?[\w.\-]+)\s*\(", re.IGNORECASE)
_SUBR_RE = re.compile(r"\bSUBR\s*\(\s*['\"]([\w.\-]+)['\"]", re.IGNORECASE)
_GOSUB_RE = re.compile(r"\bGOSUB\s+([\w.\-]+)", re.IGNORECASE)

# Reserved words that can precede ':' without being a GOSUB-paragraph label
# (e.g. a CASE/END-of-block token, or a dynamic-array subscript expression
# closing paren followed by a colon in some formatting styles).
_LABEL_STOPWORDS = {"END", "THEN", "ELSE", "CASE"}


def _module_of(filename: str) -> str:
    return PurePosixPath(filename.replace("\\", "/")).stem


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single UniBasic source file and return a `ParseOutcome`.

    Always emits exactly one `SubroutineSummary` spanning the whole file —
    see the module docstring for why (a cataloged BASIC program has no
    in-source name, and internal GOSUB paragraphs are not separate units)."""
    content = _normalise(content)
    raw_lines = content.split("\n")
    cleaned = _strip_comments(content).split("\n")
    line_count = len(raw_lines)
    warnings: List[str] = []

    module = _module_of(filename)

    header_params: str | None = None
    header_kind = ""
    pgm_name: str | None = None
    labels: List[str] = []
    calls: List[str] = []

    for line in cleaned:
        if header_params is None:
            sub = _SUBROUTINE_HDR_RE.match(line)
            if sub:
                header_params, header_kind = sub.group(1).strip(), "SUBROUTINE"
                continue
            func = _FUNCTION_HDR_RE.match(line)
            if func:
                header_params, header_kind = func.group(2).strip(), "FUNCTION"
                continue

        if pgm_name is None:
            pgm = _PGM_RE.match(line)
            if pgm:
                pgm_name = pgm.group(1)

        lbl = _LABEL_RE.match(line)
        if lbl and lbl.group(1).upper() not in _LABEL_STOPWORDS:
            labels.append(lbl.group(1))

        for m in _CALL_RE.finditer(line):
            calls.append(m.group(1))
        for m in _SUBR_RE.finditer(line):
            calls.append(m.group(1))
        # GOSUB targets are internal paragraphs, not inter-program calls —
        # intentionally excluded from called_subroutines.

    if header_kind == "SUBROUTINE":
        signature = f"SUBROUTINE {module}({header_params})"
    elif header_kind == "FUNCTION":
        signature = f"FUNCTION {module}({header_params})"
    else:
        signature = f"{module} (program)"
    if pgm_name and pgm_name != module:
        signature += f"  [PGM=\"{pgm_name}\"]"

    dynamic_calls = [c for c in calls if c.startswith("@")]
    if dynamic_calls:
        uniq = list(dict.fromkeys(dynamic_calls))
        warnings.append(
            "unibasic: "
            + f"{len(uniq)} dynamically-dispatched call target(s) captured as-written, "
            + f"not statically resolved: {', '.join(uniq[:6])}"
            + (" ..." if len(uniq) > 6 else "")
        )

    if labels:
        uniq_labels = list(dict.fromkeys(labels))
        warnings.append(
            f"unibasic: {len(uniq_labels)} internal GOSUB paragraph(s) (not separate routines): "
            + ", ".join(uniq_labels[:8])
            + (" ..." if len(uniq_labels) > 8 else "")
        )

    subroutines = [
        SubroutineSummary(
            name=module,
            signature=signature,
            line_start=1,
            line_end=line_count,
            common_block_refs=tuple(),
            called_subroutines=tuple(sorted(set(calls))),
        )
    ]

    return ParseOutcome(
        line_count=line_count,
        subroutines=subroutines,
        warnings=warnings,
        filename=filename,
    )
