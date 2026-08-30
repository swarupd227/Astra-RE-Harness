/**
 * UniBasic demo — ingest → signed spec → generated Java Spring code →
 * live validation → generated documentation.
 *
 * Rewritten 2026-08-28 against the deployed Azure environment. The prior
 * version of this file targeted a local docker-compose stack and hardcoded
 * IDs seeded into a local Postgres instance that no longer exists — this
 * version points at the real Azure API/frontend and uses IDs verified live
 * immediately before writing this script:
 *
 *   - Corpus "UniBasic Pick Sample" (7 files, 577 LOC, git-sourced)
 *   - Routine add-branch-to-user.pick — signed spec, 4 invariants /
 *     3 side effects / 4 edge cases / 4 open questions
 *   - Its Java Spring scaffold (8 files) — compile pre-baked green before
 *     recording; test-pack is the one gate re-run live on screen (real
 *     Maven compile + JUnit run, ~6s — fast enough to just let it play,
 *     no timeline-cut needed, but the mechanism is kept in case a future
 *     revision of this script adds back a slower live step)
 *   - Equivalence / falsifying are honestly left "not run": Rocket
 *     UniData/UniVerse has no free/scriptable runtime to compare against,
 *     a constraint stated directly in the generated pom.xml's own
 *     description. The demo does not pretend those gates exist.
 *
 * RECORD_DEMO=1 emits test-results/timeline-latest.json so
 * scripts/trim-demo.mjs can cut any llm-wait segment and replace it with a
 * caption card. Target trimmed length: ~2:30-3:00 before intro/outro.
 */
import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { expect, test, type Page } from '@playwright/test';

const ENG = { 'X-Dev-Persona': 'engineer' };

// Real, durable IDs on the deployed Azure instance — verified live 2026-08-28.
const CORPUS_ID = '3903395a-71f2-4316-a25a-37ec45c50082'; // UniBasic Pick Sample
const SUB_ID = '911a7b59-a17a-4231-a5a6-80bf610c3b67';     // add-branch-to-user
const SCAFFOLD_ID = '6f766341-0bd0-4e72-a916-1f0cb9d56254'; // java-spring, 8 files

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

/**
 * Navigate, then check for a genuine connectivity blip (not the ~3s cold-
 * start window every fresh mount has while its first health probe is in
 * flight — that's a normal, brief loading state). If the "unreachable" /
 * "API offline" indicators are STILL showing after settling, this is a real
 * transient failure — reload once and give it a second chance rather than
 * recording ten more seconds of broken skeleton state.
 */
async function gotoAndSettle(page: Page, url: string): Promise<void> {
  await page.goto(url);
  try { await page.waitForLoadState('domcontentloaded', { timeout: 5_000 }); } catch { /* tolerate */ }
  await page.waitForTimeout(2_500); // past the normal cold-start probe window
  try {
    const stillDown = await page.getByText(/unreachable|API offline/i).first().isVisible({ timeout: 500 });
    if (stillDown) {
      await page.waitForTimeout(2_000);
      await page.reload();
      try { await page.waitForLoadState('domcontentloaded', { timeout: 8_000 }); } catch { /* tolerate */ }
      await page.waitForTimeout(1_500);
    }
  } catch { /* tolerate — no indicator found is the good case */ }
}

test.describe('UniBasic → Java Spring: signed spec, generated code, live validation', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('UniBasic corpus → signed spec → generated Java → live test-pack → docs', async ({ page }, testInfo) => {
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
      await test.step('Home — what this platform does', async () => {
        // beforeEach already navigated to '/' to set localStorage — a second
        // goto('/') here just reloads the exact same page, doubling the
        // slowest, most-visible load of the whole recording (the opening
        // seconds). Settle on what's already loaded instead.
        try { await page.waitForLoadState('domcontentloaded', { timeout: 5_000 }); } catch { /* tolerate */ }
        await setCaption(page, `Astra reverse-engineers legacy source code and migrates it to a modern stack — with a human reviewing every claim along the way.`);
        try {
          await expect(page.getByText('ASTRA RE HARNESS')).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate cold-boot render race */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('Projects — the UniBasic project', async () => {
        await gotoAndSettle(page, '/projects');
        await setCaption(page, `This project is a real UniBasic (Pick/MultiValue) codebase — the kind of terse, 40-year-old business logic that's hardest to migrate safely.`);
        try {
          await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        // Scroll the QA-test scratch corpora (sorted first, above the real
        // projects) out of frame before the beat that actually gets shown.
        try {
          await page.getByText('UniBasic Pick Sample').first().scrollIntoViewIfNeeded({ timeout: 5_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat / 2);
      });

      await test.step('Corpus detail — parsed inventory', async () => {
        await gotoAndSettle(page, `/corpora/${CORPUS_ID}`);
        await setCaption(page, `Seven UniBasic files, parsed with Astra's own UniBasic parser into an inventory of routines — ready for extraction.`);
        try {
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('Draft spec — what the LLM actually extracted', async () => {
        await gotoAndSettle(page, `/subroutines/${SUB_ID}/spec`);
        await setCaption(page, `An LLM reads the source and extracts a structured spec — inputs, outputs, invariants, side effects, and edge cases — each one citing the exact source lines it came from.`);
        try {
          await expect(page.getByText('DRAFT SPEC')).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        await setCaption(page, `This UniBasic routine has a sentinel value for a missing argument, and an abrupt error path with no cleanup — the kind of tacit behaviour a purely mechanical translation would carry forward without ever explaining it.`);
        for (let i = 0; i < 2; i += 1) {
          await page.mouse.wheel(0, 400);
          await page.waitForTimeout(900);
        }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('Spec review — signed, evidence-backed', async () => {
        await gotoAndSettle(page, `/subroutines/${SUB_ID}/review`);
        await setCaption(page, `A human reviewer signs off on every claim. The signature is a real RFC-8785 canonical-JSON signature — checkable with the public key on any machine.`);
        const sig = page.getByTestId('evidence-block-signature');
        try { await expect(sig).toBeVisible({ timeout: 10_000 }); } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        try { await sig.scrollIntoViewIfNeeded({ timeout: 5_000 }); } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('Generated code — every package the platform has produced', async () => {
        await gotoAndSettle(page, '/scaffolds');
        await setCaption(page, `Every package generated from a signed spec lands here — across every routine, every target stack.`);
        try {
          await expect(page.getByText('Generated code').first()).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('The real, generated Java — not a template', async () => {
        await gotoAndSettle(page, `/scaffolds/${SCAFFOLD_ID}`);
        await setCaption(page, `From that signed spec, the platform generated a real Spring service — eight files, tailored to this one routine.`);
        try {
          await expect(page.getByRole('heading', { name: /\d+ files · \d+ TODOs · java-spring/ })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        await setCaption(page, `Every method cites the exact spec claim it came from — this is generated code, but it's not a black box.`);
        try {
          const files = page.locator('button', { hasText: /DynamicArrayOps\.java|UserBranchService\.java/ });
          const n = await files.count();
          for (let i = 0; i < Math.min(2, n); i += 1) {
            await files.nth(i).click();
            await page.waitForTimeout(2_500);
          }
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('Validation report — one gate re-run live, one honestly not yet possible', async () => {
        await gotoAndSettle(page, `/scaffolds/${SCAFFOLD_ID}/validation`);
        await setCaption(page, `Compile already passed. Now the platform regenerates the test pack from the signed spec and runs it live.`);
        try {
          await expect(page.getByText('Validation report')).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);

        const card = page.getByText('Test pack').locator('..').locator('..');
        const postResp = page.waitForResponse(
          (r) => r.url().includes('/validate/test-pack') && r.request().method() === 'POST',
          { timeout: 60_000 },
        );
        await llmWait('Validation · test pack regenerate + mvn test', async () => {
          try {
            await page.getByRole('button', { name: /Regenerate \+ run/ }).click();
            await postResp;
          } catch { /* tolerate a flaked click — the honest report matters more than a perfect take */ }
        });
        if (beat) await page.waitForTimeout(beat * 1.5);

        await setCaption(page, `Equivalence and the property-based gate are honestly left not-run: UniData/UniVerse has no free, scriptable runtime to compare against. The platform doesn't pretend that check exists.`);
        try {
          await page.mouse.wheel(0, 500);
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('Generated documentation — real output from reverse engineering', async () => {
        await gotoAndSettle(page, `/corpora/${CORPUS_ID}/docs`);
        await setCaption(page, `The same reverse-engineering pass also produces documentation — functional requirements, a data dictionary, a glossary — straight from the UniBasic source.`);
        try {
          await expect(page.getByText('DOCUMENTATION')).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        try {
          await page.locator('button', { hasText: /^Functional Requirements/ }).first().click({ timeout: 5_000 });
          await page.waitForTimeout(1_000);
        } catch { /* tolerate */ }
        await setCaption(page, `Twenty-six functional requirements, each traceable back to the routine that implements it.`);
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('Closing — the whole workspace', async () => {
        await gotoAndSettle(page, '/');
        await setCaption(page, `From forty-year-old UniBasic to reviewed, signed, tested Java — with a human accountable for every step.`);
        try {
          await expect(page.getByText('ASTRA RE HARNESS')).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat * 1.3);
      });

    } finally {
      await emitTimeline();
    }
  });
});
