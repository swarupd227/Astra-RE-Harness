/**
 * Phase 6.0 — Golden Dataset · Admin CRUD + scorer.
 *
 * Exercises the Phase 6.0a surface end-to-end:
 *   - GET /api/v1/golden-dataset returns the 10 seeded entries (5+5).
 *   - GET /api/v1/golden-dataset/{entryId} returns the full body.
 *   - Engineer is forbidden from CRUD + scoring.
 *   - Admin can create / update / delete an entry.
 *   - Admin POST .../{entryId}/score returns a run with a score.
 *   - Admin POST .../score-all returns an aggregate.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

test.describe('Phase 6.0 · Golden dataset', () => {
  test('GET /golden-dataset lists at least the 10 seeded entries', async ({ page }) => {
    const r = await page.request.get(`${API_BASE}/api/v1/golden-dataset`, { headers: ENG });
    expect(r.status()).toBe(200);
    const body = await r.json();
    const ids: string[] = body.data.map((e: { entryId: string }) => e.entryId);
    // Should include all 10 seeded entries — Fortran + COBOL.
    expect(ids).toEqual(
      expect.arrayContaining([
        'fortran-integer-divide-truncates',
        'fortran-equivalence-aliasing',
        'fortran-save-implicit',
        'fortran-mixed-precision',
        'fortran-arithmetic-if',
        'cobol-rounded-off-by-one',
        'cobol-redefines-dual-view',
        'cobol-occurs-depending-on',
        'cobol-evaluate-when-other',
        'cobol-perform-thru-span',
      ]),
    );
  });

  test('GET /golden-dataset?schemaId=cobol filters to 5 COBOL entries', async ({ page }) => {
    const r = await page.request.get(
      `${API_BASE}/api/v1/golden-dataset?schemaId=cobol`,
      { headers: ENG },
    );
    expect(r.status()).toBe(200);
    const body = await r.json();
    const schemas = new Set<string>(body.data.map((e: { schemaId: string }) => e.schemaId));
    expect(Array.from(schemas)).toEqual(['cobol']);
    expect(body.data.length).toBeGreaterThanOrEqual(5);
  });

  test('GET /golden-dataset/{entryId} returns inline source + claims + canonical inputs', async ({ page }) => {
    const r = await page.request.get(
      `${API_BASE}/api/v1/golden-dataset/cobol-rounded-off-by-one`,
      { headers: ENG },
    );
    expect(r.status()).toBe(200);
    const entry = await r.json();
    expect(entry.entryId).toBe('cobol-rounded-off-by-one');
    expect(entry.schemaId).toBe('cobol');
    expect(entry.sourceContent).toContain('AVG-OFF-BY-ONE');
    expect(entry.expectedClaims.length).toBeGreaterThan(0);
    // The 5847.95-vs-5848.06 canonical case is the demo's signature
    // trap — pin it so a seed-content refactor surfaces immediately.
    expect(entry.canonicalInputs).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ total: 111111.11, count: 19, expected: 5847.95 }),
      ]),
    );
  });

  test('Engineer cannot mutate or score (403 on every write path)', async ({ page }) => {
    const create = await page.request.post(`${API_BASE}/api/v1/golden-dataset`, {
      headers: ENG,
      data: { entryId: 'x', schemaId: 'cobol', sourceContent: '...' },
    });
    expect(create.status()).toBe(403);

    const update = await page.request.put(
      `${API_BASE}/api/v1/golden-dataset/cobol-rounded-off-by-one`,
      { headers: ENG, data: { status: 'approved' } },
    );
    expect(update.status()).toBe(403);

    const score = await page.request.post(
      `${API_BASE}/api/v1/golden-dataset/cobol-rounded-off-by-one/score`,
      { headers: ENG },
    );
    expect(score.status()).toBe(403);

    const scoreAll = await page.request.post(
      `${API_BASE}/api/v1/golden-dataset/score-all`,
      { headers: ENG },
    );
    expect(scoreAll.status()).toBe(403);
  });

  test('Admin can create, update, delete a custom entry', async ({ page }) => {
    const entryId = `e2e-test-${Date.now()}`;
    const create = await page.request.post(`${API_BASE}/api/v1/golden-dataset`, {
      headers: ADMIN,
      data: {
        entryId,
        schemaId: 'cobol',
        title: 'e2e test entry',
        trapCategory: 'test/e2e',
        difficulty: 'easy',
        status: 'draft',
        sourcePath: 'cobol/e2e/test.cbl',
        sourceLines: '1-3',
        sourceContent: '       IDENTIFICATION DIVISION.\n       PROGRAM-ID. E2E-TEST.\n       STOP RUN.\n',
        expectedClaims: [
          { kind: 'invariant', id: 'INV-1', pattern: '(?i)E2E.TEST' },
        ],
        notes: 'created by e2e suite',
      },
    });
    expect(create.status()).toBe(200);
    const created = await create.json();
    expect(created.entryId).toBe(entryId);
    expect(created.expectedClaims.length).toBe(1);

    const update = await page.request.put(
      `${API_BASE}/api/v1/golden-dataset/${entryId}`,
      { headers: ADMIN, data: { status: 'approved', notes: 'verified by e2e' } },
    );
    expect(update.status()).toBe(200);
    const updated = await update.json();
    expect(updated.status).toBe('approved');
    expect(updated.notes).toBe('verified by e2e');

    const del = await page.request.delete(
      `${API_BASE}/api/v1/golden-dataset/${entryId}`,
      { headers: ADMIN },
    );
    expect(del.status()).toBe(204);

    // Confirm gone
    const after = await page.request.get(
      `${API_BASE}/api/v1/golden-dataset/${entryId}`,
      { headers: ADMIN },
    );
    expect(after.status()).toBe(404);
  });

  test('Admin score on one entry persists a run with a score', async ({ page }) => {
    const r = await page.request.post(
      `${API_BASE}/api/v1/golden-dataset/cobol-rounded-off-by-one/score`,
      { headers: ADMIN, timeout: 90_000 },
    );
    expect(r.status()).toBe(200);
    const run = await r.json();
    expect(typeof run.score).toBe('number');
    expect(run.score).toBeGreaterThanOrEqual(0);
    expect(run.score).toBeLessThanOrEqual(1);
    expect(run.total).toBeGreaterThan(0);
    expect(Array.isArray(run.detail)).toBe(true);
    expect(run.modelName).toBeTruthy();
    expect(run.promptId).toBeTruthy();
  });

  test('Admin score-all returns aggregate over the whole corpus', async ({ page }) => {
    const r = await page.request.post(
      `${API_BASE}/api/v1/golden-dataset/score-all`,
      { headers: ADMIN, timeout: 180_000 },
    );
    expect(r.status()).toBe(200);
    const body = await r.json();
    expect(body.inputCount).toBeGreaterThanOrEqual(10);
    expect(body.aggregateTotal).toBeGreaterThan(0);
    expect(typeof body.aggregateScore).toBe('number');
    expect(body.runs.length).toBeGreaterThanOrEqual(10);
  });
});
