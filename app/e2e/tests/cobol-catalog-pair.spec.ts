/**
 * Item #3e — COBOL → C# catalog pair using the productized platform.
 *
 * Verifies the COBOL catalog ships end-to-end as a real pair, not just
 * a marketing claim:
 *   1. The COBOL spec schema is loaded with its typed claim taxonomy
 *      (INV-* / SC-* / IO-* / EC-* / Q-*).
 *   2. The COBOL→.NET 8 extract prompt is in the prompt library.
 *   3. The COBOL→.NET 8 scaffold archetype is in the archetype registry
 *      with compatibleSchemas=["cobol"] and its file manifest is
 *      browseable.
 * The three together = the platform is genuinely target-stack agnostic
 * across legacy languages, with the same claim → fixture mapping that
 * Fortran already enjoys.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

test.describe('Item #3e · COBOL catalog pair', () => {
  test('COBOL spec schema is loaded with typed claim kinds', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/spec-schemas/cobol`, { headers: ENG });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(body.id).toBe('cobol');
    expect(body.supportedSourceExtensions).toEqual(expect.arrayContaining(['.cob', '.cbl', '.cpy']));
    expect(body.compatibleTargetStacks).toEqual(expect.arrayContaining(['dotnet8']));
    const kindIds = (body.claimKinds as { id: string }[]).map((k) => k.id);
    for (const k of ['invariant', 'sectionContract', 'ioSideEffect', 'edgeCase', 'openQuestion']) {
      expect(kindIds).toContain(k);
    }
  });

  test('COBOL extract prompt is in the library', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/prompts/cobol/dotnet8/extract`, { headers: ENG });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    expect(body.sourceSchema).toBe('cobol');
    expect(body.targetStack).toBe('dotnet8');
    expect(body.kind).toBe('extract');
    expect(typeof body.systemTemplate).toBe('string');
    expect(typeof body.userTemplate).toBe('string');
  });

  test('cobol-canonical-rollstock archetype exists with compatibleSchemas=["cobol"]', async ({ page }) => {
    const list = await page.request.get(`${API_BASE}/api/v1/archetypes`, { headers: ENG }).then((r) => r.json());
    const ours = list.data.find((a: { id: string; targetStack: string }) =>
      a.id === 'cobol-canonical-rollstock' && a.targetStack === 'dotnet8',
    );
    expect(ours).toBeDefined();
    expect(ours.compatibleSchemas).toEqual(['cobol']);
    expect(ours.status.toLowerCase()).toContain('production');
    expect(ours.fileCount).toBeGreaterThanOrEqual(5);
  });

  test('Archetype detail endpoint returns the C# file manifest with content', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/archetypes/dotnet8/cobol-canonical-rollstock`, { headers: ENG });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const files = body.files as { path: string; content: string }[];
    const service = files.find((f) => f.path === 'src/ConsumeRollService.cs');
    expect(service).toBeDefined();
    expect(service!.content).toContain('namespace Demo.RollStock');
    expect(service!.content).toContain('IRollRepository');
    expect(service!.content).toContain('INV-1');

    const tests = files.find((f) => f.path === 'tests/ConsumeRollServiceTests.cs');
    expect(tests).toBeDefined();
    expect(tests!.content).toContain('INV-1: missing roll returns NotFound');
  });
});
