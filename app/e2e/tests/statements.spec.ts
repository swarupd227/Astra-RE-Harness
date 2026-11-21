/**
 * Packs, completeness and statements (WS7 K6).
 *
 *   - A pack says what a programme produces and how it is scored; the one that ships is data, not code.
 *   - The completeness score is arithmetic a person can check: the "before" and "after a scripted review"
 *     numbers of the design come out exactly, through the API.
 *   - A statement preview shows what the writer and the grounding gate make of one slot: every statement cites
 *     evidence of this project, gets its status from a rule, and nothing is saved. It needs the offline writer
 *     (Docs__Generator__Provider=mock); against the real provider it is skipped, not faked.
 */
import { expect, test, type Page } from '@playwright/test';
import { API_BASE, JSON_ENG, attach, getEvidence, listEvidence, newProject } from './helpers/projects';

const PREFIX = 'statements-e2e';

type Pack = {
  id: string;
  deliverables: { id: string; weight: number; staticSlots: { key: string }[]; entitySlots?: { keyPrefix: string; countKey: string } | null }[];
};
type Completeness = {
  percent: number;
  dependencyPercent: number;
  dependencyBand: string;
  deliverables: { id: string; denominator: number; earned: number; applicable: boolean; slots: { slotKey: string; credit: number; state: string }[] }[];
};

const JOBS = ['TLR_EOD_LOCK', 'TLR_EOD_COUNT', 'TLR_EOD_POST', 'TLR_DW_EXTRACT', 'TLR_EOD_REPORT', 'TLR_AM_RECON'];
const INTERFACES = ['loan-lookup', 'gl-posting', 'ofac-screening', 'cash-vault-file', 'drawer-limits', 'teller-identity', 'receipt-printer', 'dw-extract'];
const ERRORS = ['KE-01', 'KE-02', 'KE-03', 'KE-04', 'KE-05'];
const DERIVED = { 'batch-sheets': JOBS, 'interface-map': INTERFACES, 'known-errors': ERRORS };
const EXPECTED = { jobs: 6, interfaces: 8, 'known-errors': 5 };

// Slot credits as the design states them: 1 is approved with every statement supported, 0.5 has one unverified, 0 has nothing approved.
const AFTER: Record<string, number> = {
  'overview:purpose': 1, 'overview:users': 1, 'overview:business-process': 1, 'overview:tier': 1, 'overview:owners': 0.5,
  'context:diagram': 1,
  'entity:interface:loan-lookup': 1, 'entity:interface:gl-posting': 1, 'entity:interface:ofac-screening': 1, 'entity:interface:cash-vault-file': 1,
  'entity:interface:drawer-limits': 1, 'entity:interface:teller-identity': 1, 'entity:interface:receipt-printer': 1, 'entity:interface:dw-extract': 0.5,
  'entity:job:TLR_EOD_LOCK': 1, 'entity:job:TLR_EOD_COUNT': 1, 'entity:job:TLR_EOD_POST': 0.5, 'entity:job:TLR_DW_EXTRACT': 1,
  'entity:job:TLR_EOD_REPORT': 1, 'entity:job:TLR_AM_RECON': 1,
  'runbook:start-of-day': 1, 'runbook:end-of-day': 1, 'runbook:failure-handling': 0.5, 'runbook:restart': 1, 'runbook:escalation': 0,
  'entity:error:KE-01': 1, 'entity:error:KE-02': 0.5, 'entity:error:KE-03': 0, 'entity:error:KE-04': 0.5, 'entity:error:KE-05': 1,
  'env:environments': 1, 'env:access-groups': 1, 'env:contacts': 0,
  'openq:closed': 0,
};
const LEGACY = ['overview:purpose', 'overview:users', 'entity:interface:loan-lookup', 'entity:job:TLR_EOD_LOCK', 'entity:job:TLR_AM_RECON', 'runbook:start-of-day', 'env:environments'];

const slotBody = (key: string, credit: number) => ({
  slotKey: key,
  versions:
    credit === 1 ? [{ approved: true, statuses: ['supported', 'supported'] }]
    : credit === 0.5 ? [{ approved: true, statuses: ['supported', 'unverified'] }]
    : [],
});

async function getJson<T>(page: Page, path: string, persona = 'engineer'): Promise<T> {
  const res = await page.request.get(`${API_BASE}${path}`, { headers: { 'X-Dev-Persona': persona } });
  expect(res.status(), path).toBe(200);
  return res.json();
}

async function completeness(page: Page, slots: ReturnType<typeof slotBody>[], persona = 'observer'): Promise<Completeness> {
  const res = await page.request.post(`${API_BASE}/api/v1/packs/knowledge-transfer/completeness`, {
    headers: { 'X-Dev-Persona': persona, 'Content-Type': 'application/json' },
    data: { derived: DERIVED, expectedCounts: EXPECTED, slots },
  });
  expect(res.status()).toBe(200);
  return res.json();
}

test.describe('packs and completeness', () => {
  test('the shipped pack is data: eight weighted deliverables, stored under their own section kinds', async ({ page }) => {
    const list = await getJson<{ data: Pack[]; writer: { provider: string; isMock: boolean }; problems: string[] }>(page, '/api/v1/packs', 'observer');
    expect(list.problems).toEqual([]);
    expect(typeof list.writer.provider).toBe('string');
    const pack = list.data.find((p) => p.id === 'knowledge-transfer')!;
    expect(pack.deliverables.map((d) => d.id)).toEqual([
      'overview', 'context-diagram', 'interface-map', 'batch-sheets', 'runbook', 'known-errors', 'environments-access-contacts', 'open-questions',
    ]);
    expect(pack.deliverables.reduce((sum, d) => sum + d.weight, 0)).toBe(100);
    expect(pack.deliverables.find((d) => d.id === 'runbook')).toMatchObject({ sectionKind: 'd-runbook', scope: 'deliverable', staticSlots: 5 });
    expect(pack.deliverables.find((d) => d.id === 'batch-sheets')!.entitySlots).toEqual({ keyPrefix: 'entity:job:', countKey: 'jobs' });

    const full = await getJson<any>(page, '/api/v1/packs/knowledge-transfer', 'observer');
    expect(full.credit).toEqual({ approvedAllSupported: 1, approvedWithUnverified: 0.5, otherwise: 0 });
    expect(full.vocabulary.knowledgeHolder).toBe('contractor');

    const missing = await page.request.get(`${API_BASE}/api/v1/packs/no-such-pack`, { headers: { 'X-Dev-Persona': 'observer' } });
    expect(missing.status()).toBe(404);
  });

  test('before: what an older document covered scores 18.2%; after a scripted review the worked example is 76.5%', async ({ page }) => {
    const before = await completeness(page, LEGACY.map((k) => slotBody(k, 1)));
    expect(before.percent).toBeCloseTo(18.2, 1);
    expect(before.dependencyPercent).toBeCloseTo(81.8, 1);
    expect(before.dependencyBand).toBe('high');

    const after = await completeness(page, Object.entries(AFTER).map(([k, c]) => slotBody(k, c)));
    expect(after.percent).toBeCloseTo(76.5, 1);
    expect(after.dependencyBand).toBe('low');
    const earned = Object.fromEntries(after.deliverables.map((d) => [d.id, d.earned]));
    expect(earned).toMatchObject({ overview: 4.5, 'interface-map': 7.5, 'batch-sheets': 5.5, runbook: 3.5, 'known-errors': 3, 'environments-access-contacts': 2, 'open-questions': 0 });
  });

  test('a slot keeps its best approved version; a thing expected and not found counts against the deliverable', async ({ page }) => {
    const res = await page.request.post(`${API_BASE}/api/v1/packs/knowledge-transfer/completeness`, {
      headers: { 'X-Dev-Persona': 'observer', 'Content-Type': 'application/json' },
      data: {
        derived: { 'interface-map': ['a', 'b', 'c'] },
        expectedCounts: { interfaces: 4 },
        slots: [
          { slotKey: 'entity:interface:a', versions: [{ approved: true, statuses: ['supported', 'unverified'] }, { approved: true, statuses: ['supported'] }, { approved: false, statuses: ['supported'] }] },
          { slotKey: 'entity:interface:b', versions: [{ approved: true, statuses: ['conflict'] }] },
        ],
      },
    });
    const body: Completeness = await res.json();
    const map = body.deliverables.find((d) => d.id === 'interface-map')!;
    expect(map.denominator).toBe(4);
    expect(map.earned).toBe(1);   // a: its best version, 1.0; b: a conflict still open earns nothing; c: nothing; one more expected and not found
    expect(map.slots.find((s) => s.slotKey === 'entity:interface:a')).toMatchObject({ credit: 1, state: 'approved-supported' });
    expect(map.slots.find((s) => s.slotKey === 'entity:interface:b')).toMatchObject({ credit: 0, state: 'not-approved' });
    expect(map.slots.some((s) => s.state === 'missing' && s.slotKey.includes('not found'))).toBe(true);
    // Deliverables with nothing to fill take no part: no jobs or errors were found or expected here.
    expect(body.deliverables.find((d) => d.id === 'batch-sheets')!.applicable).toBe(false);
  });

  test('an unknown pack is not found', async ({ page }) => {
    const res = await page.request.post(`${API_BASE}/api/v1/packs/no-such-pack/completeness`, {
      headers: { 'X-Dev-Persona': 'observer', 'Content-Type': 'application/json' },
      data: { slots: [] },
    });
    expect(res.status()).toBe(404);
  });
});

const DDL = `-- Staged transactions waiting to be posted.
CREATE TABLE dbo.TLR_STAGE (
    STAGE_ID INT IDENTITY(1,1) NOT NULL,
    TXN_REF VARCHAR(40) NOT NULL,
    CONSTRAINT PK_TLR_STAGE PRIMARY KEY (STAGE_ID),
    CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)
);
GO
`;

const TICKETS = [
  'number,opened_at,priority,state,short_description,description,cmdb_ci',
  'INC-T-0412,2026-03-02,2,Closed,EOD post fails,"Job TLR_EOD_POST failed with error 2627 on UQ_TLR_STAGE_TXN_REF. The staged duplicate was deleted and the chain restarted from step 3.",TLR_EOD_POST',
].join('\n') + '\n';

test.describe('statement preview', () => {
  let isMock = false;

  test.beforeAll(async ({ request }) => {
    const res = await request.get(`${API_BASE}/api/v1/packs`, { headers: { 'X-Dev-Persona': 'observer' } });
    isMock = (await res.json()).writer.isMock === true;
  });

  async function project(page: Page) {
    const id = await newProject(page, ['sql', 'ticket'], { prefix: PREFIX });
    const r = await attach(id, [{ path: 'db/ddl.sql', data: DDL }, { path: 'tickets/export.csv', data: TICKETS }]);
    expect(r.status).toBe(200);
    const sql = (await listEvidence(page, id, { kind: 'sql' })).data.find((e) => e.label.includes('TLR_STAGE'))!;
    const ticket = (await listEvidence(page, id, { kind: 'ticket' })).data[0]!;
    return { id, sql, ticket };
  }

  const preview = (page: Page, id: string, body: Record<string, unknown>, persona = 'engineer') =>
    page.request.post(`${API_BASE}/api/v1/corpora/${id}/statements/preview`, { headers: { 'X-Dev-Persona': persona, 'Content-Type': 'application/json' }, data: body });

  test('every statement cites this project’s evidence and gets its status from the gate; nothing is saved', async ({ page }) => {
    test.skip(!isMock, 'needs the offline writer (Docs__Generator__Provider=mock)');
    const { id, sql, ticket } = await project(page);

    const res = await preview(page, id, { slotKey: 'runbook:failure-handling', evidenceIds: [sql.shortId, ticket.shortId] });
    expect(res.status()).toBe(200);
    const body = await res.json();

    expect(body.provider).toBe('mock');
    expect(body.document.schema).toBe('statements/v1');
    const items: { id: string; slotKey: string; text: string; citations: string[]; status: string }[] = body.document.sections.flatMap((s: any) => s.items);
    expect(items.length).toBe(2);
    for (const item of items) {
      expect(item.slotKey).toBe('runbook:failure-handling');
      expect([sql.shortId, ticket.shortId]).toEqual(expect.arrayContaining(item.citations));
      expect(item.citations.length).toBeGreaterThan(0);
    }
    // Structured evidence the check agreed with is Supported; a ticket on its own is only Unverified.
    expect(items.find((i) => i.citations.includes(sql.shortId))!.status).toBe('supported');
    expect(items.find((i) => i.citations.includes(ticket.shortId))!.status).toBe('unverified');
    expect(body.gate.counts).toEqual({ supported: 1, unverified: 1, conflict: 0, dropped: 0 });
    expect(body.gate.attempts).toBe(1);

    // Derived markdown carries the citations and marks what is not plainly supported.
    expect(body.markdown).toContain(`[${sql.shortId}]`);
    expect(body.markdown).toContain('_(unverified)_');

    // Every call was repeatable (temperature 0, a replay key) and the writer only saw what it was given.
    expect(body.calls.map((c: any) => c.promptId)).toEqual(['knowledge-statement-writer', 'knowledge-support-check']);
    expect(body.calls.every((c: any) => c.temperature === 0 && typeof c.replayKey === 'string')).toBe(true);
    expect(body.evidence.map((e: any) => e.id).sort()).toEqual([sql.shortId, ticket.shortId].sort());

    // What it cited can be opened.
    for (const cite of items.flatMap((i) => i.citations)) expect((await getEvidence(page, id, cite)).status).toBe(200);
  });

  test('evidence can be chosen by a query, and an Observer cannot ask for a write', async ({ page }) => {
    test.skip(!isMock, 'needs the offline writer (Docs__Generator__Provider=mock)');
    const { id } = await project(page);

    const byQuery = await preview(page, id, { slotKey: 'runbook:failure-handling', query: 'TLR_STAGE', limit: 5 });
    expect(byQuery.status()).toBe(200);
    expect((await byQuery.json()).evidence.length).toBeGreaterThan(0);

    const observer = await preview(page, id, { slotKey: 'runbook:failure-handling', query: 'TLR_STAGE' }, 'observer');
    expect(observer.status()).toBe(403);
    expect((await observer.json()).error.code).toBe('auth.writer_required');
  });

  test('a request that cannot be answered says why', async ({ page }) => {
    const { id, sql } = await project(page);

    const badSlot = await preview(page, id, { slotKey: 'not a slot', evidenceIds: [sql.shortId] });
    expect(badSlot.status()).toBe(400);
    expect((await badSlot.json()).error.code).toBe('statements.invalid_slot');

    const noBrief = await preview(page, id, { slotKey: 'custom:thing', evidenceIds: [sql.shortId] });
    expect(noBrief.status()).toBe(400);
    expect((await noBrief.json()).error.code).toBe('statements.no_instruction');

    const noEvidence = await preview(page, id, { slotKey: 'runbook:failure-handling', evidenceIds: ['SQL-deadbeef'] });
    expect(noEvidence.status()).toBe(400);
    expect((await noEvidence.json()).error.code).toBe('statements.no_evidence');

    const missing = await page.request.post(`${API_BASE}/api/v1/corpora/00000000-0000-0000-0000-000000000001/statements/preview`, {
      headers: JSON_ENG, data: { slotKey: 'runbook:failure-handling', evidenceIds: [sql.shortId] },
    });
    expect(missing.status()).toBe(404);
  });
});
