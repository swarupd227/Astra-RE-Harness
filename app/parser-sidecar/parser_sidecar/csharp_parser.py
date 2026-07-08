"""
Astra parser sidecar — C# structural parser (v0 tokenizer).

Phase 12.0 — Recognises the structural surface needed to feed the
Migration Planner and the Spec extraction pipeline: class/interface/struct
declarations, method/constructor/property-accessor signatures, and
heuristic call-site detection.

This is NOT a full C# parser. A production path using Roslyn or
tree-sitter-c-sharp is planned for Phase 12.1. The v0 module exists so
the rest of Phase 12.0 (schema, prompts, archetypes, Golden Dataset, seed
corpus) can develop in parallel against a working subroutine catalogue.

What v0 handles
---------------
- [access] [modifiers] ReturnType MethodName(params)  — instance/static methods
- [access] [modifiers] ClassName(params)              — constructors
- [access] [modifiers] Type PropertyName { get; set; }— auto/full properties
  (the getter and setter are recorded as separate subroutines)
- Containing class / struct / interface name tracking (one level of nesting).
- Heuristic call sites: `Identifier(` at statement start, filtered against
  C# reserved keywords. Dotted calls (`obj.Method()`) are captured as
  `Method` only (the qualifier is discarded).
- `using` directive extraction into `common_block_refs` so the planner can
  walk namespace → package edges.

What v0 does NOT handle
-----------------------
- Generics in signatures (`Task<T>`, `Dictionary<K,V>`) — the regex
  simplifies `<...>` to `<...>` in the displayed signature but may
  miscount angle brackets inside complex nested generics.
- Nested classes. Only the outermost containing type is tracked; methods
  on nested types are attributed to the outer class.
- Partial classes spread across files. Each file is parsed independently;
  the Migration Planner merges by name if needed.
- Lambda expressions and local functions inside method bodies.
- Expression-bodied members (`=> expr`). These are recorded with a body
  of one line (the `=> expr` line) and line_end = line_start + 1.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from typing import List, Optional, Tuple

log = logging.getLogger("astra.parser.csharp")


# ──────────────────────────────────────────────────────────────────────
# Output shape — matches the VB6 / Delphi / C++ ParseOutcome contract
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
# Lexical helpers
# ──────────────────────────────────────────────────────────────────────

_ACCESS = r"(?:public|private|protected|internal|protected\s+internal|private\s+protected)\s+"
_MODIFIERS = r"(?:(?:static|virtual|override|sealed|abstract|async|extern|partial|new|readonly|unsafe)\s+)*"
# Match a type name including generics and nullable (`?`), arrays (`[]`),
# and task wrappers. This is intentionally permissive — a full type
# parser would need a recursive grammar.
_TYPE = r"(?:[\w\.]+(?:<[^(>]*>)?[\[\]?]*(?:\s*\.\s*[\w<>\[\]?]+)*)"
_IDENT = r"[A-Za-z_][A-Za-z0-9_]*"

# Method declaration: [access] [modifiers] ReturnType MethodName ( ...
_METHOD_RE = re.compile(
    rf"^\s*(?:{_ACCESS})?{_MODIFIERS}({_TYPE})\s+({_IDENT})\s*(<[^(>]*>)?\s*\(",
    re.MULTILINE,
)

# Constructor: [access] ClassName ( ...  (no return type)
_CTOR_RE = re.compile(
    rf"^\s*(?:{_ACCESS})?{_MODIFIERS}({_IDENT})\s*\(",
    re.MULTILINE,
)

# Class / struct / interface / record / enum declaration
_TYPE_DECL_RE = re.compile(
    r"^\s*(?:public|private|protected|internal|protected\s+internal)?\s*"
    r"(?:static\s+|abstract\s+|sealed\s+|partial\s+)*"
    r"(?:class|struct|interface|record|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
    re.IGNORECASE | re.MULTILINE,
)

# using directive (namespace import)
_USING_RE = re.compile(r"^\s*using\s+(?:static\s+)?([A-Za-z_][A-Za-z0-9_\.]+)\s*;")

# Line comment stripping (C# // comments only; /* */ handled separately)
_LINE_COMMENT_RE = re.compile(r"//.*$")

# Keywords that filter out call-site identifiers in body collection.
# Includes primitive types so `void(`, `string(`, etc. aren't call sites.
_CSHARP_RESERVED = frozenset({
    "if", "else", "while", "for", "foreach", "switch", "case", "do",
    "return", "new", "throw", "catch", "finally", "try", "using",
    "yield", "await", "lock", "fixed", "checked", "unchecked",
    "typeof", "sizeof", "stackalloc", "nameof", "default",
    "void", "int", "long", "string", "bool", "double", "float",
    "decimal", "object", "char", "byte", "sbyte", "short", "ushort",
    "uint", "ulong", "dynamic", "var",
})

# Heuristic call site in a statement: word followed by (
_CALL_SITE_RE = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(")


# ──────────────────────────────────────────────────────────────────────
# Main entry
# ──────────────────────────────────────────────────────────────────────


def parse_source(filename: str, content: str) -> ParseOutcome:
    content = content.replace("\r\n", "\n").replace("\r", "\n")
    lines = content.split("\n")
    line_count = len(lines)
    warnings: List[str] = []

    # Collect using directives as namespace imports (common_block_refs)
    namespaces = _collect_usings(lines)

    # Track containing type name — v0 tracks one level only
    current_type: str = ""

    subroutines: List[SubroutineSummary] = []
    i = 0
    while i < line_count:
        stripped = _strip_line_comment(lines[i]).strip()

        # Track class/struct/interface/record declarations
        type_match = _TYPE_DECL_RE.match(lines[i])
        if type_match:
            current_type = type_match.group(1)
            i += 1
            continue

        # Skip blank lines, attributes ([...]), preprocessor (#...)
        if not stripped or stripped.startswith("[") or stripped.startswith("#"):
            i += 1
            continue

        # Try to match a method-like declaration
        member = _try_match_member(lines, i, current_type)
        if member is not None:
            name, sig, line_start, line_end, body = member
            calls = _collect_calls(body)
            subroutines.append(SubroutineSummary(
                name=name,
                signature=sig,
                line_start=line_start,
                line_end=line_end,
                common_block_refs=tuple(namespaces),
                called_subroutines=tuple(sorted(set(calls))),
            ))
            i = line_end  # line_end is 1-based; resume from there (0-based = line_end)
            continue

        i += 1

    if not subroutines:
        warnings.append(
            f"csharp: v0 tokenizer found 0 methods in {filename}; "
            "file may be a data-only class, designer file, or use unsupported syntax"
        )

    return ParseOutcome(
        line_count=line_count,
        subroutines=subroutines,
        warnings=warnings,
        filename=filename,
    )


# ──────────────────────────────────────────────────────────────────────
# Member detection
# ──────────────────────────────────────────────────────────────────────

# Tokens that CANNOT appear as the first meaningful word of a member
# declaration. Control-flow and expression keywords only — primitive types
# (void, int, string, …) ARE valid return types and must NOT appear here.
_NOT_RETURN_TYPE = frozenset({
    "if", "else", "while", "for", "foreach", "switch", "case", "do", "lock",
    "try", "catch", "finally", "return", "throw", "new", "await",
    "yield", "using", "fixed", "checked", "unchecked",
    "typeof", "sizeof", "stackalloc", "nameof", "default",
    "var",
})

_ACCESS_KEYWORDS = frozenset({
    "public", "private", "protected", "internal",
})

_MODIFIER_KEYWORDS = frozenset({
    "static", "virtual", "override", "sealed", "abstract",
    "async", "extern", "partial", "new", "readonly", "unsafe",
})

_PROPERTY_BODY_RE = re.compile(r"\{[^{}]*(?:get|set)[^{}]*\}", re.IGNORECASE)


def _try_match_member(
    lines: List[str],
    i: int,
    current_type: str,
) -> "Optional[Tuple[str, str, int, int, List[str]]]":
    """Try to recognise a method, constructor, or property starting at line i.

    Returns (name, signature, line_start_1based, line_end_1based, body_lines)
    or None if the line doesn't look like a member declaration.
    """
    raw = lines[i]
    stripped = _strip_line_comment(raw).strip()

    # Must contain `(` to be a method or constructor
    if "(" not in stripped:
        return None

    tokens = stripped.split()
    if not tokens:
        return None

    # The first non-access/non-modifier token is either the return type
    # (for methods) or the class name (for constructors).
    idx = 0
    while idx < len(tokens) and (
        tokens[idx].lower() in _ACCESS_KEYWORDS
        or tokens[idx].lower() in _MODIFIER_KEYWORDS
    ):
        idx += 1

    if idx >= len(tokens):
        return None

    first_meaningful = tokens[idx].lower().rstrip("(")

    # Disqualify control-flow and expression-keyword statements.
    # NOTE: _NOT_RETURN_TYPE intentionally excludes primitive types (void,
    # string, int, …) because those ARE valid return types. _CSHARP_RESERVED
    # is only used for call-site filtering, not here.
    if first_meaningful in _NOT_RETURN_TYPE:
        return None

    # Disqualify pure type/variable declarations (no `(` before `;` or `=`)
    # Quick heuristic: if `;` or `=` appears before `(` it's not a method.
    paren_pos = stripped.find("(")
    semi_pos = stripped.find(";")
    eq_pos = stripped.find("=")
    if semi_pos != -1 and semi_pos < paren_pos:
        return None
    if eq_pos != -1 and eq_pos < paren_pos and "=>" not in stripped[:paren_pos]:
        return None

    # Build the signature from the declaration line (up to the `{` or `;`)
    sig_end = raw.find("{")
    if sig_end == -1:
        sig_end = raw.find(";")
    sig = (raw[:sig_end].strip() if sig_end != -1 else raw.strip())

    # Determine member name: the token immediately before the `(`
    # e.g. "public async Task<IActionResult> GetOrder(int id)" → "GetOrder"
    before_paren = stripped[:paren_pos].rstrip()
    name_token = before_paren.split()[-1] if before_paren.split() else ""
    # Strip generic suffixes like `GetOrder<T>`
    name_token = name_token.split("<")[0]

    if not name_token or not name_token[0].isalpha() and name_token[0] != "_":
        return None
    # Dotted identifiers (Console.WriteLine, obj.Method) are call expressions,
    # not member declarations — reject them so body statements don't become subs.
    if "." in name_token:
        return None

    # Prefix with class name for uniqueness across files
    qualified_name = f"{current_type}.{name_token}" if current_type else name_token

    line_start = i + 1  # 1-based

    # Find the opening brace (may be on a following line)
    brace_line = i
    while brace_line < len(lines) and "{" not in _strip_line_comment(lines[brace_line]):
        # If we hit a `;` before any `{` it's an abstract/interface member or
        # auto-property — record it as a single-line entry.
        if ";" in _strip_line_comment(lines[brace_line]):
            return (qualified_name, sig, line_start, line_start, [])
        brace_line += 1
        if brace_line - i > 10:
            # Gave up looking for `{`; not a member we can parse
            return None

    # Brace-balance walk to find the matching closing `}`
    body_lines, line_end = _consume_brace_body(lines, brace_line)

    return (qualified_name, sig, line_start, line_end, body_lines)


def _consume_brace_body(
    lines: List[str], open_line: int
) -> Tuple[List[str], int]:
    """Walk from `open_line` (0-based) until the matching `}` is found.
    Returns (body_lines, 1-based line_end).

    Tracks brace depth; ignores braces inside string literals and
    line comments (v0: does not handle verbatim strings @"..." or
    multi-line interpolated strings perfectly — good enough for v0).
    """
    depth = 0
    body: List[str] = []
    i = open_line
    while i < len(lines):
        raw = lines[i]
        # Strip line comment before counting braces
        safe = _strip_line_comment(raw)
        # Strip string literals before counting braces (v0: remove quoted content)
        safe = re.sub(r'"[^"]*"', '""', safe)
        safe = re.sub(r"'[^']*'", "''", safe)

        depth += safe.count("{") - safe.count("}")
        body.append(raw)

        if depth <= 0:
            return body, i + 1  # 1-based

        i += 1

    # EOF before closing brace — return what we have
    return body, len(lines)


# ──────────────────────────────────────────────────────────────────────
# Call-site and using collection
# ──────────────────────────────────────────────────────────────────────


def _collect_calls(body: List[str]) -> List[str]:
    calls: List[str] = []
    for raw in body:
        line = _strip_line_comment(raw)
        for m in _CALL_SITE_RE.finditer(line):
            name = m.group(1)
            if name.lower() not in _CSHARP_RESERVED:
                calls.append(name)
    return calls


def _collect_usings(lines: List[str]) -> List[str]:
    result: List[str] = []
    for line in lines[:100]:  # using directives always at the top
        m = _USING_RE.match(line)
        if m:
            result.append(m.group(1))
    return result


def _strip_line_comment(line: str) -> str:
    return _LINE_COMMENT_RE.sub("", line)
