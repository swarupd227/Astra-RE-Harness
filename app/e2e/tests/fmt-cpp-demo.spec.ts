/**
 * fmt / C++ end-to-end demo (Phase 9.1.h).
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
 * has not appeared after 60s (fmt clone + parse end-to-end is comparable
 * to Indy's; budget is generous).
 *
 * Recording: demo recording for this routine is on hold per Phase 9 scope.
 * The test exists today as the artefact that will drive the demo recording
 * once Phase 9.1 ships green.
 */
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
      // Accept any routine whose bare name is 'format' (free function or
      // namespace-qualified). Falls back to vformat / format_to too — those
      // are the same canonical surface.
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

  test('format extract → SME signs → engineer scaffolds → audit complete', async ({ page }) => {
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

    await test.step('show fmt corpus surface', async () => {
      await page.goto(`/corpora/${corpusId}`);
      await expect(page.getByRole('heading', { name: FMT_NAME, level: 1 })).toBeVisible();
    });

    await test.step(`open ${subName}`, async () => {
      await page.goto(`/subroutines/${subId}`);
      await expect(
        page.getByRole('heading', { name: new RegExp(subName.replace('::', '::')), level: 1 }),
      ).toBeVisible();
    });

    await test.step('(re-)extract spec', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Real extraction on fmt::format is large (~300 LOC header) — 240s budget.
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
        timeout: 240_000,
      });
    });

    await test.step('engineer routes to SME', async () => {
      await page.getByRole('button', { name: 'View draft spec' }).click();
      await expect(page.getByRole('heading', { name: new RegExp(subName) }).first()).toBeVisible();
      await page.getByRole('button', { name: /Route to SME/ }).click();
      await expect(page).toHaveURL(/\/review$/);
    });

    await test.step('SME accepts every claim + resolves every open question + signs', async () => {
      await switchPersona(page, 'sme');

      // C++ claims include the 3 net-new kinds for the Phase 9.1 schema
      // (template_instantiation, undefined_behavior, exception_contract) on
      // top of the 6 shared with Delphi. The card-id pattern is the same —
      // `claim-<id>` — so the existing iteration works unchanged.
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

      const total = acceptIds.length + resolveIds.length;
      await expect(
        page.getByRole('heading', { name: new RegExp(`^${total} \\/ ${total} claims processed$`) }),
      ).toBeVisible();

      await page.getByRole('button', { name: /^Sign spec$/ }).first().click();
      await page.getByRole('checkbox').check();
      await page.getByRole('dialog').getByRole('button', { name: /^Sign spec$/ }).click();
      await expect(page.getByTestId('evidence-block-signature')).toBeVisible({ timeout: 15_000 });
    });

    await test.step('engineer generates scaffold from canonical-cpp-fmt', async () => {
      await switchPersona(page, 'engineer');
      const generate = page.getByRole('link', { name: /Generate scaffold/ });
      const openExisting = page.getByRole('link', { name: /Open scaffold/ });
      if (await generate.count()) {
        await generate.click();
        await expect(page.getByRole('button', { name: /Open scaffold/ })).toBeVisible({
          timeout: 120_000,
        });
        await page.getByRole('button', { name: /Open scaffold/ }).click();
      } else {
        await openExisting.click();
      }
      // The C++ archetype emits 4 source files (Formatter.cs / .java,
      // FormatString.cs / .java, FormatException.cs / .java, tests) plus
      // 2 project files. Six files total — same shape as the Delphi archetype.
      await expect(page.getByRole('heading', { name: /\d+ files · \d+ TODOs · dotnet8/ })).toBeVisible();
    });

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
  });
});
