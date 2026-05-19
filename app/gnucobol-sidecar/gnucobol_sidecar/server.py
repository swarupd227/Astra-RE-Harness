"""
gnucobol sidecar — REST surface for compile + run.

Mirrors the gfortran sidecar one-for-one so the API's CrossRuntime
validator path (Phase 5.6) can drive a COBOL reference binary the same
way it drives a Fortran one.

Endpoints
---------
GET  /health                       liveness probe
POST /compile                      body: { "sources": [{ "path", "content" }],
                                           "linkAs": "executable" | "module",
                                           "extraFlags": [...] }
                                   -> { "artifactId", "exitCode", "log",
                                        "warningCount", "errorCount",
                                        "durationMs" }

POST /run                          body: { "artifactId", "stdin": "<text>",
                                           "timeoutMs"? }
                                   -> { "exitCode", "stdout", "stderr",
                                        "durationMs", "timedOut" }

POST /compile-and-run              body: { sources, stdin, extraFlags?,
                                           timeoutMs? }
                                   -> shorthand that returns combined result.

Artifacts live under /var/tmp/gnucobol-runs/<artifactId>/ for the
lifetime of the container. The API layer treats each run as ephemeral;
we don't persist build outputs beyond the container's tmpfs.

The compiler is invoked as:
    cobc -x -O2 -free -Wno-truncate -o app <files...>
which produces a stand-alone executable that reads stdin / writes
stdout — same contract the gfortran sidecar honours. The default
dialect is GnuCOBOL's "default" superset, which accepts Cobol-85 + most
COBOL/400 + MF extensions; the in-scope DEPTPAY / EMPPAY / CBL0106
corpora are pure Cobol-85, so we don't need to twist the dialect knob.

To compile a fixed-form (columns 7-72) program — which is how the
openmainframeproject corpus ships — pass `extraFlags: ["-fixed"]` and
omit `-free`. The driver wrappers used by the equivalence harness
(rendered in C# via `*Equivalence.cs` helpers) ship as free-form so
they don't need to count columns.
"""

from __future__ import annotations

import json
import logging
import os
import shutil
import subprocess
import time
import uuid
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from gnucobol_sidecar import __version__

logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-gnucobol","msg":"%(message)s"}',
)
log = logging.getLogger("astra.gnucobol")

WORKDIR = Path(os.environ.get("GNUCOBOL_WORKDIR", "/var/tmp/gnucobol-runs"))
WORKDIR.mkdir(parents=True, exist_ok=True)

COMPILE_TIMEOUT_S = 120
DEFAULT_RUN_TIMEOUT_MS = 30_000

app = FastAPI(title="Astra gnucobol sidecar", version=__version__)


# ────────────────────────────────────────────────────────────────────────
# Pydantic models — camelCase aliases so the C# client (which posts
# JsonNamingPolicy.CamelCase) round-trips cleanly.
# ────────────────────────────────────────────────────────────────────────

class CobolSource(BaseModel):
    path: str = Field(..., description="Path relative to the build dir (e.g. DRIVER.COB).")
    content: str


class CompileRequest(BaseModel):
    sources: list[CobolSource]
    link_as: str = Field("executable", alias="linkAs")
    extra_flags: list[str] = Field(default_factory=list, alias="extraFlags")

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


class RunRequest(BaseModel):
    artifact_id: str = Field(..., alias="artifactId")
    stdin: str = ""
    timeout_ms: int = Field(DEFAULT_RUN_TIMEOUT_MS, alias="timeoutMs")

    class Config:
        populate_by_name = True


class RunResult(BaseModel):
    exit_code: int = Field(..., alias="exitCode")
    stdout: str
    stderr: str
    duration_ms: int = Field(..., alias="durationMs")
    timed_out: bool = Field(..., alias="timedOut")

    class Config:
        populate_by_name = True


class CompileAndRunRequest(BaseModel):
    sources: list[CobolSource]
    stdin: str = ""
    timeout_ms: int = Field(DEFAULT_RUN_TIMEOUT_MS, alias="timeoutMs")
    extra_flags: list[str] = Field(default_factory=list, alias="extraFlags")

    class Config:
        populate_by_name = True


class CompileAndRunResult(BaseModel):
    compile: CompileResult
    run: Optional[RunResult] = None
    skipped_run_reason: Optional[str] = Field(None, alias="skippedRunReason")

    class Config:
        populate_by_name = True


# ────────────────────────────────────────────────────────────────────────
# Routes
# ────────────────────────────────────────────────────────────────────────


@app.get("/health")
def health():
    return {"service": "astra-gnucobol", "version": __version__, "workdir": str(WORKDIR)}


@app.post("/compile", response_model=CompileResult, response_model_by_alias=True)
def compile_endpoint(req: CompileRequest) -> CompileResult:
    return _compile(req.sources, req.link_as, req.extra_flags)


@app.post("/run", response_model=RunResult, response_model_by_alias=True)
def run_endpoint(req: RunRequest) -> RunResult:
    return _run(req.artifact_id, req.stdin, req.timeout_ms)


@app.post("/compile-and-run", response_model=CompileAndRunResult, response_model_by_alias=True)
def compile_and_run_endpoint(req: CompileAndRunRequest) -> CompileAndRunResult:
    """Shorthand: compile then immediately run. Common case for the
    validator side — saves a round-trip and lets us clean up the
    artifact in one place."""
    compile_result = _compile(req.sources, "executable", req.extra_flags)
    if compile_result.exit_code != 0:
        return CompileAndRunResult(
            compile=compile_result,
            run=None,
            skipped_run_reason="compile_failed",
        )
    run_result = _run(compile_result.artifact_id, req.stdin, req.timeout_ms)
    return CompileAndRunResult(compile=compile_result, run=run_result)


# ────────────────────────────────────────────────────────────────────────
# Workers
# ────────────────────────────────────────────────────────────────────────


def _compile(sources: list[CobolSource], link_as: str, extra_flags: list[str]) -> CompileResult:
    if not sources:
        raise HTTPException(status_code=400, detail="At least one source file is required.")

    artifact_id = uuid.uuid4().hex
    build_dir = WORKDIR / artifact_id
    build_dir.mkdir(parents=True, exist_ok=False)

    # Materialise sources to disk; cobc compiles files, not pipes.
    source_paths: list[Path] = []
    for src in sources:
        rel = src.path.lstrip("/").replace("\\", "/")
        # Block path-traversal — paths must stay under build_dir.
        if ".." in rel.split("/"):
            shutil.rmtree(build_dir, ignore_errors=True)
            raise HTTPException(status_code=400, detail=f"Illegal path: {src.path}")
        abs_path = build_dir / rel
        abs_path.parent.mkdir(parents=True, exist_ok=True)
        abs_path.write_text(src.content, encoding="utf-8")
        source_paths.append(abs_path)

    # `-x` = stand-alone executable; `-O2` = optimise; `-free` = free-form
    # source (column 1+ rather than the legacy fixed columns 7-72) — most
    # of the driver wrappers we ship are free-form for readability. The
    # caller can override this with `extraFlags: ["-fixed"]` when feeding
    # in a real mainframe-style fixed-form program.
    output = build_dir / ("app" if link_as == "executable" else "lib.so")
    base_flags = ["-x", "-O2", "-Wno-truncate"]
    if "-fixed" not in extra_flags and "-free" not in extra_flags:
        base_flags.append("-free")
    if link_as != "executable":
        base_flags = ["-m", "-O2"]  # shared module (.so)
    flags = base_flags + list(extra_flags)
    cmd = ["cobc", *flags, "-o", str(output), *[str(p) for p in source_paths]]

    t0 = time.monotonic()
    try:
        proc = subprocess.run(
            cmd,
            cwd=str(build_dir),
            capture_output=True,
            text=True,
            timeout=COMPILE_TIMEOUT_S,
        )
    except subprocess.TimeoutExpired:
        shutil.rmtree(build_dir, ignore_errors=True)
        raise HTTPException(status_code=504, detail=f"cobc exceeded {COMPILE_TIMEOUT_S}s") from None

    elapsed_ms = int((time.monotonic() - t0) * 1000)
    log_text = (
        f"=== cobc {' '.join(cmd[1:])} ===\n"
        f"{proc.stdout}\n"
        + (f"=== stderr ===\n{proc.stderr}\n" if proc.stderr else "")
        + f"=== exit {proc.returncode} ===\n"
    )
    # cobc emits diagnostics as "<file>:<line>: warning:" and ":error:"
    # — a substring count is good enough for the report-card badge.
    lowered = log_text.lower()
    err_count = lowered.count(": error:") + lowered.count("error: ")
    warn_count = lowered.count(": warning:") + lowered.count("warning: ")
    log.info(
        "compile artifact=%s files=%d exit=%d ms=%d errors=%d warnings=%d",
        artifact_id, len(sources), proc.returncode, elapsed_ms, err_count, warn_count,
    )
    return CompileResult(
        artifact_id=artifact_id,
        exit_code=proc.returncode,
        log=log_text,
        warning_count=warn_count,
        error_count=err_count,
        duration_ms=elapsed_ms,
    )


def _run(artifact_id: str, stdin_text: str, timeout_ms: int) -> RunResult:
    build_dir = WORKDIR / artifact_id
    binary = build_dir / "app"
    if not binary.exists():
        raise HTTPException(status_code=404, detail=f"Artifact {artifact_id} has no executable.")

    t0 = time.monotonic()
    timed_out = False
    try:
        proc = subprocess.run(
            [str(binary)],
            cwd=str(build_dir),
            input=stdin_text,
            capture_output=True,
            text=True,
            timeout=max(1, timeout_ms) / 1000,
        )
        exit_code = proc.returncode
        stdout = proc.stdout
        stderr = proc.stderr
    except subprocess.TimeoutExpired as ex:
        timed_out = True
        exit_code = -1
        stdout = ex.stdout.decode("utf-8", errors="replace") if ex.stdout else ""
        stderr = (ex.stderr.decode("utf-8", errors="replace") if ex.stderr else "") + \
                 f"\n[gnucobol-sidecar] killed after {timeout_ms}ms"

    elapsed_ms = int((time.monotonic() - t0) * 1000)
    log.info(
        "run artifact=%s exit=%d ms=%d timed_out=%s stdout_bytes=%d stderr_bytes=%d",
        artifact_id, exit_code, elapsed_ms, timed_out, len(stdout), len(stderr),
    )
    return RunResult(
        exit_code=exit_code,
        stdout=stdout,
        stderr=stderr,
        duration_ms=elapsed_ms,
        timed_out=timed_out,
    )


def main() -> None:
    import uvicorn
    port = int(os.environ.get("GNUCOBOL_PORT", "51054"))
    uvicorn.run(
        "gnucobol_sidecar.server:app",
        host="0.0.0.0",
        port=port,
        log_level="warning",  # we emit our own structured logs
        access_log=False,
    )


if __name__ == "__main__":
    main()
