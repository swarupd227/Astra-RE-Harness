/**
 * Phase 5.7 — Extended demo path driver.
 *
 * Walks the 11-beat flow in `04_Delivery/phase-5.7-demo-build-plan.md`
 * end-to-end. Designed to be RECORDED:
 *
 *   RECORD_DEMO=1 BASE_URL=http://127.0.0.1:35173 API_BASE=http://127.0.0.1:38080 \
 *     LLM_PROVIDER=anthropic \
 *     npx playwright test demo-path-phase5.7
 *
 * Playwright's `video: 'on'` (set by playwright.config.ts when
 * RECORD_DEMO=1) captures one MP4 per test under
 * `playwright-report/data/<id>/video.webm`. Move it to
 * `app/e2e/demo-output/phase-5.7-raw.webm`, then re-encode + overlay
 * narration in post.
 *
 * The driver is *best-effort*: each beat is a `test.step` that lands
 * on the right page and triggers the right click. Selector failures
 * stop the recording at that beat with a clear log line, so the
 * rehearsal team can patch the selector locally before the next take.
 *
 * Beat coverage (see cue-cards.md):
 *   B0  framing                      ─ no UI action
 *   B1  open CONSUME_ROLL (Fortran)
 *   B2  extract spec (neighbourhood)
 *   B3  SME review + sign
 *   B4  scaffold + .NET validation
 *   B5  COBOL ingest (DEPTPAY.CBL)
 *   B6  COBOL extract → Java scaffold
 *   B7  Maven sidecar validation
 *   B8  DEPTPAY equivalence (5847.95)
 *   B9  Golden Dataset page
 *   B10 Cross-routine harmonisation
 *   B11 Audit close
 */
import { expect, test, type Page } from '@playwright/test';
import * as path from 'node:path';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const BEAT_PAUSE_MS = process.env.RECORD_DEMO === '1' ? 2_500 : 0;

/** Stable persona switch using the top-right menu. */
async function switchPersona(page: Page, persona: 'engineer' | 'sme' | 'observer' | 'admin') {
  const labelByPersona = { engineer: 'Engineer', sme: 'SME', observer: 'Observer', admin: 'Admin' } as const;
  const label = labelByPersona[persona];
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

async function getSeededRollSubroutineId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) throw new Error('CONSUME_ROLL seed corpus missing.');
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`).then((r) => r.json());
  return detail.latestVersion.files[0].subroutines[0].id;
}

test.describe('Phase 5.7 · 10-minute extended demo', () => {
  test.beforeEach(async ({ page }) => {
    await page.request.post(`${API_BASE}/api/v1/dev/reset`, {
      headers: { 'X-Dev-Persona': 'engineer' },
      timeout: 60_000,
    });
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('full 10-minute walkthrough — Fortran→.NET + COBOL→Java + harmonisation', async ({ page }) => {
    const subId = await getSeededRollSubroutineId(page);

    // ── B0 · framing ──────────────────────────────────────────────────
    await test.step('B0 framing (landing page)', async () => {
      await page.goto('/');
      await expect(page.getByRole('heading').first()).toBeVisible();
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B1 · open CONSUME_ROLL ───────────────────────────────────────
    await test.step('B1 open CONSUME_ROLL', async () => {
      await page.goto(`/subroutines/${subId}`);
      await expect(page.getByRole('heading', { name: 'CONSUME_ROLL', level: 1 })).toBeVisible();
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B2 · extract (with neighbourhood) ────────────────────────────
    await test.step('B2 extract spec (neighbourhood attached)', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Wait for the View-draft CTA on success. Real Anthropic takes 25-60s;
      // mock provider ~12s. Phase 7.0 also emits a neighbourhood_attached
      // SSE event the UI may surface as a badge.
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
        timeout: 120_000,
      });
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
      await page.getByRole('button', { name: 'View draft spec' }).click();
    });

    // ── B3 · SME review + sign ───────────────────────────────────────
    await test.step('B3 SME review + sign', async () => {
      await switchPersona(page, 'sme');
      // Best-effort: click any first 3 Accept buttons that exist.
      const accepts = page.getByRole('button', { name: /^Accept/i });
      const acceptCount = Math.min(3, await accepts.count());
      for (let i = 0; i < acceptCount; i++) {
        await accepts.nth(i).click({ trial: false }).catch(() => {});
        if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS / 3);
      }
      // Trigger sign-off
      const signButton = page.getByRole('button', { name: /^Sign spec$/i });
      if (await signButton.count() > 0) {
        await signButton.click();
        // Confirm sign in the modal — most platforms show "Sign" / "Confirm"
        const confirmSign = page.getByRole('button', { name: /^(Sign|Confirm sign)/i }).last();
        await confirmSign.click().catch(() => {});
      }
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B4 · scaffold + .NET validation ──────────────────────────────
    await test.step('B4 scaffold + .NET validation', async () => {
      await switchPersona(page, 'engineer');
      const genScaffold = page.getByRole('button', { name: /^Generate scaffold/i });
      if (await genScaffold.count() > 0) {
        await genScaffold.first().click();
        await page.waitForLoadState('networkidle');
      }
      // Best-effort: click compile validator if visible
      const validateCompile = page.getByRole('button', { name: /^Validate compile/i });
      if (await validateCompile.count() > 0) {
        await validateCompile.first().click();
        await page.waitForTimeout(15_000); // allow build log to stream
      }
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B5 · COBOL ingest (DEPTPAY.CBL) ──────────────────────────────
    await test.step('B5 COBOL upload', async () => {
      const deptpayPath = path.resolve(__dirname, '../fixtures/cobol/DEPTPAY.CBL');
      // Direct API ingest is the most robust path for the recording
      // (the browser file-picker is non-deterministic across OSs).
      const fs = await import('node:fs/promises');
      const buf = await fs.readFile(deptpayPath);
      const form = new FormData();
      form.append('file', new Blob([buf], { type: 'text/plain' }), 'DEPTPAY.CBL');
      // Try the multipart upload first; fall through if the endpoint
      // path differs — the cue-cards still narrate the action.
      try {
        await page.request.post(`${API_BASE}/api/v1/ingest/upload`, {
          headers: { 'X-Dev-Persona': 'engineer' },
          multipart: { file: { name: 'DEPTPAY.CBL', mimeType: 'text/plain', buffer: buf } },
          timeout: 60_000,
        });
      } catch (e) {
        // eslint-disable-next-line no-console
        console.warn('[B5] direct upload failed; presenter may need to use the browser uploader', e);
      }
      // Land on the corpora page so the new corpus surfaces visually
      await page.goto('/corpora');
      await page.waitForLoadState('networkidle');
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B6 · COBOL extract → Java scaffold ───────────────────────────
    await test.step('B6 COBOL extract → Java scaffold', async () => {
      // Best-effort: find a DEPTPAY-named subroutine via the API
      const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
      const cobol = corpora.data.find((c: { name: string }) => /DEPTPAY/i.test(c.name) || /COBOL/i.test(c.name));
      if (!cobol) return; // skip if no COBOL corpus landed
      const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${cobol.id}`).then((r) => r.json());
      const sub = detail.latestVersion?.files?.[0]?.subroutines?.[0];
      if (!sub) return;
      await page.goto(`/subroutines/${sub.id}`);
      await expect(page.getByRole('heading').first()).toBeVisible();
      const extract = page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ });
      if (await extract.count() > 0) {
        await extract.first().click();
        await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({ timeout: 120_000 });
        await page.getByRole('button', { name: 'View draft spec' }).click();
      }
      // Rehearsed: switch to SME for fast sign
      await switchPersona(page, 'sme');
      const sign = page.getByRole('button', { name: /^Sign spec$/i });
      if (await sign.count() > 0) {
        await sign.click();
        await page.getByRole('button', { name: /^(Sign|Confirm sign)/i }).last().click().catch(() => {});
      }
      await switchPersona(page, 'engineer');
      // Best-effort: pick java-spring target
      const targetSelect = page.getByLabel(/Target stack/i);
      if (await targetSelect.count() > 0) {
        await targetSelect.selectOption({ label: 'java-spring' }).catch(() => {});
      }
      const genScaffold = page.getByRole('button', { name: /^Generate scaffold/i });
      if (await genScaffold.count() > 0) {
        await genScaffold.first().click();
        await page.waitForLoadState('networkidle');
      }
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B7 · Maven sidecar validation ────────────────────────────────
    await test.step('B7 Maven validation (compile + test-pack)', async () => {
      const compile = page.getByRole('button', { name: /^Validate compile/i });
      if (await compile.count() > 0) {
        await compile.first().click();
        await page.waitForTimeout(20_000);
      }
      const testPack = page.getByRole('button', { name: /^Validate test/i });
      if (await testPack.count() > 0) {
        await testPack.first().click();
        await page.waitForTimeout(30_000);
      }
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B8 · DEPTPAY equivalence (the 5847.95 moment) ────────────────
    await test.step('B8 DEPTPAY equivalence preview', async () => {
      await switchPersona(page, 'admin');
      // Drive directly via the API — landing page is screen-recorded.
      const r = await page.request.post(
        `${API_BASE}/api/v1/validation/equivalence/preview/deptpay-average`,
        { headers: { 'X-Dev-Persona': 'admin' }, timeout: 90_000 },
      ).catch(() => null);
      if (r && r.ok()) {
        const body = await r.json();
        // eslint-disable-next-line no-console
        console.log(`[B8] DEPTPAY equivalence verdict=${body.verdict} matched=${body.matched}/${body.inputCount}`);
      }
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B9 · Golden Dataset ──────────────────────────────────────────
    await test.step('B9 Golden Dataset page', async () => {
      await page.goto('/platform/golden-dataset');
      await expect(page.getByTestId('golden-dataset-page')).toBeVisible({ timeout: 30_000 });
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
      const cobolRoundedCard = page.getByTestId('golden-entry-cobol-rounded-off-by-one');
      if (await cobolRoundedCard.count() > 0) {
        await cobolRoundedCard.click();
        await page.waitForTimeout(BEAT_PAUSE_MS || 800);
        await page.keyboard.press('Escape').catch(() => {});
      }
    });

    // ── B10 · Cross-routine harmonisation ────────────────────────────
    await test.step('B10 Harmonisation pass', async () => {
      await page.goto('/platform/harmonisation');
      await expect(page.getByTestId('harmonisation-page')).toBeVisible({ timeout: 30_000 });
      const runButton = page.getByTestId('harmonisation-run');
      if (await runButton.count() > 0) {
        await runButton.click();
        // Wait for the run card to materialise. Mock provider is
        // instant; anthropic takes 20-60s.
        await page.waitForTimeout(45_000);
      }
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS);
    });

    // ── B11 · Audit close ────────────────────────────────────────────
    await test.step('B11 audit trail', async () => {
      // Best-effort: the audit page lives under /platform/audit or
      // /audit depending on routing. Try the common path first.
      const candidates = ['/audit', '/platform/audit'];
      for (const candidate of candidates) {
        await page.goto(candidate);
        const found = await page.locator('h1, [data-testid*="audit"]').count();
        if (found > 0) break;
      }
      if (BEAT_PAUSE_MS) await page.waitForTimeout(BEAT_PAUSE_MS * 2);
    });
  });
});
