FROM debian:bookworm-slim AS base

WORKDIR /app

# fpc (Free Pascal Compiler) for compiling the Delphi source under test,
# python+pip for the REST server. ca-certificates so the optional pip
# restore can pull packages. The bookworm fpc package ships 3.2.2 which
# is the same version ADR-024 calls out for the production parser.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        fpc \
        python3 \
        python3-pip \
        python3-venv \
 && rm -rf /var/lib/apt/lists/*

RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir \
        "fastapi==0.115.5" \
        "uvicorn[standard]==0.32.1"

COPY fpc_sidecar/ fpc_sidecar/

ENV PYTHONUNBUFFERED=1 \
    FPC_PORT=51055 \
    FPC_WORKDIR=/var/tmp/fpc-runs

EXPOSE 51055

CMD ["python3", "-m", "fpc_sidecar.server"]
