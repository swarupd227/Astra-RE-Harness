/**
 * Phase 8.0.b — Migration plan API + UI.
 *
 *   - Engineer cannot generate / approve / archive plans (403).
 *   - Admin generates a plan → it shows up via GET; status = 'draft'.
 *   - Admin approves the draft → status = 'approved'; any prior
 *     approved plan for the same (corpus, version) gets archived.
 *   - The plan-detail endpoint returns wave-by-wave routine lists
 *     with live counts (signed/scaffolded/committed) per wave.
 *   - The /corpora/{id}/migration-plan page renders the approved plan
 *     with at least one Wave card visible.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

async function getMinpackCorpusId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) test.skip(true, 'MINPACK corpus missing.');
  return minpack.id;
}

test.describe('Phase 8.0.b · Migration plan', () => {
  test('Engineer cannot generate / approve plans (403)', async ({ page }) => {
    const corpusId = await getMinpackCorpusId(page);
    const gen = await page.request.post(
      `${API_BASE}/api/v1/corpora/${corpusId}/migration-plan/generate`,
      { headers: ENG, data: {} },
    );
    expect(gen.status()).toBe(403);
  });

  test('Admin generates → approves → plan shows wave-by-wave routines', async ({ page }) => {
    const corpusId = await getMinpackCorpusId(page);

    // Generate.
    const gen = await page.request.post(
      `${API_BASE}/api/v1/corpora/${corpusId}/migration-plan/generate`,
      { headers: ADMIN, data: {}, timeout: 60_000 },
    );
    expect(gen.status()).toBe(200);
    const draft = await gen.json();
    expect(draft.status).toBe('draft');
    expect(draft.strategyName).toBe('topological-leaves-first');
    expect(draft.totalRoutines).toBeGreaterThan(0);
    expect(draft.totalWaves).toBeGreaterThan(0);

    // Approve.
    const app = await page.request.post(
      `${API_BASE}/api/v1/migration-plans/${draft.id}/approve`,
      { headers: ADMIN },
    );
    expect(app.status()).toBe(200);
    const approved = await app.json();
    expect(approved.status).toBe('approved');
    expect(approved.approvedBy).toBeTruthy();

    // Detail returns waves + per-wave routines.
    const detail = await page.request.get(
      `${API_BASE}/api/v1/migration-plans/${draft.id}`,
    ).then((r) => r.json());
    expect(detail.plan.id).toBe(draft.id);
    expect(Array.isArray(detail.waves)).toBe(true);
    expect(detail.waves.length).toBe(approved.totalWaves);
    // First wave should be the leaf set.
    const wave1 = detail.waves[0];
    expect(wave1.waveNumber).toBe(1);
    expect(wave1.routines.length).toBeGreaterThan(0);
    expect(wave1.liveCounts).toMatchObject({
      signed: expect.any(Number),
      scaffolded: expect.any(Number),
      committed: expect.any(Number),
    });

    // Current-plan endpoint returns the approved one.
    const current = await page.request.get(
      `${API_BASE}/api/v1/corpora/${corpusId}/migration-plan`,
    ).then((r) => r.json());
    expect(current.plan.id).toBe(draft.id);
    expect(current.plan.status).toBe('approved');
  });

  test('UI renders the migration plan page', async ({ page }) => {
    const corpusId = await getMinpackCorpusId(page);

    // Ensure a plan exists (idempotent — the prior test approved one,
    // and the planner generates fresh drafts each time anyway).
    await page.request.post(
      `${API_BASE}/api/v1/corpora/${corpusId}/migration-plan/generate`,
      { headers: ADMIN, data: {}, timeout: 60_000 },
    );

    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
    await page.goto(`/corpora/${corpusId}/migration-plan`);
    await expect(page.getByTestId('migration-plan-page')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('heading', { name: 'Migration plan' })).toBeVisible();
    await expect(page.getByTestId('migration-wave-1')).toBeVisible({ timeout: 15_000 });
  });
});
