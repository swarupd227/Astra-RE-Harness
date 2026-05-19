/**
 * Phase 7.1 — Cross-routine harmonisation pipeline.
 *
 * Exercises the public surface:
 *   - Engineer is forbidden from running a pass or mutating findings.
 *   - Admin POST /api/v1/corpora/{id}/harmonise runs the pass and
 *     returns a RunSummary. With the mock LLM provider the run lands
 *     immediately with a stub finding (we do NOT need an API key for
 *     CI). With the anthropic provider the same path produces real
 *     findings — same shape.
 *   - GET /api/v1/corpora/{id}/harmonisation lists the runs.
 *   - GET /api/v1/harmonisation/runs/{id} returns the findings.
 *   - PUT /api/v1/harmonisation/findings/{id} moves a finding to
 *     accepted / dismissed / open.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

async function getSeededCorpusId(page) {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  // Prefer the synthetic seed (always has at least one signed spec).
  const seed = corpora.data.find((c: { name: string }) =>
    c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) test.skip(true, 'Synthetic seed corpus missing.');
  return seed.id as string;
}

test.describe('Phase 7.1 · Harmonisation', () => {
  test('Engineer cannot run the pass or mutate findings (403)', async ({ page }) => {
    const corpusId = await getSeededCorpusId(page);
    const r = await page.request.post(
      `${API_BASE}/api/v1/corpora/${corpusId}/harmonise`,
      { headers: ENG },
    );
    expect(r.status()).toBe(403);
  });

  test('Admin can run the pass; the run shows up in the list', async ({ page }) => {
    const corpusId = await getSeededCorpusId(page);
    const runRes = await page.request.post(
      `${API_BASE}/api/v1/corpora/${corpusId}/harmonise`,
      { headers: ADMIN, timeout: 120_000 },
    );
    expect(runRes.status()).toBe(200);
    const run = await runRes.json();
    expect(run.status).toMatch(/COMPLETED|FAILED|RUNNING/);
    expect(typeof run.specCount).toBe('number');
    expect(typeof run.findingCount).toBe('number');
    expect(run.promptId).toBe('cross-routine-harmonise');

    const list = await page.request.get(
      `${API_BASE}/api/v1/corpora/${corpusId}/harmonisation`,
      { headers: ADMIN },
    ).then((r) => r.json());
    expect(list.data.length).toBeGreaterThanOrEqual(1);
    expect(list.data.some((r: { id: string }) => r.id === run.id)).toBe(true);
  });

  test('Run detail returns findings; admin can update finding status', async ({ page }) => {
    const corpusId = await getSeededCorpusId(page);
    const runRes = await page.request.post(
      `${API_BASE}/api/v1/corpora/${corpusId}/harmonise`,
      { headers: ADMIN, timeout: 120_000 },
    );
    const run = await runRes.json();

    const detail = await page.request.get(
      `${API_BASE}/api/v1/harmonisation/runs/${run.id}`,
      { headers: ADMIN },
    ).then((r) => r.json());
    expect(detail.run.id).toBe(run.id);
    expect(Array.isArray(detail.findings)).toBe(true);

    // If there is at least one finding (mock provider always emits
    // one when SpecCount >= 2; anthropic may or may not), exercise
    // the status-mutation path.
    if (detail.findings.length > 0) {
      const f = detail.findings[0];
      const upd = await page.request.put(
        `${API_BASE}/api/v1/harmonisation/findings/${f.id}`,
        { headers: ADMIN, data: { status: 'accepted', adminNote: 'verified by e2e' } },
      );
      expect(upd.status()).toBe(200);
      const updated = await upd.json();
      expect(updated.status).toBe('accepted');
      expect(updated.adminNote).toBe('verified by e2e');

      // Engineer cannot mutate.
      const forbid = await page.request.put(
        `${API_BASE}/api/v1/harmonisation/findings/${f.id}`,
        { headers: ENG, data: { status: 'dismissed' } },
      );
      expect(forbid.status()).toBe(403);
    }
  });
});
