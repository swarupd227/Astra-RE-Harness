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
 *
 * It makes its own two-routine project, so it does not depend on what state
 * other specs have left the seed routine in.
 */
import { expect, test } from '@playwright/test';
import { API_BASE, JSON_ENG, newProject } from './helpers/projects';

/** ALPHA calls BETA, which is in the same project, and shares a COMMON block with it. */
const CALLER_AND_CALLEE = `\\
      SUBROUTINE ALPHA(X)
      INTEGER X, TOTAL
      COMMON /SHARED/ TOTAL
      CALL BETA(X)
      RETURN
      END
      SUBROUTINE BETA(X)
      INTEGER X, TOTAL
      COMMON /SHARED/ TOTAL
      TOTAL = TOTAL + X
      RETURN
      END
`;

/** Server-sent events as `event:` / `data:` pairs. */
function parseEvents(stream: string): Array<{ event: string; data: any }> {
  return stream
    .split(/\r?\n\r?\n/)
    .map((block) => {
      const event = /^event: (.*)$/m.exec(block)?.[1];
      const data = /^data: (.*)$/m.exec(block)?.[1];
      return event && data ? { event, data: JSON.parse(data) } : null;
    })
    .filter((e) => e !== null);
}

test.describe('Phase 7.0 · Neighbourhood context', () => {
  test('extract SSE stream emits neighbourhood_attached when callees exist', async ({ page }) => {
    const corpusId = await newProject(page, [], { prefix: 'neighbourhood-e2e', source: CALLER_AND_CALLEE });
    const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: JSON_ENG }).then((r) => r.json());
    const alpha = detail.latestVersion.files[0].subroutines.find((s: { name: string }) => s.name === 'ALPHA');

    const res = await page.request.post(`${API_BASE}/api/v1/subroutines/${alpha.id}/extract`, {
      headers: JSON_ENG,
      timeout: 60_000,
    });
    expect(res.status()).toBe(200);
    expect(res.headers()['content-type']).toContain('text/event-stream');

    const events = parseEvents((await res.body()).toString('utf-8'));
    const names = events.map((e) => e.event);
    expect(names).toContain('neighbourhood_attached');
    // Attached before the provider starts writing the spec.
    expect(names.indexOf('neighbourhood_attached')).toBeLessThan(names.indexOf('patch'));
    expect(names).not.toContain('error');

    const attached = events.find((e) => e.event === 'neighbourhood_attached')!.data;
    expect(attached.callees).toBe(1);
    expect(attached.callers).toBe(0);
    expect(attached.commonBlocks).toBeGreaterThanOrEqual(1);
    expect(typeof attached.siblingsInFile).toBe('number');
  });
});
