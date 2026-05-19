"""
Astra maven sidecar — REST surface for Java compile + JUnit-test against
generated scaffolds.

Endpoints
---------
GET  /health                        liveness probe
POST /compile                       body: { sources: [{path, content}], extraArgs?[] }
                                    -> { artifactId, exitCode, log, warningCount,
                                         errorCount, durationMs }
POST /test                          body: { artifactId, extraArgs?[], timeoutMs? }
                                    -> { exitCode, tests, failures, errors,
                                         skipped, durationMs, stdout, stderr,
                                         timedOut }
POST /compile-and-test              body: { sources, extraArgs?[], timeoutMs? }
                                    -> shorthand returning a combined result.

Artifacts live under MAVEN_WORKDIR/<artifactId>/ for the lifetime of
the container; the API layer treats each run as ephemeral (we don't
persist build outputs beyond the container's tmpfs).

The Maven invocation is offline by default — every plugin and
transitive dependency needed by the
java-spring/cobol-canonical-payroll archetype was baked into the
local repository at image build time. If the caller passes
extraArgs containing `-o=false` we let Maven hit the network
(useful for ad-hoc dependency probes in development).
"""
from __future__ import annotations

import json
import logging
import os
import re
import shutil
import subprocess
import time
import uuid
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from maven_sidecar import __version__


logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-maven","msg":"%(message)s"}',
)
log = logging.getLogger("astra.maven")

WORKDIR = Path(os.environ.get("MAVEN_WORKDIR", "/var/tmp/maven-runs"))
WORKDIR.mkdir(parents=True, exist_ok=True)

COMPILE_TIMEOUT_S = 240
DEFAULT_TEST_TIMEOUT_MS = 180_000

app = FastAPI(title="Astra maven sidecar", version=__version__)


# ────────────────────────────────────────────────────────────────────────
# Pydantic models
# ────────────────────────────────────────────────────────────────────────

class JavaSource(BaseModel):
    path: str = Field(..., description="Path relative to the build dir. Must include the pom.xml entry as a sibling 'path': 'pom.xml'.")
    content: str


class CompileRequest(BaseModel):
    sources: list[JavaSource]
    extra_args: list[str] = Field(default_factory=list, alias="extraArgs")

    class Config:
        populate_by_name = True


class CompileResult(BaseModel):
    artifact_id: str = Field(..., alias="artifactId")
    exit_code: int = Field(..., alias="exitCode")
    log: str
    warning_count: int = Field(..., alias="warningCount")
    error_count: int = Field(..., alias="errorCount")
    duration_ms: int = Field(..., alias="durationMs")

    class Config:
        populate_by_name = True


class TestRequest(BaseModel):
    artifact_id: str = Field(..., alias="artifactId")
    extra_args: list[str] = Field(default_factory=list, alias="extraArgs")
    timeout_ms: int = Field(DEFAULT_TEST_TIMEOUT_MS, alias="timeoutMs")

    class Config:
        populate_by_name = True


class TestResult(BaseModel):
    exit_code: int = Field(..., alias="exitCode")
    tests: int
    failures: int
    errors: int
    skipped: int
    duration_ms: int = Field(..., alias="durationMs")
    stdout: str
    stderr: str
    timed_out: bool = Field(..., alias="timedOut")

    class Config:
        populate_by_name = True


class CompileAndTestRequest(BaseModel):
    sources: list[JavaSource]
    extra_args: list[str] = Field(default_factory=list, alias="extraArgs")
    timeout_ms: int = Field(DEFAULT_TEST_TIMEOUT_MS, alias="timeoutMs")

    class Config:
        populate_by_name = True


class CompileAndTestResult(BaseModel):
    compile: CompileResult
    test: Optional[TestResult] = None
    skipped_test_reason: Optional[str] = Field(None, alias="skippedTestReason")

    class Config:
        populate_by_name = True


# ────────────────────────────────────────────────────────────────────────
# Routes
# ────────────────────────────────────────────────────────────────────────


@app.get("/health")
def health():
    return {"service": "astra-maven", "version": __version__, "workdir": str(WORKDIR)}


@app.post("/compile", response_model=CompileResult, response_model_by_alias=True)
def compile_endpoint(req: CompileRequest) -> CompileResult:
    return _compile(req.sources, req.extra_args)


@app.post("/test", response_model=TestResult, response_model_by_alias=True)
def test_endpoint(req: TestRequest) -> TestResult:
    return _test(req.artifact_id, req.extra_args, req.timeout_ms)


@app.post("/compile-and-test", response_model=CompileAndTestResult, response_model_by_alias=True)
def compile_and_test_endpoint(req: CompileAndTestRequest) -> CompileAndTestResult:
    """Shorthand: compile then immediately run mvn test. Common case
    for the validator — saves a round-trip and lets us clean up the
    artifact in one place."""
    compile_result = _compile(req.sources, req.extra_args)
    if compile_result.exit_code != 0:
        return CompileAndTestResult(
            compile=compile_result,
            test=None,
            skipped_test_reason="compile_failed",
        )
    test_result = _test(compile_result.artifact_id, req.extra_args, req.timeout_ms)
    return CompileAndTestResult(compile=compile_result, test=test_result)


# ────────────────────────────────────────────────────────────────────────
# Workers
# ────────────────────────────────────────────────────────────────────────


def _compile(sources: list[JavaSource], extra_args: list[str]) -> CompileResult:
    if not sources:
        raise HTTPException(status_code=400, detail="At least one source file is required.")
    if not any(s.path.endswith("pom.xml") for s in sources):
        raise HTTPException(status_code=400, detail="At least one source must be a pom.xml.")

    artifact_id = uuid.uuid4().hex
    build_dir = WORKDIR / artifact_id
    build_dir.mkdir(parents=True, exist_ok=False)

    # Write every source file as-is, creating parent directories.
    for src in sources:
        if ".." in Path(src.path).parts:
            raise HTTPException(status_code=400, detail=f"Path traversal rejected: {src.path}")
        dest = build_dir / src.path
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(src.content, encoding="utf-8")

    # Compile test sources too — surefire later uses the test-classes output.
    args = ["mvn", "-B", "-q", "-o", "test-compile"] + (extra_args or [])
    transcript_header = f"=== mvn -B -q -o test-compile (cwd={build_dir}) ===\n"
    started = time.monotonic()
    try:
        proc = subprocess.run(
            args,
            cwd=str(build_dir),
            capture_output=True,
            text=True,
            timeout=COMPILE_TIMEOUT_S,
        )
        elapsed_ms = int((time.monotonic() - started) * 1000)
        log_text = transcript_header + proc.stdout + ("\n=== stderr ===\n" + proc.stderr if proc.stderr else "") + f"\n=== exit {proc.returncode} ===\n"
        warning_count, error_count = _count_diagnostics(proc.stdout + "\n" + proc.stderr)
        log.info(
            "compile artifact=%s exit=%d warnings=%d errors=%d %dms",
            artifact_id, proc.returncode, warning_count, error_count, elapsed_ms,
        )
        return CompileResult(
            artifact_id=artifact_id,
            exit_code=proc.returncode,
            log=log_text,
            warning_count=warning_count,
            error_count=error_count,
            duration_ms=elapsed_ms,
        )
    except subprocess.TimeoutExpired:
        elapsed_ms = int((time.monotonic() - started) * 1000)
        log.warning("compile artifact=%s timed out after %ds", artifact_id, COMPILE_TIMEOUT_S)
        return CompileResult(
            artifact_id=artifact_id,
            exit_code=124,
            log=transcript_header + f"!!! TIMEOUT after {COMPILE_TIMEOUT_S}s\n",
            warning_count=0,
            error_count=1,
            duration_ms=elapsed_ms,
        )


def _test(artifact_id: str, extra_args: list[str], timeout_ms: int) -> TestResult:
    build_dir = WORKDIR / artifact_id
    if not build_dir.is_dir():
        raise HTTPException(status_code=404, detail=f"artifact_id not found: {artifact_id}")

    args = ["mvn", "-B", "-o", "test", "-Dsurefire.useFile=false"] + (extra_args or [])
    started = time.monotonic()
    timed_out = False
    try:
        proc = subprocess.run(
            args,
            cwd=str(build_dir),
            capture_output=True,
            text=True,
            timeout=max(30, timeout_ms // 1000),
        )
        elapsed_ms = int((time.monotonic() - started) * 1000)
        exit_code = proc.returncode
        stdout = proc.stdout
        stderr = proc.stderr
    except subprocess.TimeoutExpired as ex:
        elapsed_ms = int((time.monotonic() - started) * 1000)
        timed_out = True
        exit_code = 124
        stdout = ex.stdout or ""
        stderr = (ex.stderr or "") + f"\n!!! TIMEOUT after {timeout_ms}ms\n"
        if isinstance(stdout, bytes):
            stdout = stdout.decode("utf-8", errors="replace")
        if isinstance(stderr, bytes):
            stderr = stderr.decode("utf-8", errors="replace")

    counts = _parse_surefire_summary(stdout + "\n" + stderr)
    log.info(
        "test artifact=%s exit=%d tests=%d failed=%d errors=%d skipped=%d %dms timedOut=%s",
        artifact_id, exit_code, counts["tests"], counts["failures"], counts["errors"], counts["skipped"], elapsed_ms, timed_out,
    )
    return TestResult(
        exit_code=exit_code,
        tests=counts["tests"],
        failures=counts["failures"],
        errors=counts["errors"],
        skipped=counts["skipped"],
        duration_ms=elapsed_ms,
        stdout=stdout,
        stderr=stderr,
        timed_out=timed_out,
    )


# ────────────────────────────────────────────────────────────────────────
# Diagnostics parsers
# ────────────────────────────────────────────────────────────────────────


_JAVAC_WARNING_RE = re.compile(r"^\[WARNING\]|warning:", re.MULTILINE)
_JAVAC_ERROR_RE = re.compile(r"^\[ERROR\]|error:|^BUILD FAILURE$", re.MULTILINE)


def _count_diagnostics(text: str) -> tuple[int, int]:
    return (
        len(_JAVAC_WARNING_RE.findall(text)),
        len(_JAVAC_ERROR_RE.findall(text)),
    )


# Surefire's plain console output reports one summary line per test
# class plus a final aggregate, e.g.:
#   Tests run: 6, Failures: 0, Errors: 0, Skipped: 0
# We sum across every match because the per-class lines come first and
# the final aggregate may repeat the last class's numbers when
# -Dsurefire.useFile=false. The most-recent total wins.
_SUREFIRE_SUMMARY_RE = re.compile(
    r"Tests run:\s*(\d+),\s*Failures:\s*(\d+),\s*Errors:\s*(\d+),\s*Skipped:\s*(\d+)",
    re.IGNORECASE,
)


def _parse_surefire_summary(text: str) -> dict[str, int]:
    matches = _SUREFIRE_SUMMARY_RE.findall(text)
    if not matches:
        return {"tests": 0, "failures": 0, "errors": 0, "skipped": 0}
    # The last match is the aggregate when surefire prints both per-class
    # and a final summary.
    tests, fails, errs, skips = matches[-1]
    return {
        "tests": int(tests),
        "failures": int(fails),
        "errors": int(errs),
        "skipped": int(skips),
    }


# ────────────────────────────────────────────────────────────────────────
# Entrypoint
# ────────────────────────────────────────────────────────────────────────


def main():
    import uvicorn
    port = int(os.environ.get("MAVEN_PORT", "51053"))
    uvicorn.run(
        "maven_sidecar.server:app",
        host="0.0.0.0",
        port=port,
        log_config=None,
    )


if __name__ == "__main__":
    main()
