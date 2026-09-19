/**
 * Agent dock — the agent thread beside an artifact view.
 *
 *   - Artifact views (migration plan, routine …) get a top-bar toggle that
 *     opens that page's agent in the programme's own thread.
 *   - The Workspace and spec review (which owns its Spec agent) get neither.
 *   - A starter sends into the thread; on a narrow screen the dock is an
 *     overlay that starts closed and Escape dismisses.
 *
 * Assertions stay on the dock's own contract (toggle, header, starters, the
 * user's turn landing) — what the agent answers is the copilot's business.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const STARTER = 'Explain the migration plan wave by wave';

async function minpackId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) test.skip(true, 'MINPACK corpus missing.');
  return minpack.id;
}

test.beforeEach(async ({ page }) => {
  // Seed once per test: init scripts run on every navigation, and the
  // persistence test needs its own choice to survive one.
  await page.addInitScript(() => {
    if (window.sessionStorage.getItem('e2e-seeded')) return;
    window.sessionStorage.setItem('e2e-seeded', '1');
    window.localStorage.setItem('astra.devPersona', 'engineer');
    window.localStorage.setItem('astra.agentDock', 'closed');
  });
});

test.describe('Agent dock · wide screen', () => {
  test('toggle opens the Planning agent on the migration plan and closes again', async ({ page }) => {
    const id = await minpackId(page);
    await page.goto(`/corpora/${id}/migration-plan`);

    const toggle = page.getByTestId('agent-dock-toggle');
    await expect(toggle).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-label', 'Ask the Planning agent');
    await expect(page.getByTestId('agent-dock')).toHaveCount(0);

    await toggle.click();
    const dock = page.getByTestId('agent-dock');
    await expect(dock).toBeVisible();
    await expect(dock.locator('header').getByText('Planning agent', { exact: true })).toBeVisible();
    await expect(dock.getByTestId('agent-dock-open-workspace')).toHaveAttribute('href', /^\/w\/[0-9a-f-]{36}$/);
    await expect(dock.locator(`[data-testid="suggestion-chip"][data-intent="${STARTER}"]`)).toBeVisible();
    // The page keeps its own content beside the dock.
    await expect(page.getByTestId('migration-plan-page')).toBeVisible();

    await dock.getByTestId('agent-dock-close').click();
    await expect(page.getByTestId('agent-dock')).toHaveCount(0);
  });

  test('a starter lands as the user\'s turn in the dock thread', async ({ page }) => {
    const id = await minpackId(page);
    await page.goto(`/corpora/${id}/migration-plan`);
    await page.getByTestId('agent-dock-toggle').click();

    const dock = page.getByTestId('agent-dock');
    await dock.locator(`[data-testid="suggestion-chip"][data-intent="${STARTER}"]`).click();
    await expect(dock.getByText(STARTER, { exact: true }).first()).toBeVisible();
  });

  test('the open choice survives a reload; the agent follows the page', async ({ page }) => {
    const id = await minpackId(page);
    await page.goto(`/corpora/${id}/migration-plan`);
    await page.getByTestId('agent-dock-toggle').click();
    await expect(page.getByTestId('agent-dock')).toBeVisible();

    await page.goto(`/corpora/${id}/dependency-graph`);
    const dock = page.getByTestId('agent-dock');
    await expect(dock).toBeVisible();
    await expect(dock.locator('header').getByText('Discovery agent', { exact: true })).toBeVisible();
  });

  test('the Workspace and spec review get no dock', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByTestId('workspace-root')).toBeVisible();
    await expect(page.getByTestId('agent-dock-toggle')).toHaveCount(0);

    const subs = await page.request.get(`${API_BASE}/api/v1/subroutines`).then((r) => r.json());
    const sub = (subs.data ?? subs)[0];
    test.skip(!sub, 'no routines to open');
    await page.goto(`/subroutines/${sub.id}/review`);
    await expect(page.getByTestId('agent-dock-toggle')).toHaveCount(0);
    await expect(page.getByTestId('agent-dock')).toHaveCount(0);
  });
});

test.describe('Agent dock · narrow screen', () => {
  test.use({ viewport: { width: 1100, height: 800 } });

  test('an overlay that starts closed, opens from the toggle and closes on Escape', async ({ page }) => {
    const id = await minpackId(page);
    // A wide-screen "open" must not turn into a scrim over the page here.
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.agentDock', 'open'));
    await page.goto(`/corpora/${id}/migration-plan`);
    await expect(page.getByTestId('agent-dock-toggle')).toBeVisible();
    await expect(page.getByTestId('agent-dock')).toHaveCount(0);

    await page.getByTestId('agent-dock-toggle').click();
    await expect(page.getByTestId('agent-dock')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('agent-dock')).toHaveCount(0);
  });
});
