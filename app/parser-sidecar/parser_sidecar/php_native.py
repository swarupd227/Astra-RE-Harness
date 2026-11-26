"""
Astra parser sidecar — PHP via the PHP-Parser library (nikic/PHP-Parser 5).

The regex scanner in `php_parser.py` reads PHP well enough to list routines, but it cannot see what a call
targets (`$this->repo->find()`, `Invoice::where()`), resolve `use` imports, or read the literal arguments a
framework's route table is made of. This module runs a small PHP script (`php_tools/php_scan.php`) over a batch
of files with the real PHP grammar and turns its neutral summary into:

- the sidecar's `ParseOutcome` (routines, line ranges, resolved call names), and
- the raw scan, kept for the facts step (`php_facts.py`), which reads routes, tables and settings from it.

The PHP code under analysis is only parsed, never run. When PHP or the library is not available (a dev machine
without PHP) or the script fails, callers fall back to the regex scanner and say so in a warning; nothing is lost.
"""
from __future__ import annotations

import json
import logging
import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

from parser_sidecar.php_parser import ParseOutcome, SubroutineSummary

log = logging.getLogger("astra.parser.php.native")

_HERE = Path(__file__).resolve().parent
_SCRIPT_CANDIDATES = (
    os.environ.get("ASTRA_PHP_SCAN", ""),
    "/opt/php-parser/php_scan.php",
    str(_HERE.parent / "php_tools" / "php_scan.php"),
)
_VENDOR_CANDIDATES = (
    os.environ.get("ASTRA_PHP_VENDOR", ""),
    "/opt/php-parser/vendor/autoload.php",
    str(_HERE.parent / "php_tools" / "vendor" / "autoload.php"),
)

# Files per PHP process: one process start per batch, not per file.
BATCH_FILES = 200
# Characters per batch, so one process never holds a huge corpus in memory at once.
BATCH_CHARS = 8_000_000
TIMEOUT_SECONDS = 300

PARSED_BY = "php-parser"


def _first_existing(paths: Iterable[str]) -> Optional[str]:
    for p in paths:
        if p and Path(p).is_file():
            return p
    return None


@dataclass(frozen=True)
class Toolchain:
    php: str
    script: str
    vendor: str


def toolchain() -> Optional[Toolchain]:
    """The PHP binary, scan script and library, or None when any is missing (then the regex scanner is used)."""
    php = os.environ.get("ASTRA_PHP_BIN") or shutil.which("php")
    script = _first_existing(_SCRIPT_CANDIDATES)
    vendor = _first_existing(_VENDOR_CANDIDATES)
    if not php or not script or not vendor:
        return None
    return Toolchain(php=php, script=script, vendor=vendor)


def available() -> bool:
    return toolchain() is not None


class ScanFailed(RuntimeError):
    pass


def scan(files: Sequence[Tuple[str, str]], tools: Optional[Toolchain] = None) -> List[dict]:
    """Run the scanner over `(filename, content)` pairs and return one summary per file, in order."""
    tools = tools or toolchain()
    if tools is None:
        raise ScanFailed("PHP or the PHP-Parser library is not installed")
    out: List[dict] = []
    for batch in _batches(files):
        out.extend(_scan_batch(batch, tools))
    return out


def _batches(files: Sequence[Tuple[str, str]]):
    batch: List[Tuple[str, str]] = []
    size = 0
    for name, content in files:
        if batch and (len(batch) >= BATCH_FILES or size + len(content) > BATCH_CHARS):
            yield batch
            batch, size = [], 0
        batch.append((name, content))
        size += len(content)
    if batch:
        yield batch


def _scan_batch(batch: List[Tuple[str, str]], tools: Toolchain) -> List[dict]:
    payload = json.dumps({"files": [{"filename": n, "content": c} for n, c in batch]})
    env = dict(os.environ, ASTRA_PHP_VENDOR=tools.vendor)
    try:
        proc = subprocess.run(
            [tools.php, "-d", "memory_limit=1024M", "-d", "display_errors=stderr", tools.script],
            input=payload.encode("utf-8"),
            capture_output=True,
            timeout=TIMEOUT_SECONDS,
            env=env,
            check=False,
        )
    except subprocess.TimeoutExpired as e:
        raise ScanFailed(f"the PHP scanner took longer than {TIMEOUT_SECONDS}s for {len(batch)} files") from e
    if proc.returncode != 0:
        tail = proc.stderr.decode("utf-8", "replace").strip()[-400:]
        raise ScanFailed(f"the PHP scanner exited {proc.returncode}: {tail}")
    try:
        result = json.loads(proc.stdout.decode("utf-8", "replace"))
    except json.JSONDecodeError as e:
        raise ScanFailed(f"the PHP scanner's output could not be read ({e})") from e
    files = result.get("files") or []
    if len(files) != len(batch):
        raise ScanFailed(f"the PHP scanner answered {len(files)} files for {len(batch)}")
    return files


# ──────────────────────────────────────────────────────────────────────
# Summary → ParseOutcome
# ──────────────────────────────────────────────────────────────────────


def short(fqn: Optional[str]) -> Optional[str]:
    if not fqn:
        return None
    return fqn.rsplit("\\", 1)[-1]


def _module_of(filename: str) -> str:
    return PurePosixPath(filename.replace("\\", "/")).stem


def _sig_line(lines: List[str], line_start: int) -> str:
    idx = line_start - 1
    return lines[idx].strip() if 0 <= idx < len(lines) else ""


def call_names(calls: Iterable[dict], own_class: Optional[str]) -> List[str]:
    """
    The names a routine calls, in the form the API's dependency graph resolves:
    - a method on a class we can name (`$this->x()`, `self::x()`, `Repo::find()`, `$typed->x()`) → `Class.method`;
    - a function → its name (PHP's own functions such as `strlen` are left out: they are not routines of the corpus);
    - `new Foo()` → `new:Foo`.
    A method called on something whose class cannot be told (`$x->save()` on an untyped variable) is left out of the
    call list rather than guessed — matching it by name alone would join unrelated classes. It still reaches the facts.
    """
    out: List[str] = []
    seen = set()
    for c in calls:
        kind = c.get("kind")
        name: Optional[str] = None
        if kind == "function":
            if c.get("builtin"):
                continue
            name = c.get("name")
        elif kind in ("method", "static"):
            cls = short(c.get("class"))
            method = c.get("method")
            if not method:
                continue
            if not cls:
                continue
            name = f"{cls}.{method}"
        elif kind == "new":
            cls = short(c.get("class"))
            if cls:
                name = f"new:{cls}"
        if name and name not in seen:
            seen.add(name)
            out.append(name)
    return out


def to_outcome(filename: str, content: str, summary: dict) -> ParseOutcome:
    """The sidecar's routine inventory for one file, from its scan."""
    lines = content.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    line_count = int(summary.get("lineCount") or len(lines))
    warnings: List[str] = []
    errors = summary.get("errors") or []
    if errors:
        shown = "; ".join(str(e) for e in errors[:3])
        warnings.append(f"php: {len(errors)} syntax error(s) recovered: {shown}" + (" ..." if len(errors) > 3 else ""))

    subs: List[SubroutineSummary] = []
    seen_spans = set()
    for r in summary.get("routines") or []:
        start, end = int(r.get("lineStart") or 1), int(r.get("lineEnd") or 1)
        key = (r.get("name"), start, end)
        if key in seen_spans:
            continue
        seen_spans.add(key)
        cls = short(r.get("class"))
        subs.append(SubroutineSummary(
            name=str(r.get("name")),
            signature=_sig_line(lines, start),
            line_start=start,
            line_end=end,
            common_block_refs=(cls,) if cls else tuple(),
            called_subroutines=tuple(call_names(r.get("calls") or [], r.get("class"))),
        ))

    # A script's own statements (a front controller, a route file) are a unit of their own: that is where a
    # request is dispatched, so dropping them would lose the entry point.
    top = summary.get("top")
    if top:
        module = _module_of(filename)
        subs.append(SubroutineSummary(
            name=module,
            signature=f"PHP script {filename}",
            line_start=int(top.get("lineStart") or 1),
            line_end=int(top.get("lineEnd") or line_count),
            common_block_refs=tuple(),
            called_subroutines=tuple(call_names(top.get("calls") or [], None)),
        ))

    if not subs:
        module = _module_of(filename)
        subs.append(SubroutineSummary(
            name=module, signature=f"PHP unit {module}", line_start=1, line_end=line_count,
            common_block_refs=tuple(), called_subroutines=tuple()))

    subs.sort(key=lambda s: (s.line_start, s.name))
    return ParseOutcome(line_count=line_count, subroutines=subs, warnings=warnings, filename=filename)


def parse_many(files: Sequence[Tuple[str, str]]) -> Tuple[List[ParseOutcome], List[dict], List[str]]:
    """
    Parse a batch of PHP files. Returns the outcomes (in order), the raw scans (for facts; empty dicts when the
    regex fallback was used) and batch-level warnings. Never raises.
    """
    from parser_sidecar.php_parser import parse_source as regex_parse

    if not files:
        return [], [], []
    tools = toolchain()
    if tools is None:
        outcomes = [regex_parse(n, c) for n, c in files]
        return outcomes, [{} for _ in files], [
            "php: PHP-Parser is not available in this sidecar; files were read with the pattern scanner (calls and routes are approximate)."]
    try:
        scans = scan(files, tools)
    except ScanFailed as e:
        log.warning("php scan failed for %d files: %s", len(files), e)
        outcomes = [regex_parse(n, c) for n, c in files]
        return outcomes, [{} for _ in files], [f"php: the PHP-Parser scan failed ({e}); files were read with the pattern scanner."]

    outcomes: List[ParseOutcome] = []
    for (name, content), summary in zip(files, scans):
        if summary.get("routines") or summary.get("top") or summary.get("classes"):
            outcomes.append(to_outcome(name, content, summary))
        elif summary.get("ok") is False:
            # Nothing recovered: the pattern scanner may still find the routines.
            fallback = regex_parse(name, content)
            fallback.warnings.insert(0, "php: the file did not parse (" + "; ".join(str(e) for e in (summary.get("errors") or [])[:2])
                                     + "); routines were found with the pattern scanner.")
            outcomes.append(fallback)
        else:
            outcomes.append(to_outcome(name, content, summary))
    return outcomes, scans, []


def parse_one(filename: str, content: str) -> ParseOutcome:
    outcomes, _, warnings = parse_many([(filename, content)])
    outcome = outcomes[0]
    if warnings:
        outcome.warnings.extend(warnings)
    return outcome


def by_filename(files: Sequence[Tuple[str, str]], scans: Sequence[dict]) -> Dict[str, dict]:
    return {name: s for (name, _), s in zip(files, scans) if s}
