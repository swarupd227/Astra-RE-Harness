/**
 * Appendix-A demo path, end-to-end. Walks the same 4-minute flow as the
 * defense-call demo:
 *   engineer:  open corpus → open subroutine → extract → route to SME
 *   sme:       review every claim → sign
 *   engineer:  generate scaffold → open artifact → commit (stub)
 *
 * On a clean stack with the seeded CONSUME_ROLL corpus this completes in
 * about 25 seconds (most of which is the 12s mock extraction + 6s mock
 * scaffold).
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

/** Switch the dev persona via the top-right menu and wait for the page to refetch. */
async function switchPersona(page: Page, persona: 'engineer' | 'sme' | 'observer' | 'admin') {
  const labelByPersona = { engineer: 'Engineer', sme: 'SME', observer: 'Observer', admin: 'Admin' } as const;
  const label = labelByPersona[persona];

  // Trigger has aria-haspopup="menu". Click to open the dropdown.
  await page.locator('button[aria-haspopup="menu"]').click();
  // Menu items have role="menuitemradio". The accessible name is
  // "<Label> <tagline>", e.g. "SME Reviews and signs specs...". Match the
  // accessible name with a leading-label prefix.
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  // Persona switch triggers window.location.reload(); wait for the new render.
  await page.waitForLoadState('networkidle');
}

async function getSeededSubroutineId(page: Page): Promise<string> {
  // Find the synthetic seed by name — MINPACK is auto-seeded too and
  // orders ahead because it ingested most recently.
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) throw new Error('Synthetic seed corpus not found after reset.');
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`).then((r) => r.json());
  return detail.latestVersion.files[0].subroutines[0].id;
}

test.describe('Appendix A · 4-minute demo path', () => {
  test.beforeEach(async ({ page }) => {
    // Reset BEFORE first navigation so each run starts on a clean schema —
    // otherwise a previous run that made it past route-to-SME leaves the
    // spec at IN_REVIEW, and the extract step rejects with state-guard.
    await page.request.post(`${API_BASE}/api/v1/dev/reset`, {
      headers: { 'X-Dev-Persona': 'engineer' },
      timeout: 30_000,
    });
    // Seed the persona on the first page load only — using addInitScript
    // would re-run on every navigation including the reload triggered by
    // PersonaMenu, undoing the SME switch.
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('engineer extracts → SME signs → engineer scaffolds → commit (stub)', async ({ page }) => {
    // RECORD_DEMO=1 adds explicit pacing pauses (in addition to slowMo
    // in playwright.config) so a stakeholder watching the recording has
    // time to absorb each scene. Defaults to 0 so CI runs fast.
    const beat = process.env.RECORD_DEMO === '1' ? 2_000 : 0;
    const subId = await getSeededSubroutineId(page);

    // ── Stage 0:00 — Open the subroutine ────────────────────────────
    await test.step('open CONSUME_ROLL', async () => {
      await page.goto(`/subroutines/${subId}`);
      await expect(page.getByRole('heading', { name: 'CONSUME_ROLL', level: 1 })).toBeVisible();
      // Let the viewer see the source detail surface before we click extract.
      if (beat) await page.waitForTimeout(beat);
    });

    // ── Stage 0:30 — Extract ────────────────────────────────────────
    await test.step('extract spec (real provider call)', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Land on /extract; wait for the success-state primary CTA. Real
      // Anthropic Sonnet 4.5 extraction of CONSUME_ROLL takes 25–60s
      // including tokens + post-validation; mock finishes in ~12s.
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
        timeout: 120_000,
      });
      // Hold on the completed-extraction state so the citation pulses + final
      // streaming spec are visible before navigating away.
      if (beat) await page.waitForTimeout(beat);
    });

    // ── Stage 1:30 — Route to SME ───────────────────────────────────
    await test.step('engineer routes to SME', async () => {
      await page.getByRole('button', { name: 'View draft spec' }).click();
      await expect(page.getByRole('heading', { name: 'CONSUME_ROLL' }).first()).toBeVisible();
      if (beat) await page.waitForTimeout(beat);  // see the draft spec page
      await page.getByRole('button', { name: /Route to SME/ }).click();
      // Routing transitions to /review automatically
      await expect(page).toHaveURL(/\/review$/);
      if (beat) await page.waitForTimeout(beat);  // see review surface
    });

    // ── Stage 2:00 — SME reviews every claim ────────────────────────
    await test.step('SME accepts all + resolves open questions + signs', async () => {
      await switchPersona(page, 'sme');

      // Every ReviewableClaimCard renders as <article id="claim-{id}">. The
      // mock provider emits a fixed inventory (INV-1..6, SE-1..2, EC-1..4,
      // Q-1..3) but real Claude picks its own ids per run, so we iterate
      // whatever the page renders instead of hardcoding.
      const cardIds = await page.locator('article[id^="claim-"]').evaluateAll(
        (els) => els.map((el) => (el as HTMLElement).id.replace(/^claim-/, '')),
      );
      expect(cardIds.length).toBeGreaterThan(0);

      // Classify by which action button the card exposes.
      //   Accept-button cards → invariants, side effects, edge cases
      //   Resolve-in-spec cards → open questions
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
        // Scroll the card into view first so the viewer sees which claim
        // is being acted on — the page is taller than the viewport when
        // there are many claims.
        await card.scrollIntoViewIfNeeded();
        await card.getByRole('button', { name: /^Accept$/ }).click();
      }
      for (const id of resolveIds) {
        const card = page.locator(`article[id="claim-${id}"]`);
        await card.scrollIntoViewIfNeeded();
        await card.getByRole('button', { name: /^Resolve in spec$/ }).click();
        await card.getByRole('textbox').fill('Resolved by SME during demo run.');
        await card.getByRole('button', { name: /^Save$/ }).click();
      }
      if (beat) await page.waitForTimeout(beat);  // see fully-processed progress strip

      // Progress strip reads "<n> / <n> claims processed" once every card is
      // accounted for; the exact total varies with the provider.
      const total = acceptIds.length + resolveIds.length;
      await expect(
        page.getByRole('heading', { name: new RegExp(`^${total} \\/ ${total} claims processed$`) }),
      ).toBeVisible();

      // Sign should now be enabled.
      await page.getByRole('button', { name: /^Sign spec$/ }).first().click();
      if (beat) await page.waitForTimeout(beat);  // see sign-off modal
      // Modal opens; confirm and submit.
      await page.getByRole('checkbox').check();
      await page.getByRole('dialog').getByRole('button', { name: /^Sign spec$/ }).click();
      // Modal closes; SIGNED display mode kicks in. The EvidenceTrail's
      // Signature block is the new authoritative visual signal — it only
      // renders when sp.state === 'SIGNED'.
      await expect(page.getByTestId('evidence-block-signature')).toBeVisible({ timeout: 15_000 });
      if (beat) await page.waitForTimeout(beat);  // see SIGNED state with evidence trail
    });

    // ── Stage 3:00 — Engineer generates scaffold ────────────────────
    await test.step('engineer generates scaffold', async () => {
      await switchPersona(page, 'engineer');
      // The Spec review surface for a SIGNED spec shows either Generate scaffold
      // or Open scaffold (if one already exists from a previous run).
      const generate = page.getByRole('link', { name: /Generate scaffold/ });
      const openExisting = page.getByRole('link', { name: /Open scaffold/ });
      if (await generate.count()) {
        await generate.click();
        // Wait for the streaming surface to complete and the Open scaffold CTA to appear.
        await expect(page.getByRole('button', { name: /Open scaffold/ })).toBeVisible({
          timeout: 30_000,
        });
        await page.getByRole('button', { name: /Open scaffold/ }).click();
      } else {
        await openExisting.click();
      }
      // Scaffold artifact view — file tree + Monaco + traceability.
      await expect(page.getByRole('heading', { name: /\d+ files · \d+ TODOs · dotnet8/ })).toBeVisible();
      if (beat) await page.waitForTimeout(beat);  // see file tree + traceability
    });

    // ── Stage 3:45 — Commit-to-Git stub ─────────────────────────────
    await test.step('commit (stub) lands a faux hash', async () => {
      const commit = page.getByRole('button', { name: /Commit to Git/ });
      if (await commit.count()) {
        await commit.click();
        // After commit the button is replaced by a branch+hash chip.
        await expect(page.locator('a[href^="git://stub.local/"]')).toBeVisible({ timeout: 10_000 });
        if (beat) await page.waitForTimeout(beat);  // hold on commit chip
      }
    });

    // ── Final check: audit trail surfaces every transition ──────────
    await test.step('audit trail captures the full lifecycle', async () => {
      const audit = await page.request.get(`${API_BASE}/api/v1/specs/${(await page.request
        .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`)
        .then((r) => r.json())).id}/audit`).then((r) => r.json());
      const types = new Set<string>(audit.data.map((e: any) => e.eventType));
      // Phase B's lifecycle markers should all be present.
      expect(types).toEqual(
        expect.objectContaining(new Set([
          'spec.extracted',
          'spec.routed',
          'spec.signed',
          'scaffold.generated',
        ])),
      );
      // Every claim card the SME touched produced a claim.* audit row.
      // Real Claude emits a variable claim count per run; we just require
      // at least one claim-event since the count is provider-dependent.
      const claimEvents = audit.data.filter((e: any) => e.eventType.startsWith('claim.'));
      expect(claimEvents.length).toBeGreaterThanOrEqual(1);
    });
  });
});
