FROM debian:bookworm-slim AS base

WORKDIR /app

# Just python + pip — the sidecar shells nothing out, it just runs
# Hypothesis and talks HTTP to the caller-supplied equivalence callback.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        python3 \
        python3-pip \
        python3-venv \
 && rm -rf /var/lib/apt/lists/*

RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"

RUN pip install --no-cache-dir \
        "fastapi==0.115.5" \
        "uvicorn[standard]==0.32.1" \
        "httpx==0.27.2" \
        "hypothesis==6.122.3"

COPY property_test_sidecar/ property_test_sidecar/

ENV PYTHONUNBUFFERED=1 \
    PROPERTY_TEST_PORT=51056

EXPOSE 51056

CMD ["python3", "-m", "property_test_sidecar.server"]
