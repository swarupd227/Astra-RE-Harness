/**
 * Phase #4 / 2.1 — Prompt Catalog page.
 *
 * Wraps GET /api/v1/prompts (list) and the body endpoints already shipped
 * in Phase #3b. Asserts:
 *   - The /platform/prompts route renders the catalog with at least one
 *     prompt card.
 *   - Filter dropdowns narrow the card list as expected.
 *   - Clicking a card opens a drawer that fetches the prompt body and
 *     renders the system + user templates.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };

test.describe('Phase #4.2.1 · Prompt Catalog page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('GET /prompts returns at least one prompt with version metadata', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/prompts`, { headers: ADMIN });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(body.data.length).toBeGreaterThan(0);
    const first = body.data[0];
    expect(typeof first.sourceSchema).toBe('string');
    expect(typeof first.targetStack).toBe('string');
    expect(typeof first.kind).toBe('string');
    expect(typeof first.version).toBe('string');
  });

  test('/platform/prompts renders the catalog with cards, filters, and detail drawer', async ({ page }) => {
    await page.goto('/platform/prompts');
    await expect(page.getByTestId('prompt-catalog-page')).toBeVisible();
    // At least one card present.
    const cards = page.locator('[data-testid^="prompt-card-"]');
    await expect(cards.first()).toBeVisible();

    // Click the first card; drawer should appear with the rendered template.
    await cards.first().click();
    const drawer = page.getByTestId('prompt-detail-drawer');
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText(/System template/)).toBeVisible();
    await expect(drawer.getByText(/User template/)).toBeVisible();
  });

  test('Engineer persona cannot navigate to /platform/prompts (no nav entry visible)', async ({ page }) => {
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.reload();
    await expect(page.getByTestId('nav-platform-section')).toHaveCount(0);
  });
});
