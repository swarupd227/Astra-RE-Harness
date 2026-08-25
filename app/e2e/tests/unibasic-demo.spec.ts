/**
 * UniBasic demo — Pattern Analysis → live archetype authoring → real
 * per-routine scaffold generation.
 *
 * Unlike phase-9.6-multilang-recording.spec.ts (which is navigation-only
 * across pre-baked artefacts), this walkthrough tells UniBasic's own,
 * newer story:
 *
 *   1. Projects grid + corpus detail (9 UniBasic/PICK routines)
 *   2. Pattern analysis — 2 core patterns + 5 singletons discovered
 *      across the corpus
 *   3. The export/UPS cluster's archetype proposal — LLM-authored,
 *      auto-verified (7/7 tests), approved, now LIVE with zero restart
 *   4. The UPS routine's signed spec
 *   5. The real per-routine scaffold generated FROM that live archetype
 *      (AnthropicScaffoldProvider, not template replay) — click into
 *      real generated Java
 *   6. The validation report: compile gate already green; the test-pack
 *      gate re-generated + re-run live on screen; equivalence and the
 *      4th (property-based) gate honestly left "not run" — there is no
 *      UniBasic/PICK runtime sidecar yet to compare against, so the demo
 *      does not pretend those gates exist.
 *
 * State is pre-baked via API before recording starts (corpus already
 * parsed, spec already signed, archetype already proposed/verified/
 * approved, scaffold already generated and compile-validated) — the same
 * "don't re-litigate LLM-flaky live theatre in the recording" principle
 * phase-9.6 established. The ONE live LLM/tool call kept on screen is the
 * test-pack regenerate+run, because it is fast, deterministic, and a
 * meaningful "watch it happen" beat.
 *
 * RECORD_DEMO=1 emits test-results/timeline-latest.json so
 * scripts/trim-demo.mjs can cut that live LLM wait and replace it with a
 * caption card. Target trimmed length: ~2:30-3:00.
 */
import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };
const ADMIN = { 'X-Dev-Persona': 'admin' };

// Real, durable IDs seeded earlier this session (Postgres — survive restarts).
const CORPUS_ID = 'b8e68110-217e-4dbe-851c-dd53c24f09a5'; // unibasic-combined-pattern-analysis
const CLUSTER_ID = '7c0fdb2d-827d-465d-a914-0b7e15341875'; // export/UPS batch-export pattern
const UPS_SUB_ID = '785b884d-324a-4590-8cbb-72241ce8ec16';
const UPS_SPEC_ID = '3bb10b62-30e1-41ba-9753-5540da863faf';
const UPS_SCAFFOLD_ID = '0cad47e6-e882-4643-8b08-645fff05dd07';

type TimelineEntry = { kind: 'llm-wait'; label: string; startMs: number; endMs: number };
let timeline: TimelineEntry[] = [];
let videoT0 = 0;

async function llmWait(label: string, fn: () => Promise<void>): Promise<void> {
  const startMs = Date.now() - videoT0;
  await fn();
  const endMs = Date.now() - videoT0;
  timeline.push({ kind: 'llm-wait', label, startMs, endMs });
}

/**
 * Inject a fixed bottom-of-screen caption banner in plain language.
 * Re-created after every page.goto() (navigation wipes the DOM).
 */
async function setCaption(page: Page, text: string): Promise<void> {
  try { await page.waitForLoadState('domcontentloaded', { timeout: 5_000 }); } catch { /* tolerate */ }
  await page.evaluate((t) => {
    let banner = document.getElementById('demo-caption-banner') as HTMLDivElement | null;
    if (!banner) {
      banner = document.createElement('div');
      banner.id = 'demo-caption-banner';
      banner.setAttribute('aria-hidden', 'true');
      banner.style.cssText = [
        'position:fixed', 'left:0', 'right:0', 'bottom:0',
        'background:rgba(15,23,42,0.96)', 'color:#f8fafc',
        'padding:18px 40px', 'font-size:20px', 'line-height:1.45',
        'font-weight:500', 'text-align:center', 'z-index:2147483647',
        'border-top:3px solid #6366f1',
        'box-shadow:0 -8px 28px rgba(0,0,0,0.45)',
        'font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,system-ui,sans-serif',
        'pointer-events:none',
      ].join(';');
      document.body.appendChild(banner);
    } else if (banner.parentNode !== document.body) {
      document.body.appendChild(banner);
    }
    banner.textContent = t;
  }, text);
}

// ─── Pre-bake helpers (API only — no on-screen wait) ─────────────────────

async function ensureSpecSigned(req: APIRequestContext): Promise<void> {
  const spec = await req.get(`${API_BASE}/api/v1/specs/${UPS_SPEC_ID}`, { headers: ENG });
  if (spec.ok()) {
    const body = await spec.json();
    if (body.state === 'SIGNED') return;
  }
  // Already signed earlier this session via sign_ups.py; if not, this demo
  // will simply show whatever state the spec is really in — no faking.
}

async function ensureProposalLive(req: APIRequestContext): Promise<string | null> {
  const r = await req.get(`${API_BASE}/api/v1/corpora/${CORPUS_ID}/archetype-proposals`, { headers: ENG });
  if (!r.ok()) return null;
  const body = await r.json();
  const forCluster = (body.data ?? []).filter((p: any) => p.patternClusterId === CLUSTER_ID);
  const live = forCluster.find((p: any) => p.state === 'PRODUCTION');
  return live?.id ?? forCluster[0]?.id ?? null;
}

async function ensureMigrationPlan(req: APIRequestContext): Promise<void> {
  const existing = await req.get(`${API_BASE}/api/v1/corpora/${CORPUS_ID}/migration-plan`, { headers: ENG });
  if (existing.ok()) return;
  // Deterministic (Kahn BFS on the call graph) — no LLM call, safe to pre-bake.
  await req.post(`${API_BASE}/api/v1/corpora/${CORPUS_ID}/migration-plan/generate`, {
    headers: ADMIN, data: {},
  });
}

test.describe('UniBasic — Pattern Analysis + live archetype authoring + real scaffold', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('UniBasic corpus → pattern analysis → live archetype → real scaffold → validation', async ({ page, request }, testInfo) => {
    const beat = process.env.RECORD_DEMO === '1' ? 3_800 : 0;
    videoT0 = Date.now();
    timeline = [];

    async function emitTimeline() {
      if (process.env.RECORD_DEMO !== '1') return;
      const totalMs = Date.now() - videoT0;
      const payload = {
        videoTotalMs: totalMs,
        entries: timeline,
        outputDir: testInfo.outputDir,
      };
      const json = JSON.stringify(payload, null, 2);
      const timelineDir = resolve(dirname(testInfo.config.configFile ?? testInfo.config.rootDir), 'test-results');
      await mkdir(timelineDir, { recursive: true });
      await writeFile(join(timelineDir, 'timeline-latest.json'), json);
    }

    try {
      await ensureSpecSigned(request);
      const proposalId = await ensureProposalLive(request);
      await ensureMigrationPlan(request);

      await test.step('Projects grid — the UniBasic project', async () => {
        await page.goto('/projects');
        await setCaption(page, `This project has nine UniBasic (PICK/MultiValue) routines from a real client codebase.`);
        try {
          await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate cold-boot render race */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('corpus detail — file + routine counts', async () => {
        await page.goto(`/corpora/${CORPUS_ID}`);
        await setCaption(page, `Nine UniBasic files, parsed with our own UniBasic parser — over six hundred lines of business logic. Next, the platform looks for shared patterns across all of them.`);
        try {
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('dependency graph — who calls whom', async () => {
        await page.goto(`/corpora/${CORPUS_ID}/dependency-graph`);
        await page.waitForLoadState('networkidle').catch(() => { /* tolerate */ });
        await setCaption(page, `The platform maps which routines call which — including calls out to external programs it hasn't seen yet. This map decides the migration order.`);
        try {
          await expect(page.locator('canvas, svg').first()).toBeVisible({ timeout: 8_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat * 1.5);
      });

      await test.step('migration plan — what moves in which wave', async () => {
        await page.goto(`/corpora/${CORPUS_ID}/migration-plan`);
        await setCaption(page, `From that map, the migration is planned in waves. Routines others depend on move first — export goes in the last wave because it calls VZ.SALES.BR.`);
        try {
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 8_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        for (let i = 0; i < 2; i += 1) {
          await page.mouse.wheel(0, 350);
          await page.waitForTimeout(900);
        }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('pattern analysis — how many distinct behaviours does this codebase actually have?', async () => {
        await page.goto(`/corpora/${CORPUS_ID}/pattern-analysis`);
        await setCaption(page, `The platform reads every routine in one pass and groups the ones that behave alike. This corpus has two shared patterns and five one-off routines.`);
        try {
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        // Scroll past the 3 stat tiles + cluster cards.
        for (let i = 0; i < 3; i += 1) {
          await page.mouse.wheel(0, 400);
          await page.waitForTimeout(800);
        }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('the export/UPS cluster — a live, LLM-authored archetype', async () => {
        await setCaption(page, `The platform proposed this pattern, then compiled and tested it automatically. Once it passed, it went live — no code change, no restart.`);
        if (proposalId) {
          // Expand the proposal panel so the "Live" badge + test count + file
          // list are all visible on screen at once.
          const proposalRow = page.locator('button', { hasText: 'canonical-unibasic' }).first();
          try {
            await proposalRow.scrollIntoViewIfNeeded({ timeout: 5_000 });
            await proposalRow.click({ timeout: 5_000 });
          } catch { /* tolerate — badge is still visible even collapsed */ }
        }
        if (beat) await page.waitForTimeout(beat);
        try {
          await expect(page.getByText('Live', { exact: true }).first()).toBeVisible({ timeout: 8_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step(`UPS routine — the signed spec`, async () => {
        await page.goto(`/subroutines/${UPS_SUB_ID}/review`);
        await setCaption(page, `An engineer extracted a spec from the UniBasic source, and an SME reviewed and signed it. The generator uses this signed spec.`);
        const sig = page.getByTestId('evidence-block-signature');
        try { await expect(sig).toBeVisible({ timeout: 10_000 }); } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        for (let i = 0; i < 2; i += 1) {
          await page.mouse.wheel(0, 400);
          await page.waitForTimeout(900);
        }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('the real, generated scaffold — not a template', async () => {
        await page.goto(`/scaffolds/${UPS_SCAFFOLD_ID}`);
        await setCaption(page, `From that signed spec, the platform generated this package one routine at a time. Twelve files, tailored to UPS.`);
        try {
          await expect(page.getByRole('heading', { name: /\d+ files · \d+ TODOs · java-spring/ })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        // Click into the real generated service + exception classes.
        await setCaption(page, `This is the generated Java code — the export logic, the SFTP upload, and error handling — based on the signed spec.`);
        try {
          const files = page.locator('button', { hasText: /BatchExportService\.java|SftpUploadException\.java/ });
          const n = await files.count();
          for (let i = 0; i < Math.min(2, n); i += 1) {
            await files.nth(i).click();
            await page.waitForTimeout(2_500);
          }
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('validation report — one gate re-run live, two honestly not yet possible', async () => {
        await page.goto(`/scaffolds/${UPS_SCAFFOLD_ID}/validation`);
        await setCaption(page, `Compile already passed. Now the platform regenerates and runs the test-pack gate live.`);
        try {
          await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);

        const card = page.getByTestId('validation-card-test-pack');
        const postResp = page.waitForResponse(
          (r) => r.url().includes('/validate/test-pack') && r.request().method() === 'POST',
          { timeout: 180_000 },
        );
        await llmWait('Validation · test pack regenerate + mvn test', async () => {
          try {
            await card.getByRole('button', { name: /Regenerate \+ run/ }).click();
            await postResp;
          } catch { /* tolerate a flaked click — the honest report matters more than a perfect take */ }
        });
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('the generated documentation — real output from reverse engineering', async () => {
        await page.goto(`/corpora/${CORPUS_ID}/docs`);
        await setCaption(page, `The platform also generates documentation from what it reverse-engineered — routine summaries, a data dictionary, a glossary, and business rules.`);
        try {
          await expect(page.getByText('Documentation', { exact: true }).first()).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);

        // Routine summaries — open one, written straight from the real UniBasic source.
        try {
          await page.locator('nav button', { hasText: 'Routines' }).first().click({ timeout: 5_000 });
          await page.waitForTimeout(1_000);
          await page.locator('div.flex.min-h-0.flex-col button.w-full.border-b').first().click({ timeout: 5_000 });
        } catch { /* tolerate */ }
        await setCaption(page, `Every routine gets a summary of what it does and what it depends on, generated from the UniBasic source.`);
        if (beat) await page.waitForTimeout(beat);

        // Data dictionary — the shared vocabulary the platform extracted across the corpus.
        try {
          await page.locator('nav button', { hasText: 'Data Dictionary' }).first().click({ timeout: 5_000 });
          await page.waitForTimeout(1_000);
          await page.locator('div.flex.min-h-0.flex-col button.w-full.border-b').first().click({ timeout: 5_000 });
        } catch { /* tolerate */ }
        await setCaption(page, `A data dictionary and glossary capture how this UniBasic system uses its fields and terms.`);
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('portfolio dashboard — closing wide shot across every project', async () => {
        await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
        await page.goto('/platform/portfolio');
        await setCaption(page, `This is progress tracked across every migration project on the platform, including UniBasic — specs extracted, signed, scaffolded, and validated.`);
        try {
          await expect(page.getByRole('heading', { name: /Portfolio dashboard/i, level: 1 })).toBeVisible({ timeout: 10_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        for (let i = 0; i < 2; i += 1) {
          await page.mouse.wheel(0, 500);
          await page.waitForTimeout(1_100);
        }
        if (beat) await page.waitForTimeout(beat);
      });

    } finally {
      await emitTimeline();
    }
  });
});
