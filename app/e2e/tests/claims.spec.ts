/**
 * Claims and ticket clusters (WS7 K7).
 *
 *   - Tickets about the same problem are grouped with no model: three incidents with one error code are a
 *     candidate known error, routine requests (a password, a printer) are tagged and left out.
 *   - A claims run reads the narrative evidence (what people said and wrote) into atomic claims, each from one
 *     piece of evidence of this project, checks each against what code, SQL and configuration state, and keeps
 *     the result. Reading needs a writer, so the run is for people who can write. The run needs the offline writer
 *     (Docs__Generator__Provider=mock); against the real provider it is skipped, not faked.
 *
 * Which claim is Supported, Unverified or a Conflict, and how a written statement joins a claim, are rules with
 * no model and are covered by unit tests (ClaimTests).
 */
import { expect, test, type Page } from '@playwright/test';
import { API_BASE, JSON_ENG, attach, getEvidence, newProject } from './helpers/projects';

const PREFIX = 'claims-e2e';

const DDL = `CREATE TABLE dbo.TLR_STAGE (
    STAGE_ID INT IDENTITY(1,1) NOT NULL,
    TXN_REF VARCHAR(40) NOT NULL,
    CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)
);
GO
`;

const TICKETS = [
  'number,opened_at,priority,state,short_description,description,cmdb_ci',
  'INC-T-0412,2026-03-02,2,Closed,EOD post fails,"Job TLR_EOD_POST failed with error 2627 on UQ_TLR_STAGE_TXN_REF. The staged duplicate was deleted and the chain restarted from step 3.",TLR_EOD_POST',
  'INC-T-0431,2026-03-09,2,Closed,Posting stopped,"Nightly TLR_EOD_POST stopped, SQL error 2627 duplicate key in TLR_STAGE. Deleted the second row and restarted.",TLR_EOD_POST',
  'INC-T-0455,2026-03-16,2,Closed,EOD run duplicate,"EOD run failed: error 2627, duplicate TXN_REF in TLR_STAGE. Restarted from step 3 after clearing the row.",TLR_EOD_POST',
  'INC-T-0460,2026-03-17,4,Closed,Password reset,"Please reset my password, locked out after the weekend.",',
  'INC-T-0461,2026-03-18,4,Closed,Printer jam,"The printer on floor two is jammed again.",',
].join('\n') + '\n';

const NOTES = `# Handover notes

\`TLR_EOD_POST\` writes to \`TLR_STAGE\` every night and fails with error 2627 when a teller re-submits a payment.
When it fails, delete the second staged row and restart from step 3.
`;

type Cluster = { key: string; evidenceIds: string[]; references: string[]; candidate: boolean };

async function project(page: Page) {
  const id = await newProject(page, ['sql', 'ticket', 'document'], { prefix: PREFIX });
  const r = await attach(id, [
    { path: 'db/ddl.sql', data: DDL },
    { path: 'tickets/export.csv', data: TICKETS },
    { path: 'notes/handover.md', data: NOTES },
  ]);
  expect(r.status).toBe(200);
  return id;
}

const run = (page: Page, id: string, persona = 'engineer') =>
  page.request.post(`${API_BASE}/api/v1/corpora/${id}/claims/run`, { headers: { 'X-Dev-Persona': persona } });

test.describe('ticket clusters', () => {
  test('three incidents with one error code are a candidate known error; routine requests are left out', async ({ page }) => {
    const id = await project(page);
    const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/ticket-clusters`, { headers: { 'X-Dev-Persona': 'observer' } });
    expect(res.status()).toBe(200);
    const body: { clusters: Cluster[]; routine: string[] } = await res.json();

    const cluster = body.clusters[0]!;
    expect(cluster.key).toBe('2627');
    expect(cluster.candidate).toBe(true);
    expect([...cluster.references].sort()).toEqual(['INC-T-0412', 'INC-T-0431', 'INC-T-0455']);
    expect(body.routine.length).toBe(2);
    expect(body.clusters.flatMap((c) => c.evidenceIds)).not.toEqual(expect.arrayContaining(body.routine));

    // Everything it names can be opened.
    for (const eid of [...cluster.evidenceIds, ...body.routine]) expect((await getEvidence(page, id, eid)).status).toBe(200);
  });

  test('an unknown project is a 404', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/corpora/00000000-0000-0000-0000-000000000001/ticket-clusters`, { headers: JSON_ENG });
    expect(res.status()).toBe(404);
  });
});

test.describe('claims run', () => {
  let isMock = false;

  test.beforeAll(async ({ request }) => {
    const res = await request.get(`${API_BASE}/api/v1/packs`, { headers: { 'X-Dev-Persona': 'observer' } });
    isMock = (await res.json()).writer.isMock === true;
  });

  test('before a run there are no claims; an Observer cannot start one', async ({ page }) => {
    const id = await project(page);
    const none = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/claims`, { headers: JSON_ENG });
    expect(none.status()).toBe(404);
    expect((await none.json()).error.code).toBe('claims.not_run');

    const denied = await run(page, id, 'observer');
    expect(denied.status()).toBe(403);
    expect((await denied.json()).error.code).toBe('auth.writer_required');
  });

  test('claims come from evidence of this project, each with a status by rule; routine tickets are not read', async ({ page }) => {
    test.skip(!isMock, 'needs the offline writer (Docs__Generator__Provider=mock)');
    const id = await project(page);

    const started = await run(page, id);
    expect(started.status()).toBe(200);
    const summary = await started.json();
    expect(summary.provider).toBe('mock');
    expect(summary.counts.claims).toBeGreaterThan(0);
    expect(summary.counts.candidates).toBe(1);
    expect(summary.counts.routine).toBe(2);
    expect(summary.modelCalls).toBeGreaterThan(0);
    expect(typeof summary.elapsedMs).toBe('number');

    const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/claims`, { headers: { 'X-Dev-Persona': 'observer' } });
    expect(res.status()).toBe(200);
    const doc = await res.json();
    expect(doc.schema).toBe('claims/v1');
    expect(doc.claims.length).toBe(summary.counts.claims);
    expect(doc.counts.supported + doc.counts.unverified + doc.counts.conflict).toBe(doc.claims.length);

    for (const c of doc.claims as { id: string; text: string; evidence: string; status: string; reason: string }[]) {
      expect(c.id).toMatch(/^cl-[0-9a-f]{10}$/);
      expect(['supported', 'unverified', 'conflict']).toContain(c.status);
      expect(c.reason.length).toBeGreaterThan(0);
      expect((await getEvidence(page, id, c.evidence)).status, c.evidence).toBe(200);
    }
    // The offline writer states nothing that can be checked, so nothing is claimed to be verified.
    expect(doc.claims.every((c: any) => c.status === 'unverified')).toBe(true);
    // Routine requests were never read.
    expect(doc.claims.map((c: any) => c.evidence)).not.toEqual(expect.arrayContaining(doc.routine));
    expect(doc.claims.some((c: any) => /password|printer/i.test(c.text))).toBe(false);

    // Filtered by status; running again replaces rather than adds.
    const unverified = await (await page.request.get(`${API_BASE}/api/v1/corpora/${id}/claims?status=unverified`, { headers: JSON_ENG })).json();
    expect(unverified.claims.length).toBe(doc.claims.length);
    const conflicts = await (await page.request.get(`${API_BASE}/api/v1/corpora/${id}/claims?status=conflict`, { headers: JSON_ENG })).json();
    expect(conflicts.claims).toEqual([]);

    const again = await (await run(page, id)).json();
    expect(again.counts.claims).toBe(summary.counts.claims);
  });
});
