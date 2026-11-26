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
 *
 * The two page tests build what they need on the seed routine (signed spec,
 * generated code) through the API, so they never skip for want of an earlier run.
 */
import { expect, test } from '@playwright/test';
import { ensureSeedScaffold, ensureSeedSignedSpec } from './helpers/scaffold';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

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
    expect(body.promptLibrary.targetStack).toBe('dotnet10');
  });

  test('Validation page renders the full Provider Settings card with trust chips', async ({ page }) => {
    test.setTimeout(300_000);
    const scaffold = await ensureSeedScaffold(page.request, API_BASE);

    await page.goto(`/scaffolds/${scaffold.id}/validation`);
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
    const { subroutineId } = await ensureSeedSignedSpec(page.request, API_BASE);

    await page.goto(`/subroutines/${subroutineId}/review`);

    const card = page.getByTestId('provider-settings-card');
    await expect(card).toBeVisible();
    // At least one residency-style chip is rendered.
    await expect(card.getByText(/ZDR|No training|No retention|Enterprise endpoint|Offline/).first()).toBeVisible();
  });
});
