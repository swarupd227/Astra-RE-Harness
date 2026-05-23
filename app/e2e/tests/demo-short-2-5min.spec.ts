/**
 * Phase 5.7 — Short cut, ~2 minute total recording.
 *
 * Hard rule: every navigation must land on a built, populated screen.
 * No /audit (which is the NotFoundPage "Not yet built"); no /dev/reset
 * (which drops the schema and only re-seeds the synthetic corpus, so
 * the Golden Dataset + Harmonisation pages render empty after it).
 *
 * Run:
 *   RECORD_DEMO=1 BASE_URL=http://127.0.0.1:35173 \
 *     API_BASE=http://127.0.0.1:38080 \
 *     npx playwright test demo-short-2-5min
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
// 5s per surface lets the audience read each screen at slowMo=600.
const BEAT = process.env.RECORD_DEMO === '1' ? 5_000 : 0;

async function switchPersona(page: Page, persona: 'engineer' | 'sme' | 'observer' | 'admin') {
  const labelByPersona = { engineer: 'Engineer', sme: 'SME', observer: 'Observer', admin: 'Admin' } as const;
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${labelByPersona[persona]}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

async function getSeededSubroutineId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) throw new Error('CONSUME_ROLL seed missing.');
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`).then((r) => r.json());
  return detail.latestVersion.files[0].subroutines[0].id;
}

async function getSeededCorpusId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) throw new Error('CONSUME_ROLL seed missing.');
  return seed.id;
}

test.describe('Demo · 2.5-minute cut', () => {
  test.beforeEach(async ({ page }) => {
    // NO /dev/reset — it wipes the Golden Dataset + Harmonisation
    // tables and the GoldenDatasetSeed only runs on API startup. We
    // rely on whatever the running stack already has loaded.
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('Fortran → .NET happy path + COBOL equivalence punchline', async ({ page }) => {
    const subId = await getSeededSubroutineId(page);
    // corpusId reserved for any future pre-warm or admin step that
    // wants to scope by corpus; not used in the visual beats today.
    await getSeededCorpusId(page);

    // ── 1 · CONSUME_ROLL (Fortran) ───────────────────────────────────
    await test.step('open CONSUME_ROLL', async () => {
      await page.goto(`/subroutines/${subId}`);
      await expect(page.getByRole('heading', { name: 'CONSUME_ROLL', level: 1 })).toBeVisible();
      if (BEAT) await page.waitForTimeout(BEAT);
    });

    // ── 2 · Extract ──────────────────────────────────────────────────
    await test.step('extract spec', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
        timeout: 120_000,
      });
      await page.getByRole('button', { name: 'View draft spec' }).click();
      if (BEAT) await page.waitForTimeout(BEAT);
    });

    // ── 3 · SME sign ─────────────────────────────────────────────────
    await test.step('SME signs', async () => {
      await switchPersona(page, 'sme');
      const sign = page.getByRole('button', { name: /^Sign spec$/i });
      if (await sign.count() > 0) {
        await sign.click();
        const confirm = page.getByRole('button', { name: /^(Sign|Confirm sign)/i }).last();
        await confirm.click().catch(() => {});
      }
      if (BEAT) await page.waitForTimeout(BEAT);
    });

    // ── 4 · Scaffold + validation ────────────────────────────────────
    await test.step('engineer scaffolds + validates', async () => {
      await switchPersona(page, 'engineer');
      const gen = page.getByRole('button', { name: /^Generate scaffold/i });
      if (await gen.count() > 0) {
        await gen.first().click();
        await page.waitForLoadState('networkidle');
      }
      const validate = page.getByRole('button', { name: /^Validate compile/i });
      if (await validate.count() > 0) {
        await validate.first().click();
        // Brief pause to capture the build-log streaming on screen.
        await page.waitForTimeout(BEAT ? 8_000 : 2_000);
      }
    });

    // ── 5 · DEPTPAY equivalence — the 5847.95 punchline ──────────────
    await test.step('admin DEPTPAY equivalence (the 5847.95 row)', async () => {
      await switchPersona(page, 'admin');
      // Land on /platform/validation (a real "validation policy" page)
      // so the screen-time during the equivalence call shows the
      // platform's validation surface, not an under-dev placeholder.
      await page.goto('/platform/validation');
      const r = await page.request.post(
        `${API_BASE}/api/v1/validation/equivalence/preview/deptpay-average`,
        { headers: { 'X-Dev-Persona': 'admin' }, timeout: 90_000 },
      ).catch(() => null);
      if (r && r.ok()) {
        const body = await r.json();
        // eslint-disable-next-line no-console
        console.log(`[demo-2-5] DEPTPAY equivalence verdict=${body.verdict} matched=${body.matched}/${body.inputCount}`);
      }
      if (BEAT) await page.waitForTimeout(BEAT);
    });

    // ── 6 · Golden Dataset (we measure ourselves) ───────────────────
    await test.step('Golden Dataset overview', async () => {
      await page.goto('/platform/golden-dataset');
      await expect(page.getByTestId('golden-dataset-page')).toBeVisible({ timeout: 30_000 });
      if (BEAT) await page.waitForTimeout(BEAT);
      // Open the cobol-rounded-off-by-one entry — the 5847.95 trap as
      // a calibration entry. Falls back to ANY visible entry card if
      // the specific testid isn't rendered.
      let card = page.getByTestId('golden-entry-cobol-rounded-off-by-one');
      if (await card.count() === 0) {
        card = page.locator('[data-testid^="golden-entry-"]').first();
      }
      if (await card.count() > 0) {
        await card.click();
        if (BEAT) await page.waitForTimeout(BEAT);
        await page.keyboard.press('Escape').catch(() => {});
      }
    });

    // ── 7 · Platform tiles (overview of configurable surfaces) ─────
    // Earlier versions of this cut visited /platform/harmonisation,
    // but the demo's signed-spec count is zero in both seed corpora,
    // so the run summaries land on "No signed specs to harmonise · 0
    // findings" — which reads as "the feature didn't find anything to
    // do" rather than "the feature works". The platform tile page
    // surfaces every configurable Nous asset (prompts, languages,
    // validation policy, signature health, roles, golden dataset,
    // harmonisation) as Live badges with descriptions — a stronger
    // single visual than an empty runs list.
    await test.step('platform overview', async () => {
      await page.goto('/platform');
      await expect(page.getByTestId('platform-index-page')).toBeVisible({ timeout: 30_000 });
      if (BEAT) await page.waitForTimeout(BEAT);
    });

    // ── close on the compliance / audit-export surface ──────────────
    // /compliance is the real audit-trail / evidence-feed page. /audit
    // does NOT exist as a route — it routes to NotFoundPage with the
    // "Not yet built" message, which is what we explicitly avoid.
    await test.step('audit close (compliance feed)', async () => {
      await page.goto('/compliance');
      if (BEAT) await page.waitForTimeout(BEAT);
    });
  });
});
