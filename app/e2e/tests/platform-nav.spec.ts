/**
 * Phase #4 / 2.0 — Platform nav scaffold.
 *
 * Asserts:
 *   - The rail's Platform section is for everyone, but its Admin-only pages
 *     (Overview, Portfolio) and the whole Admin section are hidden from other
 *     personas.
 *   - An Admin gets those, with the configuration pages in the Admin section.
 *   - The /platform index renders for admin and links to each sub-page tile.
 *   - The /platform index gates non-admin access with an "Admin only" message.
 */
import { expect, test, type Page } from '@playwright/test';

/** Folded rail sections open on a click and remember it. */
async function openSection(page: Page, name: 'platform' | 'admin') {
  const header = page.getByTestId(`rail-section-${name}`);
  if ((await header.getAttribute('aria-expanded')) !== 'true') await header.click();
  await expect(header).toHaveAttribute('aria-expanded', 'true');
}

test.describe('Phase #4.2.0 · Platform nav scaffold', () => {
  test('Admin-only navigation is hidden for the engineer persona', async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.reload();
    // Wait for the persona to be known before asserting that anything is absent.
    await expect(page.getByRole('button', { name: 'Persona: Engineer' })).toBeVisible();
    await openSection(page, 'platform');
    const rail = page.getByTestId('left-nav');
    await expect(rail.getByTestId('nav-prompt-catalog')).toBeVisible();
    await expect(rail.getByTestId('rail-section-admin')).toHaveCount(0);
    await expect(rail.getByTestId('nav-overview')).toHaveCount(0);
    await expect(rail.getByTestId('nav-portfolio')).toHaveCount(0);
    await expect(rail.getByTestId('nav-validation-policy')).toHaveCount(0);
  });

  test('Admin persona gets the configuration pages in the rail', async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
    await page.reload();
    await openSection(page, 'platform');
    await openSection(page, 'admin');
    const rail = page.getByTestId('left-nav');
    for (const [id, label] of [
      ['nav-overview', 'Overview'],
      ['nav-prompt-catalog', 'Prompt Catalog'],
      ['nav-languages', 'Languages'],
      ['nav-signature-health', 'Signature Health'],
      ['nav-validation-policy', 'Validation Policy'],
      ['nav-roles-&-permissions', 'Roles & Permissions'],
    ]) {
      await expect(rail.getByTestId(id)).toHaveText(label);
    }
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
