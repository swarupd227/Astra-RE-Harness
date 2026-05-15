"""
gfortran sidecar — REST surface for compile + run.

Endpoints
---------
GET  /health                       liveness probe
POST /compile                      body: { "sources": [{ "path", "content" }],
                                           "linkAs": "executable" | "library" }
                                   -> { "artifactId", "exitCode", "log", "warnings", "errors" }

POST /run                          body: { "artifactId", "stdin": "<json>" or "" }
                                   -> { "exitCode", "stdout", "stderr", "durationMs", "timedOut" }

POST /compile-and-run              body: { sources, stdin, timeoutMs? }
                                   -> shorthand that returns combined result.

Artifacts live under /var/tmp/gfortran-runs/<artifactId>/ for the lifetime
of the container. The API layer treats each run as ephemeral; we don't
persist build outputs beyond the container's tmpfs.

The compiler is invoked as:
    gfortran -O0 -std=legacy -w -ffree-line-length-none -o app <files...>
which accepts F77 fixed-form and F90 free-form without barking on
ancient idioms (the parsed corpora are exactly the kind of code that
makes a modern compiler tantrum). The C# side compiles with full
warnings; we don't want them on the reference side too, that just
adds noise.
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

from gfortran_sidecar import __version__

logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-gfortran","msg":"%(message)s"}',
)
log = logging.getLogger("astra.gfortran")

WORKDIR = Path(os.environ.get("GFORTRAN_WORKDIR", "/var/tmp/gfortran-runs"))
WORKDIR.mkdir(parents=True, exist_ok=True)

COMPILE_TIMEOUT_S = 120
DEFAULT_RUN_TIMEOUT_MS = 30_000

app = FastAPI(title="Astra gfortran sidecar", version=__version__)


# ────────────────────────────────────────────────────────────────────────
# Pydantic models
# ────────────────────────────────────────────────────────────────────────

class FortranSource(BaseModel):
    path: str = Field(..., description="Path relative to the build dir (e.g. CONSUME_ROLL.FOR).")
    content: str


class CompileRequest(BaseModel):
    sources: list[FortranSource]
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
    sources: list[FortranSource]
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
    return {"service": "astra-gfortran", "version": __version__, "workdir": str(WORKDIR)}


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


def _compile(sources: list[FortranSource], link_as: str, extra_flags: list[str]) -> CompileResult:
    if not sources:
        raise HTTPException(status_code=400, detail="At least one source file is required.")

    artifact_id = uuid.uuid4().hex
    build_dir = WORKDIR / artifact_id
    build_dir.mkdir(parents=True, exist_ok=False)

    # Materialise sources to disk; gfortran needs them as files.
    source_paths: list[Path] = []
    for src in sources:
        rel = src.path.lstrip("/").replace("\\", "/")
        abs_path = build_dir / rel
        abs_path.parent.mkdir(parents=True, exist_ok=True)
        abs_path.write_text(src.content, encoding="utf-8")
        source_paths.append(abs_path)

    output = build_dir / ("app" if link_as == "executable" else "lib.a")
    flags = ["-O0", "-std=legacy", "-w", "-ffree-line-length-none"] + list(extra_flags)
    if link_as != "executable":
        flags += ["-c"]
    cmd = ["gfortran", *flags, "-o", str(output), *[str(p) for p in source_paths]]

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
        raise HTTPException(status_code=504, detail=f"gfortran exceeded {COMPILE_TIMEOUT_S}s") from None

    elapsed_ms = int((time.monotonic() - t0) * 1000)
    log_text = (
        f"=== gfortran {' '.join(cmd[1:])} ===\n"
        f"{proc.stdout}\n"
        + (f"=== stderr ===\n{proc.stderr}\n" if proc.stderr else "")
        + f"=== exit {proc.returncode} ===\n"
    )
    # gfortran's warning/error lines are formatted as "<file>:<line>:<col>:
    #   Error: ..." and ".../Warning: ...". A line-by-line count is good
    # enough for the badge.
    err_count = log_text.count("Error:")
    warn_count = log_text.count("Warning:")
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
                 f"\n[gfortran-sidecar] killed after {timeout_ms}ms"

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
    port = int(os.environ.get("GFORTRAN_PORT", "51052"))
    uvicorn.run(
        "gfortran_sidecar.server:app",
        host="0.0.0.0",
        port=port,
        log_level="warning",  # we emit our own structured logs
        access_log=False,
    )


if __name__ == "__main__":
    main()
