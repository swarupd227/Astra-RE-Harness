/**
 * Phase 5.6 — Per-routine equivalence: DEPTPAY.AVERAGE-SALARY.
 *
 * Drives the gnucobol sidecar with the AVG-DRIVER program (the
 * COBOL COMPUTE / ROUNDED line for the openmainframeproject DEPTPAY
 * sample) against an inline C# `decimal` reference that mirrors the
 * Java `BigDecimal.divide(scale=2, HALF_UP)` semantic. Asserts:
 *   - Non-admin → 403.
 *   - Admin POST returns 6 matched rows + verdict PASSED.
 *   - Specific canonical pairs land on the right scale-2 values
 *     in BOTH runtimes (so a regression on either side surfaces).
 *
 * Path:
 *   POST /api/v1/validation/equivalence/preview/deptpay-average
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };
const ENG = { 'X-Dev-Persona': 'engineer' };
const URL = `${API_BASE}/api/v1/validation/equivalence/preview/deptpay-average`;

test.describe('Phase 5.6 · Per-routine equivalence (DEPTPAY.AVERAGE-SALARY)', () => {
  test.beforeEach(async ({ page }) => {
    page.setDefaultNavigationTimeout(60_000);
    page.setDefaultTimeout(60_000);
  });

  test('non-admin POST → 403', async ({ page }) => {
    const r = await page.request.post(URL, { headers: ENG });
    expect(r.status()).toBe(403);
  });

  test('admin POST drives gnucobol + C# and returns 6/6 matching rows', async ({ page }) => {
    const r = await page.request.post(URL, { headers: ADMIN, timeout: 90_000 });
    expect(r.status()).toBe(200);
    const body = await r.json();
    expect(body.routine).toBe('DEPTPAY.AVERAGE-SALARY');
    expect(body.inputCount).toBe(6);
    expect(body.matched).toBe(6);
    expect(body.mismatched).toBe(0);
    expect(body.verdict).toBe('PASSED');
    expect(Array.isArray(body.rows)).toBe(true);
    expect(body.rows).toHaveLength(6);

    // Pin specific canonical values so a silent regression in the
    // COBOL driver or the C# reference flips this test, not just
    // the `match` flag. JSON marshalling of `decimal` comes through
    // as a JavaScript number; compare to four-decimal tolerance.
    //
    // Note the INV-4 baseline (111111.11 / 19) lands on 5847.95,
    // not 5848.06 — the Phase 5.4 PayrollServiceTest fixture's
    // hand-computed expected value is off by one; the harness
    // surfaces this on the first run.
    const expected: Record<string, number> = {
      'INV-4 baseline · surfaces Java fixture bug': 5847.95,
      'EC-1 ON SIZE ERROR (divide by zero → 0.00)': 0.00,
      'repeating decimal · HALF_UP': 3.33,
      'exact halves': 25.25,
      'round-down case': 17636.68,
      'non-terminating · HALF_UP': 76.92,
    };

    for (const row of body.rows) {
      const label = row.input.label as string;
      const want = expected[label];
      expect(want, `unexpected canonical label ${label}`).toBeDefined();
      expect(row.match).toBe(true);
      expect(Number(row.cobol)).toBeCloseTo(want, 2);
      expect(Number(row.csharp)).toBeCloseTo(want, 2);
    }
  });
});
