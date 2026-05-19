#!/usr/bin/env bash
# Phase 5.7 — Demo readiness check.
#
# Run before any rehearsal recording.  Exits 0 only when every sidecar
# is healthy, the seed corpus is present, and the golden-dataset +
# harmonisation surfaces respond.
#
#   ./scripts/demo-readiness.sh
#
# The script is read-only: it makes no mutations to the stack.

set -euo pipefail

API="${API_BASE:-http://127.0.0.1:38080}"
FRONTEND="${FRONTEND_BASE:-http://127.0.0.1:35173}"
ADMIN_HEADER="X-Dev-Persona: admin"
PASS=0
FAIL=0
SKIPPED=0

ok()    { printf '\033[32m  ✓\033[0m %s\n' "$1"; PASS=$((PASS+1)); }
fail()  { printf '\033[31m  ✗\033[0m %s\n' "$1"; FAIL=$((FAIL+1)); }
skip()  { printf '\033[33m  -\033[0m %s\n' "$1"; SKIPPED=$((SKIPPED+1)); }
hdr()   { printf '\n\033[1m%s\033[0m\n' "$1"; }

# ── Sidecars ────────────────────────────────────────────────────────────
hdr "Sidecars"

probe() {
  local label="$1" url="$2"
  if curl -sf --max-time 3 "$url" > /dev/null 2>&1; then
    ok "$label · $url"
  else
    fail "$label · $url unreachable"
  fi
}

probe "parser-sidecar (gRPC over http)" "http://127.0.0.1:50051/health" || true
probe "gfortran-sidecar"                 "http://127.0.0.1:51052/health"
probe "maven-sidecar"                    "http://127.0.0.1:51053/health"
probe "gnucobol-sidecar"                 "http://127.0.0.1:51054/health"

# ── API ────────────────────────────────────────────────────────────────
hdr "API"

if curl -sf --max-time 3 "$API/health" > /dev/null 2>&1; then
  ok "API · $API/health"
else
  fail "API · $API/health unreachable — cannot continue"
  exit 1
fi

# ── Frontend ───────────────────────────────────────────────────────────
hdr "Frontend"
if curl -sf --max-time 3 "$FRONTEND" > /dev/null 2>&1; then
  ok "Frontend · $FRONTEND"
else
  fail "Frontend · $FRONTEND unreachable"
fi

# ── Seed corpus ────────────────────────────────────────────────────────
hdr "Seed corpus"
corpora=$(curl -sf "$API/api/v1/corpora" || echo '{"data":[]}')
if echo "$corpora" | grep -q "Roll-stock inventory demo"; then
  ok "CONSUME_ROLL synthetic seed present"
else
  fail "CONSUME_ROLL seed missing — run docker compose down -v && up -d"
fi

if echo "$corpora" | grep -qi "minpack"; then
  ok "MINPACK corpus present"
else
  skip "MINPACK corpus not yet seeded (background task — may catch up)"
fi

# ── Golden Dataset ─────────────────────────────────────────────────────
hdr "Golden Dataset"
gd=$(curl -sf "$API/api/v1/golden-dataset" || echo '{"data":[]}')
count=$(echo "$gd" | python3 -c "import sys, json; print(len(json.load(sys.stdin).get('data', [])))" 2>/dev/null || echo 0)
if [ "$count" -ge 100 ]; then
  ok "Golden dataset has $count entries (target: ≥ 100)"
elif [ "$count" -gt 0 ]; then
  fail "Golden dataset has only $count entries — expected ≥ 100"
else
  fail "Golden dataset surface returned no rows"
fi

# ── Harmonisation surface ──────────────────────────────────────────────
hdr "Harmonisation surface"
seed_id=$(echo "$corpora" | python3 -c "
import sys, json
data = json.load(sys.stdin)
for c in data.get('data', []):
    if 'Roll-stock' in c.get('name', ''):
        print(c['id']); break
" 2>/dev/null || echo "")
if [ -n "$seed_id" ]; then
  runs=$(curl -sf -H "$ADMIN_HEADER" "$API/api/v1/corpora/$seed_id/harmonisation" || echo '{"data":[]}')
  if echo "$runs" | grep -q '"data"'; then
    ok "Harmonisation endpoint responds (corpus $seed_id)"
  else
    fail "Harmonisation endpoint did not return a data array"
  fi
fi

# ── COBOL fixture ──────────────────────────────────────────────────────
hdr "Demo fixtures"
if [ -f "app/e2e/fixtures/cobol/DEPTPAY.CBL" ]; then
  ok "DEPTPAY.CBL fixture exists"
else
  fail "DEPTPAY.CBL fixture missing — Phase 5.7 demo cannot ingest COBOL"
fi

# ── LLM provider ──────────────────────────────────────────────────────
hdr "LLM provider"
whoami=$(curl -sf -H "$ADMIN_HEADER" "$API/api/v1/whoami" || echo '{}')
provider=$(echo "$whoami" | python3 -c "import sys, json; print(json.load(sys.stdin).get('llmProvider', ''))" 2>/dev/null || echo "")
case "$provider" in
  anthropic)
    ok "LLM provider: anthropic (real recording mode)"
    ;;
  mock)
    skip "LLM provider: mock — fine for visual rehearsal, switch to anthropic for the actual recording"
    ;;
  fail-mock)
    fail "LLM provider: fail-mock — switch out before recording"
    ;;
  "")
    skip "LLM provider not reported in /whoami (older API)"
    ;;
  *)
    skip "LLM provider: $provider"
    ;;
esac

# ── Summary ────────────────────────────────────────────────────────────
echo
echo "─────────────────────────────────────────────"
printf "  \033[32m%d passed\033[0m · \033[31m%d failed\033[0m · \033[33m%d skipped\033[0m\n" "$PASS" "$FAIL" "$SKIPPED"
echo "─────────────────────────────────────────────"

if [ "$FAIL" -gt 0 ]; then
  echo
  echo "Phase 5.7 demo is NOT ready to record. Fix the failing checks above first."
  exit 1
fi
echo
echo "Phase 5.7 demo readiness: GREEN. Run the Playwright driver with:"
echo "  cd app/e2e && RECORD_DEMO=1 npx playwright test demo-path-phase5.7"
