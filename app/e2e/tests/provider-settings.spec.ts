/**
 * Phase #4 / value-add #1 — Provider Settings surface.
 *
 * Asserts the new GET /api/v1/providers/settings endpoint returns the
 * expected shape and the ProviderSettingsCard renders with the residency
 * trust chips on the surfaces a SOC2/SOX reviewer would care about.
 *
 * Surfaces under test:
 *   - GET /api/v1/providers/settings
 *   - /subroutines/:id/spec   (compact strip variant)
 *   - /scaffolds/:id/validation (full card variant)
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

async function getKnownIds(page: Page): Promise<{ subId: string | null; scaffoldId: string | null }> {
  const list = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
  const seed = list.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) return { subId: null, scaffoldId: null };
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`, { headers: ENG }).then((r) => r.json());
  const subId = detail.latestVersion.files[0].subroutines[0].id as string;
  const spec = await page.request
    .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
    .then((r) => r.json());
  if (!spec?.id) return { subId, scaffoldId: null };
  const sc = await page.request.get(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, { headers: ENG });
  if (sc.status() === 404) return { subId, scaffoldId: null };
  const scBody = await sc.json();
  return { subId, scaffoldId: scBody.id as string };
}

test.describe('Phase #4 · Provider Settings surface', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('GET /providers/settings returns structured provider + residency + prompt-library blocks', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/providers/settings`, { headers: ENG });
    expect(res.ok()).toBe(true);
    const body = await res.json();

    // Provider block — shape, not exact values (mock vs anthropic differ)
    expect(body.provider).toBeTruthy();
    expect(typeof body.provider.name).toBe('string');
    expect(typeof body.provider.displayName).toBe('string');
    expect(typeof body.provider.model).toBe('string');

    // Residency block — five booleans + the canonical configVersion string
    expect(body.residency).toBeTruthy();
    expect(typeof body.residency.configVersion).toBe('string');
    expect(typeof body.residency.zdr).toBe('boolean');
    expect(typeof body.residency.noTraining).toBe('boolean');
    expect(typeof body.residency.noRetention).toBe('boolean');
    expect(typeof body.residency.enterpriseEndpoint).toBe('boolean');
    expect(typeof body.residency.offline).toBe('boolean');

    // Prompt library block — schema + target are constants today; extract id+version
    // are only present when the prompt library has loaded the canonical fortran-extract prompt
    expect(body.promptLibrary).toBeTruthy();
    expect(body.promptLibrary.schemaId).toBe('fortran-f77');
    expect(body.promptLibrary.targetStack).toBe('dotnet8');
  });

  test('Validation page renders the full Provider Settings card with trust chips', async ({ page }) => {
    const { scaffoldId } = await getKnownIds(page);
    test.skip(!scaffoldId, 'No SCAFFOLDED CONSUME_ROLL — run demo-path first.');

    await page.goto(`/scaffolds/${scaffoldId}/validation`);
    await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible();

    // Card is present and labelled
    const card = page.getByTestId('provider-settings-card');
    await expect(card).toBeVisible();
    await expect(card.getByText(/AI provider/)).toBeVisible();

    // Residency chips are visible — in real-Anthropic config all four are on;
    // in mock-offline config we get the single "Offline · no network" chip.
    // Either way at least one residency chip must render.
    const trustChips = card.locator('.inline-flex').filter({
      hasText: /ZDR|No training|No retention|Enterprise endpoint|Offline/,
    });
    await expect(trustChips.first()).toBeVisible();
  });

  test('Spec page renders the compact Provider Settings strip', async ({ page }) => {
    const { subId } = await getKnownIds(page);
    test.skip(!subId, 'No CONSUME_ROLL subroutine — seed corpus missing.');

    // Try the review surface first — that's where a SME/observer arrives.
    // Fall back to the draft page if the spec is not yet in review.
    const reviewUrl = `/subroutines/${subId}/review`;
    const draftUrl = `/subroutines/${subId}/spec`;
    await page.goto(reviewUrl);
    if ((await page.getByText(/No spec yet/).count()) > 0) {
      await page.goto(draftUrl);
    }

    const card = page.getByTestId('provider-settings-card');
    await expect(card).toBeVisible();
    // At least one residency-style chip is rendered.
    await expect(card.getByText(/ZDR|No training|No retention|Enterprise endpoint|Offline/)).toBeVisible();
  });
});
