/**
 * Phase 9.3 — 4th validation gate (property-based testing / falsifying-input search).
 *
 * Exercises the new gate end-to-end:
 *   - The 4th `<StageCard>` renders on the validation report
 *   - POST `/api/v1/scaffolds/{id}/validate/falsifying` returns a ValidationRun
 *     with Stage="FALSIFYING" and Status in {PASSED, FAILED, ERRORED}
 *   - When the spec carries generatorHints, the sidecar exercises ≥ 1 claim
 *     and the metrics drilldown surfaces the per-claim breakdown
 *   - In v1 (shadow mode) every run reports `metrics.mode === 'shadow'`
 *
 * We don't depend on a hint-bearing spec being present — the test gracefully
 * accepts the "no hints" path and asserts the validator returns PASSED with
 * a no_hints summary in that case (which is the honest signal per ADR-030).
 */
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

type FalsifyingMetrics = {
  mode?: 'shadow' | 'live';
  claimsExercised: number;
  falsifyingClaimIds: string[];
  totalExamplesTried: number;
  perClaim: Array<{
    claimId: string;
    examplesTried: number;
    falsifying: Record<string, unknown> | null;
    refOutput: string | null;
    candOutput: string | null;
    elapsedMs: number;
    timedOut: boolean;
    callbackErrors: number;
    skipReason: string | null;
  }>;
  overallFalsified: boolean;
};

async function findAnyScaffold(req: APIRequestContext): Promise<{ scaffoldId: string; specId: string } | null> {
  // Walk corpora → subroutines → spec → scaffold to find any SIGNED+SCAFFOLDED
  // pair. The demo path tests typically leave at least one behind.
  const list = await req.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
  for (const corpus of list.data) {
    const detail = await req.get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG }).then((r) => r.json());
    for (const file of detail.latestVersion?.files ?? []) {
      for (const sub of file.subroutines ?? []) {
        const specResp = await req.get(`${API_BASE}/api/v1/subroutines/${sub.id}/spec`, { headers: ENG });
        if (!specResp.ok()) continue;
        const spec = await specResp.json();
        if (spec?.state !== 'SIGNED') continue;
        const scResp = await req.get(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, { headers: ENG });
        if (scResp.status() === 404) continue;
        const sc = await scResp.json();
        return { scaffoldId: sc.id, specId: spec.id };
      }
    }
  }
  return null;
}

test.describe('Phase 9.3 · 4th validation gate (falsifying-input search)', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('API — POST /validate/falsifying returns a FALSIFYING ValidationRun in shadow mode', async ({ request }) => {
    const target = await findAnyScaffold(request);
    test.skip(!target, 'No signed+scaffolded routine found. Run the demo-path tests first.');
    const { scaffoldId } = target!;

    const resp = await request.post(
      `${API_BASE}/api/v1/scaffolds/${scaffoldId}/validate/falsifying`,
      { headers: ENG },
    );
    expect(resp.status()).toBe(200);
    const run = await resp.json();

    expect(run.stage).toBe('FALSIFYING');
    expect(['PASSED', 'FAILED', 'ERRORED']).toContain(run.status);
    expect(run.metrics).toBeTruthy();

    const metrics = run.metrics as FalsifyingMetrics;
    // v1 ships in shadow mode by default; Phase 9.5 adds opt-in live
    // mode behind `LIVE_MODE_4TH_GATE_ENABLED`. Accept either — the
    // harness asserts the mode is valid, not which one fired.
    expect(['shadow', 'live']).toContain(metrics.mode);
    // Shape contract: arrays exist even when empty.
    expect(Array.isArray(metrics.perClaim)).toBeTruthy();
    expect(Array.isArray(metrics.falsifyingClaimIds)).toBeTruthy();

    if (metrics.claimsExercised > 0) {
      // Hint-bearing spec: each exercised claim should report examplesTried
      // and a deterministic elapsedMs. We don't pin a budget number because
      // the sidecar default (100) is configurable, but it must be > 0.
      expect(metrics.totalExamplesTried).toBeGreaterThan(0);
      for (const c of metrics.perClaim) {
        // skipReason is mutually exclusive with examplesTried > 0
        if (c.skipReason === null) {
          expect(c.examplesTried).toBeGreaterThan(0);
        }
      }
      // Without a falsifier, status must be PASSED and the summary mentions examples.
      if (!metrics.overallFalsified) {
        expect(run.status).toBe('PASSED');
        expect(run.summary).toMatch(/exercised|examples/i);
      }
    } else {
      // No-hints path: shadow-mode PASSED + summary calls out the absence of hints.
      expect(run.status).toBe('PASSED');
      expect(run.summary).toMatch(/no.*hints|nothing to exercise/i);
    }
  });

  test('UI — the 4th gate card renders, has a Run button, and surfaces "All 4 gates" copy', async ({ page, request }) => {
    const target = await findAnyScaffold(request);
    test.skip(!target, 'No signed+scaffolded routine found. Run the demo-path tests first.');
    const { scaffoldId } = target!;

    await page.goto(`/scaffolds/${scaffoldId}/validation`);
    await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible();

    // All four cards present, in order.
    await expect(page.getByTestId('validation-card-compile')).toBeVisible();
    await expect(page.getByTestId('validation-card-test-pack')).toBeVisible();
    await expect(page.getByTestId('validation-card-equivalence')).toBeVisible();
    await expect(page.getByTestId('validation-card-falsifying')).toBeVisible();

    // Header copy updated to mention four gates + property-based search.
    await expect(page.getByText(/Four independent gates/i)).toBeVisible();
    await expect(page.getByText(/property-based search/i)).toBeVisible();

    const falsifyingCard = page.getByTestId('validation-card-falsifying');
    const runBtn = falsifyingCard.getByRole('button', { name: /Run 4th gate/ });
    await expect(runBtn).toBeVisible();

    // Trigger the gate. The default budget is 100 inputs at ~170ms callback
    // each = ~17s round-trip — extend the wait accordingly.
    const postResp = page.waitForResponse(
      (r) => r.url().includes('/validate/falsifying') && r.request().method() === 'POST',
      { timeout: 120_000 },
    );
    await runBtn.click();
    await postResp;
    // Wait for list refetch so the card re-renders with the new verdict.
    await page.waitForResponse(
      (r) => r.url().includes('/validation') && r.request().method() === 'GET',
      { timeout: 30_000 },
    );

    // After a clean shadow-mode run, the status badge should report Passed —
    // shadow mode never returns a falsifier and the no-hints path also returns PASSED.
    await expect(falsifyingCard.getByText(/^Passed$/))
      .toBeVisible({ timeout: 30_000 });

    // Expand the metrics drilldown — exactly one of the mode badges must
    // be present (shadow-mode in v1, live-mode after Phase 9.5 when the
    // candidate is wired). We don't pin which; the harness asserts the
    // badge is there at all.
    await falsifyingCard.getByRole('button', { name: /Show metrics/ }).click();
    const shadowBadge = falsifyingCard.getByTestId('falsifying-shadow-mode');
    const liveBadge = falsifyingCard.getByTestId('falsifying-live-mode');
    await expect(shadowBadge.or(liveBadge)).toBeVisible();
  });
});
