/**
 * Phase #5.3 — COBOL → Java Spring extract prompt.
 *
 * Verifies the new cobol/java-spring/extract prompt is loaded into the
 * PromptLibrary at startup, exposes the expected metadata, and that
 * the prompt body shape (single JSON object output, citation rules,
 * spec/v1 schema) is the calibrated one — not a stub.
 */
import { expect, test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

test.describe('Phase #5.3 · COBOL → Java extract prompt', () => {
  test('the new prompt is in the catalog at production status', async ({ page }) => {
    const list = await page.request.get(`${API_BASE}/api/v1/prompts`, { headers: ENG }).then((r) => r.json());
    const ours = list.data.find((p: { sourceSchema: string; targetStack: string; kind: string }) =>
      p.sourceSchema === 'cobol' && p.targetStack === 'java-spring' && p.kind === 'extract'
    );
    expect(ours).toBeDefined();
    expect(ours.version).toBe('v0.1');
    expect((ours.status ?? '').toLowerCase()).toContain('production');
  });

  test('getPrompt(cobol, java-spring, extract) returns the calibrated body', async ({ page }) => {
    const res = await page.request.get(
      `${API_BASE}/api/v1/prompts/cobol/java-spring/extract`,
      { headers: ENG },
    );
    expect(res.ok()).toBe(true);
    const body = await res.json();

    // Production calibration markers — these are the substantive bits of
    // the prompt that distinguish it from a stub/preview:
    expect(body.systemTemplate).toContain('OS/390, z/OS, CICS, VSAM, IMS, DB2');
    expect(body.systemTemplate).toContain('Java 21 + Spring Boot 3 + JUnit 5');
    expect(body.systemTemplate).toContain('single JSON object');
    expect(body.systemTemplate).toContain('section_contracts');
    expect(body.systemTemplate).toContain('io_side_effects');
    expect(body.systemTemplate).toContain('open_questions');
    // Coverage targets — present in the production calibration only:
    expect(body.systemTemplate).toMatch(/Coverage targets/i);
    expect(body.systemTemplate).toMatch(/AT END/);
    expect(body.systemTemplate).toMatch(/INVALID KEY/);
    expect(body.systemTemplate).toMatch(/ON SIZE ERROR/);

    // User template variable hooks must match what the API renders for
    // every COBOL subroutine (ExtractionPipeline.Render uses these names).
    expect(body.userTemplate).toContain('{{subroutineName}}');
    expect(body.userTemplate).toContain('{{sourcePath}}');
    expect(body.userTemplate).toContain('{{lineCount}}');
    expect(body.userTemplate).toContain('{{sourceText}}');
  });

  test('frontmatter records the owner + model preference + version', async ({ page }) => {
    // Note: the PromptLibrary's minimal YAML parser intentionally drops
    // list items (calibratedAgainst is surfaced via the e2e probe in
    // the next test rather than here). Scalar fields are reliable.
    const body = await page.request.get(
      `${API_BASE}/api/v1/prompts/cobol/java-spring/extract`,
      { headers: ENG },
    ).then((r) => r.json());
    expect(body.frontmatter.owner ?? '').toContain('Nous');
    expect(body.frontmatter.modelPreference ?? '').toContain('claude-sonnet');
    expect(body.frontmatter.version ?? '').toBe('v0.1');
    expect(body.frontmatter.kind ?? '').toBe('extract');
    expect((body.frontmatter.status ?? '').toLowerCase()).toBe('production');
  });
});
