"""
vb6 sidecar — REST surface for VB6 compile + run.

Per ADR-037 this is a two-tier service with the same HTTP contract:

  Production tier: Windows Server Core 2022 container with
                   customer-provided VB6 runtime DLLs at /runtime.
                   Real vb6.exe + native dispatch.

  Dev tier:        Wine on Debian. msvbvm60.dll registered via
                   wine regsvr32 at container start. Covers non-COM
                   routines reliably; COM-heavy routines return a
                   structured error so the validator can mark Gate 3
                   as "skipped (dev-tier)" rather than "failed".

The validator picks the tier via the Validation__Vb6Endpoint env
var. Both tiers expose the SAME endpoints with the SAME contract —
the request/response shape mirrors fpc-sidecar (Delphi) verbatim so
the API's CrossRuntimeValidator can dispatch by source-language
without learning a second protocol.

Endpoints
---------
GET  /health                       liveness probe + tier identification
POST /compile                      body: { "sources": [{ "path", "content" }],
                                           "linkAs": "executable",
                                           "mainProject": "MyProj.vbp" (optional) }
                                   -> { "artifactId", "exitCode", "log",
                                        "warningCount", "errorCount", "durationMs" }

POST /run                          body: { "artifactId", "stdin": "<json>" or "" }
                                   -> { "exitCode", "stdout", "stderr",
                                        "durationMs", "timedOut" }

POST /compile-and-run              body: { sources, stdin, timeoutMs?, mainProject? }
                                   -> shorthand that returns combined result.

Compilation
-----------
vb6.exe is invoked as:
    vb6.exe /make <project.vbp> /out <output.exe>
where:
    /make        compile without launching the IDE
    /out         output .exe path

If `mainProject` is not supplied we SYNTHESISE a minimal .vbp from the
source list — one `Module=` line per .bas, one `Class=` line per .cls,
one `Form=` line per .frm, plus a standard Type=Exe header. Lets the
Hypothesis sidecar (10.0.g) ship a single .bas and have it linked into
a runnable .exe without authoring a project file per snippet.

Artifacts live under <WORKDIR>/<artifactId>/ for the lifetime of the
container. Same lifetime contract as fpc-sidecar.
"""

from __future__ import annotations

import logging
import os
import shutil
import subprocess
import sys
import time
import uuid
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from vb6_sidecar import __version__

logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-vb6","msg":"%(message)s"}',
)
log = logging.getLogger("astra.vb6")

WORKDIR = Path(os.environ.get("VB6_WORKDIR", "/var/tmp/vb6-runs"))
WORKDIR.mkdir(parents=True, exist_ok=True)

# Per ADR-037 the customer drops the runtime DLLs (and ideally vb6.exe)
# into this volume at deploy time. The sidecar refuses to start a
# compile when vb6.exe is absent — we explicitly do NOT redistribute.
RUNTIME_DIR = Path(os.environ.get("VB6_RUNTIME_DIR", "/runtime"))

COMPILE_TIMEOUT_S = 120
DEFAULT_RUN_TIMEOUT_MS = 30_000

# Tier identification — "windows" runs vb6.exe natively; "wine" runs it
# under wine. Auto-detected at process start so the /health endpoint
# can surface the right value.
TIER = os.environ.get("VB6_TIER", "auto")


# ────────────────────────────────────────────────────────────────────────
# Pydantic models — same shape as fpc-sidecar plus mainProject.
# ────────────────────────────────────────────────────────────────────────


class Vb6Source(BaseModel):
    path: str = Field(..., description="Path relative to the build dir (e.g. modOrders.bas, frmOrderEntry.frm).")
    content: str


class CompileRequest(BaseModel):
    sources: list[Vb6Source]
    link_as: str = Field("executable", alias="linkAs")
    extra_flags: list[str] = Field(default_factory=list, alias="extraFlags")
    main_project: Optional[str] = Field(None, alias="mainProject")

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
    sources: list[Vb6Source]
    stdin: str = ""
    timeout_ms: int = Field(DEFAULT_RUN_TIMEOUT_MS, alias="timeoutMs")
    extra_flags: list[str] = Field(default_factory=list, alias="extraFlags")
    main_project: Optional[str] = Field(None, alias="mainProject")

    class Config:
        populate_by_name = True


class CompileAndRunResult(BaseModel):
    compile: CompileResult
    run: Optional[RunResult] = None
    skipped_run_reason: Optional[str] = Field(None, alias="skippedRunReason")

    class Config:
        populate_by_name = True


app = FastAPI(title="Astra vb6 sidecar", version=__version__)


# ────────────────────────────────────────────────────────────────────────
# Routes
# ────────────────────────────────────────────────────────────────────────


@app.get("/health")
def health():
    runtime_ok, missing = _check_runtime()
    return {
        "service": "astra-vb6",
        "version": __version__,
        "tier": _resolve_tier(),
        "workdir": str(WORKDIR),
        "runtimeDir": str(RUNTIME_DIR),
        "runtimeReady": runtime_ok,
        "missingRuntimeArtifacts": missing,
    }


@app.post("/compile", response_model=CompileResult, response_model_by_alias=True)
def compile_endpoint(req: CompileRequest) -> CompileResult:
    _require_runtime()
    return _compile(req.sources, req.link_as, req.extra_flags, req.main_project)


@app.post("/run", response_model=RunResult, response_model_by_alias=True)
def run_endpoint(req: RunRequest) -> RunResult:
    return _run(req.artifact_id, req.stdin, req.timeout_ms)


@app.post("/compile-and-run", response_model=CompileAndRunResult, response_model_by_alias=True)
def compile_and_run_endpoint(req: CompileAndRunRequest) -> CompileAndRunResult:
    _require_runtime()
    compile_result = _compile(req.sources, "executable", req.extra_flags, req.main_project)
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


def _compile(
    sources: list[Vb6Source],
    link_as: str,
    extra_flags: list[str],
    main_project: Optional[str],
) -> CompileResult:
    if not sources:
        raise HTTPException(status_code=400, detail="At least one source file is required.")

    artifact_id = uuid.uuid4().hex
    build_dir = WORKDIR / artifact_id
    build_dir.mkdir(parents=True, exist_ok=False)

    # Materialise sources to disk.
    abs_paths: list[Path] = []
    for src in sources:
        rel = src.path.lstrip("/").replace("/", os.sep)
        abs_path = build_dir / rel
        abs_path.parent.mkdir(parents=True, exist_ok=True)
        abs_path.write_text(src.content, encoding="utf-8")
        abs_paths.append(abs_path)

    # Pick or synthesise the .vbp project file.
    vbp_path = _resolve_main_project(abs_paths, build_dir, main_project, link_as)

    output_exe = build_dir / "app.exe"
    cmd = _build_compile_cmd(vbp_path, output_exe, extra_flags)

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
        raise HTTPException(status_code=504, detail=f"vb6.exe exceeded {COMPILE_TIMEOUT_S}s") from None
    except FileNotFoundError as ex:
        # vb6.exe missing — surface as a 500 with a clear diagnostic.
        shutil.rmtree(build_dir, ignore_errors=True)
        raise HTTPException(
            status_code=500,
            detail=(
                f"vb6.exe not found: {ex}. Per ADR-037 the customer must drop "
                "vb6.exe and the runtime DLLs into the /runtime volume at "
                "deploy time. See /health for the missing artifact list."
            ),
        ) from None

    elapsed_ms = int((time.monotonic() - t0) * 1000)
    log_text = (
        f"=== {' '.join(cmd[1:])} ===\n"
        f"{proc.stdout}\n"
        + (f"=== stderr ===\n{proc.stderr}\n" if proc.stderr else "")
        + f"=== exit {proc.returncode} ===\n"
    )
    # vb6.exe tags diagnostics as "error:" / "warning:" in /make mode.
    # Wine's mediation can rewrite some of these — count both forms.
    lower = log_text.lower()
    err_count = lower.count("error:") + lower.count("fatal:")
    warn_count = lower.count("warning:")
    log.info(
        "compile artifact=%s files=%d project=%s exit=%d ms=%d errors=%d warnings=%d",
        artifact_id, len(sources), vbp_path.name, proc.returncode, elapsed_ms, err_count, warn_count,
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
    binary = build_dir / "app.exe"
    if not binary.exists():
        raise HTTPException(status_code=404, detail=f"Artifact {artifact_id} has no executable.")

    t0 = time.monotonic()
    timed_out = False
    cmd = _build_run_cmd(binary)
    try:
        proc = subprocess.run(
            cmd,
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
                 f"\n[vb6-sidecar] killed after {timeout_ms}ms"

    elapsed_ms = int((time.monotonic() - t0) * 1000)
    # Wine writes its own wine: prefixed diagnostics; surface them as
    # tier-aware stderr so the validator can mark Gate 3 as
    # "skipped (dev-tier)" when COM dispatch unreliability surfaces.
    if _resolve_tier() == "wine" and _looks_like_com_dispatch_error(stdout, stderr):
        stderr = (stderr + "\n[vb6-sidecar] tier=wine; COM dispatch is unreliable on the "
                            "dev-tier sidecar — re-run on the production Windows sidecar "
                            "before signing off Gate 3.").strip()

    log.info(
        "run artifact=%s tier=%s exit=%d ms=%d timed_out=%s stdout_bytes=%d stderr_bytes=%d",
        artifact_id, _resolve_tier(), exit_code, elapsed_ms, timed_out, len(stdout), len(stderr),
    )
    return RunResult(
        exit_code=exit_code,
        stdout=stdout,
        stderr=stderr,
        duration_ms=elapsed_ms,
        timed_out=timed_out,
    )


# ────────────────────────────────────────────────────────────────────────
# Tier detection + compile-cmd builder
# ────────────────────────────────────────────────────────────────────────


def _resolve_tier() -> str:
    """Return 'windows' or 'wine' based on platform + env. 'auto' resolves
    to 'windows' when running on win32 native, else 'wine' (assuming the
    wine container baseline)."""
    if TIER != "auto":
        return TIER
    return "windows" if sys.platform == "win32" else "wine"


def _vb6_exe_path() -> Path:
    """Resolve the path to vb6.exe. Customer drops it under RUNTIME_DIR
    at deploy time; we look in two common layouts:
        <RUNTIME_DIR>/vb6/vb6.exe   (recommended)
        <RUNTIME_DIR>/vb6.exe       (flat layout)
    """
    candidates = [RUNTIME_DIR / "vb6" / "vb6.exe", RUNTIME_DIR / "vb6.exe"]
    for c in candidates:
        if c.exists():
            return c
    # Fall through to the conventional path even if missing — let the
    # subprocess.run raise FileNotFoundError so the HTTP layer surfaces
    # a clear diagnostic.
    return candidates[0]


def _build_compile_cmd(vbp: Path, output_exe: Path, extra_flags: list[str]) -> list[str]:
    vb6_exe = str(_vb6_exe_path())
    rel_vbp = str(vbp.relative_to(WORKDIR / vbp.parent.name).as_posix())
    rel_out = str(output_exe.name)
    args = [vb6_exe, "/make", rel_vbp, "/out", rel_out, *extra_flags]
    if _resolve_tier() == "wine":
        # Under Wine we invoke vb6.exe via the wine launcher.
        return ["wine", *args]
    return args


def _build_run_cmd(binary: Path) -> list[str]:
    if _resolve_tier() == "wine":
        return ["wine", str(binary)]
    return [str(binary)]


def _looks_like_com_dispatch_error(stdout: str, stderr: str) -> bool:
    """Heuristic: did the run output show signs of Wine's COM emulation
    failing? Triggers the tier-aware warning on /run results."""
    needles = ("activex", "createobject", "automation error", "ole error", "iunknown")
    blob = (stdout + "\n" + stderr).lower()
    return any(n in blob for n in needles)


# ────────────────────────────────────────────────────────────────────────
# .vbp project synthesis
# ────────────────────────────────────────────────────────────────────────


def _resolve_main_project(
    abs_paths: list[Path],
    build_dir: Path,
    requested: Optional[str],
    link_as: str,
) -> Path:
    """Pick the .vbp project file. If the caller supplied one, use it;
    otherwise synthesise one from the source list (one Module/Class/Form
    line per .bas/.cls/.frm)."""
    if requested:
        candidate = (build_dir / requested).resolve()
        if not candidate.exists():
            raise HTTPException(
                status_code=400,
                detail=f"mainProject '{requested}' not found in supplied sources.",
            )
        return candidate

    vbps = [p for p in abs_paths if p.suffix.lower() == ".vbp"]
    if vbps:
        return vbps[0]

    if link_as != "executable":
        raise HTTPException(
            status_code=400,
            detail="vb6-sidecar only supports linkAs='executable' for now; "
                   "library / ActiveX-DLL output lands in a follow-on.",
        )

    # Synthesise a minimal .vbp covering every source. The header
    # references stdole2.tlb which ships in the VB6 runtime install
    # set; the customer is responsible for placing it in /runtime per
    # ADR-037 OQ-037-3.
    lines: list[str] = [
        "Type=Exe",
        'Reference=*\\G{00020430-0000-0000-C000-000000000046}#2.0#0#'
        '..\\..\\runtime\\stdole2.tlb#OLE Automation',
    ]
    for p in abs_paths:
        rel = p.relative_to(build_dir).as_posix()
        stem = p.stem
        ext = p.suffix.lower()
        if ext == ".bas":
            lines.append(f"Module={stem}; {rel}")
        elif ext == ".cls":
            lines.append(f"Class={stem}; {rel}")
        elif ext == ".frm":
            lines.append(f"Form={rel}")
    lines += [
        'ExeName32="app.exe"',
        'Name="AstraDriver"',
        'StartMode=0',
        'Title="AstraDriver"',
        'CompilationType=0',
        'OptimizationType=0',
    ]
    driver = build_dir / "AstraDriver.vbp"
    driver.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return driver


# ────────────────────────────────────────────────────────────────────────
# Runtime-DLL preflight
# ────────────────────────────────────────────────────────────────────────


# The minimum runtime artifacts vb6.exe needs to /make a project. The
# customer's deploy script places these under /runtime per ADR-037; this
# list drives the /health "missingRuntimeArtifacts" check.
REQUIRED_RUNTIME = (
    "vb6.exe",          # the compiler itself
    "msvbvm60.dll",     # VB6 Virtual Machine
    "oleaut32.dll",     # OLE Automation
    "stdole2.tlb",      # standard OLE type library
)


def _check_runtime() -> tuple[bool, list[str]]:
    """Walk RUNTIME_DIR for the required artifacts. Returns (ok, missing)."""
    if not RUNTIME_DIR.exists():
        return False, list(REQUIRED_RUNTIME)
    present_lower = {p.name.lower() for p in RUNTIME_DIR.rglob("*") if p.is_file()}
    missing = [name for name in REQUIRED_RUNTIME if name.lower() not in present_lower]
    return not missing, missing


def _require_runtime() -> None:
    ok, missing = _check_runtime()
    if not ok:
        raise HTTPException(
            status_code=503,
            detail=(
                f"vb6-sidecar runtime not ready. Missing artifacts under "
                f"{RUNTIME_DIR}: {', '.join(missing)}. Per ADR-037 the "
                "customer drops these in at deploy time; we do not "
                "redistribute. See the README for the legitimate "
                "acquisition paths."
            ),
        )


def main() -> None:
    import uvicorn
    port = int(os.environ.get("VB6_PORT", "51058"))
    uvicorn.run(
        "vb6_sidecar.server:app",
        host="0.0.0.0",
        port=port,
        log_level="warning",
        access_log=False,
    )


if __name__ == "__main__":
    main()
