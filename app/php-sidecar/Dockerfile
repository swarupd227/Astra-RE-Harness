# Astra PHP sidecar — PHP 8.4 (CLI + CGI SAPIs from the official image) behind a FastAPI shell.
# Runs one PHP routine per vector in its own php-cgi process (php-cgi keeps header()/http_response_code()
# observable; the CLI SAPI drops headers). See php_sidecar/server.py for the isolation rules.
FROM php:8.4-cli-bookworm AS runtime

WORKDIR /app

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-venv \
 && rm -rf /var/lib/apt/lists/*

RUN python3 -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
RUN pip install --no-cache-dir "fastapi==0.115.5" "uvicorn[standard]==0.32.1"

COPY php_sidecar/ php_sidecar/

# Unprivileged: the routines under test never run as root.
RUN useradd --system --uid 10001 --home-dir /var/tmp/php-runs astra \
 && mkdir -p /var/tmp/php-runs && chown astra:astra /var/tmp/php-runs
USER astra

ENV PYTHONUNBUFFERED=1 \
    PHP_PORT=51060 \
    PHP_WORKDIR=/var/tmp/php-runs

EXPOSE 51060

CMD ["python3", "-m", "php_sidecar.server"]

# `docker build --target test .` runs the sidecar's pytest suite inside the image it ships in.
FROM runtime AS test
USER root
RUN pip install --no-cache-dir "pytest==8.3.3" "httpx==0.27.2"
COPY tests/ tests/
USER astra
CMD ["python3", "-m", "pytest", "-q", "tests"]
