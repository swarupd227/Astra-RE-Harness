# Astra E2E — Appendix A demo path

Playwright walk-through of the full demo flow against the running Docker stack.

## Run

```bash
cd app/e2e
npm install                # one-time
npm run install-browsers   # one-time — downloads Chromium for Playwright
npm test                   # runs against http://127.0.0.1:35173
```

Override targets via env:

```bash
BASE_URL=http://localhost:35173 API_BASE=http://localhost:38080 npm test
```

## What the test covers

One `demo-path.spec.ts` test runs the entire Appendix A 4-minute flow:

1. **Engineer** opens `CONSUME_ROLL` → clicks **Extract spec** → waits for streaming
2. Lands on Draft spec → clicks **Route to SME for review**
3. **Switches persona** to SME via the top-right menu
4. Clicks **Accept** on every claim card (12) and **Resolve in spec** on every open question (3)
5. Clicks **Sign spec** → confirms the canonical sentence in the modal → spec goes SIGNED
6. **Switches persona** back to Engineer
7. Clicks **Generate scaffold** → waits for the 6-file stream to complete
8. Clicks **Open scaffold** → asserts the artifact view loads
9. Clicks **Commit to Git** (stub) → asserts the faux commit hash chip renders
10. Asserts the audit trail contains `spec.extracted`, `spec.routed`, `spec.signed`, `scaffold.generated`, and ≥15 `claim.*` events

Total wall-clock: ~25s (12s extract + 6s scaffold + interaction time).

## Headed / debug

```bash
npm run test:headed   # watch the run in a real browser window
npm run test:ui       # Playwright's interactive runner
npm run report        # open the last HTML report
```

## When this fails

The test is single-test by design — **the failure mode points directly at the broken stage**. Read the trace (auto-saved under `test-results/`) and the relevant `test.step` annotation in the failing assertion.

If the stack already has a signed CONSUME_ROLL spec from a previous run, the test takes the **idempotent-sign + open-existing-scaffold** path. The assertions still hold; total wall-clock drops to ~5s.

## Phase B.6 hooks

Tests will split into per-stage specs (`extract.spec.ts`, `review.spec.ts`, `scaffold.spec.ts`) once we have more failure modes to isolate. The single-test shape is right for B.5 because the demo is the only path that needs CI-gating.
