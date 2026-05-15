/**
 * Phase #4 / 2.2 — Languages page.
 *
 * Wraps GET /api/v1/spec-schemas (Phase #3a). Asserts:
 *   - The /platform/languages route renders cards for the loaded schemas
 *     (fortran-f77, cobol) AND for the roadmap entries (RPG, PL/1).
 *   - Clicking a loaded-schema card opens a drawer with the claim-kind
 *     taxonomy table.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };

test.describe('Phase #4.2.2 · Languages page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('GET /spec-schemas returns at least fortran-f77 with typed claim kinds', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/spec-schemas`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const ids = new Set<string>(body.data.map((s: { id: string }) => s.id));
    expect(ids.has('fortran-f77')).toBe(true);
    const fortran = body.data.find((s: { id: string }) => s.id === 'fortran-f77');
    expect(fortran.claimKinds.length).toBeGreaterThan(0);
  });

  test('/platform/languages shows loaded schemas + roadmap entries; drawer renders claim taxonomy', async ({ page }) => {
    await page.goto('/platform/languages');
    await expect(page.getByTestId('languages-page')).toBeVisible();
    // Loaded schemas
    await expect(page.getByTestId('language-card-fortran-f77')).toBeVisible();
    await expect(page.getByTestId('language-card-cobol')).toBeVisible();
    // Roadmap entries
    await expect(page.getByTestId('language-card-rpg')).toBeVisible();
    await expect(page.getByTestId('language-card-pl1')).toBeVisible();

    // Open the Fortran drawer
    await page.getByTestId('language-card-fortran-f77').click();
    const drawer = page.getByTestId('language-detail-drawer');
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText(/Claim kinds/)).toBeVisible();
  });
});
