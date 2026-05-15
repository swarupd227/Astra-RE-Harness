/**
 * Chaos rehearsal test — Phase B.5.x §9.4.
 *
 * Verifies the client's error UX renders correctly when the LLM provider
 * fails mid-stream. Skipped unless the API is running with the fail-mock
 * provider:
 *
 *   LLM_PROVIDER=fail-mock docker compose up -d api
 *   cd app/e2e && npm test -- chaos-extract
 *
 * The test passes if the live-extraction page surfaces a user-visible
 * "Extraction failed" state with the structured error from the provider.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

async function getSeededSubroutineId(page: Page): Promise<string> {
  // Find the synthetic seed by name — MINPACK is also seeded by default
  // and orders ahead of the demo seed because it ingested most recently,
  // so data[0] is no longer reliably the seed.
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) throw new Error('Synthetic seed corpus not found after reset.');
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`).then((r) => r.json());
  return detail.latestVersion.files[0].subroutines[0].id;
}

async function detectProvider(page: Page): Promise<string> {
  // Phase C.12: /api/v1/whoami now exposes the active provider name, so
  // the chaos check is instant — no need to consume a real extract call
  // (which would cost ~$0.04 + ~45s on real Anthropic).
  const me = await page.request
    .get(`${API_BASE}/api/v1/whoami`, { headers: { 'X-Dev-Persona': 'engineer' } })
    .then((r) => r.json());
  return me.llmProvider ?? 'unknown';
}

test.describe('Chaos · provider failure UX', () => {
  test('fail-mock provider surfaces a visible error block', async ({ page }) => {
    const provider = await detectProvider(page);
    test.skip(provider !== 'fail-mock', `Skipped: API is running with provider="${provider}". Set LLM_PROVIDER=fail-mock to run.`);

    // Re-reset (the detect-probe consumed the PARSED state) and pin persona
    await page.request.post(`${API_BASE}/api/v1/dev/reset`);
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));

    const subId = await getSeededSubroutineId(page);
    await page.goto(`/subroutines/${subId}`);
    await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();

    // The fail-mock provider emits an `error` SSE event after stage 3.
    // The page renders an ErrorBlock with the user-facing copy.
    await expect(page.getByText(/Extraction failed/i)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/Provider returned 503/i)).toBeVisible();

    // The Cancel CTA is replaced or hidden once the stream errors. The
    // user is no longer in `streaming` state — verify by checking the page
    // does not present the Cancel button anymore.
    const cancel = page.getByRole('button', { name: /^Cancel$/ });
    await expect(cancel).toHaveCount(0);
  });
});
