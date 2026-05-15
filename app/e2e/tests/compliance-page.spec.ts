/**
 * Phase #4 / value-add #6 — Compliance & audit-feed page.
 *
 * Asserts:
 *   - GET /api/v1/compliance/formats returns SOX, HIPAA, PCI definitions
 *   - The /compliance route renders three format cards + a column table
 *   - The download button triggers GET /api/v1/compliance/feed and the
 *     server responds with CSV + Content-Disposition + X-Astra-Row-Count
 *   - The export itself is captured as a `compliance.feed_exported` audit
 *     row (meta-audit) — the platform's own claim that "every export is
 *     audited" is verifiable here, not just in marketing slides.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

async function lastAuditEvent(page: Page, action: string): Promise<{ id: string; createdAt: string } | null> {
  const res = await page.request.get(`${API_BASE}/api/v1/audit?limit=20`, { headers: ENG });
  if (!res.ok()) return null;
  const body = await res.json();
  const evt = body.data?.find((e: { action: string }) => e.action === action);
  return evt ? { id: evt.id, createdAt: evt.createdAt } : null;
}

test.describe('Phase #4 · Compliance feed page', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('GET /compliance/formats lists SOX, HIPAA, PCI with column metadata', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/compliance/formats`, { headers: ENG });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const ids = new Set<string>(body.data.map((f: { id: string }) => f.id));
    expect(ids.has('sox')).toBe(true);
    expect(ids.has('hipaa')).toBe(true);
    expect(ids.has('pci')).toBe(true);
    const sox = body.data.find((f: { id: string }) => f.id === 'sox');
    expect(sox.columns.length).toBeGreaterThan(0);
    expect(typeof sox.columns[0].id).toBe('string');
    expect(typeof sox.columns[0].header).toBe('string');
  });

  test('/compliance page renders three format cards, column table, and download button', async ({ page }) => {
    await page.goto('/compliance');
    await expect(page.getByTestId('compliance-page')).toBeVisible();
    await expect(page.getByTestId('compliance-format-sox')).toBeVisible();
    await expect(page.getByTestId('compliance-format-hipaa')).toBeVisible();
    await expect(page.getByTestId('compliance-format-pci')).toBeVisible();
    // SOX selected by default → its column table should be rendered.
    await expect(page.getByText(/Columns in this bundle/)).toBeVisible();
    await expect(page.getByTestId('compliance-download')).toBeVisible();
  });

  test('clicking Download triggers the feed download + a compliance.feed_exported audit row', async ({ page }) => {
    await page.goto('/compliance');
    await expect(page.getByTestId('compliance-page')).toBeVisible();

    // Wait for the download response from the click. We don't assert the
    // actual saveAs because Playwright intercepts download events at the
    // browser layer — the server response is the verifiable artefact.
    const responseWaiter = page.waitForResponse(
      (r) => r.url().includes('/api/v1/compliance/feed') && r.request().method() === 'GET',
      { timeout: 15_000 },
    );
    await page.getByTestId('compliance-download').click();
    const res = await responseWaiter;
    expect(res.status()).toBe(200);
    expect(res.headers()['content-disposition']).toContain('attachment');
    expect(res.headers()['x-astra-row-count']).toBeDefined();

    // Meta-audit: the export itself appears in the audit log.
    const evt = await lastAuditEvent(page, 'compliance.feed_exported');
    expect(evt).not.toBeNull();
  });

  test('LeftNav has a Compliance entry that navigates to the page', async ({ page }) => {
    await page.goto('/');
    const nav = page.getByTestId('nav-compliance');
    await expect(nav).toBeVisible();
    await nav.click();
    await expect(page.getByTestId('compliance-page')).toBeVisible();
  });
});
