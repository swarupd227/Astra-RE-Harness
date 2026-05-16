/**
 * Phase #5.4 — Java Spring scaffold archetype for COBOL claim taxonomy.
 *
 * Verifies the new java-spring/cobol-canonical-payroll archetype ships:
 *   - registered as production with compatibleSchemas=["cobol"]
 *   - matches the three Phase 5 in-scope programs (DEPTPAY / EMPPAY / CBL0106)
 *   - detail endpoint returns the full Java template file manifest
 *
 * Also smoke-tests the Phase 5.4 fix to the scaffold gate: requesting a
 * java-spring scaffold for a Fortran subroutine (which has no matching
 * cobol-canonical-payroll entry) falls back to the still-preview
 * canonical-rollstock archetype and is correctly rejected.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

test.describe('Phase #5.4 · COBOL → Java archetype', () => {
  test('archetype is registered with the right metadata', async ({ page }) => {
    const list = await page.request.get(`${API_BASE}/api/v1/archetypes`, { headers: ENG }).then((r) => r.json());
    const ours = list.data.find((a: { id: string; targetStack: string }) =>
      a.id === 'cobol-canonical-payroll' && a.targetStack === 'java-spring');
    expect(ours).toBeDefined();
    expect(ours.compatibleSchemas).toEqual(['cobol']);
    expect((ours.status ?? '').toLowerCase()).toContain('production');
    expect(ours.fileCount).toBeGreaterThanOrEqual(7);
  });

  test('detail endpoint returns the Java template files with content', async ({ page }) => {
    const res = await page.request.get(
      `${API_BASE}/api/v1/archetypes/java-spring/cobol-canonical-payroll`,
      { headers: ENG },
    );
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const files = body.files as { path: string; content: string; derivedFromClaimIds?: string[] }[];

    const pom = files.find((f) => f.path === 'pom.xml');
    expect(pom).toBeDefined();
    expect(pom!.content).toContain('<artifactId>demo-payroll</artifactId>');
    expect(pom!.content).toContain('<maven.compiler.source>21</maven.compiler.source>');
    expect(pom!.content).toContain('junit-jupiter');
    expect(pom!.content).toContain('assertj-core');

    const service = files.find((f) => f.path === 'src/main/java/com/example/payroll/PayrollService.java');
    expect(service).toBeDefined();
    expect(service!.content).toContain('package com.example.payroll');
    expect(service!.content).toContain('public final class PayrollService');
    expect(service!.content).toContain('INV-1');
    expect(service!.content).toContain('IPayrollRepository');
    expect(service!.content).toContain('BigDecimal');
    expect(service!.derivedFromClaimIds).toEqual(expect.arrayContaining(['INV-1','INV-2','INV-3','INV-4','INV-5']));

    const tests = files.find((f) => f.path === 'src/test/java/com/example/payroll/PayrollServiceTest.java');
    expect(tests).toBeDefined();
    expect(tests!.content).toContain('@DisplayName("INV-1 / IO-1: missing record returns NOT_FOUND');
    expect(tests!.content).toContain('@DisplayName("EC-1');
    expect(tests!.content).toContain('org.assertj.core.api.Assertions');
    expect(tests!.content).toContain('org.mockito.Mockito');

    const repo = files.find((f) => f.path === 'src/main/java/com/example/payroll/IPayrollRepository.java');
    expect(repo).toBeDefined();
    expect(repo!.content).toContain('VSAM READ');
    expect(repo!.content).toContain('record PayrollRecord');

    const events = files.find((f) => f.path === 'src/main/java/com/example/payroll/IPayrollEventNotifier.java');
    expect(events).toBeDefined();
    expect(events!.content).toContain('emitInventoryChanged');

    const result = files.find((f) => f.path === 'src/main/java/com/example/payroll/PayrollResult.java');
    expect(result).toBeDefined();
    expect(result!.content).toContain('enum Status');
  });

  test('matches.anyOf lists the three Phase 5 in-scope programs', async ({ page }) => {
    const res = await page.request.get(
      `${API_BASE}/api/v1/archetypes/java-spring/cobol-canonical-payroll`,
      { headers: ENG },
    );
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const matches = body.matches?.anyOf as { subroutineName: string }[] | undefined;
    expect(matches).toBeDefined();
    const names = matches!.map((m) => m.subroutineName);
    expect(names).toEqual(expect.arrayContaining(['DEPTPAY', 'EMPPAY', 'CBL0106']));
  });

  test('Scaffold gate rejects java-spring on a Fortran spec (no production java archetype matches)', async ({ page }) => {
    // Find one of the seeded SIGNED MINPACK Fortran specs.
    const list = await page.request.get(`${API_BASE}/api/v1/subroutines?state=SIGNED&limit=5`, { headers: ENG }).then((r) => r.json());
    if (list.data.length === 0) test.skip(true, 'No SIGNED Fortran routines — seed not present.');
    const subId = list.data[0].id;
    const sub = await page.request.get(`${API_BASE}/api/v1/subroutines/${subId}`, { headers: ENG }).then((r) => r.json());
    // Sanity: this must be a Fortran routine
    expect(sub.sourceLanguage).toBe('fortran-f77');

    const specRes = await page.request.get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG });
    if (!specRes.ok()) test.skip(true, 'No SIGNED spec for chosen Fortran routine.');
    const specId = (await specRes.json()).id;

    // POST scaffold with java-spring target. The fallback archetype for
    // a Fortran subroutine on java-spring is the (alphabetically-first)
    // canonical-rollstock @ preview — the gate must reject it.
    const r = await page.request.post(
      `${API_BASE}/api/v1/specs/${specId}/scaffold?targetStack=java-spring`,
      { headers: ENG },
    );
    expect(r.status()).toBe(400);
    const body = await r.json();
    expect(body.error.code).toBe('scaffold.target_gated');
    // The gate message names the SPECIFIC archetype that's gated, not just
    // the target stack — this is the Phase 5.4 fix.
    expect(body.error.message.toLowerCase()).toContain('canonical-rollstock');
  });
});
