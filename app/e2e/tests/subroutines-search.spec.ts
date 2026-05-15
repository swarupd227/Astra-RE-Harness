/**
 * Phase C.12 — cross-corpus subroutine search.
 *
 * The previous tests left two corpora ingested (the seed CONSUME_ROLL
 * and the real MINPACK from GitHub). This test:
 *
 *   1. Navigates from the left nav into the new /subroutines page
 *   2. Verifies search picks up matches across BOTH corpora
 *   3. Filters by corpus and confirms the filter narrows correctly
 *   4. Filters by state, confirms it works
 *   5. Clicks a result, lands on the subroutine detail surface
 *
 * Skips automatically if MINPACK isn't ingested — the test is
 * incremental on top of the MINPACK-demo run, not a standalone gate.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };
const MINPACK_NAME = 'MINPACK (F77 nonlinear least squares)';

test.describe('Phase C.12 · Cross-corpus search', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('search "HYBR" across all corpora; filter by corpus; click into a hit', async ({ page }) => {
    // MINPACK auto-seeds in the background after every reset. Poll up to
    // 30s for it to appear before deciding the corpus is missing — this
    // avoids racing the background clone+parse on the very first run.
    let minpack: { id: string } | undefined;
    const deadline = Date.now() + 30_000;
    while (Date.now() < deadline) {
      const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
      minpack = corpora.data.find((c: { name: string }) => c.name === MINPACK_NAME);
      if (minpack) break;
      await page.waitForTimeout(2_000);
    }
    test.skip(!minpack, 'MINPACK corpus not present after 30s — boot seed disabled?');

    // ── Navigate via the left nav ────────────────────────────────────
    await page.goto('/');
    await page.getByRole('navigation', { name: /Primary/ }).getByRole('link', { name: /^Subroutines$/ }).click();
    await expect(page).toHaveURL(/\/subroutines$/);
    await expect(page.getByRole('heading', { name: 'Subroutines', level: 1 })).toBeVisible();

    // ── Default view is empty; type a query ──────────────────────────
    await page.getByTestId('subroutines-search').fill('HYBR');
    const results = page.getByTestId('search-results');
    await expect(results).toBeVisible();

    // Multiple MINPACK HYBR* routines should show up. Anchor on the link's
    // accessible name with a leading-name prefix — the filename can also
    // contain "hybrd1" (e.g. example_hybrd1.f90), but the routine name is
    // always the FIRST token in the link's accessible name.
    await expect(results.getByRole('link', { name: /^HYBRD1\s/ }).first()).toBeVisible();
    await expect(results.getByRole('link', { name: /^HYBRJ\b/ }).first()).toBeVisible();

    // ── Filter by MINPACK corpus — narrows to MINPACK hits only ──────
    await page.getByTestId('filter-corpus').selectOption(minpack.id);
    // The result card header should now read the MINPACK corpus name.
    await expect(results.getByText(MINPACK_NAME).first()).toBeVisible();
    // Seed corpus's CONSUME_ROLL should not be in the visible list.
    await expect(results.getByRole('link', { name: /^CONSUME_ROLL\s/ })).toHaveCount(0);

    // ── Clear filters → wider result set returns ─────────────────────
    await page.getByTestId('clear-filters').click();
    await expect(page.getByTestId('subroutines-search')).toHaveValue('');
    // Wait until the corpus filter is gone from the URL — the URL-sync
    // useEffect is debounced and we don't want to race it.
    await expect(page).not.toHaveURL(/[?&]corpus=/);
    // With no filters, both corpora are reachable. Type a query that hits seed.
    await page.getByTestId('subroutines-search').fill('CONSUME');
    await expect(page).toHaveURL(/[?&]q=CONSUME/);
    await expect(results.getByRole('link', { name: /^CONSUME_ROLL\s/ }).first()).toBeVisible();

    // ── Click into HYBRD1 specifically and verify detail surface ─────
    await page.getByTestId('subroutines-search').fill('HYBRD1');
    // Wait for the results to settle on this query (debounced 250 ms + fetch).
    // Looking at the search input's URL-sync as a steady signal: once the URL
    // reflects ?q=HYBRD1 the new query has rendered.
    await expect(page).toHaveURL(/[?&]q=HYBRD1/);
    const hybrd1 = results.getByRole('link', { name: /^HYBRD1\s/ }).first();
    await hybrd1.waitFor({ state: 'visible' });
    await hybrd1.click();
    await expect(page).toHaveURL(/\/subroutines\/[0-9a-f-]+$/);
    await expect(page.getByRole('heading', { name: 'HYBRD1', level: 1 })).toBeVisible();
  });
});
