"""
Static analysis of a generated Java package for the API's QUALITY gate (Q3).

Three industry tools, all baked into the image so they run offline:

* **SpotBugs + FindSecBugs** on the compiled classes — real bugs and security weaknesses (injection, weak crypto,
  unsafe deserialisation, path traversal, predictable random, …).
* **PMD** on the sources — best practices, error-prone code, security, and design limits (complexity, god classes,
  long parameter lists). The rule set is ``rules/pmd-astra.xml``.
* **Checkstyle** on the sources — the coding conventions a Java reviewer expects (naming, imports, braces, method
  length, switch defaults, equals/hashCode). The configuration is ``rules/checkstyle-astra.xml``; formatting-only
  checks (indentation width, line wrapping) are left out on purpose — they flag every generated file and say
  nothing about quality.

Every finding is normalised to one shape — tool, rule, category, severity, file, line, message — with paths relative
to the project root (``src/main/java/...``), so the API can merge them with its own rules. The parsers are pure
functions over the tools' XML/JSON output and are unit-tested without the tools.
"""
from __future__ import annotations

import json
import os
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Optional

TOOLS = Path(os.environ.get("ASTRA_TOOLS_DIR", "/opt/tools"))
RULES = Path(__file__).parent / "rules"
TOOL_TIMEOUT_S = 150


# ── severity mapping ────────────────────────────────────────────────────────

def spotbugs_severity(category: str, rank: int, priority: int = 2) -> str:
    """SpotBugs rank: 1–4 scariest, 5–9 scary, 10–14 troubling, 15–20 of concern. Priority is the detector's
    confidence (1 = high). FindSecBugs ranks even a clear injection as 'troubling', so for security findings a
    high-confidence report is critical whatever its rank."""
    if category == "SECURITY":
        return "critical" if rank <= 9 or priority == 1 else "major"
    if rank <= 4:
        return "major"
    if rank <= 14:
        return "minor"
    return "info"


def spotbugs_category(category: str) -> str:
    return "security" if category in ("SECURITY", "MALICIOUS_CODE") else "standards"


def pmd_severity(priority: int, ruleset: str) -> str:
    if ruleset.lower() == "security":
        return "critical" if priority <= 2 else "major"
    return {1: "major", 2: "major", 3: "minor"}.get(priority, "info")


def pmd_category(ruleset: str) -> str:
    r = ruleset.lower()
    if r == "security":
        return "security"
    if r == "design":
        return "maintainability"
    return "standards"


# ── parsers (pure) ──────────────────────────────────────────────────────────

def _rel(path: str, root: str) -> str:
    p = path.replace("\\", "/")
    root = root.replace("\\", "/").rstrip("/") + "/"
    return p[len(root):] if p.startswith(root) else p


def parse_spotbugs(xml_text: str, source_prefix: str = "src/main/java/") -> list[dict]:
    findings: list[dict] = []
    if not xml_text.strip():
        return findings
    root = ET.fromstring(xml_text)
    for bug in root.iter("BugInstance"):
        category = bug.get("category", "")
        rank = int(bug.get("rank", "20") or 20)
        line_el = bug.find("SourceLine")
        src = line_el.get("sourcepath") if line_el is not None else None
        start = line_el.get("start") if line_el is not None else None
        msg_el = bug.find("ShortMessage")
        long_el = bug.find("LongMessage")
        findings.append({
            "tool": "spotbugs",
            "rule": f"SPOTBUGS-{bug.get('type', 'UNKNOWN')}",
            "category": spotbugs_category(category),
            "severity": spotbugs_severity(category, rank, int(bug.get("priority", "2") or 2)),
            "file": (source_prefix + src) if src else "",
            "line": int(start) if start and start.isdigit() else None,
            "message": (long_el.text if long_el is not None and long_el.text else (msg_el.text if msg_el is not None else bug.get("type", ""))) or "",
        })
    return findings


def parse_pmd(json_text: str, root: str) -> list[dict]:
    findings: list[dict] = []
    if not json_text.strip():
        return findings
    data = json.loads(json_text)
    for f in data.get("files", []):
        path = _rel(f.get("filename", ""), root)
        for v in f.get("violations", []):
            ruleset = (v.get("ruleset") or "").replace(" ", "").lower()
            ruleset = {"bestpractices": "bestpractices", "errorprone": "errorprone"}.get(ruleset, ruleset)
            findings.append({
                "tool": "pmd",
                "rule": f"PMD-{v.get('rule', 'UNKNOWN')}",
                "category": pmd_category(ruleset),
                "severity": pmd_severity(int(v.get("priority", 3)), ruleset),
                "file": path,
                "line": v.get("beginline"),
                "message": (v.get("description") or "").strip(),
            })
    return findings


def parse_checkstyle(xml_text: str, root: str) -> list[dict]:
    findings: list[dict] = []
    if not xml_text.strip():
        return findings
    tree = ET.fromstring(xml_text)
    for file_el in tree.iter("file"):
        path = _rel(file_el.get("name", ""), root)
        for err in file_el.iter("error"):
            source = err.get("source", "")
            check = source.rsplit(".", 1)[-1].removesuffix("Check")
            line = err.get("line")
            findings.append({
                "tool": "checkstyle",
                "rule": f"CHECKSTYLE-{check}",
                "category": "standards",
                "severity": "minor" if err.get("severity") in ("error", "warning") else "info",
                "file": path,
                "line": int(line) if line and line.isdigit() else None,
                "message": err.get("message", ""),
            })
    return findings


# ── runners ────────────────────────────────────────────────────────────────

def _run(args: list[str], cwd: Path, timeout: int = TOOL_TIMEOUT_S) -> tuple[int, str, int]:
    started = time.monotonic()
    try:
        p = subprocess.run(args, cwd=str(cwd), capture_output=True, text=True, timeout=timeout)
        return p.returncode, (p.stdout or "") + ("\n" + p.stderr if p.stderr else ""), int((time.monotonic() - started) * 1000)
    except subprocess.TimeoutExpired:
        return 124, f"timed out after {timeout}s", int((time.monotonic() - started) * 1000)
    except FileNotFoundError as ex:
        return 127, str(ex), 0


def analyze(build_dir: Path) -> dict:
    """Compile (classes are needed by SpotBugs), then run the three tools. A tool that fails to run is reported, not fatal."""
    tools: dict[str, dict] = {}
    findings: list[dict] = []
    src_main = build_dir / "src" / "main" / "java"
    root = str(build_dir)

    code, out, ms = _run(["mvn", "-B", "-q", "-o", "compile"], build_dir, timeout=240)
    compiled = code == 0
    tools["compile"] = {"ok": compiled, "durationMs": ms, "log": out[-4000:] if not compiled else ""}

    # SpotBugs + FindSecBugs on target/classes, with the project's dependencies as the aux classpath.
    if compiled and (build_dir / "target" / "classes").is_dir():
        cp_file = build_dir / "target" / "astra-cp.txt"
        _run(["mvn", "-B", "-q", "-o", "dependency:build-classpath", f"-Dmdep.outputFile={cp_file}"], build_dir, timeout=120)
        aux = cp_file.read_text(encoding="utf-8").strip() if cp_file.is_file() else ""
        out_xml = build_dir / "target" / "spotbugs.xml"
        args = ["java", "-jar", str(TOOLS / "spotbugs" / "lib" / "spotbugs.jar"), "-textui", "-quiet", "-effort:max", "-low",
                "-pluginList", str(TOOLS / "spotbugs" / "plugin" / "findsecbugs-plugin.jar"),
                f"-xml:withMessages={out_xml}"]
        if aux:
            args += ["-auxclasspath", aux]
        args.append(str(build_dir / "target" / "classes"))
        code, out, ms = _run(args, build_dir)
        ok = out_xml.is_file()
        found = parse_spotbugs(out_xml.read_text(encoding="utf-8")) if ok else []
        findings += found
        tools["spotbugs"] = {"ok": ok, "count": len(found), "durationMs": ms, "error": None if ok else out[-1500:]}
    else:
        tools["spotbugs"] = {"ok": False, "count": 0, "durationMs": 0, "error": "skipped: the package does not compile"}

    if src_main.is_dir():
        report = build_dir / "target" / "pmd.json"
        report.parent.mkdir(parents=True, exist_ok=True)
        code, out, ms = _run([str(TOOLS / "pmd" / "bin" / "pmd"), "check", "--no-progress", "--no-cache", "-d", str(src_main),
                              "-R", str(RULES / "pmd-astra.xml"), "-f", "json", "-r", str(report)], build_dir)
        ok = report.is_file()
        found = parse_pmd(report.read_text(encoding="utf-8"), root) if ok else []
        findings += found
        tools["pmd"] = {"ok": ok, "count": len(found), "durationMs": ms, "error": None if ok else out[-1500:]}

        cs_out = build_dir / "target" / "checkstyle.xml"
        code, out, ms = _run(["java", "-jar", str(TOOLS / "checkstyle" / "checkstyle.jar"), "-c", str(RULES / "checkstyle-astra.xml"),
                              "-f", "xml", "-o", str(cs_out), str(src_main)], build_dir)
        ok = cs_out.is_file()
        found = parse_checkstyle(cs_out.read_text(encoding="utf-8"), root) if ok else []
        findings += found
        tools["checkstyle"] = {"ok": ok, "count": len(found), "durationMs": ms, "error": None if ok else out[-1500:]}
    else:
        for t in ("pmd", "checkstyle"):
            tools[t] = {"ok": False, "count": 0, "durationMs": 0, "error": "skipped: no src/main/java"}

    return {"compiled": compiled, "tools": tools, "findings": findings}


def coverage_from_csv(csv_text: str) -> Optional[dict]:
    """JaCoCo CSV report → line coverage over the main classes."""
    lines = [l for l in csv_text.splitlines() if l.strip()]
    if len(lines) < 2:
        return None
    header = lines[0].split(",")
    try:
        mi, co = header.index("LINE_MISSED"), header.index("LINE_COVERED")
    except ValueError:
        return None
    missed = covered = 0
    for row in lines[1:]:
        cols = row.split(",")
        if len(cols) <= max(mi, co):
            continue
        missed += int(cols[mi] or 0)
        covered += int(cols[co] or 0)
    total = missed + covered
    return {"lines": total, "covered": covered, "percent": round(100.0 * covered / total, 1) if total else None}
