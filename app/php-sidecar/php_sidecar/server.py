"""
PHP sidecar — REST surface for running one PHP routine with given inputs.

Endpoints
---------
GET  /health     liveness probe: {service, version, php, sapi}
POST /inspect    body: { files: [{path, content}], entry: {path, name, line?, className?} }
                 -> { routine: {kind, name, class?, static?, params: [{name, type, optional, byRef, default}]},
                      loadError?, prepare }
POST /run        body: { files, entry, vectors: [{id, args? | named?, superglobals?, ctorArgs?}],
                         timeoutMs? (per vector), memoryLimitMb? }
                 -> { routine, loadError?, prepare, results: [{id, ok, returnValue, returnType, output,
                      responseCode, headers, byRef, session, sessionChanged, exception?, missingInput?,
                      loadError?, exited?, warnings, notes, durationMs, timedOut, exitCode}], durationMs }

How a routine is run
--------------------
The request's files are written to a fresh run directory. `driver/run_routine.php prepare` tokenizes them once and
keeps DECLARATIONS ONLY (functions, classes, interfaces, traits, enums, namespace/use/const, literal define()s and
`if (!function_exists(...))` guards) — a script's top-level statements (its own dispatch, includes, database
connections, output) never run. Then every vector runs in its OWN php process (no state leaks between vectors):
`run` loads the declarations (classes on demand through an autoloader), sets $_GET/$_POST/$_REQUEST/$_COOKIE/
$_SERVER/$_SESSION from the vector, binds the inputs to the routine's parameters by name (or position), calls it
inside an output buffer and records what it observed.

Isolation: each php process gets `-n` (no php.ini), a memory_limit and max_execution_time plus hard rlimits
(address space, CPU seconds, file size), open_basedir = the run directory, allow_url_fopen/include off, and every
process, socket, network, mail and curl function disabled; the sidecar itself runs as an unprivileged user. Inputs
are decoded with json_decode and bound as values — nothing from the request is passed to eval().
"""

from __future__ import annotations

import hmac
import json
import logging
import os
import resource
import shutil
import subprocess
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path, PurePosixPath
from typing import Any, Optional

from fastapi import FastAPI, Header, HTTPException
from pydantic import BaseModel

from php_sidecar import __version__

logging.basicConfig(
    level=logging.INFO,
    format='{"ts":"%(asctime)s","level":"%(levelname)s","service":"astra-php","msg":"%(message)s"}',
)
log = logging.getLogger("astra.php")

WORKDIR = Path(os.environ.get("PHP_WORKDIR", "/var/tmp/php-runs"))
WORKDIR.mkdir(parents=True, exist_ok=True)
DRIVER = Path(__file__).resolve().parent / "driver" / "run_routine.php"

MAX_FILES = 600
MAX_TOTAL_BYTES = 8 * 1024 * 1024
MAX_VECTORS = 64
DEFAULT_TIMEOUT_MS = 10_000
MAX_TIMEOUT_MS = 30_000
DEFAULT_MEMORY_MB = 128
MAX_MEMORY_MB = 512
PREPARE_TIMEOUT_S = 60
PARALLEL = int(os.environ.get("PHP_PARALLEL", "4"))

# Everything that reaches outside the process: other programs, sockets, network, mail, signals, env.
DISABLED_FUNCTIONS = ",".join([
    "exec", "shell_exec", "system", "passthru", "proc_open", "popen", "proc_nice", "proc_terminate",
    "pcntl_exec", "pcntl_fork", "pcntl_signal", "posix_kill", "posix_setuid", "posix_setgid", "posix_mkfifo",
    "fsockopen", "pfsockopen", "stream_socket_client", "stream_socket_server", "stream_socket_pair",
    "socket_create", "socket_connect", "socket_bind", "socket_listen",
    "curl_init", "curl_exec", "curl_multi_init", "curl_multi_exec",
    "ftp_connect", "ftp_ssl_connect", "mail", "mb_send_mail", "dl", "putenv", "apache_setenv",
    "link", "symlink", "chown", "chgrp", "gethostbyname", "gethostbynamel", "dns_get_record", "getmxrr",
])


def _php_binary() -> str:
    explicit = os.environ.get("PHP_BINARY")
    if explicit:
        return explicit
    # php-cgi keeps header() / http_response_code() observable (the CLI SAPI drops headers); -q keeps them off stdout.
    return shutil.which("php-cgi") or shutil.which("php") or "php"


PHP_BIN = _php_binary()
SAPI = "cgi" if PHP_BIN.endswith("php-cgi") else "cli"


def _php_version() -> str:
    try:
        cli = shutil.which("php") or PHP_BIN
        return subprocess.run([cli, "-n", "-r", "echo PHP_VERSION;"], capture_output=True, text=True, timeout=10).stdout.strip()
    except Exception:  # pragma: no cover - only when php is missing
        return "unknown"


PHP_VERSION = _php_version()

app = FastAPI(title="Astra PHP sidecar", version=__version__)


# ────────────────────────────────────────────────────────────────────────
# Models
# ────────────────────────────────────────────────────────────────────────

class PhpSource(BaseModel):
    path: str
    content: str


# Field names are the wire names (camelCase), like the API sends them.
class Entry(BaseModel):
    path: Optional[str] = None
    name: str
    line: Optional[int] = None
    className: Optional[str] = None


class Vector(BaseModel):
    id: Optional[str] = None
    args: Optional[list[Any]] = None
    named: Optional[dict[str, Any]] = None
    superglobals: Optional[dict[str, Any]] = None
    ctorArgs: Optional[list[Any]] = None


class InspectRequest(BaseModel):
    files: list[PhpSource]
    entry: Entry


class RunRequest(BaseModel):
    files: list[PhpSource]
    entry: Entry
    vectors: list[Vector]
    timeoutMs: int = DEFAULT_TIMEOUT_MS
    memoryLimitMb: int = DEFAULT_MEMORY_MB


# ────────────────────────────────────────────────────────────────────────
# Routes
# ────────────────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    return {"service": "astra-php", "version": __version__, "php": PHP_VERSION, "sapi": SAPI, "workdir": str(WORKDIR)}


def _authorise(token: Optional[str]) -> None:
    """When PHP_SIDECAR_TOKEN is set (it is on Azure, where the sidecar has an HTTPS address), every call that runs
    code must carry it in X-Astra-Sidecar-Token."""
    expected = os.environ.get("PHP_SIDECAR_TOKEN")
    if expected and not hmac.compare_digest(expected, token or ""):
        raise HTTPException(status_code=401, detail="X-Astra-Sidecar-Token missing or wrong.")


@app.post("/inspect")
def inspect_endpoint(req: InspectRequest, x_astra_sidecar_token: Optional[str] = Header(None)) -> dict:
    _authorise(x_astra_sidecar_token)
    run_dir = _materialise(req.files)
    try:
        prep = _prepare(run_dir, req.files)
        info = _driver(run_dir, "inspect", {"entry": _entry(req.entry)}, "inspect", DEFAULT_TIMEOUT_MS, DEFAULT_MEMORY_MB)
        return {"routine": info.get("routine"), "loadError": info.get("loadError") or info.get("error"), "prepare": prep,
                "version": __version__, "php": PHP_VERSION}
    finally:
        _cleanup(run_dir)


@app.post("/run")
def run_endpoint(req: RunRequest, x_astra_sidecar_token: Optional[str] = Header(None)) -> dict:
    _authorise(x_astra_sidecar_token)
    if not req.vectors:
        raise HTTPException(status_code=400, detail="At least one vector is required.")
    if len(req.vectors) > MAX_VECTORS:
        raise HTTPException(status_code=400, detail=f"At most {MAX_VECTORS} vectors per run.")
    timeout_ms = max(500, min(req.timeoutMs, MAX_TIMEOUT_MS))
    memory_mb = max(16, min(req.memoryLimitMb, MAX_MEMORY_MB))
    started = time.monotonic()
    run_dir = _materialise(req.files)
    try:
        prep = _prepare(run_dir, req.files)
        info = _driver(run_dir, "inspect", {"entry": _entry(req.entry)}, "inspect", timeout_ms, memory_mb)
        routine = info.get("routine")
        load_error = info.get("loadError") or info.get("error")

        def one(i_v):
            i, v = i_v
            payload = {"entry": _entry(req.entry), "vector": _vector(v, i)}
            res = _driver(run_dir, "run", payload, f"v{i}", timeout_ms, memory_mb)
            res.setdefault("id", payload["vector"]["id"])
            return res

        if load_error and routine is None:
            results = [{"id": _vector(v, i)["id"], "ok": False, "loadError": load_error} for i, v in enumerate(req.vectors)]
        else:
            with ThreadPoolExecutor(max_workers=max(1, PARALLEL)) as pool:
                results = list(pool.map(one, enumerate(req.vectors)))
        elapsed = int((time.monotonic() - started) * 1000)
        log.info("run entry=%s files=%d vectors=%d ok=%d ms=%d", req.entry.name, len(req.files), len(results),
                 sum(1 for r in results if r.get("ok")), elapsed)
        return {"routine": routine, "loadError": load_error, "prepare": prep, "results": results, "durationMs": elapsed,
                "version": __version__, "php": PHP_VERSION, "sapi": SAPI}
    finally:
        _cleanup(run_dir)


# ────────────────────────────────────────────────────────────────────────
# Workers
# ────────────────────────────────────────────────────────────────────────

def _safe_rel(path: str) -> str:
    rel = path.replace("\\", "/").lstrip("/")
    parts = PurePosixPath(rel).parts
    if not rel or ".." in parts or any(p.startswith(".astra") for p in parts) or ":" in rel:
        raise HTTPException(status_code=400, detail=f"Rejected path: {path}")
    return rel


def _materialise(files: list[PhpSource]) -> Path:
    if not files:
        raise HTTPException(status_code=400, detail="At least one PHP file is required.")
    if len(files) > MAX_FILES:
        raise HTTPException(status_code=400, detail=f"At most {MAX_FILES} files per run.")
    total = sum(len(f.content.encode("utf-8")) for f in files)
    if total > MAX_TOTAL_BYTES:
        raise HTTPException(status_code=413, detail=f"Files total {total} bytes; the limit is {MAX_TOTAL_BYTES}.")
    run_dir = WORKDIR / uuid.uuid4().hex
    (run_dir / "src").mkdir(parents=True)
    (run_dir / ".io").mkdir()
    (run_dir / ".sessions").mkdir()
    for f in files:
        dest = run_dir / "src" / _safe_rel(f.path)
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(f.content, encoding="utf-8")
    return run_dir


def _prepare(run_dir: Path, files: list[PhpSource]) -> dict:
    root = run_dir / "src"
    payload = {"files": [_safe_rel(f.path) for f in files]}
    return _driver(run_dir, "prepare", payload, "prepare", PREPARE_TIMEOUT_S * 1000, 256, root=root)


def _entry(e: Entry) -> dict:
    d: dict[str, Any] = {"name": e.name}
    if e.path:
        d["path"] = _safe_rel(e.path)
    if e.line:
        d["line"] = e.line
    if e.className:
        d["className"] = e.className
    return d


def _vector(v: Vector, i: int) -> dict:
    d: dict[str, Any] = {"id": v.id or f"v{i + 1}"}
    if v.args is not None:
        d["args"] = v.args
    if v.named is not None:
        d["named"] = v.named
    if v.superglobals is not None:
        d["superglobals"] = v.superglobals
    if v.ctorArgs is not None:
        d["ctorArgs"] = v.ctorArgs
    return d


def _limits(memory_mb: int, timeout_s: int):
    def apply():
        # Address space covers the engine itself plus the script's memory_limit; CPU seconds back up the wall clock.
        cap = (memory_mb * 2 + 384) * 1024 * 1024
        resource.setrlimit(resource.RLIMIT_AS, (cap, cap))
        resource.setrlimit(resource.RLIMIT_CPU, (timeout_s + 1, timeout_s + 2))
        resource.setrlimit(resource.RLIMIT_FSIZE, (16 * 1024 * 1024, 16 * 1024 * 1024))
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
        os.setsid()
    return apply


def _driver(run_dir: Path, mode: str, payload: dict, tag: str, timeout_ms: int, memory_mb: int,
            root: Optional[Path] = None) -> dict:
    root = root or (run_dir / "src")
    io_dir = run_dir / ".io"
    req_path = io_dir / f"{tag}.req.json"
    res_path = io_dir / f"{tag}.res.json"
    req_path.write_text(json.dumps(payload), encoding="utf-8")
    sess = run_dir / ".sessions" / tag
    sess.mkdir(exist_ok=True)
    timeout_s = max(1, (timeout_ms + 999) // 1000)
    flags = [
        "-n",
        "-d", f"memory_limit={memory_mb}M",
        "-d", f"max_execution_time={timeout_s}",
        "-d", "display_errors=0", "-d", "log_errors=0", "-d", "html_errors=0",
        "-d", "error_reporting=32767",
        "-d", "allow_url_fopen=0", "-d", "allow_url_include=0",
        "-d", f"open_basedir={run_dir}{os.pathsep}{DRIVER.parent}",
        "-d", f"disable_functions={DISABLED_FUNCTIONS}",
        "-d", f"session.save_path={sess}", "-d", "session.use_cookies=0", "-d", "session.cache_limiter=",
        "-d", "output_buffering=0", "-d", "date.timezone=UTC", "-d", "cgi.force_redirect=0",
        "-d", f"upload_tmp_dir={run_dir}", "-d", f"sys_temp_dir={run_dir}",
    ]
    cmd = [PHP_BIN, *flags]
    if SAPI == "cgi":
        cmd.append("-q")
    cmd += [str(DRIVER), mode, str(req_path), str(res_path)]
    env = {"PATH": "/usr/local/bin:/usr/bin:/bin", "ASTRA_ROOT": str(root), "ASTRA_MODE": mode,
           "ASTRA_REQUEST": str(req_path), "ASTRA_RESULT": str(res_path), "TZ": "UTC", "LANG": "C.UTF-8"}
    t0 = time.monotonic()
    timed_out = False
    try:
        proc = subprocess.run(cmd, cwd=str(root), env=env, capture_output=True, text=True, errors="replace",
                              timeout=timeout_s + 2, preexec_fn=_limits(memory_mb, timeout_s))
        exit_code, stdout, stderr = proc.returncode, proc.stdout, proc.stderr
    except subprocess.TimeoutExpired as ex:
        timed_out = True
        exit_code = -1
        stdout = ex.stdout.decode("utf-8", "replace") if isinstance(ex.stdout, bytes) else (ex.stdout or "")
        stderr = ex.stderr.decode("utf-8", "replace") if isinstance(ex.stderr, bytes) else (ex.stderr or "")
    elapsed = int((time.monotonic() - t0) * 1000)
    result: dict[str, Any]
    try:
        result = json.loads(res_path.read_text(encoding="utf-8"))
    except Exception:
        result = {"ok": False, "error": "the php process wrote no result"
                  + (f" (killed after {timeout_s}s)" if timed_out else f" (exit {exit_code})")}
    result["timedOut"] = timed_out or bool(result.get("timedOut"))
    result["exitCode"] = exit_code
    result.setdefault("durationMs", elapsed)
    if stderr.strip():
        result["stderr"] = stderr[-2000:]
    if mode != "run" and stdout.strip():
        result["stdout"] = stdout[-2000:]
    return result


def _cleanup(run_dir: Path) -> None:
    if os.environ.get("PHP_KEEP_RUNS") == "1":
        return
    shutil.rmtree(run_dir, ignore_errors=True)


def main() -> None:
    import uvicorn
    port = int(os.environ.get("PHP_PORT", "51060"))
    uvicorn.run("php_sidecar.server:app", host="0.0.0.0", port=port, log_level="warning", access_log=False)


if __name__ == "__main__":
    main()
