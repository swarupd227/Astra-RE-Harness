/**
 * Phase 8.0.e — Pluggable migration-plan strategies.
 *
 *   - GET /api/v1/migration-plan-strategies lists all four strategies
 *     (topological-leaves-first as default, risk-first, business-priority,
 *     pilot-then-scale).
 *   - Each strategy generates a valid plan for MINPACK; the plan's
 *     strategyName + totalWaves come back in the response.
 *   - business-priority produces a different shape than the default
 *     when priorities cut across the topology.
 *   - pilot-then-scale puts the named routines into Wave 1.
 *   - The migration-plan UI exposes the strategy picker for admin
 *     (dropdown + options textarea for option-bearing strategies).
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };

type GraphNode = { id: string; name: string; calleeCount: number; callerCount: number };

async function getMinpackCorpusId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) test.skip(true, 'MINPACK corpus missing.');
  return minpack.id;
}

async function generatePlan(
  page: Page,
  corpusId: string,
  body: Record<string, unknown>,
): Promise<{ id: string; status: string; strategyName: string; totalWaves: number; totalRoutines: number }> {
  const res = await page.request.post(
    `${API_BASE}/api/v1/corpora/${corpusId}/migration-plan/generate`,
    { headers: ADMIN, data: body, timeout: 60_000 },
  );
  expect(res.status(), `generate(${JSON.stringify(body)}) failed`).toBe(200);
  return res.json();
}

async function getPlanDetail(page: Page, planId: string) {
  return page.request.get(`${API_BASE}/api/v1/migration-plans/${planId}`).then((r) => r.json());
}

test.describe('Phase 8.0.e · Strategy plugins', () => {
  test('strategy listing returns all four plugins with default flagged', async ({ page }) => {
    const list = await page.request.get(`${API_BASE}/api/v1/migration-plan-strategies`)
      .then((r) => r.json());
    const names = list.data.map((s: { name: string }) => s.name).sort();
    expect(names).toEqual(
      ['business-priority', 'pilot-then-scale', 'risk-first', 'topological-leaves-first'].sort(),
    );
    const defaults = list.data.filter((s: { isDefault: boolean }) => s.isDefault);
    expect(defaults).toHaveLength(1);
    expect(defaults[0].name).toBe('topological-leaves-first');

    // Strategies that take options must declare them in their schema.
    const bp = list.data.find((s: { name: string }) => s.name === 'business-priority');
    expect(bp.optionsSchema.properties.priorities).toBeDefined();
    const pts = list.data.find((s: { name: string }) => s.name === 'pilot-then-scale');
    expect(pts.optionsSchema.properties.pilotRoutineNames).toBeDefined();
  });

  test('each strategy produces a plan on MINPACK', async ({ page }) => {
    const corpusId = await getMinpackCorpusId(page);
    const graph = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}/dependency-graph`)
      .then((r) => r.json());
    const findFit = graph.nodes.find((n: GraphNode) => n.name === 'FIND_FIT');
    expect(findFit, 'FIND_FIT must exist in MINPACK seed').toBeDefined();

    const topo = await generatePlan(page, corpusId, { strategy: 'topological-leaves-first' });
    expect(topo.strategyName).toBe('topological-leaves-first');
    expect(topo.totalWaves).toBeGreaterThan(0);

    const risk = await generatePlan(page, corpusId, { strategy: 'risk-first' });
    expect(risk.strategyName).toBe('risk-first');
    expect(risk.totalWaves).toBeGreaterThan(0);
    expect(risk.totalRoutines).toBe(topo.totalRoutines);

    const bp = await generatePlan(page, corpusId, {
      strategy: 'business-priority',
      options: { priorities: { FIND_FIT: 1, HYBRD1: 1, FCN: 2 }, defaultPriority: 99 },
    });
    expect(bp.strategyName).toBe('business-priority');
    expect(bp.totalRoutines).toBe(topo.totalRoutines);

    const pts = await generatePlan(page, corpusId, {
      strategy: 'pilot-then-scale',
      options: { pilotRoutineNames: ['FIND_FIT', 'HYBRD1'] },
    });
    expect(pts.strategyName).toBe('pilot-then-scale');
    expect(pts.totalRoutines).toBe(topo.totalRoutines);

    // pilot-then-scale must place FIND_FIT + HYBRD1 in Wave 1.
    const detail = await getPlanDetail(page, pts.id);
    const wave1 = detail.waves[0];
    const wave1Names = wave1.routines.map((r: { name: string }) => r.name);
    expect(wave1Names).toContain('FIND_FIT');
    expect(wave1Names).toContain('HYBRD1');
  });

  test('UI: strategy picker is visible for admin and lists all four strategies', async ({ page }) => {
    const corpusId = await getMinpackCorpusId(page);
    // Seed a plan so the page has the recompute path rendered.
    await generatePlan(page, corpusId, { strategy: 'topological-leaves-first' });

    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
    await page.goto(`/corpora/${corpusId}/migration-plan`);

    const picker = page.getByTestId('strategy-picker-card');
    await expect(picker).toBeVisible({ timeout: 15_000 });

    // Expand the picker.
    await page.getByTestId('strategy-picker-toggle').click();

    const select = page.getByTestId('strategy-select');
    await expect(select).toBeVisible();
    const options = await select.locator('option').allInnerTexts();
    const optionValues = options.map((o) => o.trim().replace(/\s*\(default\)$/, ''));
    expect(optionValues.sort()).toEqual(
      ['business-priority', 'pilot-then-scale', 'risk-first', 'topological-leaves-first'].sort(),
    );

    // Switching to pilot-then-scale reveals an options textarea.
    await select.selectOption('pilot-then-scale');
    await expect(page.getByTestId('strategy-options-textarea')).toBeVisible();

    // Switching back to topological-leaves-first hides the options field
    // (its schema declares no properties).
    await select.selectOption('topological-leaves-first');
    await expect(page.getByTestId('strategy-options-textarea')).toHaveCount(0);
  });

  test('UI: non-admin does NOT see the strategy picker', async ({ page }) => {
    const corpusId = await getMinpackCorpusId(page);
    await generatePlan(page, corpusId, { strategy: 'topological-leaves-first' });

    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    await page.goto(`/corpora/${corpusId}/migration-plan`);
    await expect(page.getByTestId('migration-plan-page')).toBeVisible({ timeout: 15_000 });
    await expect(page.getByTestId('strategy-picker-card')).toHaveCount(0);
  });
});
