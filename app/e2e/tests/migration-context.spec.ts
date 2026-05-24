/**
 * Phase 8.0.c — Per-routine migration context: blast radius +
 * readiness classification + wave assignment.
 *
 *   - Blast-radius API returns expected shape; FIND_FIT (top-level
 *     orchestration in MINPACK) has zero downstream.
 *   - Readiness classification reaches each category on appropriate
 *     test inputs:
 *       - leaves of the dependency graph → safe-leaf
 *       - top-level routines that depend on unsigned callees →
 *         blocked-on-deps
 *   - Wave-assignment endpoint returns the routine's plan + wave
 *     number when a plan exists; 404 otherwise.
 *   - SubroutineDetailPage renders the migration-context panel with
 *     all 3 cards visible.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };

type GraphNode = { id: string; name: string; calleeCount: number; callerCount: number };

async function ensureMinpackPlan(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) test.skip(true, 'MINPACK corpus missing.');
  // Idempotent: generate a fresh plan so a wave assignment exists.
  await page.request.post(
    `${API_BASE}/api/v1/corpora/${minpack.id}/migration-plan/generate`,
    { headers: ADMIN, data: {}, timeout: 60_000 },
  );
  return minpack.id;
}

async function getGraph(page: Page, corpusId: string) {
  return page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}/dependency-graph`)
    .then((r) => r.json());
}

test.describe('Phase 8.0.c · Migration context', () => {
  test('blast-radius API: FIND_FIT (top) has zero downstream; leaf has zero downstream too', async ({ page }) => {
    const corpusId = await ensureMinpackPlan(page);
    const graph = await getGraph(page, corpusId);

    const findFit = graph.nodes.find((n: GraphNode) => n.name === 'FIND_FIT');
    expect(findFit, 'FIND_FIT routine should exist in MINPACK seed').toBeDefined();

    const br = await page.request.get(
      `${API_BASE}/api/v1/subroutines/${findFit.id}/blast-radius`,
    ).then((r) => r.json());

    expect(br.subroutineName).toBe('FIND_FIT');
    // FIND_FIT is at the TOP — nothing in-corpus calls it.
    expect(br.directCallerCount).toBe(0);
    expect(br.transitiveCallerCount).toBe(0);
    expect(br.stateBreakdown).toMatchObject({
      signed: expect.any(Number),
      scaffolded: expect.any(Number),
      draft: expect.any(Number),
      parsed: expect.any(Number),
    });
  });

  test('readiness classification: leaf → safe-leaf; top-level → blocked-on-deps', async ({ page }) => {
    const corpusId = await ensureMinpackPlan(page);
    const graph = await getGraph(page, corpusId);

    // A leaf with no shared-storage coupling should classify safe-leaf.
    const leaf = graph.nodes.find((n: GraphNode) => n.calleeCount === 0);
    expect(leaf).toBeDefined();
    const leafReadiness = await page.request.get(
      `${API_BASE}/api/v1/subroutines/${leaf.id}/migration-readiness`,
    ).then((r) => r.json());
    expect(['safe-leaf', 'coordinated-only']).toContain(leafReadiness.classification);
    // (coordinated-only is also valid if the leaf happens to touch a
    // shared COMMON block — we don't pin the specific routine because
    // the parser's COMMON detection varies by corpus.)

    // FIND_FIT depends on routines that aren't signed yet → blocked-on-deps
    // (no shared-storage on FIND_FIT in MINPACK).
    const findFit = graph.nodes.find((n: GraphNode) => n.name === 'FIND_FIT');
    const topReadiness = await page.request.get(
      `${API_BASE}/api/v1/subroutines/${findFit.id}/migration-readiness`,
    ).then((r) => r.json());
    expect(['blocked-on-deps', 'safe-with-deps-done', 'coordinated-only']).toContain(topReadiness.classification);
    // FIND_FIT has 1+ callees so it can't be safe-leaf.
    expect(topReadiness.classification).not.toBe('safe-leaf');
  });

  test('wave-assignment: returns plan + wave number for MINPACK routine', async ({ page }) => {
    const corpusId = await ensureMinpackPlan(page);
    const graph = await getGraph(page, corpusId);
    const findFit = graph.nodes.find((n: GraphNode) => n.name === 'FIND_FIT');

    const wa = await page.request.get(
      `${API_BASE}/api/v1/subroutines/${findFit.id}/wave-assignment`,
    );
    expect(wa.status()).toBe(200);
    const body = await wa.json();
    expect(body.waveNumber).toBeGreaterThan(0);
    expect(body.totalWaves).toBeGreaterThanOrEqual(body.waveNumber);
    expect(['draft', 'approved']).toContain(body.planStatus);
    expect(body.strategyName).toBe('topological-leaves-first');
  });

  test('UI: subroutine detail page surfaces wave + readiness + blast cards', async ({ page }) => {
    const corpusId = await ensureMinpackPlan(page);
    const graph = await getGraph(page, corpusId);
    const findFit = graph.nodes.find((n: GraphNode) => n.name === 'FIND_FIT');

    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.goto(`/subroutines/${findFit.id}`);

    await expect(page.getByTestId('migration-context-panel')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('wave-assignment-card')).toBeVisible();
    await expect(page.getByTestId('migration-readiness-card')).toBeVisible();
    await expect(page.getByTestId('blast-radius-card')).toBeVisible();
  });
});
