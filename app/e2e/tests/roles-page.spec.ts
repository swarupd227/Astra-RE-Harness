/**
 * Phase #4 / 2.5 — Roles & Permissions matrix page.
 *
 * Asserts:
 *   - GET /api/v1/personas returns four personas (engineer, sme, observer, admin).
 *   - GET /api/v1/personas/matrix returns categorised actions.
 *   - The /platform/roles page renders four persona cards + a capability
 *     matrix with at least one allowed cell per category and the expected
 *     Admin-only "manage_roles" row.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };

test.describe('Phase #4.2.5 · Roles & Permissions page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('GET /personas returns the four canonical personas', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/personas`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const ids = new Set<string>(body.data.map((p: { id: string }) => p.id));
    expect(ids.has('engineer')).toBe(true);
    expect(ids.has('sme')).toBe(true);
    expect(ids.has('observer')).toBe(true);
    expect(ids.has('admin')).toBe(true);
  });

  test('GET /personas/matrix groups actions by category and gates manage_roles to admin only', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/personas/matrix`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const categories = new Set<string>(body.actions.map((a: { category: string }) => a.category));
    expect(categories.has('Pipeline')).toBe(true);
    expect(categories.has('Review')).toBe(true);
    expect(categories.has('Audit')).toBe(true);
    expect(categories.has('Platform')).toBe(true);

    const manageRoles = body.actions.find((a: { id: string }) => a.id === 'manage_roles');
    expect(manageRoles.allowedPersonas).toEqual(['admin']);
  });

  test('/platform/roles renders four persona cards + the capability matrix', async ({ page }) => {
    await page.goto('/platform/roles');
    await expect(page.getByTestId('roles-page')).toBeVisible();
    await expect(page.getByTestId('persona-card-engineer')).toBeVisible();
    await expect(page.getByTestId('persona-card-sme')).toBeVisible();
    await expect(page.getByTestId('persona-card-observer')).toBeVisible();
    await expect(page.getByTestId('persona-card-admin')).toBeVisible();

    // The Sign-spec action allows SME and Admin only.
    const signSme = page.getByTestId('matrix-cell-sign_spec-sme');
    const signEng = page.getByTestId('matrix-cell-sign_spec-engineer');
    await expect(signSme).toHaveAttribute('aria-label', /SME can/i);
    await expect(signEng).toHaveAttribute('aria-label', /Engineer cannot/i);
  });
});
