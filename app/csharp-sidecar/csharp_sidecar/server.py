"""
csharp-sidecar — REST surface for C# / .NET 10 compile + run.

Endpoints
---------
GET  /health                       liveness probe
POST /compile                      body: { "sources": [{ "path", "content" }],
                                           "linkAs": "executable" | "library",
                                           "mainProgram": "Program.cs" (optional),
                                           "extraFlags": [...],
                                           "targetFramework": "net10.0" (optional) }
                                   -> { "artifactId", "exitCode", "log",
                                        "warningCount", "errorCount", "durationMs" }

POST /run                          body: { "artifactId", "stdin": "<text>" or "" }
                                   -> { "exitCode", "stdout", "stderr",
                                        "durationMs", "timedOut" }

POST /compile-and-run              body: { sources, stdin, timeoutMs?, mainProgram?,
                                           targetFramework?, extraFlags? }
                                   -> combined CompileAndRunResult.

The contract MATCHES the gpp / fpc / gfortran sidecars verbatim so the
API's CrossRuntimeValidator only needs a dispatch arm for "csharp".

Compilation
-----------
We create a minimal .csproj that targets net10.0, copy the supplied sources
into the project directory, and call `dotnet build`. If a .csproj is included
in the sources list, we use it instead of synthesising one. If `linkAs` is
"library" we synthesise a classlib; otherwise a console app.

Artifacts live under /var/tmp/csharp-runs/<artifactId>/ for the lifetime of
the container.

Performance note
----------------
The .NET 10 SDK caches NuGet packages in ~/.nuget/packages (pre-warmed by
the Dockerfile's warm-up step). First compile per artifact is ~2-4s; subsequent
compiles of the same NuGet closure are ~1-2s because the restore is a no-op.
The /compile endpoint timeout is 180s to accommodate first-run SDK bootstrap on
resource-constrained CI.
"""

from __future__ import annotations

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

from csharp_sidecar import __version__

logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-csharp","msg":"%(message)s"}',
)
log = logging.getLogger("astra.csharp")

WORKDIR = Path(os.environ.get("CSHARP_WORKDIR", "/var/tmp/csharp-runs"))
WORKDIR.mkdir(parents=True, exist_ok=True)

COMPILE_TIMEOUT_S = 180
DEFAULT_RUN_TIMEOUT_MS = 30_000
DEFAULT_TFM = "net10.0"

app = FastAPI(title="Astra csharp sidecar", version=__version__)


# ────────────────────────────────────────────────────────────────────────
# Pydantic models — same shape as gpp-sidecar plus targetFramework.
# ────────────────────────────────────────────────────────────────────────


class CsharpSource(BaseModel):
    path: str = Field(..., description="Path relative to project dir (e.g. Program.cs, Services/OrderService.cs).")
    content: str


class CompileRequest(BaseModel):
    sources: list[CsharpSource]
    link_as: str = Field("executable", alias="linkAs")
    extra_flags: list[str] = Field(default_factory=list, alias="extraFlags")
    main_program: Optional[str] = Field(None, alias="mainProgram")
    target_framework: str = Field(DEFAULT_TFM, alias="targetFramework")

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
    sources: list[CsharpSource]
    stdin: str = ""
    timeout_ms: int = Field(DEFAULT_RUN_TIMEOUT_MS, alias="timeoutMs")
    extra_flags: list[str] = Field(default_factory=list, alias="extraFlags")
    main_program: Optional[str] = Field(None, alias="mainProgram")
    target_framework: str = Field(DEFAULT_TFM, alias="targetFramework")

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
    return {
        "service": "astra-csharp",
        "version": __version__,
        "workdir": str(WORKDIR),
        "dotnetVersion": _dotnet_version(),
    }


@app.post("/compile", response_model=CompileResult, response_model_by_alias=True)
def compile_endpoint(req: CompileRequest) -> CompileResult:
    return _compile(req.sources, req.link_as, req.extra_flags, req.main_program, req.target_framework)


@app.post("/run", response_model=RunResult, response_model_by_alias=True)
def run_endpoint(req: RunRequest) -> RunResult:
    return _run(req.artifact_id, req.stdin, req.timeout_ms)


@app.post("/compile-and-run", response_model=CompileAndRunResult, response_model_by_alias=True)
def compile_and_run_endpoint(req: CompileAndRunRequest) -> CompileAndRunResult:
    compile_result = _compile(
        req.sources, "executable", req.extra_flags, req.main_program, req.target_framework
    )
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

_CSHARP_EXTS = (".cs", ".csx")
_PROJ_EXTS = (".csproj", ".fsproj", ".vbproj")


def _compile(
    sources: list[CsharpSource],
    link_as: str,
    extra_flags: list[str],
    main_program: Optional[str],
    target_framework: str,
) -> CompileResult:
    if not sources:
        raise HTTPException(status_code=400, detail="At least one source file is required.")

    artifact_id = uuid.uuid4().hex
    build_dir = WORKDIR / artifact_id
    build_dir.mkdir(parents=True, exist_ok=False)

    # Write all source files.
    proj_path: Optional[Path] = None
    for src in sources:
        rel = src.path.lstrip("/").replace("\\", "/")
        abs_path = build_dir / rel
        abs_path.parent.mkdir(parents=True, exist_ok=True)
        abs_path.write_text(src.content, encoding="utf-8")
        if abs_path.suffix.lower() in _PROJ_EXTS:
            proj_path = abs_path

    # If no .csproj was supplied, synthesise one.
    if proj_path is None:
        output_type = "Exe" if link_as == "executable" else "Library"
        proj_path = build_dir / "AstraProject.csproj"
        proj_path.write_text(
            _synthesise_csproj(target_framework, output_type, extra_flags),
            encoding="utf-8",
        )

    output_dir = build_dir / "publish"
    cmd = [
        "dotnet", "publish",
        str(proj_path),
        "--configuration", "Release",
        "--output", str(output_dir),
        "--nologo",
        "-p:TreatWarningsAsErrors=false",
    ]

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
        raise HTTPException(status_code=504, detail=f"dotnet publish exceeded {COMPILE_TIMEOUT_S}s") from None

    elapsed_ms = int((time.monotonic() - t0) * 1000)
    log_text = (
        f"=== dotnet publish {proj_path.name} ===\n"
        f"{proc.stdout}\n"
        + (f"=== stderr ===\n{proc.stderr}\n" if proc.stderr else "")
        + f"=== exit {proc.returncode} ===\n"
    )
    err_count = log_text.count(" error ")
    warn_count = log_text.count(" warning ")
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
    publish_dir = WORKDIR / artifact_id / "publish"
    if not publish_dir.exists():
        raise HTTPException(status_code=404, detail=f"Artifact {artifact_id} has no publish output.")

    # Find the executable: a .dll next to a .runtimeconfig.json, or a native EXE.
    exe = _find_executable(publish_dir)
    if exe is None:
        raise HTTPException(
            status_code=404,
            detail=f"Artifact {artifact_id}: no runnable binary found in publish dir.",
        )

    if exe.suffix.lower() == ".dll":
        cmd = ["dotnet", str(exe)]
    else:
        cmd = [str(exe)]

    t0 = time.monotonic()
    timed_out = False
    try:
        proc = subprocess.run(
            cmd,
            cwd=str(publish_dir),
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
        stderr = (
            (ex.stderr.decode("utf-8", errors="replace") if ex.stderr else "")
            + f"\n[csharp-sidecar] killed after {timeout_ms}ms"
        )

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


# ────────────────────────────────────────────────────────────────────────
# Helpers
# ────────────────────────────────────────────────────────────────────────


def _synthesise_csproj(tfm: str, output_type: str, extra_flags: list[str]) -> str:
    props = "\n    ".join(
        f"<{k}>{v}</{k}>" for k, v in [p.split("=", 1) for p in extra_flags if "=" in p]
    ) if extra_flags else ""
    extra_block = f"\n  <PropertyGroup>\n    {props}\n  </PropertyGroup>" if props else ""
    return f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>{output_type}</OutputType>
    <TargetFramework>{tfm}</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Optimize>false</Optimize>
  </PropertyGroup>{extra_block}
</Project>
"""


def _find_executable(publish_dir: Path) -> Optional[Path]:
    # Prefer a .dll with a matching .runtimeconfig.json (framework-dependent publish).
    for dll in publish_dir.glob("*.dll"):
        runtimeconfig = publish_dir / (dll.stem + ".runtimeconfig.json")
        if runtimeconfig.exists():
            return dll
    # Fall back to native executable (self-contained publish).
    for f in publish_dir.iterdir():
        if f.is_file() and f.suffix.lower() not in (".json", ".pdb", ".xml", ".dll"):
            if os.access(str(f), os.X_OK):
                return f
    return None


def _dotnet_version() -> str:
    try:
        proc = subprocess.run(
            ["dotnet", "--version"],
            capture_output=True,
            text=True,
            timeout=5,
        )
        return (proc.stdout or proc.stderr or "").strip() or "unknown"
    except Exception:
        return "unavailable"


def main() -> None:
    import uvicorn
    port = int(os.environ.get("CSHARP_PORT", "51059"))
    uvicorn.run(
        "csharp_sidecar.server:app",
        host="0.0.0.0",
        port=port,
        log_level="warning",
        access_log=False,
    )


if __name__ == "__main__":
    main()
