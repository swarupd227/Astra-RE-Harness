#!/usr/bin/env node
/**
 * Pre-bake the golden demo.
 *
 * The golden demo is typed into the programme's thread, not clicked, and it
 * must run with no live waits a viewer would notice. Everything slow or
 * expensive is therefore done here, once, through the same endpoints the
 * agents use, so the recorder's sentences all land on ready state:
 *
 *   1. the programme exists and has a thread
 *   2. pattern analysis (survey + cluster) has SUCCEEDED
 *   3. one "hero" routine has a SIGNED spec (extract → route → accept all → sign)
 *   4. that routine has generated code and a PASSED compile gate
 *   5. a migration plan exists
 *   6. an assessment exists
 *
 * Idempotent: every step checks before it acts. Writes
 * e2e/generated/golden.json for the recorder (corpus, thread, hero, spec).
 *
 * Usage:
 *   API_BASE=http://127.0.0.1:38080 node scripts/prebake-golden.mjs [--corpus oatpp] [--hero <name|id>]
 */
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const args = Object.fromEntries(process.argv.slice(2).map((a, i, all) => (a.startsWith('--') ? [a.slice(2), all[i + 1]] : [])).filter((e) => e.length));
const corpusHint = args.corpus ?? 'oatpp';
const heroHint = args.hero;

const H = (persona) => ({ 'X-Dev-Persona': persona, 'Content-Type': 'application/json' });
const log = (m) => console.log(`[prebake] ${m}`);

async function get(path, persona = 'admin') {
  const r = await fetch(`${API_BASE}${path}`, { headers: H(persona) });
  if (r.status === 404) return null;
  if (!r.ok) throw new Error(`GET ${path} → ${r.status} ${(await r.text()).slice(0, 200)}`);
  return r.json();
}
async function post(path, body, persona = 'admin') {
  const r = await fetch(`${API_BASE}${path}`, { method: 'POST', headers: H(persona), body: JSON.stringify(body ?? {}) });
  const text = await r.text();
  if (!r.ok) throw new Error(`POST ${path} → ${r.status} ${text.slice(0, 300)}`);
  return text ? JSON.parse(text) : null;
}
/** POST an SSE route and drain it; returns the last `done` payload if any. */
async function sse(path, persona) {
  const r = await fetch(`${API_BASE}${path}`, { method: 'POST', headers: { 'X-Dev-Persona': persona, Accept: 'text/event-stream' } });
  if (!r.ok) throw new Error(`POST ${path} → ${r.status} ${(await r.text()).slice(0, 300)}`);
  const text = await r.text();
  const done = [...text.matchAll(/event: done\s+data: (\{[^\n]*\})/g)].pop();
  const error = [...text.matchAll(/event: error\s+data: (\{[^\n]*\})/g)].pop();
  if (error && !done) throw new Error(`${path} ended with error: ${error[1].slice(0, 300)}`);
  return done ? JSON.parse(done[1]) : null;
}
async function until(label, probe, { every = 5_000, max = 30 * 60_000 } = {}) {
  const start = Date.now();
  for (;;) {
    const v = await probe();
    if (v) return v;
    if (Date.now() - start > max) throw new Error(`${label}: gave up after ${Math.round(max / 1000)}s`);
    await sleep(every);
  }
}

// 1. programme + thread
const corpora = (await get('/api/v1/corpora')).data ?? [];
const corpus = corpora.find((c) => c.id === corpusHint) ?? corpora.find((c) => c.name.toLowerCase().includes(corpusHint.toLowerCase()));
if (!corpus) throw new Error(`No corpus matching "${corpusHint}". Ingest it first (POST /api/v1/ingest/git).`);
log(`programme: ${corpus.name} (${corpus.id}) state=${corpus.state}`);
const threads = await get(`/api/v1/conversations?corpusId=${corpus.id}`);
const thread = (threads.data ?? threads).find((t) => t.kind === 'programme');
if (!thread) throw new Error('No programme thread — the API creates it on first touch; check the corpus has routines.');
log(`thread: ${thread.id} (${thread.messageCount} messages)`);

// 2. pattern analysis
const runs = (await get(`/api/v1/corpora/${corpus.id}/pattern-analysis-runs`)) ?? [];
const runList = runs.data ?? runs.runs ?? runs;
let analysed = runList.find?.((r) => r.state === 'SUCCEEDED' || r.state === 'PARTIAL');
if (!analysed) {
  log('pattern analysis: none — starting survey + cluster (minutes on a real key)');
  const started = await post(`/api/v1/corpora/${corpus.id}/pattern-analysis?stages=survey,cluster`, {}, 'admin');
  analysed = await until('pattern analysis', async () => {
    const r = await get(`/api/v1/pattern-analysis/runs/${started.runId}`);
    return r && ['SUCCEEDED', 'PARTIAL', 'FAILED', 'CANCELLED'].includes(r.state) ? r : null;
  }, { every: 10_000 });
  if (analysed.state === 'FAILED') throw new Error(`pattern analysis failed: ${analysed.errorSummary ?? analysed.summary}`);
}
log(`pattern analysis: ${analysed.state} — ${analysed.summary ?? ''}`.slice(0, 160));

// 3. hero routine with a SIGNED spec
async function pickHero() {
  if (heroHint) {
    const hits = (await get(`/api/v1/subroutines?corpus=${corpus.id}&limit=20&q=${encodeURIComponent(heroHint)}`, 'engineer')).data;
    const exact = hits.find((s) => s.id === heroHint || s.name === heroHint) ?? hits[0];
    if (!exact) throw new Error(`hero "${heroHint}" not found`);
    return exact;
  }
  // Prefer a routine that is already signed, then one already drafted, then
  // a substantial parsed routine whose name is unique in the corpus (the
  // recorder addresses it by name).
  const page = (await get(`/api/v1/subroutines?corpus=${corpus.id}&limit=500`, 'engineer')).data;
  const counts = page.reduce((m, s) => m.set(s.name, (m.get(s.name) ?? 0) + 1), new Map());
  const notFixture = (s) => !/test|fuzz|mock|example|sample|bench/i.test(`${s.name} ${s.file?.relativePath ?? ''}`);
  const unique = page
    .filter((s) => counts.get(s.name) === 1 && s.lineEnd - s.lineStart >= 15 && notFixture(s))
    .sort((a, b) => (b.lineEnd - b.lineStart) - (a.lineEnd - a.lineStart));
  return unique.find((s) => s.state === 'SIGNED' || s.state === 'SCAFFOLDED')
    ?? unique.find((s) => s.state === 'DRAFT' || s.state === 'IN_REVIEW')
    ?? unique[0];
}
const hero = await pickHero();
log(`hero routine: ${hero.name} (${hero.id}) state=${hero.state}`);

let spec = await get(`/api/v1/subroutines/${hero.id}/spec`, 'engineer');
if (!spec) {
  log('extracting a spec (mock: seconds; real key: ~1 min)');
  await sse(`/api/v1/subroutines/${hero.id}/extract`, 'engineer');
  spec = await until('spec', () => get(`/api/v1/subroutines/${hero.id}/spec`, 'engineer'), { every: 2_000, max: 3 * 60_000 });
}
log(`spec: ${spec.id} state=${spec.state}`);
if (spec.state === 'DRAFT') {
  await post(`/api/v1/specs/${spec.id}/route`, { reviewerIds: null, routingNote: 'golden demo prebake' }, 'engineer');
  spec = await get(`/api/v1/subroutines/${hero.id}/spec`, 'engineer');
  log(`routed → ${spec.state}`);
}
if (spec.state === 'IN_REVIEW') {
  const j = spec.specJson ?? spec.spec ?? {};
  let accepted = 0;
  for (const section of ['invariants', 'side_effects', 'edge_cases', 'open_questions']) {
    for (const claim of j[section] ?? []) {
      await post(`/api/v1/specs/${spec.id}/claims/review`, { claimPath: `$.${section}[?(@.id=='${claim.id}')]`, action: 'accept', reason: null, editedText: null }, 'sme');
      accepted++;
    }
  }
  await post(`/api/v1/specs/${spec.id}/sign`, { confirmation: 'I have reviewed every claim' }, 'sme');
  spec = await get(`/api/v1/subroutines/${hero.id}/spec`, 'engineer');
  log(`accepted ${accepted} claims, signed → ${spec.state}`);
}
if (spec.state !== 'SIGNED') throw new Error(`hero spec is ${spec.state}, expected SIGNED`);

// 4. generated code + green compile gate
let scaffold = await get(`/api/v1/specs/${spec.id}/scaffold`, 'engineer');
if (!scaffold) {
  log('generating code on the default target');
  const done = await sse(`/api/v1/specs/${spec.id}/scaffold`, 'engineer');
  scaffold = await get(`/api/v1/specs/${spec.id}/scaffold`, 'engineer') ?? done;
}
log(`scaffold: ${scaffold.id} target=${scaffold.targetPlatform} files=${scaffold.fileCount ?? scaffold.files?.length ?? '?'}`);
const validation = (await get(`/api/v1/scaffolds/${scaffold.id}/validation`, 'engineer')) ?? [];
const vlist = validation.data ?? validation.runs ?? validation;
if (!vlist.some?.((r) => r.stage === 'COMPILE' && r.status === 'PASSED')) {
  log('running the compile gate');
  const run = await post(`/api/v1/scaffolds/${scaffold.id}/validate/compile`, {}, 'engineer');
  log(`compile gate: ${run.status} — ${run.summary}`);
  if (run.status !== 'PASSED') throw new Error(`compile gate ${run.status}: ${run.summary}`);
} else {
  log('compile gate: already PASSED');
}

// 5. migration plan
let plan = await get(`/api/v1/corpora/${corpus.id}/migration-plan`, 'admin');
if (!plan || (plan.waves ?? plan.data?.waves ?? []).length === 0) {
  log('generating the migration plan');
  plan = await post(`/api/v1/corpora/${corpus.id}/migration-plan/generate`, {}, 'admin');
}
log(`migration plan: ${(plan.waves ?? plan.data?.waves ?? []).length} waves`);

// 6. assessment
let assessment = await get(`/api/v1/corpora/${corpus.id}/assessment`, 'admin');
if (!assessment?.section) {
  log('running the assessment (one Sonnet call on a real key)');
  await post(`/api/v1/corpora/${corpus.id}/assessment`, {}, 'admin');
  assessment = await until('assessment', async () => {
    const a = await get(`/api/v1/corpora/${corpus.id}/assessment`, 'admin');
    return a?.section ? a : null;
  }, { every: 5_000, max: 10 * 60_000 });
}
log(`assessment: ${assessment.card?.recommendation?.mode ?? '?'} on ${assessment.card?.recommendation?.targetStack ?? '?'}`);

// manifest for the recorder
const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, '..', 'generated');
await mkdir(outDir, { recursive: true });
const manifest = {
  apiBase: API_BASE,
  corpusId: corpus.id,
  corpusName: corpus.name,
  threadId: thread.id,
  heroSubroutineId: hero.id,
  heroName: hero.name,
  specId: spec.id,
  scaffoldId: scaffold.id,
  targetStack: scaffold.targetPlatform,
  bakedAt: new Date().toISOString(),
};
await writeFile(join(outDir, 'golden.json'), JSON.stringify(manifest, null, 2));
log(`wrote generated/golden.json — hero ${hero.name}, thread ${thread.id}`);
