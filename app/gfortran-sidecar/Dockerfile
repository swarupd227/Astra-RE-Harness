FROM debian:bookworm-slim AS base

WORKDIR /app

# gfortran for compiling the source under test, python+pip for the REST
# server. ca-certificates so the optional pip restore can pull packages.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        gfortran \
        gcc \
        make \
        python3 \
        python3-pip \
        python3-venv \
 && rm -rf /var/lib/apt/lists/*

# Virtualenv to keep our deps separate from the system python managed by apt.
RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir \
        "fastapi==0.115.5" \
        "uvicorn[standard]==0.32.1"

COPY gfortran_sidecar/ gfortran_sidecar/

ENV PYTHONUNBUFFERED=1 \
    GFORTRAN_PORT=51052 \
    GFORTRAN_WORKDIR=/var/tmp/gfortran-runs

EXPOSE 51052

CMD ["python3", "-m", "gfortran_sidecar.server"]
