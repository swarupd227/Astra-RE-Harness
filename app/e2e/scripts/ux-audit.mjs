/**
 * UX audit — visits every page in both themes at desktop width and in dark at
 * phone width, and records what can be measured instead of eyeballed:
 *
 *   overflow   the page scrolls sideways (or something spills out of <main>)
 *   console    errors the page logged
 *   axe        serious/critical accessibility violations (axe-core, WCAG 2 A/AA)
 *   lightInDark  large light surfaces in the dark theme (a token escape)
 *
 * plus a screenshot per run. Run against a local stack:
 *
 *   npm install --no-save axe-core          (not a repo dependency)
 *   BASE_URL=http://localhost:35173 API_BASE=http://localhost:38080 \
 *     node scripts/ux-audit.mjs [outDir]
 *
 * Known noise: Monaco's lazily loaded editor layer (an absolutely positioned,
 * ~16-million-px-wide dark box) can intermittently be counted by axe's colour
 * check as sitting under unrelated text (seen on the routine page's target
 * cards, roughly one run in three there). The rendered colours are fine; re-run
 * that page before treating a lone colour-contrast hit that names
 * `.monaco-editor-background` as real.
 *
 * The two pages that start a run the moment they mount (live extraction, live
 * scaffold generation) have their request aborted, so nothing is generated.
 * Everything else is read-only.
 */
import { chromium } from '@playwright/test';
import { createRequire } from 'node:module';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const BASE_URL = process.env.BASE_URL ?? 'http://localhost:35173';
const API_BASE = process.env.API_BASE ?? 'http://localhost:38080';
const OUT = process.argv[2] ?? join(process.cwd(), 'ux-audit-out');
mkdirSync(OUT, { recursive: true });

const require = createRequire(import.meta.url);
let axePath;
try {
  axePath = require.resolve('axe-core/axe.min.js');
} catch {
  console.error('axe-core is not installed. Run: npm install --no-save axe-core');
  process.exit(1);
}

const get = async (path) => (await fetch(`${API_BASE}${path}`, { headers: { 'X-Dev-Persona': 'admin' } })).json();

// ── Ids the routes need, discovered from the running API ──────────────────
const corpora = (await get('/api/v1/corpora')).data;
const corpus = corpora.find((c) => c.name.includes('MINPACK')) ?? corpora[0];
const scaffolds = (await get(`/api/v1/scaffolds?corpusId=${corpus.id}`)).data;
const sc = scaffolds[0];
if (!sc) throw new Error('need at least one scaffold in the local data');
const thread = (await get(`/api/v1/conversations?corpusId=${corpus.id}`)).data[0].id;
const c = corpus.id;

const ROUTES = [
  ['workspace', '/'],
  ['programme-thread', `/w/${thread}`],
  ['home', '/home'],
  ['system', '/system'],
  ['projects', '/projects'],
  ['projects-new', '/projects/new'],
  ['project', `/projects/${c}`],
  ['dependency-graph', `/corpora/${c}/dependency-graph`],
  ['migration-plan', `/corpora/${c}/migration-plan`],
  ['flow-board', `/corpora/${c}/board`],
  ['docs', `/corpora/${c}/docs`],
  ['pattern-analysis', `/corpora/${c}/pattern-analysis`],
  ['assessment', `/corpora/${c}/assessment`],
  ['routines', '/subroutines'],
  ['routine', `/subroutines/${sc.subroutineId}`],
  ['draft-spec', `/subroutines/${sc.subroutineId}/spec`],
  ['spec-review', `/subroutines/${sc.subroutineId}/review`],
  ['audit-trail', `/specs/${sc.specId}/audit`],
  ['scaffolds', '/scaffolds'],
  ['scaffold-artifact', `/scaffolds/${sc.id}`],
  ['validation-report', `/scaffolds/${sc.id}/validation`],
  ['my-reviews', '/my-reviews'],
  ['comments', '/comments'],
  ['compliance', '/compliance'],
  ['platform', '/platform'],
  ['portfolio', '/platform/portfolio'],
  ['golden-dataset', '/platform/golden-dataset'],
  ['prompt-catalog', '/platform/prompts'],
  ['harmonisation', '/platform/harmonisation'],
  ['languages', '/platform/languages'],
  ['signature-health', '/platform/signatures'],
  ['validation-policy', '/platform/validation'],
  ['roles', '/platform/roles'],
  ['llm-settings', '/platform/llm'],
  ['not-found', '/this-page-does-not-exist'],
  // These two stream a run on mount — the request is aborted (see below).
  ['live-extraction', `/subroutines/${sc.subroutineId}/extract`],
  ['live-scaffold', `/specs/${sc.specId}/scaffold`],
];

const RUNS = [
  { theme: 'dark', vp: 'desktop', size: { width: 1440, height: 900 } },
  { theme: 'light', vp: 'desktop', size: { width: 1440, height: 900 } },
  { theme: 'dark', vp: 'phone', size: { width: 390, height: 844 } },
];

// ── What runs inside the page ─────────────────────────────────────────────
const measure = () => {
  const vw = window.innerWidth;
  const main = document.querySelector('main#main-content');
  const scroller = document.scrollingElement;
  const overflowPx = Math.max(
    scroller ? scroller.scrollWidth - vw : 0,
    main ? main.scrollWidth - main.clientWidth : 0,
  );

  const clipped = (el, stop) => {
    for (let p = el.parentElement; p && p !== stop; p = p.parentElement) {
      const o = getComputedStyle(p).overflowX;
      if (o === 'auto' || o === 'scroll' || o === 'hidden') return true;
    }
    return false;
  };
  const label = (el) =>
    `${el.tagName.toLowerCase()}${el.id ? '#' + el.id : ''}${
      typeof el.className === 'string' && el.className ? '.' + el.className.trim().split(/\s+/).slice(0, 3).join('.') : ''
    }`;

  const offenders = [];
  if (overflowPx > 1) {
    const edge = main ? main.getBoundingClientRect().right : vw;
    for (const el of document.querySelectorAll('main *')) {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0 || r.right <= edge + 1 || clipped(el, main)) continue;
      offenders.push({ el: label(el), over: Math.round(r.right - edge) });
    }
    offenders.sort((a, b) => b.over - a.over);
  }

  const lum = (rgb) => {
    const f = (v) => {
      v /= 255;
      return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * f(rgb[0]) + 0.7152 * f(rgb[1]) + 0.0722 * f(rgb[2]);
  };
  const lightSurfaces = [];
  if (document.documentElement.getAttribute('data-theme') === 'dark') {
    for (const el of document.querySelectorAll('body *')) {
      if (el.closest('.monaco-editor, canvas, svg, img, video')) continue;
      const cs = getComputedStyle(el);
      const m = cs.backgroundColor.match(/rgba?\(([^)]+)\)/);
      if (!m) continue;
      const [r, g, b, a = 1] = m[1].split(/[ ,/]+/).filter(Boolean).map(Number);
      if (a < 0.6 || lum([r, g, b]) < 0.6) continue;
      // A saturated fill (the volt accent, a status colour) is deliberate, not a white panel that escaped the tokens.
      if (Math.max(r, g, b) - Math.min(r, g, b) > 100) continue;
      const rect = el.getBoundingClientRect();
      if (rect.width * rect.height < 3000 || rect.bottom < 0 || rect.top > window.innerHeight) continue;
      lightSurfaces.push({ el: label(el), bg: cs.backgroundColor, area: Math.round(rect.width * rect.height) });
    }
    lightSurfaces.sort((a, b) => b.area - a.area);
  }

  return { overflowPx, offenders: offenders.slice(0, 3), lightSurfaces: lightSurfaces.slice(0, 3), lightCount: lightSurfaces.length };
};

// ── Run ───────────────────────────────────────────────────────────────────
const browser = await chromium.launch();
const results = [];

for (const run of RUNS) {
  const ctx = await browser.newContext({ viewport: run.size });
  await ctx.addInitScript(([theme]) => {
    localStorage.setItem('astra.theme', theme);
    localStorage.setItem('astra.devPersona', 'admin');
    localStorage.setItem('astra.agentDock', 'closed');
  }, [run.theme]);
  // Never let a mount start a run.
  await ctx.route('**/api/v1/specs/*/scaffold*', (r) => r.abort());
  await ctx.route('**/api/v1/subroutines/*/extract*', (r) => r.abort());

  for (const [name, path] of ROUTES) {
    const page = await ctx.newPage();
    const errors = [];
    page.on('console', (m) => {
      // A request we aborted on purpose is not a finding.
      if (m.type() === 'error' && !/ERR_FAILED|net::ERR_ABORTED|Failed to load resource/.test(m.text())) errors.push(m.text().slice(0, 160));
    });
    page.on('pageerror', (e) => errors.push(`pageerror: ${String(e.message).slice(0, 160)}`));

    const row = { name, path, ...run, errors, axe: [], overflowPx: 0, offenders: [], lightSurfaces: [], lightCount: 0, failed: null };
    try {
      await page.goto(`${BASE_URL}${path}`, { waitUntil: 'load', timeout: 90_000 });
      await page.waitForTimeout(2500);
      Object.assign(row, await page.evaluate(measure));
      await page.addScriptTag({ path: axePath });
      const axe = await page.evaluate(async () =>
        window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] }, resultTypes: ['violations'] }),
      );
      row.axe = axe.violations
        .filter((v) => v.impact === 'serious' || v.impact === 'critical')
        .map((v) => ({ rule: v.id, impact: v.impact, nodes: v.nodes.length, sample: v.nodes[0]?.target?.join(' ').slice(0, 90) }));
      await page.screenshot({ path: join(OUT, `${name}__${run.theme}-${run.vp}.png`) });
    } catch (e) {
      row.failed = String(e.message).split('\n')[0].slice(0, 160);
    }
    results.push(row);
    process.stdout.write(`${run.theme}-${run.vp} ${name}${row.failed ? '  FAILED' : ''}\n`);
    await page.close();
  }
  await ctx.close();
}
await browser.close();

writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
console.log(`\nDone: ${results.length} runs → ${join(OUT, 'results.json')}`);
