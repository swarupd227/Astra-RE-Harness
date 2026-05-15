"""
Astra parser sidecar — fparser2-backed Fortran parser.

Extracts subroutine inventory + lightweight cross-references (COMMON
block refs, CALL graph) from a single source file. Designed for the
mixed F77 / F90+ codebases that show up in Kiwiplan-style corpora:

  - Files with `.f`, `.f77`, `.for`, `.FOR` extensions parse in fixed form.
  - Files with `.f90`, `.f95`, `.f03`, `.f08` parse in free form.
  - Anything else: caller must override via `form="fixed"|"free"`.

Returns a `ParseOutcome` dataclass — the gRPC servicer maps that to
`ParseResult` so this module stays import-light and unit-testable.
"""

from __future__ import annotations

import io
import logging
import os
import re
from dataclasses import dataclass, field
from typing import Iterable, List, Optional

from fparser.common.readfortran import FortranStringReader
from fparser.common.sourceinfo import FortranFormat
from fparser.two import Fortran2003
from fparser.two.parser import ParserFactory
from fparser.two.utils import walk, FortranSyntaxError

log = logging.getLogger("astra.parser.fortran")


@dataclass(frozen=True)
class SubroutineSummary:
    name: str
    signature: str
    line_start: int
    line_end: int
    common_block_refs: tuple[str, ...]
    called_subroutines: tuple[str, ...]


@dataclass
class ParseOutcome:
    filename: str
    line_count: int
    subroutines: List[SubroutineSummary] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)


# ────────────────────────────────────────────────────────────────────
# Form detection
# ────────────────────────────────────────────────────────────────────

_FIXED_FORM_EXTS = {".f", ".f77", ".for", ".fpp", ".ftn"}
_FREE_FORM_EXTS = {".f90", ".f95", ".f03", ".f08", ".f15", ".f18"}


def detect_form(filename: str) -> str:
    """Return "fixed" or "free" based on the filename extension."""
    ext = os.path.splitext(filename)[1].lower()
    if ext in _FIXED_FORM_EXTS:
        return "fixed"
    if ext in _FREE_FORM_EXTS:
        return "free"
    # Unknown — default to free, the modern Fortran form.
    return "free"


# ────────────────────────────────────────────────────────────────────
# Top-level parse entry point
# ────────────────────────────────────────────────────────────────────


def parse_source(content: str, filename: str = "<inline>.f90", form: str = "") -> ParseOutcome:
    """
    Parse a single Fortran source file and return its subroutine summary.

    `form` overrides extension-based detection if non-empty. Parse errors
    are caught and surfaced as warnings — callers receive an outcome with
    `subroutines=[]` rather than an exception, so a malformed file does
    not poison a whole corpus ingest.
    """
    text = content if content.endswith("\n") else content + "\n"
    line_count = text.count("\n")

    chosen_form = (form or detect_form(filename)).lower()
    is_free = chosen_form != "fixed"

    outcome = ParseOutcome(filename=filename, line_count=line_count)

    # We compile lines once for line-anchored signature reconstruction —
    # fparser2's `item.span` is reliable for the SUBROUTINE..END block,
    # but the original signature line (with continuation) is easier to
    # render verbatim from the source.
    lines = text.splitlines()

    try:
        reader = FortranStringReader(
            text,
            ignore_comments=False,
            include_dirs=None,
        )
        # FortranStringReader defaults to non-strict fixed; pin the form
        # explicitly so .f90 doesn't get treated as F77 and vice versa.
        reader.set_format(FortranFormat(is_free, True))
    except Exception as ex:  # pragma: no cover — defensive
        outcome.warnings.append(f"reader_init_failed: {type(ex).__name__}: {ex}")
        return outcome

    try:
        parser = ParserFactory().create(std="f2008")
        tree = parser(reader)
    except FortranSyntaxError as ex:
        outcome.warnings.append(f"syntax_error: {ex}")
        return outcome
    except Exception as ex:
        outcome.warnings.append(f"parse_failed: {type(ex).__name__}: {ex}")
        return outcome

    if tree is None:
        outcome.warnings.append("parse_returned_none")
        return outcome

    for node in walk(tree, Fortran2003.Subroutine_Subprogram):
        try:
            summary = _summarise_subroutine(node, lines)
            if summary is not None:
                outcome.subroutines.append(summary)
        except Exception as ex:  # pragma: no cover — defensive
            outcome.warnings.append(f"summarise_failed: {type(ex).__name__}: {ex}")

    if not outcome.subroutines:
        # Fall back to a regex sweep so a parse-resistant fixed-form F77
        # file still yields *something* the API can list. Heuristic; we
        # tag the result with a warning so reviewers know it's degraded.
        regex_subs = _regex_fallback(lines)
        if regex_subs:
            outcome.subroutines.extend(regex_subs)
            outcome.warnings.append(
                f"fparser2_produced_no_subroutines; fell back to regex scan "
                f"({len(regex_subs)} match{'es' if len(regex_subs) != 1 else ''})"
            )

    return outcome


# ────────────────────────────────────────────────────────────────────
# Per-subroutine summarisation
# ────────────────────────────────────────────────────────────────────


def _summarise_subroutine(node: Fortran2003.Subroutine_Subprogram, lines: List[str]) -> Optional[SubroutineSummary]:
    # SubroutineStmt is the first child; EndSubroutineStmt the last.
    sub_stmt = node.children[0]
    end_stmt = node.children[-1]

    name = _subroutine_name(sub_stmt)
    if not name:
        return None

    line_start = _span_start(sub_stmt)
    line_end = _span_end(end_stmt) or _span_start(end_stmt) or line_start

    signature = _render_signature(lines, line_start)

    commons = sorted({c.upper() for c in _walk_common_block_refs(node)})
    calls = sorted({c.upper() for c in _walk_called_subroutines(node)})

    return SubroutineSummary(
        name=name.upper(),
        signature=signature,
        line_start=line_start,
        line_end=line_end,
        common_block_refs=tuple(commons),
        called_subroutines=tuple(calls),
    )


def _subroutine_name(sub_stmt: Fortran2003.Subroutine_Stmt) -> str:
    # SubroutineStmt children: ['SUBROUTINE', Name, Dummy_Arg_List|None, ...]
    for child in sub_stmt.children:
        if isinstance(child, Fortran2003.Name):
            return str(child)
    # Fallback: stringify and slice off "SUBROUTINE "
    s = str(sub_stmt).strip()
    if s.upper().startswith("SUBROUTINE"):
        rest = s[len("SUBROUTINE"):].strip()
        paren = rest.find("(")
        return (rest[:paren] if paren > 0 else rest).strip()
    return ""


def _render_signature(lines: List[str], line_start: int) -> str:
    """Reconstruct the SUBROUTINE declaration verbatim, joining continuations.

    F77 fixed-form continues a line by putting any non-space, non-zero char
    in column 6 of the next line. F90+ free-form continues by trailing `&`.
    We handle both heuristically since we already know we're on a SUBROUTINE
    statement.
    """
    if line_start < 1 or line_start > len(lines):
        return ""
    parts: List[str] = []
    i = line_start - 1
    while i < len(lines):
        raw = lines[i].rstrip()
        # Strip free-form continuation marker
        stripped = raw.rstrip()
        if stripped.endswith("&"):
            parts.append(stripped[:-1].rstrip())
            i += 1
            continue
        parts.append(stripped)
        # Look ahead for fixed-form continuation
        if i + 1 < len(lines):
            nxt = lines[i + 1]
            if len(nxt) >= 6 and nxt[:5].strip() == "" and nxt[5] not in (" ", "0"):
                # Continuation line — content starts at column 7 (index 6)
                i += 1
                continue
        break
    sig = " ".join(p.strip() for p in parts if p.strip())
    # Trim the leading SUBROUTINE keyword's surrounding whitespace artifacts
    return re.sub(r"\s+", " ", sig)


def _span_start(stmt) -> int:
    span = getattr(stmt, "item", None)
    if span is not None and getattr(span, "span", None) is not None:
        return int(span.span[0])
    return 0


def _span_end(stmt) -> int:
    span = getattr(stmt, "item", None)
    if span is not None and getattr(span, "span", None) is not None:
        return int(span.span[1])
    return 0


def _walk_common_block_refs(node) -> Iterable[str]:
    """Yield COMMON block names appearing inside the subroutine.

    Common_Stmt.items[0] is a list of (Name|None, Common_Block_Object_List)
    tuples. The first element is the block name (None for unnamed COMMON).
    """
    for c in walk(node, Fortran2003.Common_Stmt):
        first = c.items[0] if c.items else None
        if not first:
            continue
        for entry in first:
            if isinstance(entry, tuple) and len(entry) >= 1 and entry[0] is not None:
                yield str(entry[0]).strip("/")


def _walk_called_subroutines(node) -> Iterable[str]:
    """Yield names appearing in CALL statements inside the subroutine.

    Call_Stmt.items[0] is the procedure designator (a Name or
    Procedure_Designator). items[1] is the actual-arg list.
    """
    for call in walk(node, Fortran2003.Call_Stmt):
        if not call.items:
            continue
        designator = call.items[0]
        if designator is None:
            continue
        yield str(designator).strip()


# ────────────────────────────────────────────────────────────────────
# Regex fallback (last-resort F77 ingest path)
# ────────────────────────────────────────────────────────────────────

_SUB_RE = re.compile(r"^\s*SUBROUTINE\s+([A-Z_][A-Z0-9_]*)\s*(\([^)]*\))?", re.IGNORECASE)
_END_RE = re.compile(r"^\s*END(?:\s+SUBROUTINE(?:\s+([A-Z_][A-Z0-9_]*))?)?\s*$", re.IGNORECASE)
_CALL_RE = re.compile(r"^\s*CALL\s+([A-Z_][A-Z0-9_]*)", re.IGNORECASE)
_COMMON_RE = re.compile(r"^\s*COMMON\s*/\s*([A-Z_][A-Z0-9_]*)\s*/", re.IGNORECASE)


def _regex_fallback(lines: List[str]) -> List[SubroutineSummary]:
    """Conservative regex sweep — used only when fparser2 yields nothing."""
    out: List[SubroutineSummary] = []
    i = 0
    n = len(lines)
    while i < n:
        m = _SUB_RE.match(lines[i])
        if m and not _looks_like_comment(lines[i]):
            name = m.group(1).upper()
            start = i + 1  # 1-based
            sig_parts = [lines[i].rstrip()]
            j = i + 1
            # Pick up trivial continuations (F77 col-6 or F90 trailing &)
            while j < n and (
                (sig_parts[-1].rstrip().endswith("&"))
                or (len(lines[j]) >= 6 and lines[j][:5].strip() == "" and lines[j][5] not in (" ", "0", "C", "c", "*"))
            ):
                sig_parts[-1] = sig_parts[-1].rstrip().rstrip("&")
                sig_parts.append(lines[j])
                j += 1
            calls: set[str] = set()
            commons: set[str] = set()
            end_line = start
            while j < n:
                if _END_RE.match(lines[j]) and not _looks_like_comment(lines[j]):
                    end_line = j + 1
                    j += 1
                    break
                if cm := _CALL_RE.match(lines[j]):
                    calls.add(cm.group(1).upper())
                if km := _COMMON_RE.match(lines[j]):
                    commons.add(km.group(1).upper())
                j += 1
            else:
                end_line = n  # unterminated — close at EOF
            sig = re.sub(r"\s+", " ", " ".join(p.strip() for p in sig_parts if p.strip()))
            out.append(SubroutineSummary(
                name=name,
                signature=sig,
                line_start=start,
                line_end=end_line,
                common_block_refs=tuple(sorted(commons)),
                called_subroutines=tuple(sorted(calls)),
            ))
            i = j
            continue
        i += 1
    return out


def _looks_like_comment(line: str) -> bool:
    """Fixed-form comment: column 1 is C, c, *, or !."""
    if not line:
        return False
    return line[0] in ("C", "c", "*", "!")
