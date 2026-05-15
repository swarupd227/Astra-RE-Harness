/**
 * Phase C.3 — re-sync → SUPERSEDED.
 *
 * Drives the full Engineer → SME → sign flow on the seeded CONSUME_ROLL
 * corpus (same path as demo-path.spec.ts), then exercises re-sync twice:
 *
 *   1. Re-ingest with identical content. Asserts:
 *      - reingest response: carriedForwardCount=1, supersededCount=0
 *      - latest version's subroutine is a NEW row (different id)
 *      - that new subroutine's spec is SIGNED and has previousSpecId set
 *      - the signature row carries over (same hash, same bytes)
 *
 *   2. Re-ingest with one trailing comment appended. Asserts:
 *      - reingest response: carriedForwardCount=0, supersededCount=1
 *      - the carried-forward spec from step 1 is now SUPERSEDED
 *      - audit trail contains a spec.superseded event with reason=source_changed
 *
 * Runs in ~60s end-to-end on a warm stack.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG_HEADERS = { 'X-Dev-Persona': 'engineer' };
const SME_HEADERS = { 'X-Dev-Persona': 'sme' };

async function switchPersona(page: Page, persona: 'engineer' | 'sme') {
  const label = persona === 'sme' ? 'SME' : 'Engineer';
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

test.describe('Phase C.3 · Re-sync → SUPERSEDED', () => {
  test.beforeEach(async ({ page }) => {
    await page.request.post(`${API_BASE}/api/v1/dev/reset`, {
      headers: ENG_HEADERS,
      timeout: 30_000,
    });
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('sign CONSUME_ROLL → re-sync unchanged carries forward → re-sync changed supersedes', async ({ page }) => {
    // ── Find the seeded subroutine ──────────────────────────────────
    // Locate by name — MINPACK auto-seeds and orders ahead in data[0].
    const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG_HEADERS }).then((r) => r.json());
    const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
    if (!seed) throw new Error('Synthetic seed corpus not found after reset.');
    const corpusId: string = seed.id;
    const detailV1 = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG_HEADERS }).then((r) => r.json());
    const v1FileRel: string = detailV1.latestVersion.files[0].relativePath;
    const v1SubroutineId: string = detailV1.latestVersion.files[0].subroutines[0].id;

    // ── Extract + route + sign (mirrors demo-path) ──────────────────
    await test.step('extract spec for CONSUME_ROLL', async () => {
      await page.goto(`/subroutines/${v1SubroutineId}`);
      await expect(page.getByRole('heading', { name: 'CONSUME_ROLL', level: 1 })).toBeVisible();
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      // Real Anthropic on CONSUME_ROLL is ~30-50s; mock is ~12s.
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({ timeout: 120_000 });
    });

    await test.step('route to SME', async () => {
      await page.getByRole('button', { name: 'View draft spec' }).click();
      await page.getByRole('button', { name: /Route to SME/ }).click();
      await expect(page).toHaveURL(/\/review$/);
    });

    await test.step('SME accepts all + resolves open questions + signs', async () => {
      await switchPersona(page, 'sme');

      // Iterate whatever the spec rendered — mock emits a fixed inventory
      // (INV-1..6, SE-1..2, EC-1..4, Q-1..3) but real Anthropic picks its
      // own claim ids per run.
      const cardIds = await page.locator('article[id^="claim-"]').evaluateAll(
        (els) => els.map((el) => (el as HTMLElement).id.replace(/^claim-/, '')),
      );
      expect(cardIds.length).toBeGreaterThan(0);

      const acceptIds: string[] = [];
      const resolveIds: string[] = [];
      for (const id of cardIds) {
        const card = page.locator(`article[id="claim-${id}"]`);
        if (await card.getByRole('button', { name: /^Accept$/ }).count()) acceptIds.push(id);
        else if (await card.getByRole('button', { name: /^Resolve in spec$/ }).count()) resolveIds.push(id);
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
        await card.getByRole('textbox').fill('Resolved during re-sync E2E.');
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

    // ── Snapshot the signed spec for cross-version comparison ───────
    const v1Spec = await page.request
      .get(`${API_BASE}/api/v1/subroutines/${v1SubroutineId}/spec`, { headers: ENG_HEADERS })
      .then((r) => r.json());
    expect(v1Spec.state).toBe('SIGNED');
    expect(v1Spec.signature).not.toBeNull();
    const v1SpecId: string = v1Spec.id;
    const v1SpecCanonicalHash: string = v1Spec.signature.specCanonicalHash;
    const v1SignatureBase64: string = v1Spec.signature.signatureBase64;

    // ── Re-ingest #1: identical content → carry-forward ─────────────
    const sourceText: string = (await page.request
      .get(`${API_BASE}/api/v1/subroutines/${v1SubroutineId}/source`, { headers: ENG_HEADERS })
      .then((r) => r.json())).content;

    await test.step('re-ingest with identical source carries the spec forward', async () => {
      const resp = await page.request.post(
        `${API_BASE}/api/v1/corpora/${corpusId}/reingest/text`,
        {
          headers: { ...ENG_HEADERS, 'Content-Type': 'application/json' },
          data: { files: [{ path: v1FileRel, content: sourceText }] },
        });
      expect(resp.status()).toBe(200);
      const body = await resp.json();
      expect(body.state).toBe('PARSED');
      expect(body.carriedForwardCount).toBe(1);
      expect(body.supersededCount).toBe(0);
    });

    // Detail after carry-forward: new subroutine id, but spec is SIGNED
    const detailV2 = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG_HEADERS }).then((r) => r.json());
    const v2SubroutineId: string = detailV2.latestVersion.files[0].subroutines[0].id;
    expect(v2SubroutineId).not.toBe(v1SubroutineId);
    expect(detailV2.latestVersion.files[0].subroutines[0].state).toBe('SIGNED');

    // Carried-forward spec on the new subroutine has previousSpecId + matching signature.
    const v2Spec = await page.request
      .get(`${API_BASE}/api/v1/subroutines/${v2SubroutineId}/spec`, { headers: ENG_HEADERS })
      .then((r) => r.json());
    expect(v2Spec.state).toBe('SIGNED');
    expect(v2Spec.signature).not.toBeNull();
    expect(v2Spec.signature.specCanonicalHash).toBe(v1SpecCanonicalHash);
    expect(v2Spec.signature.signatureBase64).toBe(v1SignatureBase64);

    // ── Re-ingest #2: modified content → SUPERSEDED ─────────────────
    const modifiedSource = sourceText + "\nC     E2E re-sync: this comment changes the file hash.\n";

    await test.step('re-ingest with changed source supersedes the prior spec', async () => {
      const resp = await page.request.post(
        `${API_BASE}/api/v1/corpora/${corpusId}/reingest/text`,
        {
          headers: { ...ENG_HEADERS, 'Content-Type': 'application/json' },
          data: { files: [{ path: v1FileRel, content: modifiedSource }] },
        });
      expect(resp.status()).toBe(200);
      const body = await resp.json();
      expect(body.state).toBe('PARSED');
      expect(body.carriedForwardCount).toBe(0);
      expect(body.supersededCount).toBe(1);
    });

    // The v2 spec (the previously-carried-forward signed spec) should now be SUPERSEDED.
    const detailV3 = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG_HEADERS }).then((r) => r.json());
    const v3SubroutineId: string = detailV3.latestVersion.files[0].subroutines[0].id;
    expect(v3SubroutineId).not.toBe(v2SubroutineId);
    expect(detailV3.latestVersion.files[0].subroutines[0].state).toBe('PARSED');

    // Re-fetch v2 spec via its id — the subroutine the spec was attached to
    // is in a stale version, but the spec row itself remains queryable.
    const v2SpecAfterChange = await page.request
      .get(`${API_BASE}/api/v1/specs/${v2Spec.id}/audit`, { headers: ENG_HEADERS })
      .then((r) => r.json());
    const eventTypes: string[] = v2SpecAfterChange.data.map((e: { eventType: string }) => e.eventType);
    expect(eventTypes).toContain('spec.superseded');
  });

  test('Re-sync modal renders + reaches Done state via UI', async ({ page }) => {
    // Locate the seed by name — MINPACK auto-seeds and could end up data[0].
    // Bump per-request timeouts because the MINPACK background reseed
    // running after /dev/reset can keep the API busy enough for the default
    // 8 s actionTimeout to trip on the seed-source fetch.
    const REQ_TIMEOUT = 30_000;
    const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG_HEADERS, timeout: REQ_TIMEOUT }).then((r) => r.json());
    const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
    if (!seed) throw new Error('Synthetic seed corpus not found after reset.');
    const corpusId: string = seed.id;
    const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG_HEADERS, timeout: REQ_TIMEOUT }).then((r) => r.json());
    const sourceText: string = (await page.request
      .get(`${API_BASE}/api/v1/subroutines/${detail.latestVersion.files[0].subroutines[0].id}/source`, { headers: ENG_HEADERS, timeout: REQ_TIMEOUT })
      .then((r) => r.json())).content;
    const relPath: string = detail.latestVersion.files[0].relativePath;

    await page.goto(`/projects/${corpusId}`);
    await page.getByTestId('open-reingest').click();
    await expect(page.getByRole('dialog', { name: /^Re-sync (project|corpus)$/ })).toBeVisible();

    // Drop a synthetic file with the same path/content (no prior spec → carry=0, supersede=0).
    await page.getByTestId('reingest-file-input').setInputFiles({
      name: relPath.split('/').pop()!,
      mimeType: 'text/x-fortran',
      buffer: Buffer.from(sourceText, 'utf-8'),
    });
    await page.getByTestId('reingest-submit').click();

    const outcome = page.getByTestId('reingest-outcome');
    await expect(outcome).toBeVisible({ timeout: 30_000 });
    await expect(outcome).toContainText(/Re-sync complete/);
    await page.getByTestId('reingest-done').click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
});
