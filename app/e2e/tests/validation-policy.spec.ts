/**
 * Phase #4 / 2.3 — Validation Policy page.
 *
 * Read-only today. Asserts:
 *   - GET /api/v1/validation/policy returns three gates (compile, test_pack,
 *     equivalence), each marked required, and a commit gate that requires
 *     all-green.
 *   - The /platform/validation page renders the commit-gate card, three
 *     gate cards, and the retry policy block.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };

test.describe('Phase #4.2.3 · Validation Policy page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('GET /validation/policy returns three required gates + an all-green commit gate', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/validation/policy`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const gateIds = new Set<string>(body.gates.map((g: { id: string }) => g.id));
    expect(gateIds.has('compile')).toBe(true);
    expect(gateIds.has('test_pack')).toBe(true);
    expect(gateIds.has('equivalence')).toBe(true);
    for (const g of body.gates) expect(g.required).toBe(true);
    expect(body.commitGate.requireAllGreen).toBe(true);
  });

  test('/platform/validation renders three gate cards + commit-gate + retry policy blocks', async ({ page }) => {
    await page.goto('/platform/validation');
    await expect(page.getByTestId('validation-policy-page')).toBeVisible();
    await expect(page.getByTestId('policy-gate-compile')).toBeVisible();
    await expect(page.getByTestId('policy-gate-test_pack')).toBeVisible();
    await expect(page.getByTestId('policy-gate-equivalence')).toBeVisible();
    await expect(page.getByText(/Commit gate/)).toBeVisible();
    await expect(page.getByText(/Retry & flake policy/)).toBeVisible();
  });
});
