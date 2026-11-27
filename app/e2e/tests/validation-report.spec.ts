/**
 * Phase #2d — validation report card.
 *
 * Smoke-tests the new /scaffolds/{id}/validation surface:
 *   - The four stage cards render
 *   - Each stage's "Run" CTA triggers the underlying validator
 *   - The summary text + status badges update after a successful run
 *   - The build/test/equivalence log is reachable via the "View log" modal
 *
 * Builds the seed routine's signed spec and generated code through the API if
 * they are not there yet. The gates themselves are real: compile and test pack
 * run `dotnet` inside the API, equivalence needs the gfortran sidecar and the
 * fourth gate the property-test sidecar.
 */
import { expect, test } from '@playwright/test';
import { ensureSeedScaffold } from './helpers/scaffold';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

test.describe('Phase #2d · Validation report card', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('stage cards render; Run buttons trigger validators; logs are reachable', async ({ page }) => {
    test.setTimeout(600_000);
    const { id: scaffoldId } = await ensureSeedScaffold(page.request, API_BASE);

    await page.goto(`/scaffolds/${scaffoldId}/validation`);
    await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible();

    // All four cards present.
    await expect(page.getByTestId('validation-card-compile')).toBeVisible();
    await expect(page.getByTestId('validation-card-test-pack')).toBeVisible();
    await expect(page.getByTestId('validation-card-equivalence')).toBeVisible();
    await expect(page.getByTestId('validation-card-falsifying')).toBeVisible();

    // Helper: click a card's Run button, wait for the underlying POST to land,
    // then for the list-refetch GET to settle. Without this, "Build succeeded"
    // text from a PRIOR run satisfies the visibility check immediately and the
    // page hasn't yet re-rendered with the new run id (which makes View log
    // race against stale state).
    async function triggerStage(cardTestId: string, buttonRegex: RegExp, postPath: string) {
      const card = page.getByTestId(cardTestId);
      const postResp = page.waitForResponse(
        (r) => r.url().includes(postPath) && r.request().method() === 'POST',
        // A gate is a real build or test run; the first one also restores packages.
        { timeout: 300_000 },
      );
      await card.getByRole('button', { name: buttonRegex }).click();
      await postResp;
      // Then wait for the list refetch to bring the new run into view.
      await page.waitForResponse(
        (r) => r.url().includes(`/validation`) && r.request().method() === 'GET',
        { timeout: 30_000 },
      );
    }

    await triggerStage('validation-card-compile', /Run compile/, '/validate/compile');
    const compileCard = page.getByTestId('validation-card-compile');
    await expect(compileCard.getByText(/Build succeeded/)).toBeVisible({ timeout: 60_000 });

    await triggerStage('validation-card-test-pack', /Regenerate \+ run/, '/validate/test-pack');
    const testPackCard = page.getByTestId('validation-card-test-pack');
    await expect(testPackCard.getByText(/tests passed/)).toBeVisible({ timeout: 120_000 });

    await triggerStage('validation-card-equivalence', /Run equivalence/, '/validate/equivalence');
    const equivalenceCard = page.getByTestId('validation-card-equivalence');
    // CONSUME_ROLL has a per-routine harness: gfortran runs the original beside the C# on the same inputs.
    await expect(equivalenceCard.getByText(/Per-unit equivalence: CONSUME_ROLL .* agree row-for-row/))
      .toBeVisible({ timeout: 60_000 });

    // The overall verdict needs all four gates, so run the property-based one too.
    await triggerStage('validation-card-falsifying', /Run 4th gate/, '/validate/falsifying');
    await expect(page.getByTestId('validation-card-falsifying').getByText(/^Passed$/)).toBeVisible({ timeout: 60_000 });
    await expect(page.getByText(/All 4 gates green/)).toBeVisible();

    // Log drill-down works. Use the COMPILE card's View log button.
    await compileCard.getByRole('button', { name: /^View log$/ }).click();
    const dialog = page.getByRole('dialog', { name: /Validation log/ });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText(/dotnet build/)).toBeVisible({ timeout: 15_000 });
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toHaveCount(0);
  });
});
