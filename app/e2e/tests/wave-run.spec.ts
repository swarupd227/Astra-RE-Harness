/**
 * Parallel multi-routine work (P5/P6), driven by sentences typed into a thread
 * against the mock providers:
 *
 *   - "Run wave 1" pauses on ONE Confirm card, then many agents draft specs at once.
 *   - The API is killed mid-run and started again: the run carries on from the queue.
 *   - It stops at the human wait state: nothing is signed or generated until an SME signs.
 *   - Signing one spec releases that routine only; pause / resume / cancel are sentences.
 *   - A finished run records serial against parallel agent time.
 *   - SME and observer cannot queue or control agent work.
 *
 * The restart step needs the API to run in a local Docker container named in
 * API_CONTAINER; without it that step is skipped (never restart a shared API).
 */
import { execFileSync } from 'node:child_process';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

import { API_BASE } from './helpers/projects';

const API_CONTAINER = process.env.API_CONTAINER;
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };
const SME = { 'X-Dev-Persona': 'sme', 'Content-Type': 'application/json' };
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const T = 120_000;

type Stage = { stage: string; total: number; pending: number; running: number; succeeded: number; failed: number; skipped: number; waiting: number; cancelled: number };
type Snapshot = {
  runId: string; state: string; summary: string; stages: Stage[]; waitingForSignoffCount: number; failedCount: number; idleOnPeople: boolean;
  calls: number; metrics: { serialMs: number; busyMs: number; speedup: number; peakConcurrency: number; items: number };
};
type Item = { id: string; kind: string; subroutineId: string; name: string; state: string; attempts: number; error: string | null };
type Run = { run: Snapshot; items: Item[] };

/** `leaves` independent routines (wave 1) and one caller of the first two (wave 2). */
function fortran(prefix: string, leaves: number): string {
  const routine = (name: string, body: string[]) => ['      SUBROUTINE ' + name + '(X)', '      INTEGER X', ...body, '      RETURN', '      END', ''].join('\n');
  const out = Array.from({ length: leaves }, (_, i) => routine(`${prefix}${i + 1}`, [`      X = X + ${i + 1}`]));
  out.push(routine(`${prefix}TOP`, [`      CALL ${prefix}1(X)`, `      CALL ${prefix}2(X)`]));
  return out.join('\n');
}

async function newPlannedProgramme(request: APIRequestContext, prefix: string, leaves: number) {
  const name = `wave-e2e-${prefix.toLowerCase()}-${Date.now()}`;
  const ingest = await request.post(`${API_BASE}/api/v1/ingest/text`, {
    headers: ENG, data: { name, files: [{ path: `${prefix.toLowerCase()}.f`, content: fortran(prefix, leaves) }] }, timeout: T,
  });
  expect(ingest.ok(), await ingest.text()).toBe(true);
  const { corpusId } = await ingest.json();
  const draft = await request.post(`${API_BASE}/api/v1/corpora/${corpusId}/migration-plan/generate`, { headers: ADMIN, data: {}, timeout: T });
  expect(draft.ok(), await draft.text()).toBe(true);
  const plan = await draft.json();
  const approve = await request.post(`${API_BASE}/api/v1/migration-plans/${plan.id ?? plan.planId}/approve`, { headers: ADMIN, data: {}, timeout: T });
  expect(approve.ok(), await approve.text()).toBe(true);
  const threads = await request.get(`${API_BASE}/api/v1/conversations?corpusId=${corpusId}`, { timeout: T }).then((r) => r.json());
  return { corpusId: corpusId as string, threadId: threads.data[0].id as string, name };
}

async function openThread(page: Page, threadId: string, persona: 'engineer' | 'sme' | 'admin' | 'observer') {
  await page.goto('/');
  await page.evaluate((p) => localStorage.setItem('astra.devPersona', p), persona);
  await page.goto(`/w/${threadId}`);
  await expect(page.getByTestId('composer-input')).toBeVisible({ timeout: 30_000 });
}

async function say(page: Page, sentence: string) {
  const input = page.getByTestId('composer-input');
  await input.click();
  await input.fill(sentence);
  await page.keyboard.press('Enter');
}

async function confirm(page: Page) {
  const button = page.getByTestId('confirm-action').last();
  await expect(button).toBeVisible({ timeout: 30_000 });
  await button.click();
}

async function runsOf(request: APIRequestContext, corpusId: string): Promise<Array<{ id: string; state: string }>> {
  return (await request.get(`${API_BASE}/api/v1/work-runs?corpusId=${corpusId}`, { timeout: T }).then((r) => r.json())).items;
}

async function readRun(request: APIRequestContext, runId: string): Promise<Run | null> {
  try {
    const res = await request.get(`${API_BASE}/api/v1/work-runs/${runId}`, { timeout: 10_000 });
    return res.ok() ? ((await res.json()) as Run) : null;
  } catch {
    return null; // the API is restarting
  }
}

async function waitForRun(request: APIRequestContext, runId: string, what: string, done: (r: Run) => boolean, timeout = 180_000): Promise<Run> {
  let last: Run | null = null;
  await expect
    .poll(
      async () => {
        last = await readRun(request, runId);
        return last !== null && done(last);
      },
      { message: `waiting for ${what}`, timeout, intervals: [400, 800, 1500] },
    )
    .toBe(true);
  return last!;
}

const stage = (r: Run, kind: string) => r.run.stages.find((s) => s.stage === kind)!;
const itemsOf = (r: Run, subroutineId: string) => Object.fromEntries(r.items.filter((i) => i.subroutineId === subroutineId).map((i) => [i.kind, i]));

/** An SME reviews every claim and signs, through the same API the review page uses. */
async function signAsSme(request: APIRequestContext, subroutineId: string) {
  const spec = await request.get(`${API_BASE}/api/v1/subroutines/${subroutineId}/spec`, { headers: SME, timeout: T }).then((r) => r.json());
  expect(spec.id).toBeTruthy();
  for (const section of ['invariants', 'side_effects', 'edge_cases', 'open_questions']) {
    for (const claim of (spec.spec?.[section] ?? []) as Array<{ id: string }>) {
      const claimPath = `$.${section}[?(@.id=='${claim.id}')]`;
      const data = section === 'open_questions' ? { claimPath, action: 'edit', editedText: 'Resolved for the e2e run.' } : { claimPath, action: 'accept' };
      const res = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, { headers: SME, data, timeout: T });
      expect(res.ok(), await res.text()).toBe(true);
    }
  }
  const sign = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/sign`, { headers: SME, data: { confirmation: 'I have reviewed every claim.' }, timeout: T });
  expect(sign.ok(), await sign.text()).toBe(true);
}

test.describe.serial('Wave run: parallel agents, restart, sign-off wait', () => {
  test.setTimeout(600_000);

  let corpusId: string;
  let threadId: string;
  let runId: string;
  const LEAVES = 14;

  test('one Confirm card queues the wave, and agents work on several routines at once', async ({ page, request }) => {
    ({ corpusId, threadId } = await newPlannedProgramme(request, 'WV', LEAVES));
    await openThread(page, threadId, 'engineer');

    await say(page, 'Run wave 1');
    const card = page.getByTestId('confirm-card').last();
    await expect(card.getByTestId('confirm-summary')).toContainText(`${LEAVES} routines`, { timeout: 30_000 });
    await expect(card.getByTestId('confirm-summary')).toContainText(`draft ${LEAVES} specs`);
    await expect(card.getByTestId('confirm-summary')).toContainText('wait for an SME to sign');
    await expect(card.getByTestId('confirm-summary')).toContainText('agents never sign');
    expect(await runsOf(request, corpusId)).toHaveLength(0); // nothing starts before the confirmation

    await confirm(page);
    await expect(page.getByTestId('work-run-card').last()).toBeVisible({ timeout: 30_000 });
    const runs = await runsOf(request, corpusId);
    expect(runs).toHaveLength(1);
    runId = runs[0].id;

    const busy = await waitForRun(request, runId, 'several extractions in flight', (r) => (stage(r, 'extract')?.running ?? 0) >= 3, 60_000);
    expect(stage(busy, 'extract').total).toBe(LEAVES);
    await expect(page.getByTestId('work-run-working').last()).toBeVisible({ timeout: 20_000 });
  });

  test('the API is killed mid-run; after the restart the run finishes drafting', async ({ request }) => {
    test.skip(!API_CONTAINER, 'Set API_CONTAINER to the local API container to exercise a restart.');
    const before = await waitForRun(request, runId, 'extractions in flight', (r) => stage(r, 'extract').running >= 1, 60_000);
    const interrupted = before.items.filter((i) => i.state === 'RUNNING').map((i) => i.id);
    expect(stage(before, 'extract').succeeded).toBeLessThan(LEAVES);

    // A hard kill, not a graceful stop: the leases are left dangling and must lapse.
    execFileSync('docker', ['kill', API_CONTAINER!]);
    execFileSync('docker', ['start', API_CONTAINER!]);

    const after = await waitForRun(request, runId, 'every draft after the restart', (r) => stage(r, 'extract').succeeded === LEAVES, 240_000);
    const resumed = after.items.filter((i) => interrupted.includes(i.id));
    expect(resumed.every((i) => i.state === 'SUCCEEDED')).toBe(true);
    // At least one of them died with the process and was leased a second time.
    expect(resumed.some((i) => i.attempts >= 2)).toBe(true);
    expect(after.run.failedCount).toBe(0);
  });

  test('it stops at the sign-off wait: no agent signs or generates', async ({ page, request }) => {
    const idle = await waitForRun(request, runId, 'the run to be idle on people', (r) => r.run.idleOnPeople && r.run.waitingForSignoffCount === LEAVES);
    expect(idle.run.state).toBe('RUNNING');
    expect(idle.run.metrics.peakConcurrency).toBeGreaterThanOrEqual(3);
    expect(idle.run.metrics.speedup).toBeGreaterThan(1.5);
    expect(idle.run.calls).toBeGreaterThanOrEqual(LEAVES);

    await page.waitForTimeout(6_000); // several scheduler passes
    const still = (await readRun(request, runId))!;
    expect(stage(still, 'signoff').succeeded).toBe(0);
    expect(stage(still, 'scaffold').pending).toBe(LEAVES);
    expect(stage(still, 'scaffold').running + stage(still, 'scaffold').succeeded).toBe(0);
    const board = await request.get(`${API_BASE}/api/v1/corpora/${corpusId}/flow-board`, { timeout: T }).then((r) => r.json());
    const column = (key: string) => board.columns.find((c: { key: string }) => c.key === key).total;
    expect(column('in_review')).toBe(LEAVES); // routed for review, not signed
    expect(column('signed') + column('built') + column('verified')).toBe(0);

    await openThread(page, threadId, 'sme');
    await expect(page.getByTestId('thread-message').filter({ hasText: 'Agents are idle until an SME signs' }).last()).toBeVisible({ timeout: 30_000 });
    await say(page, 'Show the review queue');
    const queue = page.getByTestId('review-queue-card').last();
    await expect(queue).toHaveAttribute('data-count', String(LEAVES), { timeout: 30_000 });
    await expect(queue.getByTestId('review-queue-item').first()).toContainText('holds 3 agent steps');
  });

  test('signing one spec releases that routine only', async ({ request }) => {
    const idle = (await readRun(request, runId))!;
    const first = idle.items.find((i) => i.kind === 'signoff')!;
    await signAsSme(request, first.subroutineId);

    const moved = await waitForRun(request, runId, 'the signed routine to be generated and gated', (r) => {
      const mine = itemsOf(r, first.subroutineId);
      return ['SUCCEEDED', 'FAILED', 'SKIPPED'].includes(mine['test-pack'].state);
    }, 400_000);
    const mine = itemsOf(moved, first.subroutineId);
    expect(mine.signoff.state).toBe('SUCCEEDED');
    expect(mine.scaffold.state, mine.scaffold.error ?? '').toBe('SUCCEEDED');
    expect(mine.compile.state, mine.compile.error ?? '').toBe('SUCCEEDED');
    expect(mine['test-pack'].state, mine['test-pack'].error ?? '').toBe('SUCCEEDED');
    expect(moved.run.waitingForSignoffCount).toBe(LEAVES - 1);
    expect(stage(moved, 'scaffold').pending).toBe(LEAVES - 1);
  });

  test('pause, resume and cancel are sentences, each behind its Confirm card', async ({ page, request }) => {
    await openThread(page, threadId, 'engineer');
    await say(page, 'Pause the wave run');
    await confirm(page);
    await waitForRun(request, runId, 'the pause', (r) => r.run.state === 'PAUSED', 30_000);

    const paused = (await readRun(request, runId))!;
    const next = paused.items.find((i) => i.kind === 'signoff' && i.state === 'WAITING')!;
    await signAsSme(request, next.subroutineId);
    await page.waitForTimeout(5_000);
    expect(itemsOf((await readRun(request, runId))!, next.subroutineId).scaffold.state).toBe('PENDING'); // nothing new starts

    await say(page, 'Resume the wave run');
    await confirm(page);
    await waitForRun(request, runId, 'the released routine to start', (r) => itemsOf(r, next.subroutineId).scaffold.state !== 'PENDING', 60_000);

    await say(page, 'Cancel the wave run');
    await confirm(page);
    const cancelled = await waitForRun(request, runId, 'the cancel, with nothing left running',
      (r) => r.run.state === 'CANCELLED' && r.items.every((i) => i.state !== 'RUNNING'), 120_000);
    expect(cancelled.run.waitingForSignoffCount).toBe(0);
    expect(cancelled.run.failedCount, 'a step stopped by the cancel is not a failure').toBe(0);
    expect(cancelled.items.some((i) => i.state === 'PENDING' || i.state === 'WAITING')).toBe(false);
    expect(stage(cancelled, 'extract').succeeded).toBe(LEAVES); // finished work is kept
    await expect(page.getByTestId('thread-message').filter({ hasText: 'was cancelled' }).last()).toBeVisible({ timeout: 60_000 });
  });

  test('only an engineer queues code work; SME and observer cannot queue or control', async ({ request }) => {
    for (const persona of ['sme', 'observer', 'admin']) {
      const res = await request.post(`${API_BASE}/api/v1/work-runs`, {
        headers: { 'X-Dev-Persona': persona, 'Content-Type': 'application/json' }, data: { corpusId, wave: 2 },
      });
      expect(res.status(), persona).toBe(403);
    }
    for (const persona of ['sme', 'observer']) {
      const res = await request.post(`${API_BASE}/api/v1/work-runs/${runId}/retry-failed`, { headers: { 'X-Dev-Persona': persona, 'Content-Type': 'application/json' }, data: {} });
      expect(res.status(), persona).toBe(403);
    }
  });
});

test.describe('Wave run: completion', () => {
  test.setTimeout(600_000);

  test('a signed-off run completes and records parallel against serial agent time', async ({ page, request }) => {
    const { corpusId, threadId } = await newPlannedProgramme(request, 'CP', 2);
    await openThread(page, threadId, 'engineer');
    await say(page, 'Extract the specs for all routines');
    await expect(page.getByTestId('confirm-summary').last()).toContainText('draft 3 specs', { timeout: 30_000 });
    await confirm(page);
    await expect(page.getByTestId('work-run-card').last()).toBeVisible({ timeout: 30_000 });
    const runId = (await runsOf(request, corpusId))[0].id;

    const drafted = await waitForRun(request, runId, 'three drafts waiting', (r) => r.run.waitingForSignoffCount === 3);
    for (const sign of drafted.items.filter((i) => i.kind === 'signoff')) await signAsSme(request, sign.subroutineId);

    const done = await waitForRun(request, runId, 'completion', (r) => r.run.state === 'COMPLETED', 60_000);
    expect(done.run.failedCount).toBe(0);
    expect(done.run.metrics.items).toBe(3);
    expect(done.run.metrics.serialMs).toBeGreaterThan(done.run.metrics.busyMs);
    const closing = page.getByTestId('thread-message').filter({ hasText: 'working in parallel against' }).last();
    await expect(closing).toBeVisible({ timeout: 60_000 });
    await expect(closing.getByTestId('work-run-timing')).toBeVisible();
  });
});
