# Phase 10.0.f — vb6-sidecar production tier per ADR-037.
#
# Windows Server Core 2022 base. Python 3.12 for the FastAPI server.
# Customer drops vb6.exe + msvbvm60.dll + oleaut32.dll + stdole2.tlb
# (and any additional OCX dependencies the customer's corpus uses) into
# the /runtime volume at deploy time — we do NOT redistribute.
#
# Requires Windows containers on the host (Docker Desktop with Windows
# Containers OR a Windows hyper-v isolated node pool in AKS / EKS / GKE).
# Linux dev environments must use Dockerfile.wine instead.

# escape=`

FROM mcr.microsoft.com/windows/servercore:ltsc2022

SHELL ["powershell", "-NoProfile", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

# ── Python 3.12 ──────────────────────────────────────────────────────────
# Download + install the official Python embeddable distribution to keep
# the image small. Embeddable Python ships without pip; we bootstrap pip
# via get-pip.py.
ENV PYTHON_VERSION=3.12.7
RUN Invoke-WebRequest -Uri https://www.python.org/ftp/python/$env:PYTHON_VERSION/python-$env:PYTHON_VERSION-amd64.exe -OutFile python-installer.exe; `
    Start-Process -FilePath python-installer.exe -ArgumentList '/quiet','InstallAllUsers=1','PrependPath=1','Include_test=0' -Wait; `
    Remove-Item python-installer.exe

# Refresh PATH for subsequent layers (the installer updates the registry
# but the running shell needs it re-imported).
ENV PATH="C:\Program Files\Python312;C:\Program Files\Python312\Scripts;${PATH}"

# Install the FastAPI server deps.
RUN python -m pip install --no-cache-dir `
        "fastapi==0.115.5" `
        "uvicorn[standard]==0.32.1"

# ── Volumes ──────────────────────────────────────────────────────────────
# /runtime is where the customer drops:
#   vb6.exe, msvbvm60.dll, oleaut32.dll, stdole2.tlb (minimum)
#   + any OCX controls their corpus uses (mscomctl.ocx, comdlg32.ocx, ...).
# We declare it as a VOLUME so docker compose / k8s can mount the
# customer-managed dir; the README documents the setup script.
#
# /var/tmp/vb6-runs is per-artifact build output; ephemeral.
VOLUME ["C:\\runtime", "C:\\var\\tmp\\vb6-runs"]

# ── App ──────────────────────────────────────────────────────────────────
WORKDIR C:\\app
COPY vb6_sidecar\\ vb6_sidecar\\

ENV PYTHONUNBUFFERED=1 `
    VB6_PORT=51058 `
    VB6_WORKDIR=C:\\var\\tmp\\vb6-runs `
    VB6_RUNTIME_DIR=C:\\runtime `
    VB6_TIER=windows

EXPOSE 51058

# Healthcheck calls our /health endpoint; the response surfaces
# runtimeReady=false until the customer drops the runtime DLLs in.
HEALTHCHECK --interval=10s --timeout=5s --retries=5 `
    CMD powershell -Command "try { (Invoke-WebRequest -Uri http://localhost:51058/health -UseBasicParsing).StatusCode -eq 200 } catch { exit 1 }"

CMD ["python", "-m", "vb6_sidecar.server"]
