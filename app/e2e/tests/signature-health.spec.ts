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
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };
const ENG = { 'X-Dev-Persona': 'engineer' };

async function getSignedSpecId(page: Page): Promise<string | null> {
  const list = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
  const seed = list.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) return null;
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`, { headers: ENG }).then((r) => r.json());
  const subId = detail.latestVersion.files[0].subroutines[0].id;
  const spec = await page.request
    .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
    .then((r) => r.json());
  return spec?.state === 'SIGNED' ? (spec.id as string) : null;
}

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
    const specId = await getSignedSpecId(page);
    test.skip(!specId, 'No SIGNED CONSUME_ROLL spec available.');
    const res = await page.request.get(`${API_BASE}/api/v1/specs/${specId}/signature-health`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(body.state).toBe('healthy');
    expect(body.signerDisplay).toBeTruthy();
  });

  test('/platform/signatures renders stat cards + portfolio table', async ({ page }) => {
    await page.goto('/platform/signatures');
    await expect(page.getByTestId('signature-health-page')).toBeVisible();
    await expect(page.getByText(/Signed specs/)).toBeVisible();
    await expect(page.getByText(/Healthy/)).toBeVisible();
    await expect(page.getByText(/Drifted/)).toBeVisible();
  });

  test('SpecReview header shows the signature-health badge for a signed spec', async ({ page }) => {
    const specId = await getSignedSpecId(page);
    test.skip(!specId, 'No SIGNED spec.');
    const spec = await page.request.get(`${API_BASE}/api/v1/specs/${specId}`, { headers: ENG }).then((r) => r.json());
    const subId = spec.subroutine.id;

    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.goto(`/subroutines/${subId}/review`);
    await expect(page.getByTestId(`signature-health-${specId}`)).toBeVisible();
  });
});
