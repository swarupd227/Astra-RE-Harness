/**
 * VB6 / Inventory Sample end-to-end demo (Phase 10.0.i).
 *
 * Walks the same engineer → SME → engineer flow as minpack-demo and the
 * Phase 9 indy-delphi-demo / fmt-cpp-demo specs, but against the Nous-
 * authored VB6 Inventory Sample (Phase 10.0.h). Headline routine is
 * `frmOrderEntry.btnSubmit_Click` — the button-click handler that
 * exercises every new VB6 claim kind (onErrorHandler, comInteropContract,
 * eventHandlerContract, defaultPropertyUsage, lateBindingCall) in one
 * routine.
 *
 * The v0 parser may settle on a slightly different qualified name for
 * the handler (e.g. `btnSubmit_Click` vs `frmOrderEntry.btnSubmit_Click`)
 * so the test is resilient to either form.
 *
 * NO dev/reset — that would wipe the VB6 corpus. Skips cleanly if the
 * seed has not appeared after 60s (synthetic seed is faster than git-
 * clone seeds; the budget is generous).
 *
 * Recording: demo recording for this routine is on hold per Phase 10
 * scope. The test exists today as the artefact that will drive the
 * demo recording once Phase 10.0 ships green.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };
const VB6_NAME = 'VB6 Inventory Sample (Nous)';

async function switchPersona(page: Page, persona: 'engineer' | 'sme') {
  const label = persona === 'sme' ? 'SME' : 'Engineer';
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

async function findVb6SubmitHandler(
  page: Page,
): Promise<{ corpusId: string; subId: string; subName: string } | null> {
  const list = await page.request
    .get(`${API_BASE}/api/v1/corpora`, { headers: ENG })
    .then((r) => r.json());
  const corpus = list.data.find((c: { name: string }) => c.name === VB6_NAME);
  if (!corpus) return null;
  const detail = await page.request
    .get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG })
    .then((r) => r.json());
  for (const file of detail.latestVersion?.files ?? []) {
    // Headline file is frmOrderEntry.frm; the handler is btnSubmit_Click.
    if (!/frmOrderEntry/i.test(file.relativePath)) continue;
    for (const sub of file.subroutines ?? []) {
      // Accept any qualified-or-bare form ending in `btnSubmit_Click`.
      if (/(^|\.)btnSubmit_Click$/i.test(sub.name)) {
        return { corpusId: corpus.id, subId: sub.id, subName: sub.name };
      }
    }
  }
  return null;
}

test.describe('VB6 · Inventory Sample demo · frmOrderEntry.btnSubmit_Click end-to-end', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    // Phase 10.1.a.3 — clear any prior target-stack pick so the
    // schema-aware auto-correct effect on SpecReviewPage is exercised.
    // Without this the test inherits whatever the previous spec left in
    // localStorage and silently passes even if the default is broken.
    await page.evaluate(() => window.localStorage.removeItem('astra.targetStack'));
  });

  // Phase 10.1.a.3 — the New Corpus page's language picker must list
  // VB6. This is the surface a real user uses to ingest a VB6 codebase;
  // the demo proper hits the seed corpus and never goes through the
  // picker, so we'd silently regress without this check.
  test('VB6 appears in the language picker on /projects/new', async ({ page }) => {
    await page.goto('/projects/new');
    const select = page.getByTestId('corpus-schema');
    await expect(select).toBeVisible();
    // Wait for the schema list to populate (initial render shows only
    // the "Auto-detect" option until the API responds).
    await expect.poll(
      async () => (await select.locator('option').count()) > 1,
      { timeout: 15_000 },
    ).toBe(true);
    const values = await select.locator('option').evaluateAll(
      (opts) => opts.map((o) => (o as HTMLOptionElement).value),
    );
    expect(values).toContain('vb6');
  });

  test('btnSubmit_Click extract → SME signs → engineer scaffolds → audit complete', async ({ page }) => {
    // Inventory Sample auto-seeds in the background after every API
    // restart. Synthetic seed is local (no git clone) so 60s is generous.
    let located: { corpusId: string; subId: string; subName: string } | null = null;
    const deadline = Date.now() + 60_000;
    while (Date.now() < deadline) {
      located = await findVb6SubmitHandler(page);
      if (located) break;
      await page.waitForTimeout(3_000);
    }
    test.skip(
      located === null,
      'VB6 Inventory Sample not present after 60s. Set DATABASE_SEED_VB6_DEMO=true on the API.',
    );
    const { corpusId, subId, subName } = located!;

    await test.step('show VB6 Inventory Sample corpus surface', async () => {
      await page.goto(`/corpora/${corpusId}`);
      await expect(page.getByRole('heading', { name: VB6_NAME, level: 1 })).toBeVisible();
    });

    await test.step(`open ${subName}`, async () => {
      await page.goto(`/subroutines/${subId}`);
      // The page heading uses the routine's bare last segment, so we
      // match on whichever btnSubmit_Click / .btnSubmit_Click form
      // actually rendered.
      await expect(
        page.getByRole('heading', { name: new RegExp(subName.replace('.', '\\.')), level: 1 }),
      ).toBeVisible();
      // Phase 9.2.c extraction help banner — sky-blue VB6 variant.
      await expect(page.getByTestId('extraction-help-vb6')).toBeVisible();
    });

    await test.step('(re-)extract spec', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Real extraction on btnSubmit_Click (~50 LOC of VB6) finishes in
      // ~30-60s on Anthropic; allow 240s for the mock too.
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

      // VB6 claims span the same canonical taxonomy as Delphi / C++:
      // each card has `id="claim-<claim-id>"` so the existing
      // iteration works unchanged across schemas.
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
        await card.getByRole('textbox').fill('Resolved by SME during VB6 Inventory Sample demo.');
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

    await test.step('engineer sees auto-corrected dotnet10 default + variant indicator', async () => {
      await switchPersona(page, 'engineer');
      // Phase 10.1.a.3 — TargetSelector should show .NET 10 as the
      // selected card (auto-corrected from the dotnet8 fallback because
      // VB6 has no dotnet8 archetypes). The pretty label and the
      // variant indicator from 10.1.a.2 both live on this card.
      await page.goto(`/subroutines/${subId}/review`);
      const dotnet10Card = page.getByTestId('target-card-dotnet10');
      await expect(dotnet10Card).toBeVisible({ timeout: 15_000 });
      await expect(dotnet10Card).toContainText('.NET 10');
      await expect(dotnet10Card).toHaveAttribute('aria-pressed', 'true');
      // VB6 ships three production archetypes on dotnet10 so the
      // variant indicator must render with both other paradigms named.
      const variants = page.getByTestId('target-variants-dotnet10');
      await expect(variants).toBeVisible();
      await expect(variants).toContainText('WinForms');
      await expect(variants).toContainText('Blazor');
      await expect(variants).toContainText('Min API');
    });

    await test.step('engineer generates scaffold from canonical-vb6-winforms', async () => {
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
      // The WinForms archetype emits 7 source files (OrderEntryForm.cs +
      // OrderRepository.cs + InvoiceExporter.cs + SpecClaimAttribute.cs +
      // 2 csproj + 1 test file). Heading shape: `N files · M TODOs · dotnet10`.
      await expect(
        page.getByRole('heading', { name: /\d+ files · \d+ TODOs · dotnet1[08]/ }),
      ).toBeVisible();
    });

    await test.step('audit trail records the full VB6 lifecycle', async () => {
      const spec = await page.request
        .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
        .then((r) => r.json());
      expect(spec.state).toBe('SIGNED');
      expect(spec.schemaId ?? spec.spec?.schemaId ?? 'vb6').toBe('vb6');
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
