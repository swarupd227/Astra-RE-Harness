FROM debian:bookworm-slim AS base

WORKDIR /app

# g++ (GNU C++ compiler) for compiling the C++ source under test, python+pip
# for the REST server. ca-certificates so the optional pip restore can pull
# packages. The bookworm g++ package ships 12.2.0 which covers C++20 fully
# (and most of C++23) — sufficient for the fmt corpus.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        g++ \
        python3 \
        python3-pip \
        python3-venv \
 && rm -rf /var/lib/apt/lists/*

RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir \
        "fastapi==0.115.5" \
        "uvicorn[standard]==0.32.1"

COPY gpp_sidecar/ gpp_sidecar/

ENV PYTHONUNBUFFERED=1 \
    GPP_PORT=51057 \
    GPP_WORKDIR=/var/tmp/gpp-runs

EXPOSE 51057

CMD ["python3", "-m", "gpp_sidecar.server"]
