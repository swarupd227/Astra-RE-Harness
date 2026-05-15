/**
 * Phase #2d — validation report card.
 *
 * Smoke-tests the new /scaffolds/{id}/validation surface:
 *   - The three stage cards render
 *   - Each stage's "Run" CTA triggers the underlying validator
 *   - The summary text + status badges update after a successful run
 *   - The build/test/equivalence log is reachable via the "View log" modal
 *
 * Assumes the demo-path test has already scaffolded a CONSUME_ROLL spec
 * (which is the common e2e prelude). We pick the seeded scaffold by
 * walking the seed corpus rather than relying on a SCAFFOLDED state from
 * a previous test run, since playwright workers don't share state by
 * default.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

async function getScaffoldId(page: Page): Promise<string | null> {
  const list = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
  const seed = list.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) return null;
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`, { headers: ENG }).then((r) => r.json());
  const subId = detail.latestVersion.files[0].subroutines[0].id;
  const spec = await page.request.get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG }).then((r) => r.json());
  if (!spec?.id) return null;
  const sc = await page.request.get(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, { headers: ENG });
  if (sc.status() === 404) return null;
  const scBody = await sc.json();
  return scBody.id;
}

test.describe('Phase #2d · Validation report card', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('three stage cards render; Run buttons trigger validators; logs are reachable', async ({ page }) => {
    const scaffoldId = await getScaffoldId(page);
    test.skip(!scaffoldId, 'No SCAFFOLDED CONSUME_ROLL — run demo-path first.');

    await page.goto(`/scaffolds/${scaffoldId}/validation`);
    await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible();

    // All three cards present.
    await expect(page.getByTestId('validation-card-compile')).toBeVisible();
    await expect(page.getByTestId('validation-card-test-pack')).toBeVisible();
    await expect(page.getByTestId('validation-card-equivalence')).toBeVisible();

    // Helper: click a card's Run button, wait for the underlying POST to land,
    // then for the list-refetch GET to settle. Without this, "Build succeeded"
    // text from a PRIOR run satisfies the visibility check immediately and the
    // page hasn't yet re-rendered with the new run id (which makes View log
    // race against stale state).
    async function triggerStage(cardTestId: string, buttonRegex: RegExp, postPath: string) {
      const card = page.getByTestId(cardTestId);
      const postResp = page.waitForResponse(
        (r) => r.url().includes(postPath) && r.request().method() === 'POST',
        { timeout: 120_000 },
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
    await expect(equivalenceCard.getByText(/Smoke equivalence/)).toBeVisible({ timeout: 60_000 });

    // Overall verdict banner should report "all gates green" once all three are PASSED.
    await expect(page.getByText(/All gates green/)).toBeVisible();

    // Log drill-down works. Use the COMPILE card's View log button.
    await compileCard.getByRole('button', { name: /^View log$/ }).click();
    const dialog = page.getByRole('dialog', { name: /Validation log/ });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText(/dotnet build/)).toBeVisible({ timeout: 15_000 });
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(dialog).toHaveCount(0);
  });
});
