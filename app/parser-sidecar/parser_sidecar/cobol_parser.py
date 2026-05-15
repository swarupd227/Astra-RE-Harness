"""
Astra parser sidecar — COBOL-85 fixed-form parser.

Targets the on-the-wire COBOL surface that ships in the demo repo
(openmainframeproject/cobol-programming-course): clean COBOL-85 fixed
form, no copybooks, no EXEC SQL, no CICS preprocessor directives.

Per PROGRAM-ID we surface one `SubroutineSummary` to match the proto
contract the API consumes for Fortran:

    name              → PROGRAM-ID upper-cased
    signature         → "PROGRAM-ID. <NAME>." normalised
    line_start/end    → 1-based inclusive over the file
    common_block_refs → COBOL COPY-book identifiers (Astra reuses the
                        field for any "external include" reference)
    called_subroutines→ all PARAGRAPH names referenced from PERFORM
                        statements within the PROCEDURE DIVISION

The parser intentionally does NOT try to be a full COBOL-85 parser.
For the in-scope demo programs (DEPTPAY, EMPPAY, CBL0106) this
focused parser is correct; richer features (DB2 EXEC SQL, CICS
EXEC, COPY books with REPLACING, nested programs) land in Phase 7
when a ProLeap-backed sidecar replaces this module.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from typing import List, Tuple

log = logging.getLogger("astra.parser.cobol")


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
    line_count: int
    subroutines: List[SubroutineSummary]
    warnings: List[str]


# ──────────────────────────────────────────────────────────────────────
# Format helpers
# ──────────────────────────────────────────────────────────────────────

# Fixed-form COBOL: cols 1-6 = sequence area, col 7 = indicator
# (`*` / `/` = comment line, `-` = continuation, space = normal),
# cols 8-72 = code area (A area 8-11, B area 12-72), 73-80 =
# identification area. Any line shorter than 7 chars is whitespace.

_COMMENT_INDICATOR = {"*", "/"}


def _strip_line(raw: str) -> str:
    """Return the code-area content of a COBOL fixed-form line.

    Removes the sequence area (cols 1-6) and the identification area
    (cols 73-80, if the line is that long). Returns "" for comment
    lines (col 7 is `*` or `/`) and for lines too short to contain
    the indicator column.
    """
    if len(raw) < 7:
        return ""
    indicator = raw[6]
    if indicator in _COMMENT_INDICATOR:
        return ""
    # Drop sequence area + indicator; trim identification area if present.
    body = raw[7:]
    body = body[:65]  # cols 8..72 = 65 chars
    return body


_PROGRAM_ID_RE = re.compile(r"^\s*PROGRAM-ID\s*\.\s*([A-Z0-9][A-Z0-9-]*)\s*\.?\s*$", re.IGNORECASE)
_DIVISION_RE = re.compile(r"^\s*(IDENTIFICATION|ENVIRONMENT|DATA|PROCEDURE)\s+DIVISION\s*\.\s*$", re.IGNORECASE)
_SECTION_RE = re.compile(r"^\s*([A-Z][A-Z0-9-]*)\s+SECTION\s*\.\s*$", re.IGNORECASE)
_PARAGRAPH_RE = re.compile(r"^\s*([A-Z][A-Z0-9-]*)\s*\.\s*$", re.IGNORECASE)
_PERFORM_RE = re.compile(r"\bPERFORM\s+([A-Z][A-Z0-9-]*)\b", re.IGNORECASE)
_COPY_RE = re.compile(r"^\s*COPY\s+([A-Z][A-Z0-9-]*)\b", re.IGNORECASE)


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single COBOL source file. Tolerant of malformed input;
    every recovery path appends to `warnings` so the caller can log
    + ship a partial result."""
    raw_lines = content.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    line_count = len(raw_lines)
    warnings: List[str] = []

    program_id: str | None = None
    program_id_line: int | None = None
    current_division: str | None = None
    paragraphs_in_procedure: List[Tuple[str, int]] = []
    performs: List[str] = []
    copybooks: List[str] = []

    for idx, raw in enumerate(raw_lines, start=1):
        code = _strip_line(raw).rstrip()
        if not code.strip():
            continue

        # 1) DIVISION transitions
        m_div = _DIVISION_RE.match(code)
        if m_div:
            current_division = m_div.group(1).upper()
            continue

        # 2) PROGRAM-ID (lives in IDENTIFICATION DIVISION)
        m_pid = _PROGRAM_ID_RE.match(code)
        if m_pid and program_id is None:
            program_id = m_pid.group(1).upper()
            program_id_line = idx
            continue

        # 3) COPY books — book name reused via common_block_refs field
        m_copy = _COPY_RE.match(code)
        if m_copy:
            copybooks.append(m_copy.group(1).upper())
            continue

        # 4) Within PROCEDURE DIVISION, find paragraphs + PERFORMs
        if current_division == "PROCEDURE":
            # PERFORM target on any line — anywhere in the code area
            for m_perf in _PERFORM_RE.finditer(code):
                target = m_perf.group(1).upper()
                # Filter out PERFORM keywords like UNTIL/VARYING; the
                # regex above matches PERFORM <ident>, which can also
                # capture "PERFORM UNTIL". Drop reserved words.
                if target not in _COBOL_RESERVED:
                    performs.append(target)

            # Section header inside PROCEDURE — these aren't subroutines
            # in our model, but they reset paragraph parsing.
            if _SECTION_RE.match(code):
                continue

            # Paragraph header — name + period only on the line, in col A
            # area. Strip leading spaces; A area starts at col 8 of the
            # original line (already col 1 of `code`).
            m_para = _PARAGRAPH_RE.match(code)
            if m_para and code.lstrip() == code:
                name = m_para.group(1).upper()
                # Don't treat the PROCEDURE-DIVISION-USING header or
                # END PROGRAM lines as paragraphs.
                if name not in {"END-PROGRAM", "END"}:
                    paragraphs_in_procedure.append((name, idx))

    if program_id is None:
        warnings.append("no PROGRAM-ID found; emitting empty subroutine inventory")
        return ParseOutcome(line_count=line_count, subroutines=[], warnings=warnings)

    # Build the single Subroutine summary.
    last_non_empty = line_count
    for i in range(line_count, 0, -1):
        if raw_lines[i - 1].strip():
            last_non_empty = i
            break

    signature = f"PROGRAM-ID. {program_id}."
    # Dedup performs but preserve order.
    seen_perf: set[str] = set()
    deduped_performs: List[str] = []
    for p in performs:
        if p not in seen_perf:
            seen_perf.add(p)
            deduped_performs.append(p)
    seen_copy: set[str] = set()
    deduped_copies: List[str] = []
    for c in copybooks:
        if c not in seen_copy:
            seen_copy.add(c)
            deduped_copies.append(c)

    sub = SubroutineSummary(
        name=program_id,
        signature=signature,
        line_start=1,
        line_end=last_non_empty,
        common_block_refs=tuple(deduped_copies),
        called_subroutines=tuple(deduped_performs),
    )

    log.info(
        "Parsed COBOL program %s (file=%s, lines=%d, paragraphs=%d, performs=%d, copies=%d)",
        program_id, filename, line_count, len(paragraphs_in_procedure),
        len(deduped_performs), len(deduped_copies),
    )

    return ParseOutcome(line_count=line_count, subroutines=[sub], warnings=warnings)


# COBOL reserved words that follow PERFORM but are NOT paragraph names.
# Conservative list — enough for the in-scope demo programs.
_COBOL_RESERVED: set[str] = {
    "UNTIL", "VARYING", "THRU", "THROUGH", "WITH", "TEST",
    "BEFORE", "AFTER", "TIMES",
}
