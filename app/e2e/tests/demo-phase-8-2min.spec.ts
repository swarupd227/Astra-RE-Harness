/**
 * Demo · final 2-minute cut.
 *
 *   Storyline: Projects → MINPACK project → polished dep graph (with
 *   path-highlight on click) → migration plan → drill into HYBRD1
 *   (Powell's hybrid nonlinear solver) → live extract → SME signs →
 *   generate scaffold → view REAL translated .NET code → validate
 *   compile → teaser beats over the platform-intelligence surfaces
 *   (Golden Dataset, Prompt Catalog, Portfolio, Harmonisation,
 *   Signature Health, Validation Policy, Compliance Feed) → close.
 *
 *   No CONSUME_ROLL: the synthetic 66-LOC seed is gated off by
 *   default; the demo now opens on two substantive public Fortran
 *   corpora (MINPACK 10k LoC, LAPACK Reference BLAS 48k LoC).
 *
 * Run:
 *   RECORD_DEMO=1 BASE_URL=http://127.0.0.1:35173 \
 *     API_BASE=http://127.0.0.1:38080 \
 *     npx playwright test demo-phase-8-2min
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const SME = { 'X-Dev-Persona': 'sme', 'Content-Type': 'application/json' };
const ENGINEER = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

const BEAT_SHORT = process.env.RECORD_DEMO === '1' ? 2_000 : 0;
const BEAT_MEDIUM = process.env.RECORD_DEMO === '1' ? 3_000 : 0;
const BEAT_LONG = process.env.RECORD_DEMO === '1' ? 4_500 : 0;
const BEAT_TEASER = process.env.RECORD_DEMO === '1' ? 2_500 : 0;

async function caption(page: Page, text: string) {
  await page.evaluate((t) => {
    let el = document.getElementById('__demo_caption');
    if (!el) {
      el = document.createElement('div');
      el.id = '__demo_caption';
      Object.assign(el.style, {
        position: 'fixed',
        top: '12px',
        right: '12px',
        zIndex: '2147483647',
        background: 'rgba(17, 24, 39, 0.93)',
        color: 'white',
        padding: '10px 16px',
        borderRadius: '8px',
        font: "500 14px/1.4 'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif",
        maxWidth: '440px',
        boxShadow: '0 6px 24px rgba(0, 0, 0, 0.35)',
        pointerEvents: 'none',
      });
      document.body.appendChild(el);
    }
    el.textContent = t;
    el.style.opacity = '1';
  }, text);
}

async function switchPersona(page: Page, persona: 'engineer' | 'sme' | 'observer' | 'admin') {
  const labelByPersona = {
    engineer: 'Engineer',
    sme: 'SME',
    observer: 'Observer',
    admin: 'Admin',
  } as const;
  await page.locator('button[aria-haspopup="menu"]').click();
  await page
    .getByRole('menuitemradio', { name: new RegExp(`^${labelByPersona[persona]}\\s`) })
    .click();
  await page.waitForLoadState('networkidle');
}

async function getMinpackId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) throw new Error('MINPACK corpus missing.');
  return minpack.id;
}

async function getHybrd1SubId(page: Page, corpusId: string): Promise<string> {
  // Prefer the F77 entry point at src/hybrd1.f (the canonical wrapper).
  const subs = await page.request
    .get(`${API_BASE}/api/v1/subroutines?corpus=${corpusId}&q=HYBRD1&limit=20`)
    .then((r) => r.json());
  const entryPoint = subs.data.find(
    (s: { name: string; file: { relativePath: string } }) =>
      s.name === 'HYBRD1' && s.file.relativePath.endsWith('hybrd1.f'),
  ) ?? subs.data.find((s: { name: string }) => s.name === 'HYBRD1');
  if (!entryPoint) throw new Error('HYBRD1 routine missing in MINPACK.');
  return entryPoint.id;
}

test.describe('Demo · final 2-minute end-to-end', () => {
  test.beforeAll(async ({ request }) => {
    // Reset to a known clean state. CONSUME_ROLL is gated off so the
    // dashboard opens on MINPACK + LAPACK BLAS only. MINPACK + LAPACK
    // re-seed in the background after reset; we poll for both.
    await request.post(`${API_BASE}/api/v1/dev/reset`, {
      headers: { 'X-Dev-Persona': 'engineer' },
      timeout: 60_000,
    });
    const deadline = Date.now() + 120_000;
    let minpackId: string | undefined;
    while (Date.now() < deadline) {
      const corpora = await request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
      const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
      const blas = corpora.data.find((c: { name: string }) => c.name.includes('LAPACK'));
      if (minpack && blas) {
        minpackId = minpack.id;
        break;
      }
      await new Promise((r) => setTimeout(r, 2_000));
    }
    if (!minpackId) throw new Error('MINPACK + LAPACK BLAS did not seed within 2 min of /dev/reset.');

    // Wait for MINPACK's parse to settle.
    while (Date.now() < deadline) {
      const detail = await request
        .get(`${API_BASE}/api/v1/corpora/${minpackId}`)
        .then((r) => r.json());
      if (detail?.state === 'PARSED' && detail.latestVersion?.files?.length > 0) break;
      await new Promise((r) => setTimeout(r, 2_000));
    }

    // Seed an approved migration plan so the wave page renders.
    const draft = await request
      .post(`${API_BASE}/api/v1/corpora/${minpackId}/migration-plan/generate`, {
        headers: ADMIN,
        data: {},
        timeout: 60_000,
      })
      .then((r) => r.json());
    await request.post(`${API_BASE}/api/v1/migration-plans/${draft.id}/approve`, {
      headers: ADMIN,
    });

    // Pre-populate the platform with a fleet of signed + scaffolded
    // routines so the Signature Health / Portfolio Dashboard / Admin
    // console land on populated screens (the demo only signs ONE
    // routine live; the rest are seeded off-camera).
    //
    // We pick a representative slice: top-level + mid + leaf MINPACK
    // routines so the resulting signature-health table has a healthy
    // mix of names the audience recognises.
    const PRE_SIGN_ROUTINES = ['HYBRD', 'HYBRJ', 'HYBRJ1', 'LMDIF', 'LMDIF1', 'LMDER', 'LMDER1'];
    const PRE_SCAFFOLD_ROUTINES = new Set(['HYBRD', 'LMDIF', 'LMDER']);

    // Look each one up; some names appear in multiple files — prefer
    // the `src/<name>.f` entry-point if present.
    const subsResp = await request
      .get(`${API_BASE}/api/v1/subroutines?corpus=${minpackId}&limit=500`)
      .then((r) => r.json());
    const allSubs: { id: string; name: string; file: { relativePath: string } }[] = subsResp.data;

    for (const name of PRE_SIGN_ROUTINES) {
      try {
        const sub = allSubs.find((s) => s.name === name && s.file.relativePath.startsWith('src/'))
          ?? allSubs.find((s) => s.name === name);
        if (!sub) {
          // eslint-disable-next-line no-console
          console.warn(`[demo seed] routine ${name} not found in corpus`);
          continue;
        }

        // Extract — SSE stream; the request completes once the LLM
        // pipeline finishes writing the DRAFT spec.
        const ext = await request.post(`${API_BASE}/api/v1/subroutines/${sub.id}/extract`, {
          headers: ADMIN, timeout: 60_000,
        });
        if (!ext.ok()) {
          // eslint-disable-next-line no-console
          console.warn(`[demo seed] extract for ${name} failed: ${ext.status()} ${await ext.text()}`);
          continue;
        }
        // Drain the SSE body — needed so the server-side pipeline
        // finalises the spec record before we proceed to sign.
        await ext.text().catch(() => '');

        // Re-fetch the spec record.
        const spec = await request
          .get(`${API_BASE}/api/v1/subroutines/${sub.id}/spec`)
          .then((r) => r.json());

        // Route to SME — transitions DRAFT → IN_REVIEW so the spec
        // becomes eligible for signing. Routing is engineer-only by
        // policy (admin can't route).
        const routeResp = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/route`, {
          headers: ENGINEER,
          data: { routingNote: 'Pre-seeded for demo recording.' },
        });
        if (!routeResp.ok()) {
          // eslint-disable-next-line no-console
          console.warn(`[demo seed] route of ${name} failed: ${routeResp.status()} ${await routeResp.text()}`);
        }

        // Accept every claim under SME persona so the signature gate
        // unlocks. The mock LLM emits a fixed taxonomy; we iterate
        // whatever it actually returned to stay robust.
        for (const section of ['inputs', 'outputs', 'invariants', 'side_effects', 'edge_cases']) {
          for (const claim of (spec.spec[section] ?? []) as { id: string }[]) {
            await request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, {
              headers: SME,
              data: {
                claimPath: `$.${section}[?(@.id=='${claim.id}')]`,
                action: 'accept',
              },
            });
          }
        }
        for (const q of (spec.spec.open_questions ?? []) as { id: string }[]) {
          await request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, {
            headers: SME,
            data: {
              claimPath: `$.open_questions[?(@.id=='${q.id}')]`,
              action: 'edit',
              editedText: 'Resolved off-camera during demo seed.',
            },
          });
        }

        // Sign — confirmation must include the canonical sentence
        // "I have reviewed every claim" (case-insensitive substring).
        const signResp = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/sign`, {
          headers: SME,
          data: { confirmation: 'I have reviewed every claim and consider the spec authoritative.' },
        });
        if (!signResp.ok()) {
          // eslint-disable-next-line no-console
          console.warn(`[demo seed] sign of ${name} failed: ${signResp.status()} ${await signResp.text()}`);
        } else {
          // eslint-disable-next-line no-console
          console.log(`[demo seed] signed ${name}`);
        }

        // Optionally scaffold a few so Portfolio Dashboard shows
        // scaffolded > 1 and Sig Health rows have "SCAFFOLDED" state.
        // Scaffold is engineer-only and streams SSE; we drain the body
        // so the request completes only after the pipeline finalises
        // the Scaffold record.
        if (PRE_SCAFFOLD_ROUTINES.has(name)) {
          const sc = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, {
            headers: ENGINEER, timeout: 60_000,
          });
          if (sc.ok()) {
            await sc.text().catch(() => '');
            // eslint-disable-next-line no-console
            console.log(`[demo seed] scaffolded ${name}`);
          } else {
            // eslint-disable-next-line no-console
            console.warn(`[demo seed] scaffold of ${name} failed: ${sc.status()} ${await sc.text()}`);
          }
        }
      } catch (e) {
        // Best-effort — one routine failing shouldn't block the demo.
        // eslint-disable-next-line no-console
        console.warn(`[demo seed] pre-sign of ${name} failed:`, (e as Error).message);
      }
    }
  });

  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('source → graph → plan → migrate HYBRD1 → certify → teasers', async ({ page }) => {
    const minpackId = await getMinpackId(page);
    const hybrd1SubId = await getHybrd1SubId(page, minpackId);

    // ── 1 · Projects list opener ─────────────────────────────────────
    await test.step('opener · projects list', async () => {
      await page.goto('/projects');
      await page.waitForLoadState('networkidle');
      await caption(
        page,
        'Two real Fortran portfolios: MINPACK (10k LoC) and LAPACK Reference BLAS (48k LoC)',
      );
      if (BEAT_LONG) await page.waitForTimeout(BEAT_LONG);
    });

    // ── 2 · MINPACK project detail ───────────────────────────────────
    await test.step('project detail · MINPACK', async () => {
      await page.goto(`/corpora/${minpackId}`);
      await page.waitForLoadState('networkidle');
      await caption(
        page,
        'MINPACK · 50 source files · 52 routines indexed by the parser',
      );
      if (BEAT_MEDIUM) await page.waitForTimeout(BEAT_MEDIUM);
    });

    // ── 3 · Pure-SVG dep graph: hold, click HYBRD1 to highlight path ─
    await test.step('dependency graph (path-highlight demo)', async () => {
      await page.goto(`/corpora/${minpackId}/dependency-graph`);
      await expect(page.getByTestId('dependency-graph-page')).toBeVisible({ timeout: 15_000 });
      // Wait for the force-directed layout simulation to settle (140
      // iterations runs in <1s but React then paints the new positions).
      await page.waitForTimeout(1_200);

      // First pause on the un-selected graph so the viewer sees the
      // overall shape + the wave-coloured legend.
      await caption(
        page,
        'Dependency graph · click any routine to highlight its full upstream + downstream path',
      );
      if (BEAT_SHORT) await page.waitForTimeout(BEAT_SHORT);

      // Now click HYBRD1. The SVG node `<g>` lives inside a transformed
      // group, so a `.click()` would scroll the page; we dispatch the
      // synthetic event directly on the React-tracked element so the
      // onClick handler fires without any geometry math.
      const nodeSel = `[data-testid="graph-node-${hybrd1SubId}"]`;
      const clicked = await page.evaluate((sel) => {
        const el = document.querySelector(sel);
        if (!el) return false;
        // Dispatch a real MouseEvent so React's synthetic system picks it up.
        el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
        return true;
      }, nodeSel);

      if (clicked) {
        await caption(
          page,
          'Path-focus on HYBRD1 · orange edges trace every caller and every transitive callee',
        );
      }
      if (BEAT_LONG) await page.waitForTimeout(BEAT_LONG);
    });

    // ── 4 · Migration plan ───────────────────────────────────────────
    await test.step('migration plan · waves', async () => {
      await page.goto(`/corpora/${minpackId}/migration-plan`);
      await expect(page.getByTestId('migration-plan-page')).toBeVisible({ timeout: 15_000 });
      await expect(page.getByTestId('migration-wave-1')).toBeVisible({ timeout: 15_000 });
      await caption(page, 'Topological wave plan · leaves first, callers after their callees');
      if (BEAT_MEDIUM) await page.waitForTimeout(BEAT_MEDIUM);
    });

    // ── 5 · Drill into HYBRD1 ────────────────────────────────────────
    await test.step('drill · HYBRD1', async () => {
      await page.goto(`/subroutines/${hybrd1SubId}`);
      await expect(page.getByRole('heading', { name: 'HYBRD1', level: 1 })).toBeVisible();
      await caption(
        page,
        "Migrating Powell's hybrid Newton/Broyden nonlinear solver — entry point of MINPACK",
      );
      if (BEAT_SHORT) await page.waitForTimeout(BEAT_SHORT);
    });

    // ── 6 · Extract spec (live LLM) ──────────────────────────────────
    await test.step('extract spec', async () => {
      await page.getByRole('button', { name: /^(Extract|Re-extract) spec$/ }).click();
      await expect(page.getByRole('button', { name: 'View draft spec' })).toBeVisible({
        timeout: 120_000,
      });
      await caption(page, 'Cited LLM extraction · every claim points to specific source lines');
      if (BEAT_SHORT) await page.waitForTimeout(BEAT_SHORT);
      await page.getByRole('button', { name: 'View draft spec' }).click();
      await page.waitForLoadState('networkidle');
    });

    // ── 7 · Route to SME + sign + scroll to edge cases ───────────────
    await test.step('SME signs · edge cases visible', async () => {
      const route = page.getByRole('button', { name: /Route to SME/ });
      if (await route.count()) {
        await route.click();
        await expect(page).toHaveURL(/\/review$/);
      }
      await switchPersona(page, 'sme');

      // Pre-process all claims via API for speed.
      const spec = await page.request
        .get(`${API_BASE}/api/v1/subroutines/${hybrd1SubId}/spec`)
        .then((r) => r.json());
      for (const section of ['inputs', 'outputs', 'invariants', 'side_effects', 'edge_cases']) {
        for (const claim of (spec.spec[section] ?? []) as { id: string }[]) {
          await page.request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, {
            headers: SME,
            data: {
              claimPath: `$.${section}[?(@.id=='${claim.id}')]`,
              action: 'accept',
            },
          });
        }
      }
      for (const q of (spec.spec.open_questions ?? []) as { id: string }[]) {
        await page.request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, {
          headers: SME,
          data: {
            claimPath: `$.open_questions[?(@.id=='${q.id}')]`,
            action: 'edit',
            editedText: 'Resolved by SME during demo.',
          },
        });
      }
      await page.reload();
      await page.waitForLoadState('networkidle');

      // Briefly show the edge-cases section before signing — scroll to it.
      const ecHeading = page.getByRole('heading', { name: /Edge case/i }).first();
      if (await ecHeading.count()) {
        await ecHeading.scrollIntoViewIfNeeded();
        await caption(
          page,
          'Edge cases + open questions surfaced from source · the SME reviews each one',
        );
        if (BEAT_MEDIUM) await page.waitForTimeout(BEAT_MEDIUM);
      }

      // Click Sign.
      const sign = page.getByRole('button', { name: /^Sign spec$/ }).first();
      if (await sign.count()) {
        await sign.scrollIntoViewIfNeeded();
        await sign.click();
        await page.getByRole('checkbox').check();
        await page.getByRole('dialog').getByRole('button', { name: /^Sign spec$/ }).click();
        await expect(page.getByTestId('evidence-block-signature')).toBeVisible({ timeout: 15_000 });
      }
      await caption(page, 'Cryptographically signed · independently verifiable evidence trail');
      if (BEAT_SHORT) await page.waitForTimeout(BEAT_SHORT);
    });

    // ── 8 · Generate .NET scaffold → land on Hybrd1Service.cs ───────
    await test.step('view translated .NET code', async () => {
      await switchPersona(page, 'engineer');
      const spec = await page.request
        .get(`${API_BASE}/api/v1/subroutines/${hybrd1SubId}/spec`)
        .then((r) => r.json());
      await page.goto(`/specs/${spec.id}/scaffold`);
      await expect(page.getByRole('button', { name: /Open scaffold/ })).toBeVisible({
        timeout: 60_000,
      });
      await page.getByRole('button', { name: /Open scaffold/ }).click();
      await expect(
        page.getByRole('heading', { name: /\d+ files · \d+ TODOs · dotnet8/ }),
      ).toBeVisible({ timeout: 30_000 });

      // Click the actual business-logic file in the tree.
      const serviceFile = page.locator('text=Hybrd1Service.cs').first();
      if (await serviceFile.count()) {
        await serviceFile.click();
        await page.waitForTimeout(1_200);
      }
      await caption(
        page,
        'Translated .NET · INV-1..6 mapped 1:1 to C# guards · cited per claim · zero stubs',
      );
      if (BEAT_LONG) await page.waitForTimeout(BEAT_LONG);
    });

    // ── 9 · Validate compile ─────────────────────────────────────────
    await test.step('validate compile', async () => {
      const validate = page.getByRole('button', { name: /^Validate compile/i });
      if (await validate.count()) {
        await validate.first().click();
        await page.waitForTimeout(BEAT_MEDIUM || 2_000);
      }
      await caption(page, 'Compile gate · independent validation of generated code');
      if (BEAT_SHORT) await page.waitForTimeout(BEAT_SHORT);
    });

    // ── 10–16 · Teaser glimpses of platform intelligence ─────────────
    await test.step('teaser · Golden Dataset (LLM calibration)', async () => {
      await switchPersona(page, 'admin');
      await page.goto('/platform/golden-dataset');
      await page.waitForLoadState('networkidle');
      await caption(
        page,
        'Golden Dataset · Andrew-Ng-style edge-case examples that calibrate the extraction LLM',
      );
      if (BEAT_TEASER) await page.waitForTimeout(BEAT_TEASER);
    });

    await test.step('teaser · Portfolio Dashboard', async () => {
      await page.goto('/platform/portfolio');
      await page.waitForLoadState('networkidle');
      await caption(page, 'Portfolio rollup · every corpus, every wave, every LLM dollar');
      if (BEAT_TEASER) await page.waitForTimeout(BEAT_TEASER);
    });

    await test.step('teaser · Signature Health', async () => {
      await page.goto('/platform/signatures');
      await page.waitForLoadState('networkidle');
      await caption(page, 'Signature health · zero silent drift when source changes');
      if (BEAT_TEASER) await page.waitForTimeout(BEAT_TEASER);
    });

    await test.step('teaser · Validation Policy', async () => {
      await page.goto('/platform/validation');
      await page.waitForLoadState('networkidle');
      await caption(page, 'Three independent validation gates · compile · tests · equivalence');
      if (BEAT_TEASER) await page.waitForTimeout(BEAT_TEASER);
    });

    await test.step('teaser · Compliance Feed', async () => {
      await page.goto('/compliance');
      await page.waitForLoadState('networkidle');
      await caption(page, 'SOX · HIPAA · PCI evidence export · everything an auditor needs');
      if (BEAT_TEASER) await page.waitForTimeout(BEAT_TEASER);
    });

    // ── 17 · Close ──────────────────────────────────────────────────
    await test.step('close · home', async () => {
      await page.goto('/');
      await caption(
        page,
        'Ingest · plan · migrate · certify — a portfolio modernization platform for Fortran & COBOL',
      );
      if (BEAT_SHORT) await page.waitForTimeout(BEAT_SHORT);
    });
  });
});
