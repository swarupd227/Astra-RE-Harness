/**
 * Phase 5.7 — Short cut, ~2.5 minute total recording.
 *
 *   beat 1 (00:00–00:25)  open CONSUME_ROLL (Fortran)
 *   beat 2 (00:25–01:10)  extract spec (mock provider for determinism)
 *   beat 3 (01:10–01:30)  SME sign-off
 *   beat 4 (01:30–01:55)  engineer scaffold + .NET validation badge
 *   beat 5 (01:55–02:25)  admin DEPTPAY equivalence preview — 5847.95 row
 *   end   (02:25–02:30)   close on the audit timeline
 *
 * Pacing: the Playwright config's slowMo: 600 (RECORD_DEMO=1) gives
 * stakeholder-readable click timing without per-beat waits longer
 * than necessary. The total wall-clock at slowMo=600 is ~2:20–2:30
 * depending on provider speed; mock provider runs deterministically.
 *
 *   RECORD_DEMO=1 BASE_URL=http://127.0.0.1:35173 \
 *     API_BASE=http://127.0.0.1:38080 \
 *     npx playwright test demo-short-2-5min
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
// 5.5s per beat lands the recording in the 1:45–2:15 range with the
// 7-beat arc, which leaves comfortable room under the 2:30 cap and
// reads as paced rather than rushed at slowMo=600.
const BEAT = process.env.RECORD_DEMO === '1' ? 5_500 : 0;

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

test.describe('Demo · 2.5-minute cut', () => {
  test.beforeEach(async ({ page }) => {
    await page.request.post(`${API_BASE}/api/v1/dev/reset`, {
      headers: { 'X-Dev-Persona': 'engineer' },
      timeout: 60_000,
    });
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('Fortran → .NET in 90s, COBOL equivalence punchline in 30s', async ({ page }) => {
    const subId = await getSeededSubroutineId(page);

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
      // Drive directly via the API and land on a page that shows the
      // run. The validation page renders any equivalence run for the
      // current corpus; landing there gives the audience something to
      // see while the call completes.
      await page.goto('/audit');
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
      // Open the cobol-rounded-off-by-one entry — the 5847.95 trap in
      // the calibration corpus itself.
      const card = page.getByTestId('golden-entry-cobol-rounded-off-by-one');
      if (await card.count() > 0) {
        await card.click();
        if (BEAT) await page.waitForTimeout(BEAT);
        await page.keyboard.press('Escape').catch(() => {});
      }
    });

    // ── 7 · Harmonisation (corpus-wide consistency) ─────────────────
    await test.step('Harmonisation pass', async () => {
      await page.goto('/platform/harmonisation');
      await expect(page.getByTestId('harmonisation-page')).toBeVisible({ timeout: 30_000 });
      if (BEAT) await page.waitForTimeout(BEAT);
    });

    // ── close on the audit timeline ─────────────────────────────────
    await test.step('audit close', async () => {
      await page.goto('/audit');
      if (BEAT) await page.waitForTimeout(BEAT);
    });
  });
});
