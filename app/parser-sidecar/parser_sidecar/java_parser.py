"""
Astra parser sidecar — Java (.java) structural parser.

**Phase 14.0 (v0).** A brace-aware tokenizer that recognises the structural
surface a Java line-of-business app exposes: package + imports, the type
hierarchy (class / interface / enum / record, including nested types),
constructors and methods (concrete and interface/abstract stubs), and
`new` / call sites. Produces the canonical `ParseOutcome` shape shared with
every other language parser.

Why this matters
----------------
Java is the client's Tier-3 technology: the Kiwiplan app runs on Java 11,
which reaches end-of-life in September 2026, and the Spring Boot 3 upgrade
path requires Java 17+. The documentation the platform generates supports the
Java 11 → 21 modernisation; Kiwiplan also has a Fortran component the platform
already covers, so the Java parser completes the picture for that app.

What v0 handles
---------------
- `.java` files. `//` line, `/* */` block, and `/** */` Javadoc comments, plus
  string / char / text-block (`\"\"\"`) literals — all blanked so a brace or
  parenthesis inside them is never miscounted.
- Type declarations (`class` / `interface` / `enum` / `record`) with nesting;
  a routine's `common_block_refs` carries its innermost enclosing type.
- Constructors and concrete methods, found by matching each `{ … }` block's
  header against a method signature (brace depth is unambiguous in Java, so
  the body is delimited exactly).
- Interface / abstract method stubs (signatures terminated by `;`, no body).
- `new Type(…)` and bare `name(…)` call sites inside method bodies for the
  dependency graph. Conservative — dotted `obj.method()` chains are not
  collected (they'd over-count), matching the other v0 parsers.

What v0 does NOT handle (deferred to a JavaParser/tree-sitter backend)
---------------------------------------------------------------------
- Annotation arguments that contain braces (`@Foo({A.class})`) may confuse the
  block scanner in rare cases.
- Anonymous-class bodies (`new Runnable(){…}`) are skipped as routines (the
  `new` keyword in the header is the signal).
- Generic method type-parameter bounds spanning multiple `<…>` with nested
  `>` are captured coarsely; sufficient for name + line-range detection.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import List, Optional, Tuple

log = logging.getLogger("astra.parser.java")


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
# Comment + literal blanking (preserves line numbers and out-of-string
# structure; blanks the CONTENT of comments/strings so their braces and
# parentheses never affect the scan).
# ──────────────────────────────────────────────────────────────────────


def _normalise(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


def _blank(content: str) -> str:
    out: List[str] = []
    i, n = 0, len(content)
    state = "normal"  # normal | line | block | string | char | text
    while i < n:
        c = content[i]
        nxt = content[i + 1] if i + 1 < n else ""
        if state == "normal":
            if c == "/" and nxt == "/":
                state = "line"; out.append("  "); i += 2; continue
            if c == "/" and nxt == "*":
                state = "block"; out.append("  "); i += 2; continue
            if content[i:i + 3] == '"""':
                state = "text"; out.append('"""'); i += 3; continue
            if c == '"':
                state = "string"; out.append('"'); i += 1; continue
            if c == "'":
                state = "char"; out.append("'"); i += 1; continue
            out.append(c); i += 1; continue
        if state == "line":
            if c == "\n": state = "normal"; out.append("\n"); i += 1; continue
            out.append(" "); i += 1; continue
        if state == "block":
            if c == "*" and nxt == "/": state = "normal"; out.append("  "); i += 2; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "string":
            if c == "\\": out.append("  "); i += 2; continue
            if c == '"': state = "normal"; out.append('"'); i += 1; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "char":
            if c == "\\": out.append("  "); i += 2; continue
            if c == "'": state = "normal"; out.append("'"); i += 1; continue
            out.append(" "); i += 1; continue
        # text block """ … """
        if content[i:i + 3] == '"""':
            state = "normal"; out.append('"""'); i += 3; continue
        out.append("\n" if c == "\n" else " "); i += 1; continue
    return "".join(out)


# ──────────────────────────────────────────────────────────────────────
# Grammar
# ──────────────────────────────────────────────────────────────────────

_PACKAGE_RE = re.compile(r"^\s*package\s+([\w.]+)\s*;", re.MULTILINE)
_IMPORT_RE = re.compile(r"^\s*import\s+(?:static\s+)?([\w.*]+)\s*;", re.MULTILINE)
_TYPE_RE = re.compile(r"\b(class|interface|enum|record)\s+(\w+)")

# A method/constructor signature: an identifier followed by a (possibly
# empty) parameter list, optionally followed by a throws clause, at the END
# of the block header. Applied to the text preceding a `{` or a `;`.
_METHOD_RE = re.compile(
    r"(\w+)\s*\(([^;{)]*)\)\s*(?:throws\s[\w.,\s<>]+)?\s*$", re.DOTALL)

# Interface / abstract method stub: a `;`-terminated signature with NO body.
# Anchored per line and requiring a real return type BEFORE the name — this is
# what separates a declaration (`void onAudit(String a);`) from a call
# statement (`throw new Foo(...);`, `return bar();`, `obj.baz();`), which the
# earlier loose pattern wrongly captured.
_STUB_RE = re.compile(
    r"^[ \t]*"
    r"(?:(?:public|private|protected|static|final|abstract|default|native|synchronized|strictfp)\s+)*"
    r"(?:<[^>]+>\s*)?"
    r"([\w.$]+(?:<[^;=(){}]*>)?(?:\[\])*)\s+"   # return type (required)
    r"(\w+)\s*"                                  # method name
    r"\(([^;{}=]*)\)\s*"                         # params (no '=' → not an assignment)
    r"(?:throws\s[\w.,\s<>]+)?\s*;[ \t]*$",
    re.MULTILINE)

# Return-type positions that are actually statement keywords → not a decl.
_STMT_KW = frozenset({
    "return", "throw", "yield", "assert", "new", "else", "case", "do",
    "break", "continue", "if", "for", "while", "switch", "synchronized",
    "super", "this",
})

_NEW_RE = re.compile(r"\bnew\s+([\w.$]+)\s*\(", re.MULTILINE)
_CALL_RE = re.compile(r"(?<![.\w])(\w+)\s*\(")

_KEYWORDS = frozenset({
    "if", "for", "while", "switch", "catch", "synchronized", "return", "new",
    "else", "do", "try", "finally", "throw", "throws", "case", "default",
    "break", "continue", "assert", "yield", "instanceof", "super", "this",
    "class", "interface", "enum", "record", "void", "int", "long", "double",
    "float", "boolean", "char", "byte", "short", "var", "record",
})


def _module_of(filename: str) -> str:
    return PurePosixPath(filename.replace("\\", "/")).stem


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single Java source file and return a `ParseOutcome`."""
    content = _normalise(content)
    blanked = _blank(content)
    line_count = content.count("\n") + 1
    warnings: List[str] = []

    # 1-based line number for every character index.
    line_of: List[int] = []
    ln = 1
    for ch in blanked:
        line_of.append(ln)
        if ch == "\n":
            ln += 1

    imports = [m.group(1) for m in _IMPORT_RE.finditer(blanked)]

    subroutines: List[SubroutineSummary] = []
    seen_spans: set[Tuple[int, int]] = set()

    # Brace scan: for each `{ … }` block, the header is the text since the last
    # statement boundary. Classify it as a type (push context) or a method.
    brace_stack: List[Tuple[str, int, int, bool, Optional[str]]] = []
    type_stack: List[str] = []
    last_boundary = 0
    n = len(blanked)
    for i in range(n):
        c = blanked[i]
        if c == ";":
            last_boundary = i + 1
        elif c == "{":
            header = blanked[last_boundary:i]
            tm = _TYPE_RE.search(header)
            is_type = bool(tm)
            brace_stack.append((header, last_boundary, i, is_type, tm.group(2) if tm else None))
            if is_type:
                type_stack.append(tm.group(2))
            last_boundary = i + 1
        elif c == "}":
            if brace_stack:
                header, hstart, open_pos, is_type, tname = brace_stack.pop()
                if is_type:
                    if type_stack:
                        type_stack.pop()
                else:
                    _maybe_record_method(
                        header, hstart, open_pos, i, line_of, content,
                        type_stack[-1] if type_stack else None,
                        blanked, subroutines, seen_spans)
            last_boundary = i + 1

    # Interface / abstract stubs (`;`-terminated signatures with no body).
    for m in _STUB_RE.finditer(blanked):
        rtype = m.group(1).split("<")[0].split(".")[0].strip().lower()
        name = m.group(2)
        if name.lower() in _KEYWORDS or rtype in _STMT_KW:
            continue
        start_line = line_of[m.start(2)]
        end_line = line_of[min(m.end() - 1, n - 1)]
        if (start_line, end_line) in seen_spans:
            continue
        seen_spans.add((start_line, end_line))
        subroutines.append(SubroutineSummary(
            name=name,
            signature=_sig_line(content, start_line),
            line_start=start_line,
            line_end=end_line,
            common_block_refs=tuple(),
            called_subroutines=tuple(),
        ))

    # Fallback: a file with a type but no routines (a pure DTO / marker type)
    # still deserves one documentable unit.
    if not subroutines:
        module = _module_of(filename)
        subroutines.append(SubroutineSummary(
            name=module,
            signature=f"Type {module}",
            line_start=1,
            line_end=line_count,
            common_block_refs=tuple(),
            called_subroutines=tuple(),
        ))

    if imports:
        shown = ", ".join(imports[:8])
        warnings.append(
            f"java: file imports {len(imports)} type(s)/package(s): {shown}"
            + (" ..." if len(imports) > 8 else ""))

    subroutines.sort(key=lambda s: s.line_start)
    return ParseOutcome(
        line_count=line_count,
        subroutines=subroutines,
        warnings=warnings,
        filename=filename,
    )


def _maybe_record_method(
    header: str, hstart: int, open_pos: int, close_pos: int, line_of: List[int],
    raw: str, enclosing: Optional[str], blanked: str,
    subroutines: List[SubroutineSummary], seen_spans: set,
) -> None:
    """Classify a `{`-block header. Record it if it is a constructor/method
    signature (not a control-flow, initializer, or anonymous-class block)."""
    stripped = header.strip()
    if not stripped:
        return
    if re.search(r"\bnew\b", stripped):
        return  # anonymous class / object instantiation, not a declaration
    m = _METHOD_RE.search(stripped)
    if not m:
        return
    name = m.group(1)
    if name.lower() in _KEYWORDS:
        return
    # The first non-space char of the header locates the signature's start line.
    first = hstart
    while first < len(blanked) and blanked[first] in " \t\n":
        first += 1
    start_line = line_of[min(first, len(line_of) - 1)]
    end_line = line_of[min(close_pos, len(line_of) - 1)]
    if (start_line, end_line) in seen_spans:
        return
    seen_spans.add((start_line, end_line))
    body = blanked[open_pos + 1:close_pos]
    subroutines.append(SubroutineSummary(
        name=name,
        signature=_sig_line(raw, start_line),
        line_start=start_line,
        line_end=end_line,
        common_block_refs=(enclosing,) if enclosing else tuple(),
        called_subroutines=tuple(sorted(set(_collect_calls(body)))),
    ))


def _sig_line(raw: str, line_start: int) -> str:
    lines = raw.split("\n")
    idx = line_start - 1
    return lines[idx].strip() if 0 <= idx < len(lines) else ""


def _collect_calls(body: str) -> List[str]:
    calls: List[str] = []
    for m in _NEW_RE.finditer(body):
        calls.append(f"new:{m.group(1)}")
    for m in _CALL_RE.finditer(body):
        name = m.group(1)
        if name.lower() not in _KEYWORDS:
            calls.append(name)
    return calls
