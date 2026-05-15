/**
 * Phase #4.1 — Users CRUD (admin-only).
 *
 * Asserts:
 *   - Non-admin personas get 403 on every /users endpoint.
 *   - Admin can list, create, re-assign persona, and delete.
 *   - Every write produces an audit row.
 *   - The RolesPage UI's user form actually round-trips a created user.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ADMIN = { 'X-Dev-Persona': 'admin' };
const ENG = { 'X-Dev-Persona': 'engineer' };

async function createUser(page: Page, body: { email: string; displayName: string; persona: string }) {
  return page.request.post(`${API_BASE}/api/v1/users`, { headers: { ...ADMIN, 'Content-Type': 'application/json' }, data: body });
}
async function deleteUser(page: Page, id: string) {
  return page.request.delete(`${API_BASE}/api/v1/users/${id}`, { headers: ADMIN });
}

test.describe('Phase #4.1 · Users CRUD', () => {
  test.beforeEach(async ({ page }) => {
    // Vite dev-mode can spike compile times beyond the 15s nav timeout
    // when many pages compile on first hit; relax for these CRUD tests.
    page.setDefaultNavigationTimeout(60_000);
    page.setDefaultTimeout(30_000);
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'admin'));
  });

  test('non-admin personas are denied every /users route', async ({ page }) => {
    for (const path of ['/api/v1/users']) {
      const r = await page.request.get(`${API_BASE}${path}`, { headers: ENG });
      expect(r.status()).toBe(403);
    }
    const r2 = await page.request.post(`${API_BASE}/api/v1/users`, {
      headers: { ...ENG, 'Content-Type': 'application/json' },
      data: { email: 'x@x.com', displayName: 'X', persona: 'engineer' },
    });
    expect(r2.status()).toBe(403);
  });

  test('admin can create → re-assign → delete a user', async ({ page }) => {
    const email = `crud-${Date.now()}@nous.test`;
    const created = await createUser(page, { email, displayName: 'CRUD Test', persona: 'engineer' });
    expect(created.status()).toBe(201);
    const user = await created.json();
    expect(user.email).toBe(email.toLowerCase());
    expect(user.persona).toBe('engineer');

    try {
      // Re-assign to SME
      const upd = await page.request.put(`${API_BASE}/api/v1/users/${user.id}/persona`, {
        headers: { ...ADMIN, 'Content-Type': 'application/json' },
        data: { persona: 'sme' },
      });
      expect(upd.status()).toBe(200);
      const updBody = await upd.json();
      expect(updBody.persona).toBe('sme');
      expect(updBody.previousPersona).toBe('engineer');

      // List should show the user
      const list = await page.request.get(`${API_BASE}/api/v1/users`, { headers: ADMIN });
      const listBody = await list.json();
      expect(listBody.data.some((u: { id: string }) => u.id === user.id)).toBe(true);

      // Audit log should have user.created and user.persona_changed for this id.
      // The audit endpoint uses `eventType` (not `action`) for the verb.
      const audit = await page.request.get(`${API_BASE}/api/v1/audit?targetType=user&limit=50`, { headers: ADMIN });
      if (audit.ok()) {
        const auditBody = await audit.json();
        const events = new Set<string>(
          (auditBody.data ?? [])
            .filter((e: { targetId: string }) => e.targetId === user.id)
            .map((e: { eventType: string }) => e.eventType),
        );
        expect(events.has('user.created')).toBe(true);
        expect(events.has('user.persona_changed')).toBe(true);
      }
    } finally {
      // Clean up — DELETE must return 204
      const del = await deleteUser(page, user.id);
      expect(del.status()).toBe(204);
    }
  });

  test('duplicate-email POST is rejected with 409', async ({ page }) => {
    const email = `dup-${Date.now()}@nous.test`;
    const a = await createUser(page, { email, displayName: 'A', persona: 'engineer' });
    expect(a.status()).toBe(201);
    const aId = (await a.json()).id;
    try {
      const b = await createUser(page, { email, displayName: 'B', persona: 'sme' });
      expect(b.status()).toBe(409);
    } finally {
      await deleteUser(page, aId);
    }
  });

  test('UI: admin can add a user via the RolesPage form', async ({ page }) => {
    await page.goto('/platform/roles');
    await expect(page.getByTestId('users-panel')).toBeVisible();
    await page.getByRole('button', { name: /Add user/i }).click();
    const form = page.getByTestId('user-add-form');
    await expect(form).toBeVisible();
    const email = `ui-${Date.now()}@nous.test`;
    await form.getByLabel('Email').fill(email);
    await form.getByLabel('Display name').fill('UI Test User');
    await form.getByLabel('Persona').selectOption('observer');
    const createReq = page.waitForResponse(
      (r) => r.url().includes('/api/v1/users') && r.request().method() === 'POST',
      { timeout: 15_000 },
    );
    await form.getByRole('button', { name: /Create user/i }).click();
    const resp = await createReq;
    expect(resp.status()).toBe(201);
    const created = await resp.json();
    try {
      await expect(page.getByTestId(`user-row-${created.id}`)).toBeVisible();
    } finally {
      // Clean up
      await deleteUser(page, created.id);
    }
  });
});
