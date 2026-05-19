/**
 * Phase 7.0 — Neighbourhood context attached to every extraction.
 *
 * Verifies the pipeline:
 *   - Builds a Neighbourhood from parser-extracted call-graph metadata
 *     before invoking the LLM provider.
 *   - Emits a `neighbourhood_attached` SSE event with counts visible to
 *     the UI so reviewers know what cross-routine context the extract
 *     had access to.
 *   - The mock provider still works end-to-end (the neighbourhood is an
 *     additive input; old prompts that don't reference it must keep
 *     working).
 *
 * This is the regression test for the Andrew Ng follow-up: per-routine
 * extraction without cross-file context misses real dependencies. The
 * companion golden-dataset entry
 * `fortran-callee-side-effect-inheritance` measures the LLM-quality
 * improvement; this test pins the wiring itself.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

async function getSeededRollRoutineId(page: Page): Promise<string> {
  // The CONSUME_ROLL synthetic seed has CallSubroutines = [INV_READ,
  // INV_WRITE, EMIT_EVENT] — a perfect neighbourhood test bed.
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) test.skip(true, 'CONSUME_ROLL seed corpus not present.');
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`).then((r) => r.json());
  return detail.latestVersion.files[0].subroutines[0].id;
}

test.describe('Phase 7.0 · Neighbourhood context', () => {
  test('extract SSE stream emits neighbourhood_attached when callees exist', async ({ page }) => {
    const subId = await getSeededRollRoutineId(page);

    // Hit the SSE endpoint and read the raw event stream. We don't need
    // to wait for the full extract — just the early `neighbourhood_attached`
    // event (fires before the provider call).
    const res = await page.request.post(
      `${API_BASE}/api/v1/subroutines/${subId}/extract`,
      { headers: ENG, timeout: 60_000 },
    );
    expect(res.status()).toBe(200);
    expect(res.headers()['content-type']).toContain('text/event-stream');

    // Read up to the first ~24 lines — enough for the prelude.
    const reader = (await res.body()).toString('utf-8').split('\n');
    const neighbourhoodEvent = reader
      .filter((l) => l.startsWith('data:'))
      .map((l) => l.slice(5).trim())
      .map((s) => {
        try {
          return JSON.parse(s);
        } catch {
          return null;
        }
      })
      .find((d) => d && d.type === 'neighbourhood_attached');

    expect(neighbourhoodEvent, 'neighbourhood_attached event missing from extract stream').toBeTruthy();
    // CONSUME_ROLL calls INV_READ, INV_WRITE, EMIT_EVENT — but those
    // routines aren't separately ingested in the seed (the source is a
    // self-contained single-file synthetic). So `callees` may be ≥ 0
    // depending on whether the seed registered the callees as
    // resolvable rows. The wiring is what we pin; the count specifics
    // are checked by the golden-dataset scorer.
    expect(typeof neighbourhoodEvent.data.callees).toBe('number');
    expect(typeof neighbourhoodEvent.data.callers).toBe('number');
    expect(typeof neighbourhoodEvent.data.commonBlocks).toBe('number');
    expect(typeof neighbourhoodEvent.data.siblingsInFile).toBe('number');
  });
});
