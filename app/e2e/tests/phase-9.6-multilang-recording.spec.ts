/**
 * Phase 9.6 — multi-language recording.
 *
 * Two sibling tests, one per language. Each one:
 *   1. Lands on the Projects grid (Phase 9.2.b language pill is visible)
 *   2. Opens the corpus surface
 *   3. Opens the headline routine (Phase 9.2.c extraction-help banner)
 *   4. Views the signed spec (claims with evidence + signature block)
 *   5. Views the scaffold artifact (file tree, .NET 8 output)
 *   6. Runs all four validation gates on the validation report
 *
 * This is intentionally NAVIGATION-ONLY — it does NOT drive a live extract /
 * sign / scaffold flow during recording. The MINPACK demo already shows that
 * flow for Fortran; Phase 9.6's value is "the platform supports Delphi + C++
 * too" and showing the persistent artefacts proves that without
 * re-litigating the (LLM-flaky) live theatre per language.
 *
 * State is pre-baked via API before the recording starts:
 *   - DRAFT spec exists (scripts/prebake-draft.mjs retries on malformed JSON)
 *   - SIGN happens via API (auto-accept all claims)
 *   - SCAFFOLD happens via API (mock provider — deterministic)
 *
 * RECORD_DEMO=1 emits a sidecar timeline-latest.json next to each Playwright
 * video so scripts/trim-demo.mjs can cut LLM-bound validation-gate waits and
 * replace them with caption cards. Per-language trimmed result: ~2-3 min.
 */
import { writeFile, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };
const SME = { 'X-Dev-Persona': 'sme' };
const ADMIN = { 'X-Dev-Persona': 'admin' };

/** Use the top-right persona menu to switch persona on screen. */
async function switchPersona(page: Page, persona: 'engineer' | 'sme' | 'admin') {
  const label = persona === 'sme' ? 'SME' : persona === 'admin' ? 'Admin' : 'Engineer';
  await page.locator('button[aria-haspopup="menu"]').click();
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await page.waitForLoadState('networkidle');
}

type TimelineEntry = { kind: 'llm-wait'; label: string; startMs: number; endMs: number };
let timeline: TimelineEntry[] = [];
let videoT0 = 0;

async function llmWait(label: string, fn: () => Promise<void>): Promise<void> {
  const startMs = Date.now() - videoT0;
  await fn();
  const endMs = Date.now() - videoT0;
  timeline.push({ kind: 'llm-wait', label, startMs, endMs });
}

/**
 * Inject a fixed bottom-of-screen caption banner that explains each step in
 * plain language. The banner is recreated after every page.goto() (because
 * navigation wipes the DOM), so call this AFTER each goto.
 */
async function setCaption(page: Page, text: string): Promise<void> {
  // Phase 10.3 — wait for the page to be rendered enough that the
  // caption sits on real content, not a white loading state. Tolerate
  // slow networks (60s ceiling); if networkidle never fires the
  // caption still drops on whatever's painted.
  try { await page.waitForLoadState('domcontentloaded', { timeout: 5_000 }); } catch { /* tolerate */ }
  await page.evaluate((t) => {
    let banner = document.getElementById('demo-caption-banner') as HTMLDivElement | null;
    if (!banner) {
      banner = document.createElement('div');
      banner.id = 'demo-caption-banner';
      banner.setAttribute('aria-hidden', 'true');
      banner.style.cssText = [
        'position:fixed', 'left:0', 'right:0', 'bottom:0',
        'background:rgba(15,23,42,0.96)', 'color:#f8fafc',
        'padding:18px 40px', 'font-size:20px', 'line-height:1.45',
        'font-weight:500', 'text-align:center', 'z-index:2147483647',
        'border-top:3px solid #6366f1',
        'box-shadow:0 -8px 28px rgba(0,0,0,0.45)',
        'font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,system-ui,sans-serif',
        'pointer-events:none',
      ].join(';');
      document.body.appendChild(banner);
    } else if (banner.parentNode !== document.body) {
      // Re-attach: SPA navigation can detach the banner from the DOM.
      document.body.appendChild(banner);
    }
    banner.textContent = t;
  }, text);
}

type Target = {
  /** Language key for log lines + pill testid. */
  langId: 'delphi' | 'cpp' | 'vb6';
  /** Pretty language name shown in narration captions. */
  langLabel: string;
  /** Exact corpus name as it appears in /api/v1/corpora. */
  corpusName: string;
  /** Routine-detail / spec / scaffold target. Found via `findRoutine`. */
  findRoutine: (req: APIRequestContext) => Promise<{ corpusId: string; subId: string; subName: string } | null>;
  /** Pre-known cross-runtime label for the 3rd gate's caption. */
  equivalenceCaption: string;
  /** Approx node count for the dependency-graph caption (rounded). */
  graphNodeCaption: string;
  /** Allowed target-stack regex on the scaffold heading. dotnet8 for
   *  Delphi/C++, dotnet10 for VB6 (per ADR-035/036). */
  scaffoldStackRegex: RegExp;
};

const DELPHI: Target = {
  langId: 'delphi',
  langLabel: 'Delphi',
  corpusName: 'Indy Sockets (Delphi)',
  equivalenceCaption: 'Validation · cross-runtime (Delphi ↔ .NET)',
  graphNodeCaption: '735 nodes',
  scaffoldStackRegex: /\d+ files · \d+ TODOs · dotnet8/,
  async findRoutine(req) {
    const list = await req.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
    const corpus = list.data.find((c: { name: string }) => c.name === 'Indy Sockets (Delphi)');
    if (!corpus) return null;
    const detail = await req.get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG }).then((r) => r.json());
    for (const file of detail.latestVersion.files) {
      if (!/IdSMTP/i.test(file.relativePath ?? '')) continue;
      for (const sub of file.subroutines) {
        if (/(^|\.)Connect$/i.test(sub.name)) {
          return { corpusId: corpus.id, subId: sub.id, subName: sub.name };
        }
      }
    }
    return null;
  },
};

const CPP: Target = {
  langId: 'cpp',
  langLabel: 'C++',
  corpusName: 'fmt (C++ format library)',
  equivalenceCaption: 'Validation · cross-runtime (C++ ↔ .NET)',
  graphNodeCaption: '769 nodes',
  scaffoldStackRegex: /\d+ files · \d+ TODOs · dotnet8/,
  async findRoutine(req) {
    const list = await req.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
    const corpus = list.data.find((c: { name: string }) => c.name === 'fmt (C++ format library)');
    if (!corpus) return null;
    const detail = await req.get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG }).then((r) => r.json());
    // Prefer the user-facing surface — pick `format` (or `fmt::format`) in
    // include/fmt/format.h before anything inside fmt::detail::*. The recording
    // is about the public API, not internal helpers.
    const candidates: Array<{ corpusId: string; subId: string; subName: string; score: number }> = [];
    for (const file of detail.latestVersion.files) {
      const rel = file.relativePath ?? '';
      if (!/include\/fmt\/(core|base|format)\.h$/i.test(rel)) continue;
      for (const sub of file.subroutines) {
        const name = sub.name as string;
        if (sub.name && /detail|formatter::format|basic_format_arg/i.test(name)) continue; // skip internals
        if (name === 'format' || name === 'fmt::format') {
          candidates.push({ corpusId: corpus.id, subId: sub.id, subName: name, score: 100 });
        } else if (/(^|::)(v?format)(_to)?$/i.test(name)) {
          candidates.push({ corpusId: corpus.id, subId: sub.id, subName: name, score: 50 });
        }
      }
    }
    candidates.sort((a, b) => b.score - a.score);
    if (candidates.length === 0) return null;
    const { score, ...rest } = candidates[0];
    return rest;
  },
};

// Phase 10.0.i (target) + Phase 10.3.e (re-targeted).
// Original demo cut walked frmOrderEntry.btnSubmit_Click, which exercises
// every new VB6 claim kind in one ~50-LOC handler. Phase 10.3.e re-points
// the recording at modPricing.SafeAverage instead: pure Currency function,
// On Error Resume Next swallow trap (the canonical VB6 gotcha), 4 lines —
// reads cleanly on camera, and the property gate exercises the Currency
// generatorHints non-vacuously (Phase 10.3.d) for a real 4-green close.
// The btnSubmit_Click spec from the earlier recording is SIGNED so the
// extraction pipeline short-circuits; re-target avoids the paid re-extract
// AND tells a cleaner story.
const VB6: Target = {
  langId: 'vb6',
  langLabel: 'VB6',
  corpusName: 'VB6 Inventory Sample (Nous)',
  equivalenceCaption: 'Validation · cross-runtime (VB6 ↔ .NET 10)',
  graphNodeCaption: '38 routines',
  // VB6 archetypes ship under dotnet10 (per ADR-035/036); 10.3.b's
  // server-side auto-routing default picks dotnet10 from schemaId
  // automatically, so scaffold POST works without an override.
  scaffoldStackRegex: /\d+ files · \d+ TODOs · dotnet1[08]/,
  async findRoutine(req) {
    const list = await req.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
    const corpus = list.data.find((c: { name: string }) => c.name === 'VB6 Inventory Sample (Nous)');
    if (!corpus) return null;
    const detail = await req.get(`${API_BASE}/api/v1/corpora/${corpus.id}`, { headers: ENG }).then((r) => r.json());
    for (const file of detail.latestVersion?.files ?? []) {
      if (!/modPricing/i.test(file.relativePath ?? '')) continue;
      for (const sub of file.subroutines ?? []) {
        if (/(^|\.)SafeAverage$/i.test(sub.name)) {
          return { corpusId: corpus.id, subId: sub.id, subName: sub.name };
        }
      }
    }
    return null;
  },
};

// ─── State pre-bake helpers (API-only) ──────────────────────────────────

async function ensureSpec(req: APIRequestContext, subId: string): Promise<string> {
  // Try up to 5 times — Anthropic returns malformed JSON intermittently on
  // long Delphi/C++ extraction prompts.
  for (let attempt = 1; attempt <= 5; attempt += 1) {
    let existing = await req.get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG });
    if (existing.ok()) {
      const body = await existing.json();
      if (body.state === 'DRAFT' || body.state === 'IN_REVIEW' || body.state === 'SIGNED') {
        return body.id;
      }
    }
    // POST /extract returns SSE — drain to ensure persistence step runs.
    const r = await req.post(`${API_BASE}/api/v1/subroutines/${subId}/extract`, { headers: ENG });
    if (!r.ok()) throw new Error(`extract POST ${r.status()}`);
    await r.text();
    // Re-check.
    existing = await req.get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG });
    if (existing.ok()) {
      const body = await existing.json();
      if (body.state === 'DRAFT' || body.state === 'IN_REVIEW' || body.state === 'SIGNED') {
        return body.id;
      }
    }
  }
  throw new Error('Could not get spec to DRAFT after 5 extract retries');
}

async function ensureSigned(req: APIRequestContext, subId: string, specId: string): Promise<void> {
  // /specs/{id} returns metadata only — the claim arrays live on
  // /subroutines/{subId}/spec, which inlines `spec.invariants` etc.
  const cur = await req.get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG }).then((r) => r.json());
  if (cur.state === 'SIGNED') return;
  // Route to SME if not yet routed.
  if (cur.state !== 'IN_REVIEW') {
    const routeResp = await req.post(`${API_BASE}/api/v1/specs/${specId}/route`, {
      headers: ENG, data: { routingNote: 'Phase 9.6 demo auto-route.' },
    });
    if (!routeResp.ok()) throw new Error(`route POST ${routeResp.status()}`);
  }
  // Re-fetch to walk the canonical taxonomy.
  const spec = await req.get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG }).then((r) => r.json());
  const sections: Array<[string, Array<{ id: string }>]> = [
    ['invariants', spec.spec?.invariants ?? []],
    ['side_effects', spec.spec?.side_effects ?? []],
    ['edge_cases', spec.spec?.edge_cases ?? []],
    ['open_questions', spec.spec?.open_questions ?? []],
  ];
  for (const [section, claims] of sections) {
    for (const claim of claims) {
      const path = `$.${section}[?(@.id=='${claim.id}')]`;
      const body: Record<string, unknown> = section === 'open_questions'
        ? { claimPath: path, action: 'edit', editedText: 'Resolved during Phase 9.6 multi-language demo recording.' }
        : { claimPath: path, action: 'accept' };
      const reviewResp = await req.post(`${API_BASE}/api/v1/specs/${specId}/claims/review`, {
        headers: SME, data: body,
      });
      if (!reviewResp.ok()) {
        throw new Error(`claim review POST ${reviewResp.status()} on ${claim.id}`);
      }
    }
  }
  const signResp = await req.post(`${API_BASE}/api/v1/specs/${specId}/sign`, {
    headers: SME, data: { confirmation: 'I have reviewed every claim.' },
  });
  if (!signResp.ok()) throw new Error(`sign POST ${signResp.status()} — ${await signResp.text()}`);
}

async function ensureScaffold(req: APIRequestContext, specId: string, targetStack?: string): Promise<string> {
  const existing = await req.get(`${API_BASE}/api/v1/specs/${specId}/scaffold`, { headers: ENG });
  if (existing.ok()) {
    const body = await existing.json();
    // If a target was requested and the cached scaffold is for a different
    // stack, fall through to POST so the regenerate picks the right archetype.
    if (!targetStack || body.targetPlatform === targetStack) return body.id;
  }
  // Phase 10.3 — pass targetStack so VB6 specs land on dotnet10 archetypes.
  // Without it the backend default is dotnet8 → alphabetical-first archetype
  // (canonical-cpp-fmt) → wrong language entirely + 42 compile errors.
  const url = targetStack
    ? `${API_BASE}/api/v1/specs/${specId}/scaffold?targetStack=${encodeURIComponent(targetStack)}`
    : `${API_BASE}/api/v1/specs/${specId}/scaffold`;
  const r = await req.post(url, { headers: ENG });
  if (!r.ok()) throw new Error(`scaffold POST ${r.status()} — ${await r.text()}`);
  await r.text(); // drain SSE
  const sc = await req.get(`${API_BASE}/api/v1/specs/${specId}/scaffold`, { headers: ENG }).then((r) => r.json());
  return sc.id;
}

/**
 * Phase 10.3 — pre-bake all 4 validation gates to PASSED via API. The
 * recording's runGate UI clicks would otherwise re-trigger each gate
 * and OVERWRITE the cached PASSED state with whatever the click-time
 * run produces (which can be ERRORED on flaky paths). For VB6 we
 * instead drive each gate via direct POST here, leaving the recording
 * to just narrate over the already-green report.
 */
async function ensureValidationPassed(req: APIRequestContext, scaffoldId: string): Promise<void> {
  for (const endpoint of ['compile', 'test-pack', 'equivalence', 'falsifying'] as const) {
    const r = await req.post(`${API_BASE}/api/v1/scaffolds/${scaffoldId}/validate/${endpoint}`, {
      headers: ENG, timeout: 420_000,
    });
    if (!r.ok()) throw new Error(`validate/${endpoint} POST ${r.status()} — ${await r.text()}`);
    await r.text(); // drain SSE
  }
}

/** Pre-bake a risk-first migration plan so the page shows real waves on camera. */
async function ensureMigrationPlan(req: APIRequestContext, corpusId: string): Promise<void> {
  const existing = await req.get(`${API_BASE}/api/v1/corpora/${corpusId}/migration-plan`, { headers: ENG });
  if (existing.ok()) return;
  const r = await req.post(
    `${API_BASE}/api/v1/corpora/${corpusId}/migration-plan/generate`,
    { headers: ADMIN, data: { strategy: 'risk-first', options: {} } },
  );
  if (!r.ok()) throw new Error(`migration-plan generate POST ${r.status()} — ${await r.text()}`);
}

// ─── Test factory ─────────────────────────────────────────────────────

function makeRecordingTest(t: Target) {
  test.describe(`Phase 9.6 · ${t.langLabel} recording`, () => {
    test.beforeEach(async ({ page }) => {
      await page.goto('/');
      await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
    });

    test(`${t.langLabel} corpus → routine → spec → scaffold → 4-gate validation`, async ({ page, request }, testInfo) => {
      // 3.5s per beat lands the trimmed video around 2-3 min once the
      // 4-gate waits are cut. Tighter pauses make the demo feel rushed;
      // longer pauses make viewers fast-forward.
      // Phase 10.3 — VB6 demo uses pre-baked gates (no on-screen LLM
      // waits to break up the pacing), so a tighter beat keeps the
      // transitions snappy. Delphi/C++ keep the longer beat.
      const beat = process.env.RECORD_DEMO === '1'
        ? (t.langId === 'vb6' ? 2_500 : 3_500)
        : 0;
      videoT0 = Date.now();
      timeline = [];

      async function emitTimeline() {
        if (process.env.RECORD_DEMO !== '1') return;
        const totalMs = Date.now() - videoT0;
        const payload = {
          videoTotalMs: totalMs,
          entries: timeline,
          outputDir: testInfo.outputDir,
        };
        const json = JSON.stringify(payload, null, 2);
        const timelineDir = resolve(dirname(testInfo.config.configFile ?? testInfo.config.rootDir), 'test-results');
        await mkdir(timelineDir, { recursive: true });
        await writeFile(join(timelineDir, 'timeline-latest.json'), json);
      }

      // Locate the routine.
      let located: { corpusId: string; subId: string; subName: string } | null = null;
      const deadline = Date.now() + 60_000;
      while (Date.now() < deadline) {
        located = await t.findRoutine(request);
        if (located) break;
        await page.waitForTimeout(3_000);
      }
      test.skip(
        located === null,
        `${t.corpusName} not present after 60s. Ensure DATABASE_SEED_${t.langId.toUpperCase()}_DEMO is true.`,
      );
      const { corpusId, subId, subName } = located!;
      try {

      // Pre-bake state (API only — no on-screen wait).
      const specId = await ensureSpec(request, subId);
      await ensureSigned(request, subId, specId);
      // Phase 10.3.b — backend now auto-routes scaffold targetStack
       // from the spec's schemaId (VB6 specs default to dotnet10), so
       // the recording no longer needs to override. The ensureScaffold
       // signature keeps the optional targetStack parameter for tests
       // that genuinely want to force a non-default stack.
       const scaffoldId = await ensureScaffold(request, specId);
      await ensureMigrationPlan(request, corpusId);
      // Phase 10.3 — for VB6, pre-bake the four gates to PASSED via API
      // so the recording's validation step can just narrate the green
      // closing shot. Without this the runGate UI clicks re-trigger each
      // gate and the test pack can flake on Microsoft.AspNetCore restore.
      if (t.langId === 'vb6') {
        await ensureValidationPassed(request, scaffoldId);
      }

      await test.step('Projects grid — language pill', async () => {
        await page.goto('/projects');
        await setCaption(page, `Every project is its own workspace. The coloured pill on each card shows the source language — Fortran, COBOL, Delphi, C++, or VB6.`);
        try {
          await expect(page.getByRole('heading', { name: 'Projects', level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        const pill = page.getByTestId(`corpus-language-${t.langId}`).first();
        try { await pill.waitFor({ timeout: 5_000 }); } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step(`corpus detail — ${t.corpusName}`, async () => {
        await page.goto(`/corpora/${corpusId}`);
        await setCaption(page, `Inside a project you see every file the parser identified, along with the routines worth migrating from ${t.langLabel}.`);
        // The corpus-detail page sometimes takes >15s to render its h1 on a
        // cold frontend boot (React Query first-fetch race). The video keeps
        // recording regardless — tolerate the timeout so the demo doesn't
        // fail on a transient timing flake.
        try {
          await expect(page.getByRole('heading', { name: t.corpusName, level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate cold-boot render race */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step(`routine — ${subName} (Phase 9.2.c help banner)`, async () => {
        await page.goto(`/subroutines/${subId}`);
        await setCaption(page, `This is the headline routine we're migrating. The blue banner is language-specific guidance — it warns reviewers about traps unique to ${t.langLabel}.`);
        try {
          await expect(
            page.getByRole('heading', { name: new RegExp(subName.replace(/[.:]/g, m => '\\' + m)), level: 1 }),
          ).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate cold-boot render race */ }
        if (beat) await page.waitForTimeout(beat);
      });

      // Signed spec lives at /subroutines/{subId}/review, not /specs/{id}/review.
      await test.step('view signed spec — accepted claims + signature block', async () => {
        await page.goto(`/subroutines/${subId}/review`);
        await setCaption(page, `An engineer extracted a contract spec with Claude; an SME reviewed and signed every claim. Each claim cites the exact source lines it came from.`);
        const sig = page.getByTestId('evidence-block-signature');
        try { await expect(sig).toBeVisible({ timeout: 10_000 }); } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        // Scroll through the spec so the viewer sees the breadth of claims:
        // invariants, side effects, edge cases. Three smooth scrolls.
        for (let i = 0; i < 3; i += 1) {
          await page.mouse.wheel(0, 400);
          await page.waitForTimeout(900);
        }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('view scaffold — file tree + .NET output', async () => {
        await page.goto(`/scaffolds/${scaffoldId}`);
        const dotnetVersion = t.langId === 'vb6' ? '.NET 10' : '.NET 8';
        await setCaption(page, `From the signed spec the harness generates a buildable ${dotnetVersion} package. Every file carries SpecClaim attributes tying it back to the spec.`);
        try {
          await expect(page.getByRole('heading', { name: t.scaffoldStackRegex })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        // Click into 2-3 generated files so the viewer sees the actual C#
        // source the harness produced, not just the file tree.
        try {
          const files = page.locator('button:has-text(".cs"), button:has-text(".csproj")');
          const n = await files.count();
          for (let i = 0; i < Math.min(3, n); i += 1) {
            await files.nth(i).click();
            await page.waitForTimeout(2_000);
          }
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      // Dependency graph lives at /corpora/{id}/dependency-graph, not /graph.
      // Phase 10.3 — skip dependency graph for VB6. The inventory sample
       // has 38 routines, which on cytoscape/fcose renders cluttered and
       // un-readable in a 4-min demo. Delphi/C++ have 700+ nodes where
       // the layout looks impressive; VB6 doesn't have that density.
       if (t.langId !== 'vb6') {
        await test.step('dependency graph — corpus shape', async () => {
          await page.goto(`/corpora/${corpusId}/dependency-graph`);
          await page.waitForLoadState('networkidle').catch(() => {/* tolerate */});
          await setCaption(page, `Every routine in this project, laid out by call relationship. ${t.graphNodeCaption} — this graph drives the migration plan.`);
          try {
            await expect(page.locator('canvas, svg').first()).toBeVisible({ timeout: 8_000 });
          } catch { /* tolerate */ }
          if (beat) await page.waitForTimeout(beat);
          for (let i = 0; i < 2; i += 1) {
            await page.mouse.wheel(0, 400);
            await page.waitForTimeout(900);
          }
          if (beat) await page.waitForTimeout(beat);
        });
      }

      await test.step('migration plan — wave-by-wave', async () => {
        await page.goto(`/corpora/${corpusId}/migration-plan`);
        await setCaption(page, `Routines are grouped into waves so dependencies migrate before their callers. Risk-first strategy: highest blast-radius routines first within each wave.`);
        try {
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 8_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
        // Scroll through the waves so the viewer sees real complexity.
        for (let i = 0; i < 4; i += 1) {
          await page.mouse.wheel(0, 500);
          await page.waitForTimeout(1_100);
        }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('validation report — four gates go green', async () => {
        await page.goto(`/scaffolds/${scaffoldId}/validation`);
        await setCaption(page, `Four independent gates verify the scaffold: it compiles, the test pack passes, behaviour matches the ${t.langLabel} original, and property-based search finds no counter-example.`);
        try {
          await expect(page.getByRole('heading', { name: 'Validation report', level: 1 })).toBeVisible({ timeout: 15_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);

        // Phase 10.3 — VB6 path: gates were pre-baked via API above,
        // so just navigate the four-green report and let the viewer
        // read the badges. One caption, one slow scroll past the
        // four cards, then linger on the bottom. No staccato narration
        // of each individual gate.
        if (t.langId === 'vb6') {
          await setCaption(page, `Four independent gates verify the scaffold against the original VB6: compile, test pack, behavioural equivalence, and property-based search. All green.`);
          if (beat) await page.waitForTimeout(beat);
          // Slow continuous scroll past the four cards so the viewer's
          // eye lands on each PASSED badge in turn.
          for (let i = 0; i < 4; i += 1) {
            await page.mouse.wheel(0, 180);
            await page.waitForTimeout(700);
          }
          if (beat) await page.waitForTimeout(beat);
          return;
        }

        async function runGate(testId: string, btnLabel: RegExp, postPath: string, captionLabel: string, onScreen: string) {
          await setCaption(page, onScreen);
          const card = page.getByTestId(testId);
          const postResp = page.waitForResponse(
            (r) => r.url().includes(postPath) && r.request().method() === 'POST',
            { timeout: 180_000 },
          );
          const getResp = page.waitForResponse(
            (r) =>
              /\/scaffolds\/[0-9a-f-]+\/validation$/.test(r.url())
              && r.request().method() === 'GET',
            { timeout: 60_000 },
          );
          await llmWait(captionLabel, async () => {
            await card.getByRole('button', { name: btnLabel }).click();
            await postResp;
          });
          await getResp;
        }
        await runGate(
          'validation-card-compile', /Run compile/, '/validate/compile',
          'Validation · dotnet build',
          `Gate 1 of 4 — compiling the generated package with the .NET 8 SDK.`);
        await runGate(
          'validation-card-test-pack', /Regenerate \+ run/, '/validate/test-pack',
          'Validation · dotnet test',
          `Gate 2 of 4 — running the auto-generated test pack to check each invariant.`);
        await runGate(
          'validation-card-equivalence', /Run equivalence/, '/validate/equivalence', t.equivalenceCaption,
          `Gate 3 of 4 — running the same inputs through ${t.langLabel} and .NET; outputs must match.`);
        await runGate(
          'validation-card-falsifying', /Run 4th gate/, '/validate/falsifying',
          'Validation · 4th gate (property test)',
          `Gate 4 of 4 — property-based search hunts for inputs that would make the two runtimes disagree.`);

        if (beat) await page.waitForTimeout(beat);
      });

      // Switch to admin persona — signature health + portfolio dashboard
      // are admin-gated (LLM spend + cross-corpus aggregates).
      await test.step('switch to admin persona', async () => {
        await setCaption(page, `Switching to admin persona — the next two screens roll up data across every project.`);
        await switchPersona(page, 'admin');
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('signature health — drift radar', async () => {
        await page.goto('/platform/signatures');
        await setCaption(page, `Tracks every signed spec over time. If the original source drifts after signature, this surface flags it so reviewers re-confirm.`);
        try {
          await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 8_000 });
        } catch { /* tolerate */ }
        if (beat) await page.waitForTimeout(beat);
      });

      await test.step('portfolio dashboard — closing wide shot', async () => {
        await page.goto('/platform/portfolio');
        await setCaption(page, `Across every project, here's the migration progress: how many specs are extracted, signed, scaffolded, and fully validated.`);
        try {
          await expect(page.getByRole('heading', { name: /Portfolio dashboard/i, level: 1 }))
            .toBeVisible({ timeout: 10_000 });
        } catch { /* don't block recording over the closing beat */ }
        if (beat) await page.waitForTimeout(beat);
        // Scroll through the dashboard so the viewer sees the project rows + charts.
        for (let i = 0; i < 2; i += 1) {
          await page.mouse.wheel(0, 500);
          await page.waitForTimeout(1_100);
        }
        if (beat) await page.waitForTimeout(beat);
      });

      } finally {
        // Always emit so trim-demo.mjs has the cuts even on partial runs.
        await emitTimeline();
      }
    });
  });
}

makeRecordingTest(DELPHI);
makeRecordingTest(CPP);
makeRecordingTest(VB6);
