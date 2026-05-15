/**
 * One-off — capture a PNG per Phase 1+2 surface so the demo video can
 * splice them onto the end of the existing HYBRD1 walkthrough.
 *
 * Not part of the regular suite — invoke explicitly:
 *   npx playwright test snap-platform-tour --project=chromium
 *
 * Each shot lands in demo-output/tour/NN-name.png at the configured
 * viewport (1600x1000). The downstream ffmpeg pipeline scales to 800x500.
 */
import { test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

const OUT = 'demo-output/tour';

async function setPersona(page: Page, persona: 'engineer' | 'sme' | 'admin') {
  await page.goto('/');
  await page.evaluate((p) => window.localStorage.setItem('astra.devPersona', p), persona);
}

async function findSignedSubroutineId(page: Page): Promise<string | null> {
  // Pick the first SIGNED routine across all projects. The demo seed
  // produces five (FDJAC1, HYBRJ1, LMDER1, LMDIF1, R1UPDT).
  const res = await page.request.get(`${API_BASE}/api/v1/subroutines?state=SIGNED&limit=1`, {
    headers: { 'X-Dev-Persona': 'engineer' },
  });
  if (!res.ok()) return null;
  const body = await res.json();
  return body.data[0]?.id ?? null;
}

test.describe.configure({ mode: 'serial', timeout: 180_000 });

test.beforeEach(async ({ page }) => {
  // Vite cold-compile + dev-mode bundling can spike to 30s+; the global
  // 15s navigation timeout is too tight when we re-load each page from
  // scratch. Snap tests aren't latency-sensitive.
  page.setDefaultNavigationTimeout(90_000);
  page.setDefaultTimeout(45_000);
});

test.describe('Snap platform-tour', () => {
  test('01 · Provider Settings card (compact strip on Spec page)', async ({ page }) => {
    await setPersona(page, 'engineer');
    const subId = await findSignedSubroutineId(page);
    test.skip(!subId, 'No SIGNED subroutine — run demo-path first.');
    await page.goto(`/subroutines/${subId}/review`);
    // Wait for the provider settings card to render.
    await page.waitForSelector('[data-testid="provider-settings-card"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/01-provider-settings.png`, fullPage: false });
  });

  test('02 · Target Selector (on SIGNED spec)', async ({ page }) => {
    await setPersona(page, 'engineer');
    const subId = await findSignedSubroutineId(page);
    test.skip(!subId, 'No SIGNED subroutine.');
    await page.goto(`/subroutines/${subId}/review`);
    await page.waitForSelector('[data-testid="target-selector"]', { state: 'visible' });
    // Scroll the selector into view so it dominates the visible area.
    await page.locator('[data-testid="target-selector"]').scrollIntoViewIfNeeded();
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/02-target-selector.png`, fullPage: false });
  });

  test('03 · Compliance page (format cards + column table)', async ({ page }) => {
    await setPersona(page, 'engineer');
    await page.goto('/compliance');
    await page.waitForSelector('[data-testid="compliance-page"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/03-compliance.png`, fullPage: false });
  });

  test('04 · Platform index (admin)', async ({ page }) => {
    await setPersona(page, 'admin');
    await page.goto('/platform');
    await page.waitForSelector('[data-testid="platform-index-page"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/04-platform-index.png`, fullPage: false });
  });

  test('05 · Prompt Catalog list', async ({ page }) => {
    await setPersona(page, 'admin');
    await page.goto('/platform/prompts');
    await page.waitForSelector('[data-testid="prompt-catalog-page"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/05-prompts-list.png`, fullPage: false });
  });

  test('06 · Prompt detail drawer', async ({ page }) => {
    await setPersona(page, 'admin');
    await page.goto('/platform/prompts');
    await page.waitForSelector('[data-testid^="prompt-card-"]', { state: 'visible' });
    await page.locator('[data-testid^="prompt-card-"]').first().click();
    await page.waitForSelector('[data-testid="prompt-detail-drawer"]', { state: 'visible' });
    await page.waitForTimeout(500);
    await page.screenshot({ path: `${OUT}/06-prompt-drawer.png`, fullPage: false });
  });

  test('07 · Languages', async ({ page }) => {
    await setPersona(page, 'admin');
    await page.goto('/platform/languages');
    await page.waitForSelector('[data-testid="languages-page"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/07-languages.png`, fullPage: false });
  });

  test('08 · Roles & Permissions', async ({ page }) => {
    await setPersona(page, 'admin');
    await page.goto('/platform/roles');
    await page.waitForSelector('[data-testid="roles-page"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/08-roles.png`, fullPage: false });
  });

  test('09 · Validation Policy', async ({ page }) => {
    await setPersona(page, 'admin');
    await page.goto('/platform/validation');
    await page.waitForSelector('[data-testid="validation-policy-page"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/09-validation-policy.png`, fullPage: false });
  });

  test('10 · Signature Health portfolio', async ({ page }) => {
    await setPersona(page, 'admin');
    await page.goto('/platform/signatures');
    await page.waitForSelector('[data-testid="signature-health-page"]', { state: 'visible' });
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${OUT}/10-signature-health.png`, fullPage: false });
  });
});
