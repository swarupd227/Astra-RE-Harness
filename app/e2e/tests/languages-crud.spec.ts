/**
 * Phase #4.4 — Languages override CRUD (admin-only).
 *
 * Asserts:
 *   - Non-admin PUT/DELETE on /spec-schemas/{id}/override → 403.
 *   - Admin PUT applies an override that surfaces on subsequent GET.
 *   - DELETE reverts the override.
 *   - Unknown schema id → 404 on PUT.
 *   - UI toggle on /platform/languages flips enabled state via the override.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

async function clearOverrides(page: import('@playwright/test').Page) {
  for (const id of ['fortran-f77', 'cobol']) {
    await page.request.delete(`${API_BASE}/api/v1/spec-schemas/${id}/override`, { headers: ADMIN });
  }
}

test.describe('Phase #4.4 · Languages override', () => {
  test.beforeEach(async ({ page }) => {
    page.setDefaultNavigationTimeout(60_000);
    page.setDefaultTimeout(30_000);
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
    await clearOverrides(page);
  });

  test.afterEach(async ({ page }) => {
    await clearOverrides(page);
  });

  test('non-admin override writes are denied', async ({ page }) => {
    const r1 = await page.request.put(`${API_BASE}/api/v1/spec-schemas/fortran-f77/override`, {
      headers: ENG,
      data: { enabled: false },
    });
    expect(r1.status()).toBe(403);
    const r2 = await page.request.delete(`${API_BASE}/api/v1/spec-schemas/fortran-f77/override`, { headers: ENG });
    expect(r2.status()).toBe(403);
  });

  test('admin can toggle a schema disabled and revert it', async ({ page }) => {
    const beforeList = await page.request.get(`${API_BASE}/api/v1/spec-schemas`, { headers: ADMIN }).then((r) => r.json());
    const fBefore = beforeList.data.find((s: { id: string }) => s.id === 'fortran-f77');
    expect(fBefore.enabled).toBe(true);
    expect(fBefore.overrideActive).toBe(false);

    const put = await page.request.put(`${API_BASE}/api/v1/spec-schemas/fortran-f77/override`, {
      headers: ADMIN,
      data: { enabled: false, status: 'paused' },
    });
    expect(put.status()).toBe(200);
    const updated = await put.json();
    expect(updated.enabled).toBe(false);
    expect(updated.status).toBe('paused');
    expect(updated.overrideActive).toBe(true);

    // GET-list also reflects the override
    const list = await page.request.get(`${API_BASE}/api/v1/spec-schemas`, { headers: ADMIN }).then((r) => r.json());
    const f = list.data.find((s: { id: string }) => s.id === 'fortran-f77');
    expect(f.enabled).toBe(false);
    expect(f.status).toBe('paused');

    // Revert
    const del = await page.request.delete(`${API_BASE}/api/v1/spec-schemas/fortran-f77/override`, { headers: ADMIN });
    expect(del.status()).toBe(204);
    const after = await page.request.get(`${API_BASE}/api/v1/spec-schemas/fortran-f77`, { headers: ADMIN }).then((r) => r.json());
    expect(after.enabled).toBe(true);
    expect(after.overrideActive).toBe(false);
  });

  test('unknown schema id is 404', async ({ page }) => {
    const r = await page.request.put(`${API_BASE}/api/v1/spec-schemas/bogus/override`, {
      headers: ADMIN,
      data: { enabled: false },
    });
    expect(r.status()).toBe(404);
  });

  test('UI: admin can flip the enabled toggle on a language card', async ({ page }) => {
    await page.goto('/platform/languages');
    await expect(page.getByTestId('languages-page')).toBeVisible();
    const toggle = page.getByTestId('schema-toggle-fortran-f77');
    await expect(toggle).toBeVisible();
    await expect(toggle).toBeChecked();

    const putReq = page.waitForResponse(
      (r) => r.url().includes('/spec-schemas/fortran-f77/override') && r.request().method() === 'PUT',
      { timeout: 15_000 },
    );
    await toggle.click();
    const resp = await putReq;
    expect(resp.status()).toBe(200);

    // After re-fetch the card should show the Disabled badge
    await expect(page.getByText(/Disabled/).first()).toBeVisible();
  });
});
