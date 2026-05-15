/**
 * MINPACK end-to-end demo (Phase C.1 + B.1..B.5 against real third-party Fortran).
 *
 * Assumes the MINPACK corpus has already been ingested via
 *   POST /api/v1/ingest/git
 *   { "name": "MINPACK (F77 nonlinear least squares)",
 *     "url":  "https://github.com/certik/minpack.git" }
 *
 * Then drives HYBRD1 through the full Appendix-A flow:
 *   engineer  : open HYBRD1 → (re-)extract → route to SME
 *   sme       : accept every claim, resolve every open question, sign
 *   engineer  : generate scaffold → open artifact → commit (stub)
 *
 * NO dev/reset is called — that would wipe MINPACK. The test idempotently
 * picks whatever subroutine the corpus reports as HYBRD1 and proceeds.
 * Run with RECORD_DEMO=1 to capture a slow-paced video for stakeholders.
 */
import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };
const MINPACK_NAME = 'MINPACK (F77 nonlinear least squares)';
const TARGET_SUBROUTINE = 'HYBRD1';

async function switchPersona(page: Page, persona: 'engineer' | 'sme') {
  const label = persona === 'sme' ? 'SME' : 'Engineer';
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

// Timeline marker for LLM-driven waits — emitted as a sidecar JSON next to
// the Playwright video so the post-process script can cut those segments
// out (real Anthropic calls still happen; viewers just don't watch the
// spinner spin). All timestamps are ms since the recording started.
type TimelineEntry = { kind: 'llm-wait'; label: string; startMs: number; endMs: number };
const timeline: TimelineEntry[] = [];
let videoT0 = 0;

async function llmWait(label: string, fn: () => Promise<void>): Promise<void> {
  const startMs = Date.now() - videoT0;
  await fn();
  const endMs = Date.now() - videoT0;
  timeline.push({ kind: 'llm-wait', label, startMs, endMs });
}

async function findMinpackSubroutineId(page: Page, subroutineName: string): Promise<{ corpusId: string; subId: string } | null> {
  const list = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
  const corpus = list.data.find((c: { name: string }) => c.name === MINPACK_NAME);
  if (!corpus) return null;
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG }).then((r) => r.json());
  for (const file of detail.latestVersion.files) {
    for (const sub of file.subroutines) {
      if (sub.name === subroutineName) return { corpusId: corpus.id, subId: sub.id };
    }
  }
  return null;
}

test.describe('MINPACK · real-GitHub demo', () => {
  test.beforeEach(async ({ page }) => {
    // Seed persona — no dev/reset; we are reusing the pre-ingested MINPACK corpus.
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('HYBRD1 extract → SME signs → engineer scaffolds → commit (stub)', async ({ page }, testInfo) => {
    const beat = process.env.RECORD_DEMO === '1' ? 2_000 : 0;
    // Anchor the timeline to the moment the test starts driving the page.
    // Playwright's video starts when the browser context is created (a few
    // ms before this point); the trim script accepts a small fudge factor.
    videoT0 = Date.now();
    timeline.length = 0;
    // MINPACK auto-seeds in the background after every reset. Poll up to
    // 30s for it to appear; skip cleanly if it never does.
    let located: { corpusId: string; subId: string } | null = null;
    const deadline = Date.now() + 30_000;
    while (Date.now() < deadline) {
      located = await findMinpackSubroutineId(page, TARGET_SUBROUTINE);
      if (located) break;
      await page.waitForTimeout(2_000);
    }
    test.skip(located === null, 'MINPACK corpus not present after 30s. Set DATABASE_SEED_MINPACK=true on the API.');
    const { corpusId, subId } = located!;

    await test.step('show MINPACK corpus surface', async () => {
      await page.goto(`/corpora/${corpusId}`);
      await expect(page.getByRole('heading', { name: MINPACK_NAME, level: 1 })).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step(`open ${TARGET_SUBROUTINE}`, async () => {
      await page.goto(`/subroutines/${subId}`);
      await expect(page.getByRole('heading', { name: TARGET_SUBROUTINE, level: 1 })).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('(re-)extract spec via real Anthropic', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Real extraction on HYBRD1 (~123 LOC) finishes in ~45-60s. Buffer to 180s.
      // The wait itself is the boring part; mark its bounds so the trim
      // script can cut it from the final video and replace with a caption.
      await llmWait('Anthropic · spec extraction on HYBRD1', async () => {
        await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
          timeout: 180_000,
        });
      });
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('engineer routes to SME', async () => {
      await page.getByRole('button', { name: 'View draft spec' }).click();
      await expect(page.getByRole('heading', { name: TARGET_SUBROUTINE }).first()).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
      await page.getByRole('button', { name: /Route to SME/ }).click();
      await expect(page).toHaveURL(/\/review$/);
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('SME accepts every claim + resolves every open question + signs', async () => {
      await switchPersona(page, 'sme');

      // Iterate whatever Claude rendered — claim ids are not deterministic
      // across runs on real Anthropic.
      const cardIds = await page.locator('article[id^="claim-"]').evaluateAll(
        (els) => els.map((el) => (el as HTMLElement).id.replace(/^claim-/, '')),
      );
      expect(cardIds.length).toBeGreaterThan(0);

      const acceptIds: string[] = [];
      const resolveIds: string[] = [];
      for (const id of cardIds) {
        const card = page.locator(`article[id="claim-${id}"]`);
        if (await card.getByRole('button', { name: /^Accept$/ }).count()) {
          acceptIds.push(id);
        } else if (await card.getByRole('button', { name: /^Resolve in spec$/ }).count()) {
          resolveIds.push(id);
        }
      }

      for (const id of acceptIds) {
        const card = page.locator(`article[id="claim-${id}"]`);
        await card.scrollIntoViewIfNeeded();
        await card.getByRole('button', { name: /^Accept$/ }).click();
      }
      for (const id of resolveIds) {
        const card = page.locator(`article[id="claim-${id}"]`);
        await card.scrollIntoViewIfNeeded();
        await card.getByRole('button', { name: /^Resolve in spec$/ }).click();
        await card.getByRole('textbox').fill('Resolved by SME during MINPACK demo.');
        await card.getByRole('button', { name: /^Save$/ }).click();
      }
      if (beat) await page.waitForTimeout(beat);

      const total = acceptIds.length + resolveIds.length;
      await expect(
        page.getByRole('heading', { name: new RegExp(`^${total} \\/ ${total} claims processed$`) }),
      ).toBeVisible();

      await page.getByRole('button', { name: /^Sign spec$/ }).first().click();
      if (beat) await page.waitForTimeout(beat);
      await page.getByRole('checkbox').check();
      await page.getByRole('dialog').getByRole('button', { name: /^Sign spec$/ }).click();
      await expect(page.getByTestId('evidence-block-signature')).toBeVisible({ timeout: 15_000 });
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('engineer generates scaffold', async () => {
      await switchPersona(page, 'engineer');
      const generate = page.getByRole('link', { name: /Generate scaffold/ });
      const openExisting = page.getByRole('link', { name: /Open scaffold/ });
      if (await generate.count()) {
        await generate.click();
        // Real scaffold generation is also an LLM-driven wait (~30-45s).
        // Mark it so the trim script can cut it from the demo video.
        await llmWait('Anthropic · scaffold generation', async () => {
          await expect(page.getByRole('button', { name: /Open scaffold/ })).toBeVisible({
            timeout: 60_000,
          });
        });
        if (beat) await page.waitForTimeout(beat);
        await page.getByRole('button', { name: /Open scaffold/ }).click();
      } else {
        await openExisting.click();
      }
      await expect(page.getByRole('heading', { name: /\d+ files · \d+ TODOs · dotnet8/ })).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    // ── Validation report (recording-only) ──────────────────────────────
    // Visit the three-gate validation surface before commit so the demo
    // shows COMPILE / TEST_PACK / EQUIVALENCE turning green. The gates
    // are config-off by default (commits don't require them), so this
    // step exists purely for the visual narrative — it's the on-screen
    // proof of "harness validates the scaffold before it ships."
    if (process.env.RECORD_DEMO === '1') {
      await test.step('validation report — three gates go green', async () => {
        // Resolve the scaffold id via the API; the page URL is the easiest hop.
        const spec = await page.request
          .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
          .then((r) => r.json());
        const scaffold = await page.request
          .get(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, { headers: ENG })
          .then((r) => r.json());
        await page.goto(`/scaffolds/${scaffold.id}/validation`);
        await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible();

        // Helper: trigger one stage's Run button and wait for the POST to
        // finish so the badge updates before we move on. The actual gate
        // duration (dotnet build / dotnet test / gfortran compile+run) is
        // wrapped in llmWait so the trim script cuts it.
        async function runGate(testId: string, btnLabel: RegExp, postPath: string, captionLabel: string) {
          const card = page.getByTestId(testId);
          // Set up BOTH response listeners before the click so neither
          // misses its target. React Query refetches the validation list
          // ~10 ms after the POST returns; if we set up the GET listener
          // only after awaiting the POST, we race past the refetch.
          const postResp = page.waitForResponse(
            (r) => r.url().includes(postPath) && r.request().method() === 'POST',
            { timeout: 180_000 },
          );
          const getResp = page.waitForResponse(
            (r) =>
              /\/scaffolds\/[0-9a-f-]+\/validation$/.test(r.url())
              && r.request().method() === 'GET',
            { timeout: 60_000 },
          );
          await llmWait(captionLabel, async () => {
            await card.getByRole('button', { name: btnLabel }).click();
            await postResp;
          });
          // The refetch listener was set up before the click, so even if
          // the GET fired immediately after the POST, we still catch it.
          await getResp;
        }
        await runGate('validation-card-compile', /Run compile/, '/validate/compile', 'Validation · dotnet build');
        await runGate('validation-card-test-pack', /Regenerate \+ run/, '/validate/test-pack', 'Validation · dotnet test');
        await runGate('validation-card-equivalence', /Run equivalence/, '/validate/equivalence', 'Validation · cross-runtime');

        await expect(page.getByText(/All gates green/)).toBeVisible();
        if (beat) await page.waitForTimeout(beat);

        // Hop back to the scaffold artifact so the commit-button is in view
        // for the next step.
        await page.goto(`/scaffolds/${scaffold.id}`);
        await expect(page.getByRole('heading', { name: /\d+ files · \d+ TODOs · dotnet8/ })).toBeVisible();
      });
    }

    await test.step('commit (stub) lands a faux hash', async () => {
      const commit = page.getByRole('button', { name: /Commit to Git/ });
      if (await commit.count()) {
        await commit.click();
        await expect(page.locator('a[href^="git://stub.local/"]')).toBeVisible({ timeout: 10_000 });
        if (beat) await page.waitForTimeout(beat);
      }
    });

    await test.step('audit trail records the full lifecycle', async () => {
      const spec = await page.request
        .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
        .then((r) => r.json());
      expect(spec.state).toBe('SIGNED');
      const audit = await page.request
        .get(`${API_BASE}/api/v1/specs/${spec.id}/audit`, { headers: ENG })
        .then((r) => r.json());
      const types = new Set<string>(audit.data.map((e: { eventType: string }) => e.eventType));
      expect(types).toEqual(
        expect.objectContaining(new Set([
          'spec.extracted',
          'spec.routed',
          'spec.signed',
          'scaffold.generated',
        ])),
      );
    });

    // ── Batch montage (Phase #1, fast-track) ────────────────────────────
    // Recording-only. Walks 5 more MINPACK routines through extract → sign
    // in rapid succession. Each routine flashes its detail page briefly,
    // clicks Extract via UI (so the real Anthropic call still happens and
    // gets cut by the trim script), then drives Route + SME-claim-accept
    // + sign via the API — skipping the slow SME UI loop. After cuts each
    // routine is ~8 seconds of visible flipbook instead of 50.
    const SME = { 'X-Dev-Persona': 'sme' };
    if (process.env.RECORD_DEMO === '1') {
      const montageRoutines = ['LMDIF1', 'LMDER1', 'HYBRJ1', 'FDJAC1', 'R1UPDT'];
      for (const routine of montageRoutines) {
        await test.step(`montage · ${routine}`, async () => {
          const located = await findMinpackSubroutineId(page, routine);
          if (!located) return; // MINPACK didn't carry this name — skip the slot

          // 1. Brief visual flash of the routine's detail page.
          await page.goto(`/subroutines/${located.subId}`);
          await expect(page.getByRole('heading', { name: routine, level: 1 })).toBeVisible();

          // 2. Click Extract — the only step that has to be UI-driven so the
          //    real LLM call streams. The llmWait wrap means trim-demo cuts
          //    the actual wait window.
          await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
          await llmWait(`Anthropic · spec extraction on ${routine}`, async () => {
            await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
              timeout: 180_000,
            });
          });

          // 3. Drive the rest via the API. Faster (no slowMo, no UI ceremony),
          //    same audit-log + state-machine effect. We pull the spec, route
          //    it, accept every claim under the SME persona, and sign — all
          //    in <2 s wall clock for the whole routine.
          const spec = await page.request
            .get(`${API_BASE}/api/v1/subroutines/${located.subId}/spec`, { headers: ENG })
            .then((r) => r.json());
          // Route to SME (engineer auth)
          await page.request.post(`${API_BASE}/api/v1/specs/${spec.id}/route`, {
            headers: ENG, data: { routingNote: 'Montage auto-route.' },
          });
          // Accept every claim across every taxonomy section. Path is the
          // JSON-Path the API expects: $.<section>[?(@.id=='<claim-id>')].
          const sections: Array<[string, Array<{ id: string }>]> = [
            ['invariants', spec.spec?.invariants ?? []],
            ['side_effects', spec.spec?.side_effects ?? []],
            ['edge_cases', spec.spec?.edge_cases ?? []],
            ['open_questions', spec.spec?.open_questions ?? []],
          ];
          for (const [section, claims] of sections) {
            for (const claim of claims) {
              const path = `$.${section}[?(@.id=='${claim.id}')]`;
              // "Resolve in spec" on an open question = action="edit" with
              // editedText (the SME rewrites the question into a resolved
              // claim). The `question` action is for raising a NEW question
              // on a non-question claim and the /sign precondition treats
              // it as still-unresolved — see ReviewableClaimCard.tsx:173-177.
              const body: Record<string, unknown> = section === 'open_questions'
                ? { claimPath: path, action: 'edit', editedText: 'Resolved during montage — accept reference behaviour as-is.' }
                : { claimPath: path, action: 'accept' };
              await page.request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, {
                headers: SME, data: body,
              });
            }
          }
          // Sanity: if sign returns non-2xx, fail loudly. The previous take
          // silently passed when the precondition was unmet.
          const signResp = await page.request.post(`${API_BASE}/api/v1/specs/${spec.id}/sign`, {
            headers: SME, data: { confirmation: 'I have reviewed every claim.' },
          });
          if (!signResp.ok()) {
            throw new Error(`Montage ${routine}: sign returned ${signResp.status()} — ${await signResp.text()}`);
          }

          // 4. Brief visual flash showing the now-SIGNED state. Re-loading
          //    the detail page picks up the new state from the API. We
          //    anchor on the heading (always present) — the SIGNED badge
          //    is somewhere on the page but the exact text shape varies
          //    by version, and a missing badge shouldn't crash the demo.
          await page.goto(`/subroutines/${located.subId}`);
          await expect(page.getByRole('heading', { name: routine, level: 1 })).toBeVisible({ timeout: 10_000 });
          await page.waitForTimeout(800);
        });
      }

      // ── Progress view — SIGNED-state filter on the cross-corpus surface ──
      await test.step('progress view — six signed', async () => {
        await page.goto('/subroutines?state=SIGNED');
        await expect(page.getByRole('heading', { name: 'Subroutines', level: 1 })).toBeVisible();
        // Hold so the viewer can count the rows: HYBRD1 + LMDIF1 + LMDER1 +
        // HYBRJ1 + FDJAC1 + R1UPDT = six signed entries.
        await page.waitForTimeout(4_000);
      });

      await test.step('dashboard wide shot — MINPACK detail', async () => {
        await page.goto(`/projects/${corpusId}`);
        await expect(page.getByRole('heading', { name: MINPACK_NAME, level: 1 })).toBeVisible();
        await page.waitForTimeout(3_000);
      });
    }

    // ── Emit timeline.json next to the video for the trim script ────────
    // Playwright drops video.webm in testInfo.outputDir, but that dir name
    // is auto-encoded with Unicode separators ("·", "→"). On Windows + Node
    // those occasionally tickle ENOENT during writeFile even though the dir
    // exists, so we also drop a fixed-path sidecar at test-results/
    // timeline-latest.json which the trim script picks up reliably.
    if (process.env.RECORD_DEMO === '1') {
      const totalMs = Date.now() - videoT0;
      const payload = {
        videoTotalMs: totalMs,
        entries: timeline,
        outputDir: testInfo.outputDir,
      };
      const json = JSON.stringify(payload, null, 2);
      // Drop the timeline at an ASCII-only path so the trim script doesn't
      // have to traverse the Unicode-encoded per-test directory. Anchor to
      // the playwright config file's directory (the e2e root) — that path
      // is stable across cwd changes and doesn't depend on `__dirname`
      // (which isn't defined when Playwright runs the spec under ESM).
      const timelineDir = resolve(dirname(testInfo.config.configFile ?? testInfo.config.rootDir), 'test-results');
      await mkdir(timelineDir, { recursive: true });
      await writeFile(join(timelineDir, 'timeline-latest.json'), json);
    }
  });
});
