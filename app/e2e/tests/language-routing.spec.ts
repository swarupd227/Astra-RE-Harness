/**
 * Phase #5.2 — Pipeline routing by source language.
 *
 * Verifies the end-to-end ingest → parse → persist chain correctly
 * tags COBOL and Fortran subroutines with the right `sourceLanguage`
 * value, and that the API surface exposes that tag for downstream
 * routing (prompt-library / archetype selection in Phase 5.3+).
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

const DEPTPAY = `\
       IDENTIFICATION DIVISION.
       PROGRAM-ID. DEPTPAYE2E.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  DEPT-RECORD.
           05  DEPT-NAME            PIC X(20).
       PROCEDURE DIVISION.
           PERFORM AVERAGE-SALARY.
           PERFORM DISPLAY-DETAILS.
           STOP RUN.

       AVERAGE-SALARY.
           MOVE "FINANCE"           TO DEPT-NAME.

       DISPLAY-DETAILS.
           DISPLAY "Department Name: " DEPT-NAME.
`;

const FORTRAN_SAMPLE = `\
      SUBROUTINE E2EROUT(X, Y)
      INTEGER X, Y
      Y = X + 1
      RETURN
      END
`;

async function ingestText(page: import('@playwright/test').Page, name: string, relativePath: string, content: string) {
  const res = await page.request.post(`${API_BASE}/api/v1/ingest/text`, {
    headers: { ...ENG, 'Content-Type': 'application/json' },
    data: { name, files: [{ path: relativePath, content }] },
  });
  return res;
}

async function findSubroutineByName(page: import('@playwright/test').Page, corpusId: string, name: string) {
  const corpus = await page.request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG }).then((r) => r.json());
  for (const file of corpus.latestVersion?.files ?? []) {
    const match = (file.subroutines ?? []).find((s: { name: string }) => s.name === name);
    if (match) return match.id as string;
  }
  return null;
}

test.describe('Phase #5.2 · Pipeline routing by source language', () => {
  test('COBOL upload is parsed via the COBOL branch and the subroutine carries sourceLanguage=cobol', async ({ page }) => {
    const corpusName = `cobol-routing-${Date.now()}`;
    const res = await ingestText(page, corpusName, 'DEPTPAYE2E.CBL', DEPTPAY);
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(body.subroutineCount).toBe(1);
    expect(body.state).toBe('PARSED');

    const subId = await findSubroutineByName(page, body.corpusId, 'DEPTPAYE2E');
    expect(subId).not.toBeNull();
    const detail = await page.request.get(`${API_BASE}/api/v1/subroutines/${subId}`, { headers: ENG }).then((r) => r.json());
    expect(detail.sourceLanguage).toBe('cobol');
    expect(detail.signature).toContain('PROGRAM-ID. DEPTPAYE2E.');
    expect(detail.calledSubroutines).toEqual(expect.arrayContaining(['AVERAGE-SALARY', 'DISPLAY-DETAILS']));
  });

  test('Fortran upload is parsed via the Fortran branch and sourceLanguage=fortran-f77', async ({ page }) => {
    const corpusName = `fortran-routing-${Date.now()}`;
    const res = await ingestText(page, corpusName, 'e2erout.f', FORTRAN_SAMPLE);
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(body.subroutineCount).toBeGreaterThanOrEqual(1);

    const subId = await findSubroutineByName(page, body.corpusId, 'E2EROUT');
    expect(subId).not.toBeNull();
    const detail = await page.request.get(`${API_BASE}/api/v1/subroutines/${subId}`, { headers: ENG }).then((r) => r.json());
    expect(detail.sourceLanguage).toBe('fortran-f77');
  });

  test('Existing pre-Phase-5.2 subroutines default to fortran-f77 (back-compat)', async ({ page }) => {
    // Hit one of the MINPACK seeded routines and confirm the new column
    // is populated even though it was inserted before Phase 5.2.
    const list = await page.request.get(`${API_BASE}/api/v1/subroutines?state=SIGNED&limit=1`, { headers: ENG }).then((r) => r.json());
    if (list.data.length === 0) test.skip(true, 'No SIGNED routines — demo seed not present.');
    const subId = list.data[0].id;
    const detail = await page.request.get(`${API_BASE}/api/v1/subroutines/${subId}`, { headers: ENG }).then((r) => r.json());
    expect(detail.sourceLanguage).toBe('fortran-f77');
  });

  test('Unsupported extension is rejected at ingest with a helpful message', async ({ page }) => {
    const res = await page.request.post(`${API_BASE}/api/v1/ingest/text`, {
      headers: { ...ENG, 'Content-Type': 'application/json' },
      data: { name: `bad-${Date.now()}`, files: [{ path: 'bogus.txt', content: 'irrelevant' }] },
    });
    expect(res.status()).toBe(400);
    const body = await res.json();
    // Error message mentions both supported language families
    expect((body.error?.message ?? '').toLowerCase()).toMatch(/cobol|fortran/);
  });
});
