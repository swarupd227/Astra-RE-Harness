/**
 * Routine flow board — read model API + UI.
 *
 *   - GET /api/v1/corpora/{id}/flow-board buckets every routine into one
 *     of 8 columns and the totals sum to totalRoutines.
 *   - A column beyond the per-column cap reports hasMore and total >
 *     routines.length.
 *   - Unknown corpus → 404.
 *   - The /corpora/{id}/board page renders all 8 columns, a card's file:line,
 *     and the "+N more" link carries the right corpus + state filter into
 *     the Routines page.
 *   - Clicking a card opens that routine's detail page.
 *   - The project detail page links to the board.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const COLUMN_KEYS = ['parsed', 'extracting', 'draft', 'in_review', 'signed', 'built', 'verified', 'committed'];

async function minpackId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) test.skip(true, 'MINPACK corpus missing.');
  return minpack.id;
}

test.describe('Flow board · API', () => {
  test('buckets every routine into exactly one of 8 columns, totals reconcile', async ({ page }) => {
    const id = await minpackId(page);
    const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/flow-board`);
    expect(res.status()).toBe(200);
    const body = await res.json();

    expect(body.columns.map((c: { key: string }) => c.key)).toEqual(COLUMN_KEYS);
    const sum = body.columns.reduce((n: number, c: { total: number }) => n + c.total, 0);
    expect(sum).toBe(body.totalRoutines);
    expect(body.totalRoutines).toBeGreaterThan(0);

    for (const col of body.columns) {
      expect(col.routines.length).toBeLessThanOrEqual(col.total);
      expect(col.hasMore).toBe(col.total > col.routines.length);
      for (const r of col.routines) {
        expect(typeof r.id).toBe('string');
        expect(typeof r.name).toBe('string');
        expect(typeof r.filePath).toBe('string');
      }
    }
  });

  test('stages with generated code report every gate for every routine, and each gate tally adds up', async ({ page }) => {
    const id = await minpackId(page);
    const body = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/flow-board`).then((r) => r.json());
    const GATES = ['COMPILE', 'TEST_PACK', 'EQUIVALENCE', 'FALSIFYING'];
    const RESULTS = ['PASSED', 'FAILED', 'ERRORED', 'RUNNING', 'NOT_RUN'];

    for (const col of body.columns) {
      const withCode = ['built', 'verified', 'committed'].includes(col.key);
      if (!withCode) {
        expect(col.gateTally ?? null).toBeNull();
        for (const r of col.routines) expect(r.gates ?? null).toBeNull();
        continue;
      }
      for (const r of col.routines) {
        expect(Object.keys(r.gates)).toEqual(GATES);
        for (const g of GATES) expect(RESULTS).toContain(r.gates[g]);
      }
      if (col.total === 0) continue;
      // Every gate's results account for every routine in the stage, not just the sample.
      for (const g of GATES) {
        const sum = Object.values(col.gateTally[g] as Record<string, number>).reduce((n, v) => n + v, 0);
        expect(sum).toBe(col.total);
      }
    }
  });

  test('unknown corpus 404s', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/corpora/00000000-0000-0000-0000-000000000000/flow-board`);
    expect(res.status()).toBe(404);
  });
});

test.describe('Flow board · UI', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('renders all 8 columns with real routines and a working card link', async ({ page }) => {
    const id = await minpackId(page);
    await page.goto(`/corpora/${id}/board`);
    await expect(page.getByTestId('flow-board-page')).toBeVisible();

    for (const key of COLUMN_KEYS) {
      await expect(page.getByTestId(`flow-column-${key}`)).toBeVisible();
    }

    // MINPACK's fixed seed data always has parsed routines with no spec yet.
    const parsedColumn = page.getByTestId('flow-column-parsed');
    const firstPill = parsedColumn.locator('[data-testid^="flow-pill-"]').first();
    await expect(firstPill).toBeVisible();
    const routineId = (await firstPill.getAttribute('data-testid'))!.replace('flow-pill-', '');

    await firstPill.click();
    await expect(page).toHaveURL(new RegExp(`/subroutines/${routineId}$`));
  });

  test('a capped column\'s "+N more" link filters Routines by corpus and state', async ({ page }) => {
    const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
    // The MINPACK-sized fixture stays under the 100 cap; find one that doesn't.
    const board = await Promise.all(
      corpora.data.map(async (c: { id: string; name: string }) => ({
        c,
        board: await page.request.get(`${API_BASE}/api/v1/corpora/${c.id}/flow-board`).then((r) => r.json()),
      })),
    );
    const withCap = board.find(({ board: b }) => b.columns.some((col: { hasMore: boolean }) => col.hasMore));
    test.skip(!withCap, 'no local corpus exceeds the per-column cap');
    const capped = withCap!.board.columns.find((c: { hasMore: boolean }) => c.hasMore);

    await page.goto(`/corpora/${withCap!.c.id}/board`);
    const more = page.getByTestId(`flow-column-more-${capped.key}`);
    await expect(more).toBeVisible();
    await expect(more).toHaveText(new RegExp(`\\+.*more`));
    await more.click();

    const url = new URL(page.url());
    expect(url.searchParams.get('corpus')).toBe(withCap!.c.id);
    expect(url.searchParams.get('state')).toBeTruthy();
    await expect(page.getByTestId('subroutines-search')).toBeVisible();
  });

  // Needs the mock copilot brain (local stack): it routes this phrase to
  // get_flow_board deterministically. A real model may answer from another
  // tool, so this is not run against a deployed environment.
  test('asking "what\'s stuck" in the dock answers with a flowBoard card that matches the API', async ({ page }) => {
    const id = await minpackId(page);
    const api = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/flow-board`).then((r) => r.json());

    await page.addInitScript(() => window.localStorage.setItem('astra.agentDock', 'open'));
    await page.goto(`/corpora/${id}/board`);
    const dock = page.getByTestId('agent-dock');
    await expect(dock).toBeVisible();

    await dock.locator('[data-testid="suggestion-chip"][data-intent="What\'s stuck, and where?"]').click();

    const card = dock.getByTestId('flow-board-card').last();
    await expect(card).toBeVisible({ timeout: 30_000 });
    const stages = card.getByTestId('flow-board-stage');
    await expect(stages).toHaveCount(8);

    // The card and the page read the same builder, so every stage count agrees.
    for (const col of api.columns) {
      await expect(card.locator(`[data-stage="${col.key}"]`)).toHaveAttribute('data-count', String(col.total));
    }
    // Card size shows counts only; the routines appear once it is expanded.
    await expect(card.getByRole('link')).toHaveCount(0);
  });

  test('the project page links to the board', async ({ page }) => {
    const id = await minpackId(page);
    await page.goto(`/corpora/${id}`);
    const link = page.getByTestId('open-flow-board');
    await expect(link).toBeVisible();
    await link.click();
    await expect(page.getByTestId('flow-board-page')).toBeVisible();
  });
});
