/**
 * Phase C.1 + C.2 — real ingest path.
 *
 * Uses the upload UI to ingest a small Fortran source file with two
 * subroutines, then verifies:
 *   - corpus appears in the list with PARSED state
 *   - corpus detail page lists both subroutines with the names + line ranges
 *     the parser extracted
 *   - subroutine detail page renders source + real COMMON + CALL extracted
 *     by fparser2 (not a stub)
 *
 * Independent of the demo-path seed corpus. Uses dev/reset to clear state
 * before running so the unique-name constraint can't trip a flake.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

const FORTRAN_SOURCE = [
  '      SUBROUTINE GREET(NAME, RESULT_CD)',
  'C     Sample F77 for the ingest E2E.',
  '      IMPLICIT NONE',
  '      CHARACTER*32 NAME',
  '      INTEGER RESULT_CD',
  '      COMMON /GREETCM/ COUNT',
  '      INTEGER COUNT',
  "      CALL LOG_EVENT('greet', NAME)",
  '      COUNT = COUNT + 1',
  '      RESULT_CD = 0',
  '      RETURN',
  '      END',
  '',
  '      SUBROUTINE FAREWELL(NAME)',
  '      IMPLICIT NONE',
  '      CHARACTER*32 NAME',
  "      CALL LOG_EVENT('bye', NAME)",
  '      RETURN',
  '      END',
  '',
].join('\n');

test.describe('Phase C.1 · Ingest', () => {
  test.beforeEach(async ({ page }) => {
    // Reset BEFORE first navigation so the page renders against a fresh
    // schema (avoids the frontend racing the DB recreate on first request).
    await page.request.post(`${API_BASE}/api/v1/dev/reset`, {
      headers: { 'X-Dev-Persona': 'engineer' },
      timeout: 30_000,
    });
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('upload a Fortran file → fparser2 extracts subroutines → demo continues', async ({ page }) => {
    const corpusName = `e2e-ingest-${Date.now()}`;

    // ── Open the new-project form ────────────────────────────────────
    await page.goto('/projects');
    await page.getByTestId('add-corpus').click();
    await expect(page).toHaveURL(/\/(projects|corpora)\/new$/);

    // ── Fill the form ────────────────────────────────────────────────
    await page.getByTestId('corpus-name').fill(corpusName);

    // setInputFiles works for the hidden <input type="file"> bound to the dropzone.
    await page.getByTestId('file-input').setInputFiles({
      name: 'HELLO.FOR',
      mimeType: 'text/x-fortran',
      buffer: Buffer.from(FORTRAN_SOURCE, 'utf-8'),
    });

    // ── Submit. The form auto-navigates to /corpora/{id} ~700ms after the
    //    PARSED state arrives, so we wait for the post-navigation surface
    //    rather than racing the transient result card.
    await page.getByTestId('ingest-submit').click();

    // Auto-navigation lands us on the project detail page.
    await expect(page).toHaveURL(/\/(projects|corpora)\/[0-9a-f-]+$/, { timeout: 30_000 });
    await expect(page.getByRole('heading', { name: corpusName, level: 1 })).toBeVisible();

    // Both subroutines should be listed by name with the line ranges fparser2
    // extracted. The accessible name on the surrounding link is
    // "L<start>–<end> <NAME> <STATE>" with a Unicode en-dash; matching the
    // link by name regex sidesteps brittle text= regex escaping.
    await expect(page.getByRole('link', { name: /L1\D+\d+\s+GREET\s+PARSED/ }).first()).toBeVisible();
    await expect(page.getByRole('link', { name: /L14\D+\d+\s+FAREWELL\s+PARSED/ }).first()).toBeVisible();

    // ── Cross-check via the API — parser data, not a stub ────────────
    const corporaResp = await page.request
      .get(`${API_BASE}/api/v1/corpora`, { headers: { 'X-Dev-Persona': 'engineer' } })
      .then((r) => r.json());
    const ours = corporaResp.data.find((c: { name: string }) => c.name === corpusName);
    expect(ours).toBeDefined();

    const detail = await page.request
      .get(`${API_BASE}/api/v1/corpora/${ours.id}`, { headers: { 'X-Dev-Persona': 'engineer' } })
      .then((r) => r.json());
    const subs = detail.latestVersion.files[0].subroutines;
    expect(subs.length).toBe(2);
    const greetId = subs.find((s: { name: string }) => s.name === 'GREET').id;

    const greet = await page.request
      .get(`${API_BASE}/api/v1/subroutines/${greetId}`, { headers: { 'X-Dev-Persona': 'engineer' } })
      .then((r) => r.json());
    expect(greet.signature).toContain('SUBROUTINE GREET');
    expect(greet.commonBlockRefs).toContain('GREETCM');
    expect(greet.calledSubroutines).toContain('LOG_EVENT');

    // ── Open one of the freshly-parsed subroutines and verify the source
    //    surface renders so the rest of the pipeline can pick up from here.
    await page.locator(`a[href$="/subroutines/${greetId}"]`).click();
    await expect(page.getByRole('heading', { name: 'GREET', level: 1 })).toBeVisible();
  });
});
