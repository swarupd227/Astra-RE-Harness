/**
 * Phase C.7 — threaded comments + @-mentions, end-to-end.
 *
 *   1. Reset stack.
 *   2. Drive a fast extract via API so we have a spec to comment on.
 *   3. Engineer opens the spec audit page, posts a spec-level comment
 *      that @-mentions SME (via the autocomplete suggestion).
 *   4. Switch persona to SME — verify Comments nav badge shows 1.
 *   5. SME opens /comments inbox, sees the notification with the right
 *      author + excerpt + spec link.
 *   6. SME clicks "Open spec audit", replies in the thread with
 *      @engineer.
 *   7. SME marks the original comment resolved.
 *   8. Switch back to engineer — SME's reply produced a new unread.
 *   9. Engineer's inbox shows the reply.
 *
 * Stays on the mock provider's behaviour for the extract step so the
 * test runs in ~25-40s end-to-end. The thread + mention semantics being
 * tested are provider-agnostic.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

async function switchPersona(page: Page, persona: 'engineer' | 'sme') {
  const label = persona === 'sme' ? 'SME' : 'Engineer';
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

test.describe('Phase C.7 · Comments + @-mentions', () => {
  test.beforeEach(async ({ page }) => {
    await page.request.post(`${API_BASE}/api/v1/dev/reset`, { headers: ENG, timeout: 30_000 });
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('engineer @sme → SME sees nav badge + inbox; replies @engineer; resolves; engineer sees reply', async ({ page }) => {
    // ── Set up: get a spec to comment on ─────────────────────────────
    // Find the synthetic seed by name — MINPACK is auto-seeded too and
    // orders ahead of the demo seed because it ingested most recently.
    const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
    const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
    if (!seed) throw new Error('Synthetic seed corpus not found after reset.');
    const corpusId: string = seed.id;
    const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG }).then((r) => r.json());
    const subId: string = detail.latestVersion.files[0].subroutines[0].id;

    // Drive extraction via the UI so the spec is persisted (and we get a
    // realistic mid-flow demo). The mock provider finishes in ~12s; real
    // Anthropic in ~50s. Either way the timeout below covers it.
    await page.goto(`/subroutines/${subId}`);
    await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
    await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({ timeout: 120_000 });

    const spec = await page.request.get(
      `${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG },
    ).then((r) => r.json());
    const specId: string = spec.id;

    // ── 1. Engineer posts spec-level comment with @sme via autocomplete
    await test.step('engineer posts a spec-level @sme comment via the autocomplete', async () => {
      await page.goto(`/specs/${specId}/audit`);
      await expect(page.getByRole('heading', { name: 'Discussion' })).toBeVisible();

      const composer = page.getByTestId('thread-composer');
      const textarea = composer.getByTestId('comment-body');
      await textarea.fill('Hi ');
      // Type @s — autocomplete pops, then click @sme
      await textarea.pressSequentially('@s');
      await expect(composer.getByTestId('mention-suggest')).toBeVisible();
      await composer.getByTestId('mention-sme').click();
      // Composer should now contain "@sme " — append the rest.
      await textarea.pressSequentially('please double-check INV-3 about the linear-feet floor.');
      await composer.getByTestId('comment-submit').click();

      // Comment shows up in the thread.
      await expect(page.getByText('please double-check INV-3').first()).toBeVisible({ timeout: 5_000 });
    });

    // ── 2. Switch to SME, verify nav badge + inbox ───────────────────
    await test.step('SME sees Comments nav badge + inbox row', async () => {
      await switchPersona(page, 'sme');

      // Persona switch reloads the page. After reload, the LeftNav polls
      // unread-count; we should see "1" within a few seconds.
      const badge = page.getByTestId('nav-unread-badge');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      await expect(badge).toHaveText('1');

      // Open the inbox.
      await page.getByTestId('nav-comments').click();
      await expect(page).toHaveURL(/\/comments$/);
      const list = page.getByTestId('inbox-list');
      await expect(list).toBeVisible();
      await expect(list).toContainText(/Dev User \(Engineer\)/);
      await expect(list).toContainText(/please double-check INV-3/);
    });

    // ── 3. SME clicks through, replies, then resolves ────────────────
    await test.step('SME replies @engineer and resolves the comment', async () => {
      // Click "Open spec audit" on the only inbox row.
      const row = page.getByTestId('inbox-list').locator('li').first();
      await row.getByTestId(/^view-/).click();
      await expect(page).toHaveURL(/\/specs\/[0-9a-f-]+\/audit$/);

      // Reply.
      const original = page.getByTestId(/^comment-/).first();
      await original.getByTestId('reply-btn').click();
      // The reply composer is the nested .pl-3 block; target its inputs directly.
      const replyTextarea = page.locator('textarea[placeholder="Reply…"]');
      await replyTextarea.fill('@engineer confirmed - INV-3 matches INVCMN. All good.');
      // The reply composer's submit button shares the accessible name "Reply"
      // with the toggle in the parent's header; submit lives in its own
      // composer container, so target via the comment-submit testid scoped
      // to the nested composer.
      const replyComposer = original.locator('[data-testid="comment-body"][placeholder="Reply…"]').locator('..').locator('..');
      await replyComposer.getByTestId('comment-submit').click();

      await expect(page.getByText(/confirmed.*INV-3/).first()).toBeVisible({ timeout: 5_000 });

      // Resolve the parent comment. Re-acquire the locator because the
      // tree just re-rendered with the new reply.
      const originalAfterReply = page.getByTestId(/^comment-/).first();
      await originalAfterReply.getByTestId('resolve').click();
      await expect(originalAfterReply).toContainText(/resolved/i);
    });

    // ── 4. Switch back to engineer, verify reply mention dispatched ──
    await test.step('engineer sees SME reply as a new unread', async () => {
      await switchPersona(page, 'engineer');
      const badge = page.getByTestId('nav-unread-badge');
      await expect(badge).toBeVisible({ timeout: 10_000 });
      // Engineer may have multiple unread (e.g. resolve-emit could not, but
      // SME's reply with @engineer definitely sent one). At least 1.
      const txt = (await badge.textContent()) ?? '';
      expect(Number(txt)).toBeGreaterThanOrEqual(1);

      await page.getByTestId('nav-comments').click();
      await expect(page).toHaveURL(/\/comments$/);
      await expect(page.getByTestId('inbox-list')).toContainText(/confirmed.*INV-3/);
    });

    // ── Belt-and-suspenders: verify the audit trail saw comment events
    await test.step('audit trail includes comment events', async () => {
      const audit = await page.request
        .get(`${API_BASE}/api/v1/specs/${specId}/audit`, { headers: ENG })
        .then((r) => r.json());
      const types = new Set<string>(audit.data.map((e: { eventType: string }) => e.eventType));
      expect(types).toEqual(expect.objectContaining(new Set(['comment.posted', 'comment.resolved'])));
    });
  });
});
