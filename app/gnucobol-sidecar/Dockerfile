FROM debian:bookworm-slim AS base

WORKDIR /app

# gnucobol provides `cobc` (the COBOL compiler) which we use for the
# legacy-binary side of the per-routine equivalence harness. Python +
# FastAPI for the REST shell. ca-certificates is needed for the pip
# wheel restore.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        gnucobol \
        gcc \
        make \
        python3 \
        python3-pip \
        python3-venv \
 && rm -rf /var/lib/apt/lists/*

# Virtualenv so our deps don't fight the apt-managed python.
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir \
        "fastapi==0.115.5" \
        "uvicorn[standard]==0.32.1"

COPY gnucobol_sidecar/ gnucobol_sidecar/

ENV PYTHONUNBUFFERED=1 \
    GNUCOBOL_PORT=51054 \
    GNUCOBOL_WORKDIR=/var/tmp/gnucobol-runs

EXPOSE 51054

CMD ["python3", "-m", "gnucobol_sidecar.server"]
