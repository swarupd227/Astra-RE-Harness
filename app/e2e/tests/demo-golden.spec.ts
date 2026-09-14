/**
 * Golden demo — typed, not clicked.
 *
 *   The whole storyline is sentences typed into the programme's thread:
 *   status → pattern clusters → riskiest routines → the hero routine's
 *   spec explained → code generated (Confirm) → compile gate (Confirm) →
 *   migration waves (Confirm) → assessment (Confirm) → Mission Control.
 *   Every reply is an agent message with a card; the recorder asserts
 *   the card kind, never a page.
 *
 *   State that would make a viewer wait is pre-baked by
 *   scripts/prebake-golden.mjs (survey, signed hero spec, plan,
 *   assessment); this spec reads generated/golden.json for the corpus,
 *   thread and hero it produced.
 *
 * Run (headless check, mock brain or real):
 *   API_BASE=http://127.0.0.1:38080 node scripts/prebake-golden.mjs
 *   BASE_URL=http://127.0.0.1:35173 API_BASE=http://127.0.0.1:38080 npx playwright test demo-golden
 * Record:
 *   RECORD_DEMO=1 BASE_URL=… API_BASE=… npx playwright test demo-golden
 */
import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { expect, test, type Locator, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const RECORD = process.env.RECORD_DEMO === '1';
const BEAT = RECORD ? 2_800 : 0;
const BEAT_LONG = RECORD ? 4_500 : 0;

type Manifest = {
  corpusId: string;
  corpusName: string;
  threadId: string;
  heroSubroutineId: string;
  heroName: string;
  specId: string;
};

function loadManifest(): Manifest {
  const p = fileURLToPath(new URL('../generated/golden.json', import.meta.url));
  if (!existsSync(p)) throw new Error('generated/golden.json missing — run `node scripts/prebake-golden.mjs` first.');
  return JSON.parse(readFileSync(p, 'utf8')) as Manifest;
}

async function caption(page: Page, text: string) {
  if (!RECORD) return;
  await page.evaluate((t) => {
    let el = document.getElementById('__demo_caption');
    if (!el) {
      el = document.createElement('div');
      el.id = '__demo_caption';
      Object.assign(el.style, {
        position: 'fixed', top: '14px', left: '50%', transform: 'translateX(-50%)', zIndex: '2147483647',
        background: 'rgba(11, 11, 12, 0.92)', color: '#EFE8E0', padding: '10px 18px', borderRadius: '10px',
        border: '1px solid rgba(255, 221, 0, 0.55)',
        font: "500 15px/1.4 'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif",
        maxWidth: '640px', boxShadow: '0 8px 28px rgba(0, 0, 0, 0.45)', pointerEvents: 'none',
      });
      document.body.appendChild(el);
    }
    el.textContent = t;
  }, text);
}

async function switchPersona(page: Page, persona: 'engineer' | 'sme' | 'observer' | 'admin') {
  const label = { engineer: 'Engineer', sme: 'SME', observer: 'Observer', admin: 'Admin' }[persona];
  const trigger = page.locator('button[aria-haspopup="menu"]').first();
  const current = (await trigger.textContent()) ?? '';
  if (current.includes(label)) return;
  await trigger.click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  // The Workspace keeps a live event stream open, so "network idle" never
  // arrives; the menu trigger showing the new persona is the real signal.
  await expect(trigger).toContainText(label, { timeout: 15_000 });
}

const messages = (page: Page) => page.locator('[data-testid="thread-message"]');
const cardsOfKind = (page: Page, kind: string) => page.locator(`[data-testid="artifact-card"][data-kind="${kind}"]`);

/**
 * Type one sentence, send it, confirm if the agent asks, and wait for the
 * card the storyline expects. Returns the card so a beat can inspect it.
 */
async function say(page: Page, sentence: string, expectKind: string, opts: { timeout?: number; confirm?: boolean } = {}): Promise<Locator> {
  const timeout = opts.timeout ?? 90_000;
  const before = await cardsOfKind(page, expectKind).count();
  const messagesBefore = await messages(page).count();

  const input = page.getByTestId('composer-input');
  await input.click();
  await input.fill(sentence);
  await page.keyboard.press('Enter');

  // The agent's first reply: either the answer or a Confirm / Not now card.
  await expect.poll(() => messages(page).count(), { timeout: 60_000 }).toBeGreaterThan(messagesBefore);
  if (opts.confirm !== false) {
    const confirm = page.getByTestId('confirm-action').last();
    try {
      await confirm.waitFor({ state: 'visible', timeout: 8_000 });
      await page.waitForTimeout(BEAT);
      await confirm.click();
    } catch {
      /* no confirmation needed for a read-only question */
    }
  }
  await expect.poll(() => cardsOfKind(page, expectKind).count(), { timeout }).toBeGreaterThan(before);
  const card = cardsOfKind(page, expectKind).last();
  await card.scrollIntoViewIfNeeded();
  return card;
}

test.describe('golden demo (typed)', () => {
  test.setTimeout(RECORD ? 20 * 60_000 : 12 * 60_000);

  test('the programme conversation end to end', async ({ page }) => {
    const m = loadManifest();
    const hero = '`' + m.heroName + '`';

    await page.goto(`/w/${m.threadId}`);
    await expect(page.getByTestId('composer-input')).toBeVisible({ timeout: 30_000 });
    await switchPersona(page, 'admin');
    await caption(page, `${m.corpusName} — one conversation with the team of agents`);
    await page.waitForTimeout(BEAT_LONG);

    await caption(page, '“What’s the status?” — the Discovery agent answers with the funnel');
    await say(page, "What's the status of the programme?", 'funnel');
    await page.waitForTimeout(BEAT);

    await caption(page, 'Patterns were surveyed at ingest time; the clusters are the archetype candidates');
    await say(page, 'Show me the pattern clusters', 'clusterGrid');
    await page.waitForTimeout(BEAT_LONG);

    await caption(page, 'Risk comes from the real call graph');
    await say(page, 'Which are the riskiest routines?', 'routineList');
    await page.waitForTimeout(BEAT);

    await switchPersona(page, 'engineer');
    await caption(page, `The Spec agent explains ${m.heroName}'s signed claims in plain language`);
    await say(page, `Explain the claims in the spec for ${hero}`, 'specSummary');
    await page.waitForTimeout(BEAT_LONG);

    await caption(page, 'Generating code is a sentence — and a Confirm card, because it changes state');
    await say(page, `Generate the code for ${hero}`, 'scaffoldTree', { timeout: 6 * 60_000 });
    await page.waitForTimeout(BEAT_LONG);

    await caption(page, 'The Validation agent runs the compile gate and reports in plain words');
    const gate = await say(page, `Run the compile gate for ${hero}`, 'gateResults', { timeout: 5 * 60_000 });
    await expect(gate).toContainText(/passed|green|succeeded/i);
    await page.waitForTimeout(BEAT_LONG);

    await switchPersona(page, 'admin');
    await caption(page, 'The Planning agent drafts the waves from the dependency graph');
    await say(page, 'Plan the migration waves', 'planWaves', { timeout: 4 * 60_000 });
    await page.waitForTimeout(BEAT_LONG);

    await caption(page, 'The Architecture agent writes the assessment: effort, risk, recommended mode and target');
    const assessment = await say(page, 'Run an assessment for the programme', 'assessment', { timeout: 5 * 60_000 });
    await expect(assessment).toBeVisible();
    await page.waitForTimeout(BEAT_LONG);

    await caption(page, 'Mission Control — every programme, every agent, one feed');
    await page.goto('/');
    await expect(page.getByTestId('mission-control')).toBeVisible({ timeout: 30_000 });
    await page.waitForTimeout(BEAT_LONG);
  });
});
