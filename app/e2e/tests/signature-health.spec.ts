/**
 * Phase #4 / 2.4 — Signature Health surface.
 *
 * Asserts:
 *   - GET /api/v1/signature-health returns a portfolio with totalSigned
 *     and drifted counts.
 *   - GET /api/v1/specs/{id}/signature-health returns 'healthy' for a spec
 *     whose corpus has not been re-ingested.
 *   - The /platform/signatures page renders three stat cards + the
 *     portfolio table.
 *   - The Spec-review header shows the inline signature-health badge for
 *     a signed spec.
 *
 * Signs the seed routine through the API if nothing has yet, so the signed-spec
 * tests never skip for want of an earlier run.
 */
import { expect, test } from '@playwright/test';
import { ensureSeedSignedSpec } from './helpers/scaffold';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };

test.describe('Phase #4.2.4 · Signature Health', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('GET /signature-health returns a portfolio shape', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/signature-health`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(typeof body.totalSigned).toBe('number');
    expect(typeof body.drifted).toBe('number');
    expect(Array.isArray(body.rows)).toBe(true);
  });

  test('Per-spec verdict is healthy for a signed spec on the current SourceVersion', async ({ page }) => {
    const { specId } = await ensureSeedSignedSpec(page.request, API_BASE);
    const res = await page.request.get(`${API_BASE}/api/v1/specs/${specId}/signature-health`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(body.state).toBe('healthy');
    expect(body.signerDisplay).toBeTruthy();
  });

  test('/platform/signatures renders stat cards + portfolio table', async ({ page }) => {
    const { specId } = await ensureSeedSignedSpec(page.request, API_BASE);
    const portfolio = await page.request.get(`${API_BASE}/api/v1/signature-health`, { headers: ADMIN }).then((r) => r.json());

    await page.goto('/platform/signatures');
    await expect(page.getByTestId('signature-health-page')).toBeVisible();
    // Each card: its label and the number the API reports.
    const card = (id: string) => page.getByTestId(`signature-stat-${id}`);
    await expect(card('signed')).toHaveText(`Signed specs${portfolio.totalSigned}`);
    await expect(card('healthy')).toHaveText(`Healthy${portfolio.totalSigned - portfolio.drifted}`);
    await expect(card('drifted')).toHaveText(`Drifted${portfolio.drifted}`);
    await expect(page.getByTestId(`portfolio-row-${specId}`)).toContainText('Healthy');
  });

  test('SpecReview header shows the signature-health badge for a signed spec', async ({ page }) => {
    const { specId, subroutineId } = await ensureSeedSignedSpec(page.request, API_BASE);

    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.goto(`/subroutines/${subroutineId}/review`);
    await expect(page.getByTestId(`signature-health-${specId}`)).toHaveText('Healthy');
  });
});
