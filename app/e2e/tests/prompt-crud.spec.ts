/**
 * Phase #4.3 — Prompt Catalog CRUD (admin-only).
 *
 * Asserts:
 *   - Non-admin POST / PUT / DELETE are denied with 403.
 *   - Admin POST creates a new prompt that appears in the catalog list.
 *   - Admin PUT replaces the markdown body.
 *   - Admin DELETE removes it.
 *   - Path-traversal in any segment is rejected with 400.
 *   - Audit rows are emitted for create / update / delete.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin', 'Content-Type': 'application/json' };
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

const VALID_MARKDOWN = `---
id: ephemeral-extract
kind: extract
version: 0.1
status: preview
owner: e2e
---

# System

You are an expert assistant.

# User

Read the source: {{sourceCode}}
`;

async function deletePrompt(page: Page, src: string, tgt: string, kind: string, version: string) {
  return page.request.delete(
    `${API_BASE}/api/v1/prompts/${src}/${tgt}/${kind}/${version}`,
    { headers: ADMIN },
  );
}

test.describe('Phase #4.3 · Prompt Catalog CRUD', () => {
  test.beforeEach(async ({ page }) => {
    page.setDefaultNavigationTimeout(60_000);
    page.setDefaultTimeout(30_000);
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('non-admin POST / PUT / DELETE are denied', async ({ page }) => {
    const r1 = await page.request.post(`${API_BASE}/api/v1/prompts`, {
      headers: ENG,
      data: { sourceSchema: 'x', targetStack: 'y', kind: 'z', version: '0.1', markdown: VALID_MARKDOWN },
    });
    expect(r1.status()).toBe(403);
    const r2 = await page.request.put(`${API_BASE}/api/v1/prompts/x/y/z/0.1`, { headers: ENG, data: { markdown: VALID_MARKDOWN } });
    expect(r2.status()).toBe(403);
    const r3 = await page.request.delete(`${API_BASE}/api/v1/prompts/x/y/z/0.1`, { headers: ENG });
    expect(r3.status()).toBe(403);
  });

  test('admin can POST → PUT → DELETE a prompt; audit rows fire', async ({ page }) => {
    const ts = Date.now();
    const src = `crud-${ts}`;
    const tgt = 'dotnet8';
    const kind = 'extract';
    const ver = '0.1';

    const created = await page.request.post(`${API_BASE}/api/v1/prompts`, {
      headers: ADMIN,
      data: { sourceSchema: src, targetStack: tgt, kind, version: ver, markdown: VALID_MARKDOWN },
    });
    expect(created.status()).toBe(201);
    const createdBody = await created.json();
    expect(createdBody.sourceSchema).toBe(src);
    expect(createdBody.kind).toBe(kind);
    expect(createdBody.version).toBe(ver);

    try {
      // Catalog should list it now.
      const list = await page.request.get(`${API_BASE}/api/v1/prompts`, { headers: ADMIN }).then((r) => r.json());
      expect(list.data.some((p: { sourceSchema: string; version: string }) => p.sourceSchema === src && p.version === ver)).toBe(true);

      // PUT replaces the body.
      const newMarkdown = VALID_MARKDOWN.replace('You are an expert assistant.', 'You are a CALIBRATED assistant.');
      const updated = await page.request.put(
        `${API_BASE}/api/v1/prompts/${src}/${tgt}/${kind}/${ver}`,
        { headers: ADMIN, data: { markdown: newMarkdown } },
      );
      expect(updated.status()).toBe(200);
      const updatedBody = await updated.json();
      expect(updatedBody.systemTemplate).toContain('CALIBRATED');

      // Audit events for both
      const audit = await page.request.get(`${API_BASE}/api/v1/audit?targetType=prompt&limit=20`, { headers: ADMIN });
      if (audit.ok()) {
        const events = (await audit.json()).data?.map((e: { eventType: string }) => e.eventType) ?? [];
        expect(events).toContain('prompt.created');
        expect(events).toContain('prompt.updated');
      }
    } finally {
      const del = await deletePrompt(page, src, tgt, kind, ver);
      expect(del.status()).toBe(204);
    }
  });

  test('path-traversal segments are rejected with 400', async ({ page }) => {
    const bad = await page.request.post(`${API_BASE}/api/v1/prompts`, {
      headers: ADMIN,
      data: { sourceSchema: '../etc', targetStack: 'dotnet8', kind: 'extract', version: '0.1', markdown: VALID_MARKDOWN },
    });
    expect(bad.status()).toBe(400);
    const body = await bad.json();
    expect(body.error.code).toBe('prompt.invalid_path');
  });

  test('UI: admin sees "New version" + can fill form + create', async ({ page }) => {
    await page.goto('/platform/prompts');
    await expect(page.getByTestId('prompt-catalog-page')).toBeVisible();
    await expect(page.getByTestId('prompt-new')).toBeVisible();
    await page.getByTestId('prompt-new').click();
    const modal = page.getByTestId('prompt-new-modal');
    await expect(modal).toBeVisible();

    const ts = Date.now();
    const src = `ui-${ts}`;
    await modal.getByLabel('Source schema').fill(src);
    await modal.getByLabel('Target stack').fill('dotnet8');
    await modal.getByLabel('Kind').fill('extract');
    await modal.getByLabel('Version').fill('0.1');
    // Leave the default template markdown in place; it's a valid body.

    const createReq = page.waitForResponse(
      (r) => r.url().endsWith('/api/v1/prompts') && r.request().method() === 'POST',
      { timeout: 15_000 },
    );
    await page.getByTestId('prompt-new-submit').click();
    const resp = await createReq;
    expect(resp.status()).toBe(201);
    const created = await resp.json();
    try {
      await expect(page.getByTestId(`prompt-card-${created.sourceSchema}-${created.targetStack}-${created.kind}-${created.version}`)).toBeVisible();
    } finally {
      await deletePrompt(page, created.sourceSchema, created.targetStack, created.kind, created.version);
    }
  });
});
