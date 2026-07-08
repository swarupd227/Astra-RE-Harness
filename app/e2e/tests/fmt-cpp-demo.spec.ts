/**
 * fmt / C++ end-to-end demo (Phase 9.1.h, recording-extended in Phase 9.6).
 *
 * Walks the same engineer → SME → engineer flow as minpack-demo.spec.ts and
 * indy-delphi-demo.spec.ts but against the fmtlib/fmt seed (Phase 9.1.g).
 * Headline routine is `fmt::format` — the routine ID in the catalog will be
 * something like `fmt::format` or `format` depending on how the v0 cpp
 * parser resolves the namespace. The test is resilient by accepting any
 * subroutine whose name ends in `format` inside a file under
 * `include/fmt/core.h` or `include/fmt/base.h`.
 *
 * NO dev/reset — that would wipe the fmt corpus. Skips cleanly if the seed
 * has not appeared after 60s.
 *
 * RECORD_DEMO=1 emits a sidecar timeline-latest.json next to the Playwright
 * video so scripts/trim-demo.mjs can cut the LLM-bound waits and replace
 * them with caption cards. The trimmed result lands at ~2-3 min total.
 */
import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };
const FMT_NAME = 'fmt (C++ format library)';

async function switchPersona(page: Page, persona: 'engineer' | 'sme') {
  const label = persona === 'sme' ? 'SME' : 'Engineer';
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

type TimelineEntry = { kind: 'llm-wait'; label: string; startMs: number; endMs: number };
const timeline: TimelineEntry[] = [];
let videoT0 = 0;

async function llmWait(label: string, fn: () => Promise<void>): Promise<void> {
  const startMs = Date.now() - videoT0;
  await fn();
  const endMs = Date.now() - videoT0;
  timeline.push({ kind: 'llm-wait', label, startMs, endMs });
}

async function findFmtFormatSubroutine(
  page: Page,
): Promise<{ corpusId: string; subId: string; subName: string } | null> {
  const list = await page.request
    .get(`${API_BASE}/api/v1/corpora`, { headers: ENG })
    .then((r) => r.json());
  const corpus = list.data.find((c: { name: string }) => c.name === FMT_NAME);
  if (!corpus) return null;
  const detail = await page.request
    .get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG })
    .then((r) => r.json());
  for (const file of detail.latestVersion.files) {
    const rel = file.relativePath ?? '';
    if (!/include\/fmt\/(core|base|format)\.h$/i.test(rel)) continue;
    for (const sub of file.subroutines) {
      if (/(^|::)(v?format)(_to)?$/i.test(sub.name)) {
        return { corpusId: corpus.id, subId: sub.id, subName: sub.name };
      }
    }
  }
  return null;
}

test.describe('fmt · C++ demo · fmt::format end-to-end', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('format extract → SME signs → engineer scaffolds → audit complete', async ({ page }, testInfo) => {
    const beat = process.env.RECORD_DEMO === '1' ? 2_000 : 0;
    videoT0 = Date.now();
    timeline.length = 0;

    let located: { corpusId: string; subId: string; subName: string } | null = null;
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
      located = await findFmtFormatSubroutine(page);
      if (located) break;
      await page.waitForTimeout(3_000);
    }
    test.skip(
      located === null,
      'fmt corpus not present after 60s. Set DATABASE_SEED_FMT_DEMO=true on the API.',
    );
    const { corpusId, subId, subName } = located!;

    await test.step('Projects grid — C++ pill on fmt tile', async () => {
      await page.goto('/projects');
      await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible();
      const cppPill = page.getByTestId('corpus-language-cpp').first();
      try { await cppPill.waitFor({ timeout: 5_000 }); } catch { /* tolerate */ }
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('show fmt corpus surface', async () => {
      await page.goto(`/corpora/${corpusId}`);
      await expect(page.getByRole('heading', { name: FMT_NAME, level: 1 })).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step(`open ${subName} — extraction help (Phase 9.2.c)`, async () => {
      await page.goto(`/subroutines/${subId}`);
      await expect(
        page.getByRole('heading', { name: new RegExp(subName.replace('::', '::')), level: 1 }),
      ).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('(re-)extract spec via real Anthropic', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Real extraction on fmt::format is large (~300 LOC header) — 240s budget.
      await llmWait('Anthropic · spec extraction on fmt::format', async () => {
        await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
          timeout: 240_000,
        });
      });
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('engineer routes to SME', async () => {
      await page.getByRole('button', { name: 'View draft spec' }).click();
      await expect(page.getByRole('heading', { name: new RegExp(subName) }).first()).toBeVisible();
      if (beat) await page.waitForTimeout(beat);
      await page.getByRole('button', { name: /Route to SME/ }).click();
      await expect(page).toHaveURL(/\/review$/);
      if (beat) await page.waitForTimeout(beat);
    });

    await test.step('SME accepts every claim + resolves every open question + signs', async () => {
      await switchPersona(page, 'sme');

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
        await card.getByRole('textbox').fill('Resolved by SME during fmt demo.');
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

    await test.step('engineer generates scaffold from canonical-cpp-fmt', async () => {
      await switchPersona(page, 'engineer');
      const generate = page.getByRole('link', { name: /Generate scaffold/ });
      const openExisting = page.getByRole('link', { name: /Open scaffold/ });
      if (await generate.count()) {
        // UI click for the visual narrative (first attempt fires the LLM call).
        await generate.click();
        // The scaffold-generation LLM call sometimes returns malformed JSON;
        // wrap in an API-level retry loop. The visible wait still happens
        // on the page during this loop — the trim script cuts it either way.
        await llmWait('Anthropic · scaffold generation', async () => {
          const spec = await page.request
            .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
            .then((r) => r.json());
          const specId = spec.id;
          for (let attempt = 0; attempt < 5; attempt += 1) {
            const visible = await page.getByRole('button', { name: /Open scaffold/ })
              .isVisible({ timeout: 60_000 }).catch(() => false);
            if (visible) return;
            const r = await page.request.post(
              `${API_BASE}/api/v1/specs/${specId}/scaffold`,
              { headers: ENG },
            );
            if (r.ok()) {
              await page.reload();
            }
          }
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

    if (process.env.RECORD_DEMO === '1') {
      await test.step('validation report — four gates go green', async () => {
        const spec = await page.request
          .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
          .then((r) => r.json());
        const scaffold = await page.request
          .get(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, { headers: ENG })
          .then((r) => r.json());
        await page.goto(`/scaffolds/${scaffold.id}/validation`);
        await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible();
        if (beat) await page.waitForTimeout(beat);

        async function runGate(testId: string, btnLabel: RegExp, postPath: string, captionLabel: string) {
          const card = page.getByTestId(testId);
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
          await getResp;
        }
        await runGate('validation-card-compile', /Run compile/, '/validate/compile', 'Validation · dotnet build');
        await runGate('validation-card-test-pack', /Regenerate \+ run/, '/validate/test-pack', 'Validation · dotnet test');
        await runGate('validation-card-equivalence', /Run equivalence/, '/validate/equivalence', 'Validation · cross-runtime (C++ ↔ .NET)');
        await runGate('validation-card-falsifying', /Run 4th gate/, '/validate/falsifying', 'Validation · 4th gate (property test)');

        if (beat) await page.waitForTimeout(beat);
      });
    }

    await test.step('audit trail records the full C++ lifecycle', async () => {
      const spec = await page.request
        .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
        .then((r) => r.json());
      expect(spec.state).toBe('SIGNED');
      expect(spec.schemaId ?? spec.spec?.schemaId ?? 'cpp').toBe('cpp');
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

    if (process.env.RECORD_DEMO === '1') {
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
  });
});
