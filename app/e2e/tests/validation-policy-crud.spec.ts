/**
 * Phase #4.2 — Validation Policy override CRUD (admin-only).
 *
 * Asserts:
 *   - Non-admin gets 403 on PUT and DELETE.
 *   - Admin PUT merges into the effective policy and flips overrideActive
 *     to true.
 *   - DELETE override reverts overrideActive to false.
 *   - The UI's Edit → Save flow round-trips a change.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

test.describe('Phase #4.2 · Validation Policy override', () => {
  test.beforeEach(async ({ page }) => {
    page.setDefaultNavigationTimeout(60_000);
    page.setDefaultTimeout(30_000);
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test.afterEach(async ({ page }) => {
    // Ensure we don't leak an override between tests.
    await page.request.delete(`${API_BASE}/api/v1/validation/policy/override`, { headers: ADMIN });
  });

  test('non-admin PUT and DELETE are denied with 403', async ({ page }) => {
    const put = await page.request.put(`${API_BASE}/api/v1/validation/policy`, {
      headers: ENG,
      data: { gates: [{ id: 'test_pack', required: false }] },
    });
    expect(put.status()).toBe(403);
    const del = await page.request.delete(`${API_BASE}/api/v1/validation/policy/override`, { headers: ENG });
    expect(del.status()).toBe(403);
  });

  test('admin PUT merges override; DELETE reverts; audit rows are written', async ({ page }) => {
    const before = await page.request.get(`${API_BASE}/api/v1/validation/policy`, { headers: ADMIN }).then((r) => r.json());
    expect(before.overrideActive).toBe(false);

    const put = await page.request.put(`${API_BASE}/api/v1/validation/policy`, {
      headers: ADMIN,
      data: {
        gates: [{ id: 'test_pack', required: false, coverageThreshold: '90% min' }],
        retryDefaults: { autoRetryCount: 3, transientFlakeWindow: 'PT10M', note: 'flake-window=10m' },
      },
    });
    expect(put.status()).toBe(200);
    const merged = await put.json();
    expect(merged.overrideActive).toBe(true);
    const tp = merged.gates.find((g: { id: string }) => g.id === 'test_pack');
    expect(tp.required).toBe(false);
    expect(tp.coverageThreshold).toBe('90% min');
    expect(merged.retryDefaults.autoRetryCount).toBe(3);

    // Audit row exists
    const audit = await page.request.get(`${API_BASE}/api/v1/audit?targetType=platform_config&limit=10`, { headers: ADMIN });
    if (audit.ok()) {
      const events = (await audit.json()).data?.map((e: { eventType: string }) => e.eventType) ?? [];
      expect(events).toContain('validation.policy_updated');
    }

    const del = await page.request.delete(`${API_BASE}/api/v1/validation/policy/override`, { headers: ADMIN });
    expect(del.status()).toBe(204);

    const after = await page.request.get(`${API_BASE}/api/v1/validation/policy`, { headers: ADMIN }).then((r) => r.json());
    expect(after.overrideActive).toBe(false);
    const tpAfter = after.gates.find((g: { id: string }) => g.id === 'test_pack');
    expect(tpAfter.required).toBe(true); // back to canonical
  });

  test('invalid PUT bodies are rejected with 400', async ({ page }) => {
    // Unknown gate id
    const r1 = await page.request.put(`${API_BASE}/api/v1/validation/policy`, {
      headers: ADMIN,
      data: { gates: [{ id: 'bogus', required: false }] },
    });
    expect(r1.status()).toBe(400);
    // Bad retry count
    const r2 = await page.request.put(`${API_BASE}/api/v1/validation/policy`, {
      headers: ADMIN,
      data: { gates: [{ id: 'compile', required: true }], retryDefaults: { autoRetryCount: 99, transientFlakeWindow: 'PT5M', note: 'x' } },
    });
    expect(r2.status()).toBe(400);
  });

  test('UI: admin sees Edit + Save; saving flips overrideActive', async ({ page }) => {
    await page.goto('/platform/validation');
    await expect(page.getByTestId('validation-policy-page')).toBeVisible();
    await expect(page.getByTestId('policy-edit')).toBeVisible();
    await page.getByTestId('policy-edit').click();

    // Flip test_pack's "required" checkbox.
    const toggle = page.getByTestId('policy-gate-test_pack-required-toggle');
    await expect(toggle).toBeVisible();
    await toggle.click();

    const saveReq = page.waitForResponse(
      (r) => r.url().includes('/api/v1/validation/policy') && r.request().method() === 'PUT',
      { timeout: 15_000 },
    );
    await page.getByTestId('policy-save').click();
    const resp = await saveReq;
    expect(resp.status()).toBe(200);
    await expect(page.getByText(/Admin override active/i)).toBeVisible();
  });
});
