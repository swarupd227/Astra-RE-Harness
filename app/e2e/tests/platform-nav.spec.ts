/**
 * Phase #4 / 2.0 — Platform nav scaffold.
 *
 * Asserts:
 *   - The Platform section is hidden from non-admin personas (engineer / SME).
 *   - When persona = admin, the Platform section is visible and lists the
 *     five sub-pages (Prompts / Languages / Validation / Signatures / Roles)
 *     even when they're still showing a "Coming in 2.X" placeholder.
 *   - The /platform index renders for admin and links to each sub-page tile.
 *   - The /platform index gates non-admin access with an "Admin only" message.
 */
import { expect, test } from '@playwright/test';

test.describe('Phase #4.2.0 · Platform nav scaffold', () => {
  test('Platform section is hidden for engineer persona', async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.reload();
    await expect(page.getByTestId('nav-platform-section')).toHaveCount(0);
  });

  test('Platform section appears for admin persona with the 5 sub-page links', async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
    await page.reload();
    const section = page.getByTestId('nav-platform-section');
    await expect(section).toBeVisible();
    // Each sub-page entry must be rendered even when placeholdered.
    await expect(section.getByText(/Prompt Catalog/)).toBeVisible();
    await expect(section.getByText(/Languages/)).toBeVisible();
    await expect(section.getByText(/Validation Policy/)).toBeVisible();
    await expect(section.getByText(/Signature Health/)).toBeVisible();
    await expect(section.getByText(/Roles & Permissions/)).toBeVisible();
  });

  test('/platform index renders for admin with one tile per upcoming surface', async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
    await page.goto('/platform');
    await expect(page.getByTestId('platform-index-page')).toBeVisible();
    await expect(page.getByTestId('platform-tile-prompt-catalog')).toBeVisible();
    await expect(page.getByTestId('platform-tile-languages')).toBeVisible();
    await expect(page.getByTestId('platform-tile-validation-policy')).toBeVisible();
    await expect(page.getByTestId('platform-tile-signature-health')).toBeVisible();
    await expect(page.getByTestId('platform-tile-roles-permissions')).toBeVisible();
  });

  test('/platform index gates non-admin personas with an Admin-only message', async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.goto('/platform');
    await expect(page.getByText(/Admin only/i)).toBeVisible();
  });
});
