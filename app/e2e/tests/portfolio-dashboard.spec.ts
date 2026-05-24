/**
 * Phase 8.0.d — Portfolio dashboard: cross-corpus aggregates.
 *
 *   - Admin GET returns expected shape with totals + per-corpus rows
 *     + LLM rollup + recent activity.
 *   - Engineer GET is 403 (cost data is admin-only).
 *   - UI renders totals + corpora table + LLM cost + activity feed.
 *
 * Pre-requisite: MINPACK corpus seed must be present. The Phase 8.0.b
 * tests already created an approved plan; this test reads what's
 * there but doesn't depend on a specific plan state.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

test.describe('Phase 8.0.d · Portfolio dashboard', () => {
  test('Engineer GET is 403 (admin-only)', async ({ page }) => {
    const r = await page.request.get(
      `${API_BASE}/api/v1/platform/portfolio-summary`,
      { headers: ENG },
    );
    expect(r.status()).toBe(403);
  });

  test('Admin GET returns expected aggregate shape', async ({ page }) => {
    const r = await page.request.get(
      `${API_BASE}/api/v1/platform/portfolio-summary`,
      { headers: ADMIN },
    );
    expect(r.status()).toBe(200);
    const body = await r.json();

    expect(body.totals).toMatchObject({
      corpusCount: expect.any(Number),
      totalRoutines: expect.any(Number),
      signedCount: expect.any(Number),
      scaffoldedCount: expect.any(Number),
      committedCount: expect.any(Number),
      draftCount: expect.any(Number),
      parsedCount: expect.any(Number),
    });
    expect(body.totals.corpusCount).toBeGreaterThanOrEqual(2); // CONSUME_ROLL + MINPACK
    expect(body.totals.totalRoutines).toBeGreaterThanOrEqual(50);

    expect(body.llmTotals).toMatchObject({
      callCount: expect.any(Number),
      inputTokens: expect.any(Number),
      outputTokens: expect.any(Number),
      costUsd: expect.any(Number),
    });

    expect(Array.isArray(body.corpora)).toBe(true);
    expect(body.corpora.length).toBeGreaterThanOrEqual(2);
    // MINPACK should be in the rollup with an approved plan.
    const minpack = body.corpora.find((c: { name: string }) => c.name.includes('MINPACK'));
    expect(minpack).toBeDefined();
    expect(minpack.counts).toMatchObject({
      total: expect.any(Number),
      signed: expect.any(Number),
      scaffolded: expect.any(Number),
    });

    expect(Array.isArray(body.recent)).toBe(true);
  });

  test('UI renders totals + corpora table + activity feed', async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
    await page.goto(`/platform/portfolio`);
    await expect(page.getByTestId('portfolio-dashboard-page')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('portfolio-totals')).toBeVisible();
    await expect(page.getByTestId('portfolio-corpora-table')).toBeVisible();
    await expect(page.getByTestId('portfolio-llm-totals')).toBeVisible();
    await expect(page.getByTestId('portfolio-recent-activity')).toBeVisible();
  });
});
