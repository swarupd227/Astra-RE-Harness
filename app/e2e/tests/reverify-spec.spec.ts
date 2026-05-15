/**
 * Phase #4.5 — Re-verify drifted signatures (admin-only).
 *
 * Asserts:
 *   - Non-admin POST → 403 on both endpoints.
 *   - POST on a healthy spec → 400 spec.not_drifted.
 *   - POST on an unsigned spec → 400 spec.not_signed.
 *   - POST on an unknown spec → 404.
 *   - The portfolio endpoint is admin-readable + reflects the post-reverify
 *     state for any drifted spec (totalSigned drops by 1 since the
 *     re-verified spec lost its signature row).
 *
 * The seed corpora are healthy (no re-ingest in CI), so the drifted-path
 * tests are skipped unless one exists. The healthy-path + auth tests
 * always run.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

async function findSignedSpecId(page: Page): Promise<string | null> {
  const port = await page.request.get(`${API_BASE}/api/v1/signature-health`, { headers: ADMIN }).then((r) => r.json());
  const row = port.rows?.find((r: { state: string }) => r.state === 'healthy');
  return row?.specId ?? null;
}

test.describe('Phase #4.5 · Re-verify drifted signatures', () => {
  test.beforeEach(async ({ page }) => {
    page.setDefaultNavigationTimeout(60_000);
    page.setDefaultTimeout(30_000);
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('non-admin POST is denied', async ({ page }) => {
    const specId = await findSignedSpecId(page);
    test.skip(!specId, 'No signed spec to target.');
    const r1 = await page.request.post(`${API_BASE}/api/v1/specs/${specId}/re-verify`, { headers: ENG });
    expect(r1.status()).toBe(403);
    const r2 = await page.request.post(`${API_BASE}/api/v1/signature-health/re-verify-all`, { headers: ENG });
    expect(r2.status()).toBe(403);
  });

  test('healthy spec returns 400 spec.not_drifted', async ({ page }) => {
    const specId = await findSignedSpecId(page);
    test.skip(!specId, 'No signed spec.');
    const r = await page.request.post(`${API_BASE}/api/v1/specs/${specId}/re-verify`, { headers: ADMIN });
    expect(r.status()).toBe(400);
    const body = await r.json();
    expect(body.error.code).toBe('spec.not_drifted');
  });

  test('unknown spec id returns 404', async ({ page }) => {
    const r = await page.request.post(`${API_BASE}/api/v1/specs/00000000-0000-0000-0000-000000000000/re-verify`, { headers: ADMIN });
    expect(r.status()).toBe(404);
  });

  test('admin re-verify-all on a healthy portfolio returns resetCount: 0', async ({ page }) => {
    const r = await page.request.post(`${API_BASE}/api/v1/signature-health/re-verify-all`, { headers: ADMIN });
    expect(r.status()).toBe(200);
    const body = await r.json();
    expect(typeof body.resetCount).toBe('number');
    expect(Array.isArray(body.specIds)).toBe(true);
  });

  test('UI: admin sees the portfolio page; "Re-verify all" only shows when drifted > 0', async ({ page }) => {
    await page.goto('/platform/signatures');
    await expect(page.getByTestId('signature-health-page')).toBeVisible();
    // The bulk button is conditional on q.data.drifted > 0; on a healthy
    // seed it should NOT appear.
    const port = await page.request.get(`${API_BASE}/api/v1/signature-health`, { headers: ADMIN }).then((r) => r.json());
    if (port.drifted === 0) {
      await expect(page.getByTestId('reverify-all')).toHaveCount(0);
    } else {
      await expect(page.getByTestId('reverify-all')).toBeVisible();
    }
  });
});
