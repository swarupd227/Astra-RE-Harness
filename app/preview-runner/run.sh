#!/bin/sh
# Fetch the package, build the front end, serve everything on $PORT. PACKAGE_URL is the only input.
set -eu

: "${PACKAGE_URL:?PACKAGE_URL is required}"
mkdir -p /app && cd /app
echo "[preview] fetching package"
curl -fsSL "$PACKAGE_URL" -o package.zip
unzip -q -o package.zip
rm -f package.zip

echo "[preview] installing the back end"
python3 -m venv /venv
/venv/bin/pip install --quiet --disable-pip-version-check -r backend/requirements.txt uvicorn

echo "[preview] building the front end"
cd /app/frontend
npm install --no-audit --no-fund --loglevel=error
npx vite build

echo "[preview] serving on $PORT"
cp /opt/preview_main.py /app/backend/preview_main.py
cd /app/backend
exec /venv/bin/uvicorn preview_main:app --host 0.0.0.0 --port "${PORT:-8080}"
