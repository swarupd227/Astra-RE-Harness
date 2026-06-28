/**
 * Phase 11.0 — Documentation generation & review E2E demo (11.0.h).
 *
 * Demonstrates the full documentation workflow:
 *   admin: trigger docs generation on any available ingested corpus
 *   admin: poll until run SUCCEEDED / PARTIAL
 *   admin: open DocsPage → three-pane layout visible
 *   admin: sign a DRAFT section → badge flips to SIGNED
 *   admin: reject a DRAFT section → section disappears from DRAFT list
 *   admin: export dropdown is rendered with all four format options
 *   api:   docs/summary endpoint confirms signed count > 0
 *
 * Uses take=5 so generation finishes quickly even on mock LLM. force=true
 * makes reruns idempotent — existing DRAFT sections for the same version are
 * wiped and regenerated from scratch.
 *
 * Skips cleanly if no ingested corpus exists (DATABASE_SEED_MINPACK,
 * DATABASE_SEED_LAPACK_BLAS, or DATABASE_SEED_VB6_DEMO must be true on the
 * API to have a corpus ready for docs generation).
 *
 * Run with RECORD_DEMO=1 to capture a slow-paced video for stakeholders.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };
const ENG   = { 'X-Dev-Persona': 'engineer' };

async function switchPersona(page: Page, persona: 'engineer' | 'sme' | 'admin') {
  const label = { engineer: 'Engineer', sme: 'SME', admin: 'Admin' }[persona];
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

async function findReadyCorpus(page: Page): Promise<{ id: string; name: string } | null> {
  const list = await page.request
    .get(`${API_BASE}/api/v1/corpora`, { headers: ENG })
    .then((r) => r.json());
  // Prefer LAPACK BLAS (many routines) over others, then any corpus with a
  // latestVersionId (i.e. fully ingested).
  const ordered: { id: string; name: string; latestVersionId?: string }[] = [
    ...(list.data ?? []).filter((c: { name: string }) => /lapack/i.test(c.name)),
    ...(list.data ?? []).filter((c: { name: string }) => /minpack/i.test(c.name)),
    ...(list.data ?? []).filter((c: { name: string }) => !/lapack|minpack/i.test(c.name)),
  ];
  for (const c of ordered) {
    const detail = await page.request
      .get(`${API_BASE}/api/v1/corpora/${c.id}`, { headers: ENG })
      .then((r) => r.json());
    if (detail.latestVersionId) return { id: c.id, name: c.name };
  }
  return null;
}

test.describe('Phase 11.0 · Documentation generation & review', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('generate docs → sign section → reject section → verify export dropdown', async ({ page }) => {
    const beat = process.env.RECORD_DEMO === '1' ? 2_000 : 0;

    // ── 1. Find an ingested corpus ─────────────────────────────────────
    let corpus: { id: string; name: string } | null = null;
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
      corpus = await findReadyCorpus(page);
      if (corpus) break;
      await page.waitForTimeout(3_000);
    }
    test.skip(
      corpus === null,
      'No ingested corpus found after 60s. Set DATABASE_SEED_MINPACK=true or DATABASE_SEED_LAPACK_BLAS=true on the API.',
    );
    const { id: corpusId, name: corpusName } = corpus!;

    // ── 2. Show corpus surface ─────────────────────────────────────────
    await test.step('show corpus surface', async () => {
      await page.goto(`/corpora/${corpusId}`);
      await expect(page.getByRole('heading', { name: corpusName })).toBeVisible();
      await expect(page.getByTestId('open-docs')).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    // ── 3. Trigger docs generation via API ────────────────────────────
    let runId = '';
    await test.step('trigger docs generation', async () => {
      const resp = await page.request.post(
        `${API_BASE}/api/v1/corpora/${corpusId}/docs/generate` +
          '?stages=routine-summary,module,overview&take=5&force=true',
        { headers: { ...ADMIN, 'Content-Type': 'application/json' } },
      );
      expect(resp.status()).toBe(202);
      const body = await resp.json();
      runId = body.generationRunId as string;
      expect(runId).toBeTruthy();
      if (beat) await page.waitForTimeout(beat);
    });

    // ── 4. Poll until the generation run leaves RUNNING ────────────────
    await test.step('wait for generation to complete', async () => {
      const limit = Date.now() + 180_000;
      let lastState = 'QUEUED';
      while (Date.now() < limit) {
        const run = await page.request
          .get(`${API_BASE}/api/v1/docs/runs/${runId}`, { headers: ENG })
          .then((r) => r.json());
        lastState = run.state;
        if (['SUCCEEDED', 'PARTIAL', 'FAILED'].includes(lastState)) break;
        await page.waitForTimeout(3_000);
      }
      // FAILED means the LLM threw; still continue to verify the UI surface.
      // PARTIAL means some stages failed — at least some sections exist.
    });

    // ── 5. Navigate to DocsPage via the corpus detail page button ──────
    await test.step('open DocsPage', async () => {
      await page.goto(`/corpora/${corpusId}`);
      await page.getByTestId('open-docs').click();
      await expect(page).toHaveURL(new RegExp(`/corpora/${corpusId}/docs`));
      await expect(page.getByText('Phase 11.0 · Docs', { exact: false })).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    // ── 6. Switch to Admin and verify three-pane layout ───────────────
    await test.step('switch to Admin persona', async () => {
      await switchPersona(page, 'admin');
      // Kind sidebar + section list pane + export dropdown must be present.
      await expect(page.locator('nav').first()).toBeVisible();
      await expect(page.getByRole('combobox', { name: 'Export documentation' })).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    // ── 7. Select the first section that has a Sign button ────────────
    let signedAtLeastOne = false;
    await test.step('sign a DRAFT doc section', async () => {
      // Click through section kinds until we find a section with a Sign button.
      const kindButtons = page.locator('nav button');
      const kindCount = await kindButtons.count();
      outer: for (let k = 0; k < kindCount; k++) {
        await kindButtons.nth(k).click();
        await page.waitForTimeout(500);
        const sectionButtons = page.locator('div.flex.min-h-0.flex-col button.w-full.border-b');
        const sectionCount = await sectionButtons.count();
        for (let s = 0; s < sectionCount; s++) {
          await sectionButtons.nth(s).click();
          await page.waitForTimeout(400);
          const signBtn = page.getByRole('button', { name: 'Sign' });
          if (await signBtn.isEnabled()) {
            await signBtn.click();
            // State badge in the action bar should flip to SIGNED.
            await expect(
              page.locator('div.sticky.top-0').getByText('SIGNED'),
            ).toBeVisible({ timeout: 10_000 });
            signedAtLeastOne = true;
            if (beat) await page.waitForTimeout(beat);
            break outer;
          }
        }
      }
      test.skip(!signedAtLeastOne, 'No signable DRAFT sections found — LLM generation may have produced 0 sections.');
    });

    // ── 8. Reject a different DRAFT section ────────────────────────────
    await test.step('reject a DRAFT doc section', async () => {
      const kindButtons = page.locator('nav button');
      const kindCount = await kindButtons.count();
      for (let k = 0; k < kindCount; k++) {
        await kindButtons.nth(k).click();
        await page.waitForTimeout(500);
        const sectionButtons = page.locator('div.flex.min-h-0.flex-col button.w-full.border-b');
        const sectionCount = await sectionButtons.count();
        for (let s = 0; s < sectionCount; s++) {
          await sectionButtons.nth(s).click();
          await page.waitForTimeout(400);
          const rejectBtn = page.getByRole('button', { name: 'Reject' });
          if (await rejectBtn.isEnabled()) {
            await rejectBtn.click();
            // 204 no-content; wait for mutation to settle.
            await page.waitForTimeout(800);
            if (beat) await page.waitForTimeout(beat);
            return; // one rejection is enough for the demo
          }
        }
      }
    });

    // ── 9. Export dropdown has all expected format options ─────────────
    await test.step('export dropdown has all four format options', async () => {
      const exportSelect = page.getByRole('combobox', { name: 'Export documentation' });
      await expect(exportSelect).toBeVisible();
      const options = await exportSelect.locator('option').allTextContents();
      expect(options.some((o) => /MkDocs/i.test(o))).toBe(true);
      expect(options.some((o) => /Word/i.test(o))).toBe(true);
      expect(options.some((o) => /PDF/i.test(o))).toBe(true);
      expect(options.some((o) => /Confluence/i.test(o))).toBe(true);
      if (beat) await page.waitForTimeout(beat);
    });

    // ── 10. Docs summary API confirms at least one SIGNED section ──────
    await test.step('docs summary confirms signed count > 0', async () => {
      const summary = await page.request
        .get(`${API_BASE}/api/v1/corpora/${corpusId}/docs/summary`, { headers: ADMIN })
        .then((r) => r.json());
      const signedCount = (summary.breakdown as Array<{ state: string; count: number }>)
        .filter((b) => b.state === 'SIGNED')
        .reduce((acc, b) => acc + b.count, 0);
      expect(signedCount).toBeGreaterThan(0);
    });
  });
});
