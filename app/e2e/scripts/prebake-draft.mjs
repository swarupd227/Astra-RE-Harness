#!/usr/bin/env node
/**
 * Pre-bake a DRAFT spec for a target subroutine.
 *
 * The Anthropic provider occasionally returns malformed JSON on long Delphi
 * / C++ extraction prompts — the request "succeeds" (200) but no spec is
 * persisted. The Playwright demo recording then deadlocks waiting for the
 * "View draft spec" button. This script POSTs to `/extract` up to N times,
 * polling `/spec` after each attempt until DRAFT appears. The recording test
 * then starts from a known-good DRAFT state — even if the on-screen
 * Re-extract click silently fails, the existing DRAFT survives.
 *
 * Usage:
 *   node scripts/prebake-draft.mjs <subroutineId> [attempts]
 *   node scripts/prebake-draft.mjs <subroutineId> 5     # default
 */
import { setTimeout as sleep } from 'node:timers/promises';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

const subId = process.argv[2];
const maxAttempts = Number(process.argv[3] ?? 5);
if (!subId) {
  console.error('Usage: prebake-draft.mjs <subroutineId> [attempts]');
  process.exit(2);
}

async function getSpecState() {
  const r = await fetch(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG });
  if (r.status === 404) return { exists: false };
  if (!r.ok) throw new Error(`spec GET ${r.status}`);
  const body = await r.json();
  return { exists: true, state: body.state, specId: body.id };
}

async function startExtract() {
  // The extract endpoint streams SSE. Drain the stream so the server completes
  // the persist step before we hang up. We don't need to parse the events —
  // we'll re-check /spec afterwards.
  const r = await fetch(`${API_BASE}/api/v1/subroutines/${subId}/extract`, {
    method: 'POST', headers: ENG,
  });
  if (!r.ok) throw new Error(`extract POST ${r.status}`);
  const reader = r.body.getReader();
  const dec = new TextDecoder();
  let lastEvent = '';
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    lastEvent += dec.decode(value);
    // Keep last 4kB so we can log a tail on failure.
    if (lastEvent.length > 4096) lastEvent = lastEvent.slice(-4096);
  }
  return lastEvent;
}

let current = await getSpecState();
if (current.exists && current.state === 'DRAFT') {
  console.log(`[prebake] sub ${subId} already in DRAFT (spec ${current.specId}) — skipping.`);
  process.exit(0);
}

for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
  console.log(`[prebake] attempt ${attempt}/${maxAttempts} — POST /extract …`);
  const startMs = Date.now();
  const tail = await startExtract();
  const elapsedMs = Date.now() - startMs;
  console.log(`[prebake]   stream closed in ${(elapsedMs / 1000).toFixed(1)}s`);
  // Poll /spec for up to 20s — persistence is the last step of the SSE stream.
  for (let i = 0; i < 10; i += 1) {
    await sleep(2_000);
    const s = await getSpecState();
    if (s.exists && s.state === 'DRAFT') {
      console.log(`[prebake] DRAFT reached after attempt ${attempt} (spec ${s.specId}).`);
      process.exit(0);
    }
  }
  console.log(`[prebake]   no DRAFT after attempt ${attempt}. Tail of SSE stream:`);
  console.log(tail.split('\n').slice(-15).join('\n'));
}

console.error(`[prebake] gave up after ${maxAttempts} attempts.`);
process.exit(1);
