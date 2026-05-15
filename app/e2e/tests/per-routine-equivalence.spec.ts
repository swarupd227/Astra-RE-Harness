/**
 * Phase #5 / Followup — Per-routine equivalence: CONSUME_ROLL.
 *
 * Drives the gfortran sidecar with the canonical CONSUME_ROLL routine +
 * ISAM shim stubs against an inline C# reference that mirrors the .NET 8
 * archetype's ConsumeRollService. Asserts:
 *   - Non-admin → 403.
 *   - Admin POST returns 5 matched rows + verdict PASSED.
 *   - Each canonical input scenario (happy / depleted / insufficient /
 *     locked / not_found) produces the expected result_cd in both
 *     runtimes, and the row's `match` flag is true.
 *
 * Path:
 *   POST /api/v1/validation/equivalence/preview/consume-roll
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };
const ENG = { 'X-Dev-Persona': 'engineer' };
const URL = `${API_BASE}/api/v1/validation/equivalence/preview/consume-roll`;

test.describe('Phase #5 · Per-routine equivalence (CONSUME_ROLL)', () => {
  test.beforeEach(async ({ page }) => {
    page.setDefaultNavigationTimeout(60_000);
    page.setDefaultTimeout(60_000);
  });

  test('non-admin POST → 403', async ({ page }) => {
    const r = await page.request.post(URL, { headers: ENG });
    expect(r.status()).toBe(403);
  });

  test('admin POST drives gfortran + C# and returns 5/5 matching outcomes', async ({ page }) => {
    const r = await page.request.post(URL, { headers: ADMIN, timeout: 60_000 });
    expect(r.status()).toBe(200);
    const body = await r.json();
    expect(body.routine).toBe('CONSUME_ROLL');
    expect(body.inputCount).toBe(5);
    expect(body.matched).toBe(5);
    expect(body.mismatched).toBe(0);
    expect(body.verdict).toBe('PASSED');
    expect(Array.isArray(body.rows)).toBe(true);
    expect(body.rows).toHaveLength(5);

    // Validate the canonical result_cd per scenario, both sides.
    const expected: Record<string, number> = {
      'happy path': 0,
      'depleted (drops below MIN_REMAIN)': 0,
      'insufficient': 2,
      'locked': 3,
      'not found': 1,
    };
    for (const row of body.rows) {
      const label = row.input.label as string;
      const want = expected[label];
      expect(row.match).toBe(true);
      expect(row.fortran.result_cd).toBe(want);
      expect(row.csharp.resultCd).toBe(want);
    }
  });
});
