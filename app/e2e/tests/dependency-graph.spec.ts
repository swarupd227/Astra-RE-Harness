/**
 * Phase 8.0.a — Dependency graph: API shape + UI render.
 *
 *   - GET /api/v1/corpora/{id}/dependency-graph returns the expected
 *     shape for the seed corpus (CONSUME_ROLL with 3 external callees).
 *   - The page at /corpora/{id}/dependency-graph draws the graph
 *     (Cytoscape canvas) with the seed routine in it.
 *   - 404 on an unknown corpus id.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

async function getSeededCorpusId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const seed = corpora.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) throw new Error('CONSUME_ROLL seed corpus missing.');
  return seed.id;
}

test.describe('Phase 8.0.a · Dependency graph', () => {
  test('API returns the expected shape for the seed corpus', async ({ page }) => {
    const corpusId = await getSeededCorpusId(page);
    const res = await page.request.get(
      `${API_BASE}/api/v1/corpora/${corpusId}/dependency-graph`,
    );
    expect(res.ok()).toBe(true);
    const body = await res.json();

    expect(body.corpusId).toBe(corpusId);
    expect(typeof body.sourceVersionId).toBe('string');
    expect(Array.isArray(body.nodes)).toBe(true);
    expect(body.nodes.length).toBeGreaterThanOrEqual(1);

    // The CONSUME_ROLL routine must be there with the right shape.
    const consume = body.nodes.find((n: { name: string }) => n.name === 'CONSUME_ROLL');
    expect(consume).toBeDefined();
    expect(typeof consume.id).toBe('string');
    expect(typeof consume.state).toBe('string');
    expect(consume.sourcePath).toContain('CONSUME_ROLL');
    expect(typeof consume.calleeCount).toBe('number');
    expect(typeof consume.callerCount).toBe('number');

    // INV_READ, INV_WRITE, EMIT_EVENT are stubs that CONSUME_ROLL calls;
    // they're not separately ingested as subroutines, so they're
    // external callees in the seed.
    expect(body.externalCallees).toEqual(
      expect.arrayContaining(['INV_READ', 'INV_WRITE', 'EMIT_EVENT']),
    );

    expect(body.stats).toMatchObject({
      nodeCount: expect.any(Number),
      callEdgeCount: expect.any(Number),
      sharedStorageEdgeCount: expect.any(Number),
      sccCount: expect.any(Number),
      externalCalleeCount: expect.any(Number),
      leafCount: expect.any(Number),
      rootCount: expect.any(Number),
    });
    expect(body.stats.externalCalleeCount).toBeGreaterThanOrEqual(3);
  });

  test('404 on unknown corpus', async ({ page }) => {
    const res = await page.request.get(
      `${API_BASE}/api/v1/corpora/00000000-0000-0000-0000-000000000000/dependency-graph`,
    );
    expect(res.status()).toBe(404);
  });

  test('UI renders the graph page with the seed routine drawn', async ({ page }) => {
    const corpusId = await getSeededCorpusId(page);
    const graph = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}/dependency-graph`).then((r) => r.json());
    const consume = graph.nodes.find((n: { name: string }) => n.name === 'CONSUME_ROLL');

    await page.goto(`/corpora/${corpusId}/dependency-graph`);
    await expect(page.getByTestId('dependency-graph-page')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('heading', { name: 'Dependency graph' })).toBeVisible();
    await expect(page.getByText(`External callees (${graph.externalCallees.length})`)).toBeVisible();

    // The graph is painted on a canvas, so there is no element per routine to look for.
    // Cytoscape keeps its instance on the container; ask it what it is showing.
    const canvas = page.getByTestId('dep-graph-canvas');
    await expect(canvas.locator('canvas').first()).toBeVisible();
    await expect
      .poll(
        () =>
          canvas.evaluate((el, id) => {
            const cy = (el as unknown as { _cyreg?: { cy?: any } })._cyreg?.cy;
            if (!cy) return null;
            const node = cy.getElementById(id);
            if (node.empty()) return null;
            const box = node.renderedBoundingBox();
            const onScreen = box.x2 > 0 && box.y2 > 0 && box.x1 < el.clientWidth && box.y1 < el.clientHeight;
            return { label: node.data('label'), drawn: node.visible() && onScreen };
          }, consume.id),
        { timeout: 15_000 },
      )
      .toEqual({ label: 'CONSUME_ROLL', drawn: true });
  });
});
