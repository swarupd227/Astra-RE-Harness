"""
Astra parser sidecar — PHP (.php) structural parser.

**Phase 15.0 (v0).** A brace-aware tokenizer for PHP, the client's Tier-4
technology (a Magento / PHP 8 storefront). Recognises the structural surface a
PHP line-of-business app exposes: namespace + `use` imports, the type hierarchy
(class / interface / trait / enum, including nested closures), free functions,
methods (concrete and abstract/interface stubs), and `new` / call sites.
Produces the canonical `ParseOutcome` shape shared with every other parser.

Why PHP is easy to parse
------------------------
Unlike Java, every PHP function or method is introduced by the `function`
keyword, so a block header that declares a routine is unambiguous — there is no
control-flow / `throw new X(...)` confusion. The parser looks for
`function NAME(` in each `{ … }` block header; braces delimit the body exactly.

What v0 handles
---------------
- `.php` files with `<?php … ?>` regions (HTML outside the tags is blanked so
  its braces never miscount). `//`, `#` line comments, `/* */` and `/** */`
  docblocks, and `"…"` / `'…'` string literals.
- `class` / `interface` / `trait` / `enum` declarations with nesting; a
  routine's `common_block_refs` carries its innermost enclosing type.
- Named functions and methods (`[modifiers] function name(…) : Type { … }`),
  including magic methods like `__construct`.
- Abstract / interface method stubs (`function name(…);` — no body).
- `new Class(…)` and bare `name(…)` call sites for the dependency graph.
  `$obj->method()` and `Class::method()` chains are NOT collected (they'd
  over-count), matching the other v0 parsers.

What v0 does NOT handle (deferred)
----------------------------------
- Heredoc / nowdoc (`<<<EOT`) bodies are not blanked; a heredoc containing a
  `{` or the word `function` could confuse the scanner (uncommon in class code).
- Anonymous functions / arrow fns are not recorded as named routines.
- Attribute arguments `#[Route(...)]` are kept as normal code (their brackets
  do not affect the brace scan).
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import List, Optional, Tuple

log = logging.getLogger("astra.parser.php")


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
# Comment / literal / non-PHP blanking (preserves line numbers; blanks the
# CONTENT of comments, strings, and HTML-outside-PHP so their braces and
# parentheses never affect the scan).
# ──────────────────────────────────────────────────────────────────────


def _normalise(content: str) -> str:
    return content.replace("\r\n", "\n").replace("\r", "\n")


def _blank(content: str) -> str:
    out: List[str] = []
    i, n = 0, len(content)
    state = "html"  # html | php | line | block | dq | sq
    while i < n:
        c = content[i]
        if state == "html":
            if content[i:i + 5].lower() == "<?php":
                state = "php"; out.append("     "); i += 5; continue
            if content[i:i + 3] == "<?=":
                state = "php"; out.append("   "); i += 3; continue
            if content[i:i + 2] == "<?":
                state = "php"; out.append("  "); i += 2; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "php":
            if content[i:i + 2] == "?>":
                state = "html"; out.append("  "); i += 2; continue
            if content[i:i + 2] == "//":
                state = "line"; out.append("  "); i += 2; continue
            if c == "#":
                if content[i:i + 2] == "#[":   # PHP 8 attribute — not a comment
                    out.append("#"); i += 1; continue
                state = "line"; out.append(" "); i += 1; continue
            if content[i:i + 2] == "/*":
                state = "block"; out.append("  "); i += 2; continue
            if c == '"':
                state = "dq"; out.append('"'); i += 1; continue
            if c == "'":
                state = "sq"; out.append("'"); i += 1; continue
            out.append(c); i += 1; continue
        if state == "line":
            if content[i:i + 2] == "?>":
                state = "html"; out.append("  "); i += 2; continue
            if c == "\n":
                state = "php"; out.append("\n"); i += 1; continue
            out.append(" "); i += 1; continue
        if state == "block":
            if content[i:i + 2] == "*/":
                state = "php"; out.append("  "); i += 2; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        if state == "dq":
            if c == "\\": out.append("  "); i += 2; continue
            if c == '"': state = "php"; out.append('"'); i += 1; continue
            out.append("\n" if c == "\n" else " "); i += 1; continue
        # single-quoted
        if c == "\\": out.append("  "); i += 2; continue
        if c == "'": state = "php"; out.append("'"); i += 1; continue
        out.append("\n" if c == "\n" else " "); i += 1; continue
    return "".join(out)


# ──────────────────────────────────────────────────────────────────────
# Grammar
# ──────────────────────────────────────────────────────────────────────

_NAMESPACE_RE = re.compile(r"\bnamespace\s+([\w\\]+)\s*;", re.IGNORECASE)
_USE_RE = re.compile(r"^\s*use\s+([\w\\]+(?:\s+as\s+\w+)?)\s*;", re.IGNORECASE | re.MULTILINE)
_TYPE_RE = re.compile(r"\b(class|interface|trait|enum)\s+(\w+)", re.IGNORECASE)
# A named function/method: the `function` keyword (optionally reference-return
# `&`) followed by a name and `(`.
_FUNC_RE = re.compile(r"\bfunction\s+&?\s*(\w+)\s*\(", re.IGNORECASE)
# Abstract / interface stub: `function name(...) [: type] ;` with no body.
_STUB_RE = re.compile(
    r"\bfunction\s+&?\s*(\w+)\s*\([^{};]*\)\s*(?::\s*[?\w\\|]+)?\s*;",
    re.IGNORECASE)

_NEW_RE = re.compile(r"\bnew\s+([\w\\]+)\s*\(", re.IGNORECASE)
_CALL_RE = re.compile(r"(?<![\w$>:\\])(\w+)\s*\(")

_KEYWORDS = frozenset({
    "if", "else", "elseif", "for", "foreach", "while", "do", "switch", "case",
    "catch", "try", "finally", "function", "fn", "return", "echo", "print",
    "new", "array", "list", "isset", "empty", "unset", "exit", "die",
    "include", "require", "include_once", "require_once", "and", "or", "xor",
    "instanceof", "clone", "throw", "yield", "match", "declare", "namespace",
    "use", "global", "static", "const", "class", "interface", "trait", "enum",
    "abstract", "final", "public", "private", "protected", "readonly", "var",
})


def _module_of(filename: str) -> str:
    return PurePosixPath(filename.replace("\\", "/")).stem


def parse_source(filename: str, content: str) -> ParseOutcome:
    """Parse a single PHP source file and return a `ParseOutcome`."""
    content = _normalise(content)
    blanked = _blank(content)
    line_count = content.count("\n") + 1
    warnings: List[str] = []

    line_of: List[int] = []
    ln = 1
    for ch in blanked:
        line_of.append(ln)
        if ch == "\n":
            ln += 1

    imports = [m.group(1).strip() for m in _USE_RE.finditer(blanked)]

    subroutines: List[SubroutineSummary] = []
    seen_spans: set[Tuple[int, int]] = set()

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
                    fm = _FUNC_RE.search(header)
                    if fm and fm.group(1).lower() not in _KEYWORDS:
                        _record(fm.group(1), hstart, open_pos, i, line_of, content,
                                type_stack[-1] if type_stack else None, blanked,
                                subroutines, seen_spans)
            last_boundary = i + 1

    # Abstract / interface stubs (`function name(...);` with no body).
    for m in _STUB_RE.finditer(blanked):
        name = m.group(1)
        if name.lower() in _KEYWORDS:
            continue
        start_line = line_of[m.start(1)]
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

    # Fallback: a file with a type but no routines still gets one unit.
    if not subroutines:
        module = _module_of(filename)
        subroutines.append(SubroutineSummary(
            name=module,
            signature=f"PHP unit {module}",
            line_start=1,
            line_end=line_count,
            common_block_refs=tuple(),
            called_subroutines=tuple(),
        ))

    if imports:
        shown = ", ".join(imports[:8])
        warnings.append(
            f"php: file uses {len(imports)} symbol(s): {shown}"
            + (" ..." if len(imports) > 8 else ""))

    subroutines.sort(key=lambda s: s.line_start)
    return ParseOutcome(
        line_count=line_count,
        subroutines=subroutines,
        warnings=warnings,
        filename=filename,
    )


def _record(
    name: str, hstart: int, open_pos: int, close_pos: int, line_of: List[int],
    raw: str, enclosing: Optional[str], blanked: str,
    subroutines: List[SubroutineSummary], seen_spans: set,
) -> None:
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
        calls.append(f"new:{m.group(1).lstrip(chr(92))}")
    for m in _CALL_RE.finditer(body):
        name = m.group(1)
        if name.lower() not in _KEYWORDS:
            calls.append(name)
    return calls
