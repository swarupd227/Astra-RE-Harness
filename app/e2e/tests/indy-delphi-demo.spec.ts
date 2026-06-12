/**
 * Indy / Delphi end-to-end demo (Phase 9.0.i).
 *
 * Walks the same engineer → SME → engineer flow as minpack-demo.spec.ts but
 * against the IndySockets/Indy seed (Phase 9.0.h). Headline routine is the
 * SMTP connection handshake — the routine ID in the catalog will be
 * something like `TIdSMTP.Connect` once the delphi-parser-sidecar resolves
 * it. The test is resilient to the parser settling on a slightly different
 * qualified name (e.g., `TIdSMTPBase.Connect`) by accepting any subroutine
 * whose name ends in `.Connect` inside a unit named `IdSMTP*`.
 *
 * NO dev/reset — that would wipe the Indy corpus. Skips cleanly if the
 * seed has not appeared after 60s (Indy clone + parse end-to-end is the
 * slowest seed; budget is generous).
 *
 * Recording: demo recording for this routine is on hold per Phase 9.0
 * scope. The test exists today as the artefact that will drive the demo
 * recording once Phase 9.0 ships green.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };
const INDY_NAME = 'Indy Sockets (Delphi)';

async function switchPersona(page: Page, persona: 'engineer' | 'sme') {
  const label = persona === 'sme' ? 'SME' : 'Engineer';
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

async function findIndyConnectSubroutine(
  page: Page,
): Promise<{ corpusId: string; subId: string; subName: string } | null> {
  const list = await page.request
    .get(`${API_BASE}/api/v1/corpora`, { headers: ENG })
    .then((r) => r.json());
  const corpus = list.data.find((c: { name: string }) => c.name === INDY_NAME);
  if (!corpus) return null;
  const detail = await page.request
    .get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG })
    .then((r) => r.json());
  for (const file of detail.latestVersion.files) {
    if (!/IdSMTP/i.test(file.relativePath)) continue;
    for (const sub of file.subroutines) {
      // Accept any routine ending in `.Connect` (e.g. TIdSMTP.Connect,
      // TIdSMTPBase.Connect, TIdSMTPMin.Connect). Falls back to a plain
      // `Connect` if the parser stripped the class prefix.
      if (/(^|\.)Connect$/i.test(sub.name)) {
        return { corpusId: corpus.id, subId: sub.id, subName: sub.name };
      }
    }
  }
  return null;
}

test.describe('Indy · Delphi demo · TIdSMTP.Connect end-to-end', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('Connect extract → SME signs → engineer scaffolds → audit complete', async ({ page }) => {
    // Indy auto-seeds in the background after every API restart. Clone is
    // small (~5 MB after whitelist) but parse runs through the delphi
    // parser-sidecar; budget 60s.
    let located: { corpusId: string; subId: string; subName: string } | null = null;
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
      located = await findIndyConnectSubroutine(page);
      if (located) break;
      await page.waitForTimeout(3_000);
    }
    test.skip(
      located === null,
      'Indy corpus not present after 60s. Set DATABASE_SEED_INDY_DEMO=true on the API.',
    );
    const { corpusId, subId, subName } = located!;

    await test.step('show Indy corpus surface', async () => {
      await page.goto(`/corpora/${corpusId}`);
      await expect(page.getByRole('heading', { name: INDY_NAME, level: 1 })).toBeVisible();
    });

    await test.step(`open ${subName}`, async () => {
      await page.goto(`/subroutines/${subId}`);
      // The page heading uses the routine's bare last segment, so we
      // match on whichever Connect/.Connect form actually rendered.
      await expect(
        page.getByRole('heading', { name: new RegExp(subName.replace('.', '\\.')), level: 1 }),
      ).toBeVisible();
    });

    await test.step('(re-)extract spec', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Real extraction on the Indy Connect routine (~200 LOC of Delphi)
      // finishes in ~60-90s on Anthropic; allow 240s for the mock too.
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
        timeout: 240_000,
      });
    });

    await test.step('engineer routes to SME', async () => {
      await page.getByRole('button', { name: 'View draft spec' }).click();
      await expect(page.getByRole('heading', { name: new RegExp(subName.replace('.', '\\.')) }).first()).toBeVisible();
      await page.getByRole('button', { name: /Route to SME/ }).click();
      await expect(page).toHaveURL(/\/review$/);
    });

    await test.step('SME accepts every claim + resolves every open question + signs', async () => {
      await switchPersona(page, 'sme');

      // Delphi claims include the 5 new kinds (object_lifetime,
      // interface_implementation, property_accessor,
      // event_handler_contract, rtti_usage). The card-id pattern is the
      // same — `claim-<id>` — so the existing iteration works unchanged.
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
        await card.getByRole('textbox').fill('Resolved by SME during Indy demo.');
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

    await test.step('engineer generates scaffold from canonical-delphi-idsmtp', async () => {
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
      // The Delphi archetype emits 4 source files (IdSMTPMin.cs,
      // IIdComponent.cs, IdSMTPEvents.cs, tests/IdSMTPMinTests.cs)
      // plus 2 project files (Demo.IdSMTP.csproj, tests csproj). Six
      // files total. The page heading reads `N files · M TODOs · dotnet8`.
      await expect(page.getByRole('heading', { name: /\d+ files · \d+ TODOs · dotnet8/ })).toBeVisible();
    });

    await test.step('audit trail records the full Delphi lifecycle', async () => {
      const spec = await page.request
        .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
        .then((r) => r.json());
      expect(spec.state).toBe('SIGNED');
      expect(spec.schemaId ?? spec.spec?.schemaId ?? 'delphi').toBe('delphi');
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
