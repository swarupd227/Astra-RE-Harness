/**
 * Cloud end-to-end: UniBasic → Java Spring, driven entirely through the UI.
 *
 * Runs against the Azure deployment (astra-frontend / astra-api) and walks
 * the whole workflow the way a user would:
 *
 *   engineer : open project → open routine → Extract spec (real LLM)
 *   engineer : Route to SME for review
 *   sme      : decide every claim → Sign spec (irrevocable)
 *   engineer : pick target stack "Java Spring" → Generate scaffold (real LLM)
 *   engineer : open the artifact → assert .java files → pull them to disk
 *
 * Run it:
 *   BASE_URL=https://astra-frontend.azurewebsites.net \
 *   API_BASE=https://astra-api.azurewebsites.net \
 *   npx playwright test tests/cloud-unibasic-java.spec.ts
 *
 * Knobs (all optional):
 *   CORPUS=…          project name          (default "UniBasic Pick Sample")
 *   ROUTINE=…         subroutine name       (default "add-branch-to-user")
 *   SKIP_EXTRACT=1    reuse the existing draft spec instead of re-extracting
 *   OUT_DIR=…         where generated Java lands (default ./generated)
 *
 * IMPORTANT: this spec never calls /api/v1/dev/reset. The cloud database is
 * shared demo state — a reset would wipe every signed spec on it. The test is
 * instead state-aware: it inspects the routine's current lifecycle state and
 * resumes from whichever step is next, so re-runs are safe and cheap.
 */
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';

const API_BASE = (process.env.API_BASE ?? 'https://astra-api.azurewebsites.net').replace(/\/$/, '');
const CORPUS_NAME = process.env.CORPUS ?? 'UniBasic Pick Sample';
const ROUTINE_NAME = process.env.ROUTINE ?? 'add-branch-to-user';
const TARGET_STACK = 'java-spring';
const OUT_DIR = resolve(process.env.OUT_DIR ?? join(process.cwd(), 'generated'));

// Real Anthropic calls behind an Azure App Service cold start. Extraction of a
// ~40-line UniBasic routine lands in 30–90s; scaffold generation of an 8-file
// Spring package in 60–240s. Budget generously — a timeout here costs a whole
// re-run, and the LLM spend is the expensive part, not the wall clock.
const EXTRACT_TIMEOUT = 240_000;
const SCAFFOLD_TIMEOUT = 420_000;

type Persona = 'engineer' | 'sme' | 'observer' | 'admin';

async function apiGet<T>(request: APIRequestContext, path: string, persona: Persona = 'engineer'): Promise<T | null> {
  const res = await request.get(`${API_BASE}${path}`, {
    headers: { 'X-Dev-Persona': persona },
    timeout: 60_000,
  });
  if (!res.ok()) return null;
  return (await res.json()) as T;
}

/** Switch the dev persona via the top-right menu; the app reloads on pick. */
async function switchPersona(page: Page, persona: Persona) {
  const label = { engineer: 'Engineer', sme: 'SME', observer: 'Observer', admin: 'Admin' }[persona];
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

test.describe('Cloud · UniBasic → Java Spring, through the UI', () => {
  // Two real LLM round-trips plus a review pass. 20 min ceiling.
  test.setTimeout(20 * 60_000);

  test('extract → review → sign → scaffold Java Spring', async ({ page }, testInfo) => {
    const shot = async (name: string) => {
      await testInfo.attach(name, { body: await page.screenshot({ fullPage: false }), contentType: 'image/png' });
    };

    // ── Resolve the routine ───────────────────────────────────────────
    // Done over the API rather than by clicking through the projects list:
    // the ids are stable, and this keeps the UI assertions below about the
    // workflow rather than about navigation plumbing.
    let subId = '';
    await test.step(`locate ${ROUTINE_NAME} in "${CORPUS_NAME}"`, async () => {
      const corpora = await apiGet<{ data: { id: string; name: string }[] }>(page.request, '/api/v1/corpora', 'admin');
      const corpus = corpora?.data.find((c) => c.name === CORPUS_NAME);
      expect(corpus, `project "${CORPUS_NAME}" not found on ${API_BASE}`).toBeTruthy();

      const detail = await apiGet<any>(page.request, `/api/v1/corpora/${corpus!.id}`, 'admin');
      const subs = (detail.latestVersion?.files ?? []).flatMap((f: any) => f.subroutines ?? []);
      const match = subs.find((s: any) => s.name === ROUTINE_NAME);
      expect(match, `routine "${ROUTINE_NAME}" not found in ${CORPUS_NAME}`).toBeTruthy();
      subId = match.id;
      testInfo.annotations.push({ type: 'routine', description: `${ROUTINE_NAME} · ${subId} · state ${match.state}` });
    });

    // Seed the persona before the first render so the engineer CTAs are
    // present on load. addInitScript would re-run on the PersonaMenu reload
    // and undo the later SME switch, so this is a one-shot evaluate.
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    // Clear any target stack a previous run pinned — the selector reads
    // localStorage first and would otherwise silently keep .NET 8 selected.
    await page.evaluate(() => window.localStorage.removeItem('astra.targetStack'));

    const specState = async (): Promise<string | null> =>
      (await apiGet<{ state: string }>(page.request, `/api/v1/subroutines/${subId}/spec`))?.state ?? null;

    // ── 1. Extract ────────────────────────────────────────────────────
    await test.step('engineer extracts the spec', async () => {
      const state = await specState();
      if (state && state !== 'DRAFT') {
        // IN_REVIEW / SIGNED / SCAFFOLDED — re-extraction is state-guarded
        // server-side and would throw away review work. Resume instead.
        test.info().annotations.push({ type: 'skip-extract', description: `spec already ${state}` });
        return;
      }
      if (state === 'DRAFT' && process.env.SKIP_EXTRACT === '1') return;

      await page.goto(`/subroutines/${subId}`);
      await expect(page.getByRole('heading', { name: ROUTINE_NAME, level: 1 })).toBeVisible({ timeout: 60_000 });
      await shot('01-routine');

      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({ timeout: EXTRACT_TIMEOUT });
      await shot('02-extraction-complete');
    });

    // ── 2. Route to SME ───────────────────────────────────────────────
    await test.step('engineer routes the draft to the SME', async () => {
      if ((await specState()) !== 'DRAFT') return;
      await page.goto(`/subroutines/${subId}/spec`);
      await page.getByRole('button', { name: /Route to SME/ }).click();
      await expect(page).toHaveURL(/\/review$/, { timeout: 30_000 });
      await shot('03-in-review');
    });

    // ── 3. SME decides every claim, then signs ────────────────────────
    await test.step('SME reviews every claim and signs', async () => {
      if ((await specState()) !== 'IN_REVIEW') return;

      await page.goto(`/subroutines/${subId}/review`);
      await switchPersona(page, 'sme');
      await page.goto(`/subroutines/${subId}/review`);

      // Claim cards render as <article id="claim-{id}">. The claim inventory
      // is provider-chosen and varies per extraction, so iterate what the
      // page actually rendered rather than hardcoding ids.
      const cards = page.locator('article[id^="claim-"]');
      await expect(cards.first()).toBeVisible({ timeout: 60_000 });
      const ids = await cards.evaluateAll((els) => els.map((el) => (el as HTMLElement).id));

      for (const domId of ids) {
        const card = page.locator(`article[id="${domId}"]`);
        await card.scrollIntoViewIfNeeded();
        // Open questions expose "Resolve in spec" — the SME rewrites the
        // question into a settled claim. Everything else is a plain Accept.
        const resolve = card.getByRole('button', { name: /^Resolve in spec$/ });
        if (await resolve.count()) {
          await resolve.click();
          await card.getByRole('textbox').fill(
            'Resolved during the automated cloud walkthrough: behaviour confirmed against the UniBasic source.',
          );
          await card.getByRole('button', { name: /^Save$/ }).click();
        } else {
          await card.getByRole('button', { name: /^Accept$/ }).click();
        }
      }
      await shot('04-claims-processed');

      // Sign is disabled until every claim carries a decision — waiting on
      // the enabled state is the real precondition assertion.
      const sign = page.getByRole('button', { name: /^Sign spec$/ }).first();
      await expect(sign).toBeEnabled({ timeout: 30_000 });
      await sign.click();
      await page.getByRole('checkbox').check();
      await page.getByRole('dialog').getByRole('button', { name: /^Sign spec$/ }).click();
      await expect(page.getByTestId('evidence-block-signature')).toBeVisible({ timeout: 60_000 });
      await shot('05-signed');
    });

    expect(await specState()).toMatch(/SIGNED|SCAFFOLDED/);

    // ── 4. Pick Java Spring and generate ──────────────────────────────
    let scaffoldId = '';
    await test.step('engineer targets Java Spring and generates the scaffold', async () => {
      await page.goto(`/subroutines/${subId}/review`);
      await switchPersona(page, 'engineer');
      await page.goto(`/subroutines/${subId}/review`);

      const openExisting = page.getByRole('link', { name: /Open scaffold/ });
      if (await openExisting.count()) {
        // A scaffold already exists for this spec — the UI offers no way to
        // regenerate it against a different stack, so surface that loudly
        // rather than silently asserting on a stale .NET package.
        await openExisting.click();
        await expect(page).toHaveURL(/\/scaffolds\/[0-9a-f-]{36}$/, { timeout: 60_000 });
        scaffoldId = page.url().split('/').pop()!;
        return;
      }

      const javaCard = page.getByTestId(`target-card-${TARGET_STACK}`);
      await expect(javaCard, 'Java Spring target card missing — no production java-spring archetype for this schema')
        .toBeVisible({ timeout: 30_000 });
      await expect(javaCard, 'Java Spring is gated (preview) for this source schema').toBeEnabled();
      await javaCard.click();
      await expect(javaCard).toHaveAttribute('aria-pressed', 'true');
      await shot('06-target-java-spring');

      await page.getByRole('link', { name: /Generate scaffold/ }).click();
      // The live surface streams file-by-file; the Open scaffold CTA appears
      // only once the package is persisted.
      const openCta = page.getByRole('button', { name: /Open scaffold/ });
      await expect(openCta).toBeVisible({ timeout: SCAFFOLD_TIMEOUT });
      await shot('07-streaming-complete');
      await openCta.click();
      await expect(page).toHaveURL(/\/scaffolds\/[0-9a-f-]{36}$/, { timeout: 60_000 });
      scaffoldId = page.url().split('/').pop()!;
    });

    // ── 5. Assert the artifact is real Java, then pull it to disk ─────
    await test.step('artifact holds compiling-shaped Java Spring sources', async () => {
      await expect(
        page.getByRole('heading', { name: new RegExp(`\\d+ files · \\d+ TODOs · ${TARGET_STACK}`) }),
      ).toBeVisible({ timeout: 60_000 });

      const tree = page.getByRole('navigation', { name: 'Scaffold file tree' });
      await expect(tree.getByRole('button', { name: /\.java$/ }).first()).toBeVisible();
      await shot('08-scaffold-artifact');

      const scaffold = await apiGet<any>(page.request, `/api/v1/scaffolds/${scaffoldId}`);
      expect(scaffold?.targetPlatform).toBe(TARGET_STACK);
      const javaFiles = (scaffold.files as any[]).filter((f: any) => f.path.endsWith('.java'));
      expect(javaFiles.length, 'scaffold contains no .java files').toBeGreaterThan(0);
      expect((scaffold.files as any[]).some((f: any) => f.path === 'pom.xml'), 'no pom.xml in package').toBeTruthy();

      const root = join(OUT_DIR, `${ROUTINE_NAME}-${TARGET_STACK}`);
      for (const f of scaffold.files as any[]) {
        const dest = join(root, f.path);
        mkdirSync(dirname(dest), { recursive: true });
        writeFileSync(dest, f.content, 'utf8');
      }
      const manifest = [
        `scaffold ${scaffoldId}`,
        `routine  ${ROUTINE_NAME}`,
        `target   ${scaffold.targetPlatform}`,
        `state    ${scaffold.state}`,
        `url      ${new URL(`/scaffolds/${scaffoldId}`, page.url()).toString()}`,
        '',
        ...(scaffold.files as any[]).map((f: any) => `  ${f.path}  (${f.lineCount} lines, ${f.todoCount} TODOs)`),
      ].join('\n');
      writeFileSync(join(root, 'SCAFFOLD.txt'), manifest, 'utf8');
      await testInfo.attach('scaffold-manifest', { body: manifest, contentType: 'text/plain' });
      console.log(`\nGenerated Java written to ${root}\n${manifest}\n`);
    });
  });
});
