/**
 * Project settings and artefact kinds (WS7 K1a).
 *
 *   - GET /api/v1/artefact-kinds lists the registry; source code is the only core kind.
 *   - GET/PUT /api/v1/corpora/{id}/settings round-trips, normalises kinds, rejects bad
 *     input with a message per problem, and refuses non-Engineer/Admin writers.
 *   - A settings edit does not change the project's updatedAt (it orders the list).
 *   - GET /api/v1/corpora/{id} carries the settings and a kind on every file.
 *   - UI: the New-project page offers the optional kinds; the settings page saves them
 *     and a banner appears across the project's pages, then disappears when cleared.
 *   - SMEs/Observers see the settings read-only.
 */
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };
const OBS = { 'X-Dev-Persona': 'observer', 'Content-Type': 'application/json' };

const DEFAULTS = {
  artefactKinds: [],
  pack: null,
  banner: null,
  metadata: { tier: null, knowledgeHolder: null, wave: null },
  expectedCounts: {},
  demo: false,
};

async function minpackId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) test.skip(true, 'MINPACK corpus missing.');
  return minpack.id;
}

async function resetSettings(request: APIRequestContext, id: string) {
  await request.put(`${API_BASE}/api/v1/corpora/${id}/settings`, { headers: ENG, data: DEFAULTS });
}

test.describe('Project settings · API', () => {
  test('the registry has source code as the only core kind, and the optional kinds', async ({ page }) => {
    const body = await page.request.get(`${API_BASE}/api/v1/artefact-kinds`).then((r) => r.json());
    const ids = body.data.map((k: { id: string }) => k.id);
    expect(ids[0]).toBe('code');
    expect(ids).toEqual(expect.arrayContaining(['sql', 'job', 'etl', 'config', 'transcript', 'ticket', 'document', 'audio']));
    expect(body.data.filter((k: { core: boolean }) => k.core)).toHaveLength(1);
    const audio = body.data.find((k: { id: string }) => k.id === 'audio');
    expect(audio.text).toBe(false);
    expect(audio.trustTier).toBe('narrative');
  });

  test('settings round-trip: kinds normalised, code dropped, updatedAt untouched', async ({ page }) => {
    const id = await minpackId(page);
    try {
      const before = await page.request.get(`${API_BASE}/api/v1/corpora/${id}`).then((r) => r.json());
      const put = await page.request.put(`${API_BASE}/api/v1/corpora/${id}/settings`, {
        headers: ENG,
        data: {
          ...DEFAULTS,
          artefactKinds: ['Ticket', 'code', 'sql', 'sql'],
          banner: '  Synthetic data for demonstration  ',
          metadata: { tier: 2, knowledgeHolder: 'A Holder', wave: { name: 'Wave 1', endDate: '2027-03-31' } },
        },
      });
      expect(put.status()).toBe(200);
      const saved = await put.json();
      expect(saved.artefactKinds).toEqual(['sql', 'ticket']);
      expect(saved.banner).toBe('Synthetic data for demonstration');

      const got = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/settings`).then((r) => r.json());
      expect(got).toEqual(saved);

      const after = await page.request.get(`${API_BASE}/api/v1/corpora/${id}`).then((r) => r.json());
      expect(after.updatedAt).toBe(before.updatedAt);
      expect(after.settings.artefactKinds).toEqual(['sql', 'ticket']);
      for (const f of after.latestVersion.files) expect(f.kind).toBe('code');
    } finally {
      await resetSettings(page.request, id);
    }
  });

  test('invalid settings are rejected with a message per problem', async ({ page }) => {
    const id = await minpackId(page);
    const res = await page.request.put(`${API_BASE}/api/v1/corpora/${id}/settings`, {
      headers: ENG,
      data: { artefactKinds: ['wizardry'], metadata: { tier: 9 }, bannr: 'typo' },
    });
    expect(res.status()).toBe(400);
    const body = await res.json();
    expect(body.error.code).toBe('settings.invalid');
    expect(body.error.details.length).toBeGreaterThanOrEqual(3);
    expect(body.error.message).toContain('wizardry');
    expect(body.error.message).toContain('bannr');
  });

  test('only an Engineer or an Admin may change settings; anyone may read them', async ({ page }) => {
    const id = await minpackId(page);
    const denied = await page.request.put(`${API_BASE}/api/v1/corpora/${id}/settings`, { headers: OBS, data: DEFAULTS });
    expect(denied.status()).toBe(403);
    expect((await denied.json()).error.code).toBe('auth.engineer_or_admin_required');
    const read = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/settings`, { headers: OBS });
    expect(read.status()).toBe(200);
  });

  test('an unknown project is a 404 for both verbs', async ({ page }) => {
    const nil = '00000000-0000-0000-0000-000000000000';
    expect((await page.request.get(`${API_BASE}/api/v1/corpora/${nil}/settings`)).status()).toBe(404);
    expect((await page.request.put(`${API_BASE}/api/v1/corpora/${nil}/settings`, { headers: ENG, data: DEFAULTS })).status()).toBe(404);
  });
});

test.describe('Project settings · UI', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      if (window.sessionStorage.getItem('e2e-seeded')) return;
      window.sessionStorage.setItem('e2e-seeded', '1');
      window.localStorage.setItem('astra.devPersona', 'engineer');
    });
  });

  test('the New-project page offers the optional kinds; source code is always on', async ({ page }) => {
    await page.goto('/projects/new');
    const picker = page.getByTestId('artefact-kind-picker');
    await expect(picker).toBeVisible();
    const code = page.getByTestId('artefact-kind-code');
    await expect(code).toBeChecked();
    await expect(code).toBeDisabled();
    for (const id of ['sql', 'job', 'etl', 'config', 'transcript', 'ticket', 'document', 'audio']) {
      const box = page.getByTestId(`artefact-kind-${id}`);
      await expect(box).toBeVisible();
      await expect(box).not.toBeChecked();
    }
    await page.getByTestId('artefact-kind-transcript').check();
    await expect(page.getByTestId('artefact-kind-transcript')).toBeChecked();
  });

  test('settings save, and the banner appears then disappears across the project pages', async ({ page }) => {
    const id = await minpackId(page);
    try {
      await page.goto(`/corpora/${id}/settings`);
      await expect(page.getByTestId('project-settings-page')).toBeVisible();
      await page.getByTestId('artefact-kind-ticket').check();
      await page.getByTestId('settings-tier').selectOption('1');
      await page.getByTestId('settings-knowledge-holder').fill('A Holder (synthetic)');
      await page.getByTestId('settings-banner').fill('Synthetic data for demonstration');
      await page.getByTestId('settings-save').click();
      await expect(page.getByTestId('settings-saved')).toBeVisible();

      await page.reload();
      await expect(page.getByTestId('artefact-kind-ticket')).toBeChecked();
      await expect(page.getByTestId('settings-tier')).toHaveValue('1');
      await expect(page.getByTestId('settings-knowledge-holder')).toHaveValue('A Holder (synthetic)');

      // The banner is on this page and on a sibling project page, and on no other route.
      await expect(page.getByTestId('project-banner')).toHaveText('Synthetic data for demonstration');
      await page.goto(`/corpora/${id}`);
      await expect(page.getByTestId('project-banner')).toBeVisible();
      await page.goto('/projects');
      await expect(page.getByTestId('project-banner')).toHaveCount(0);

      await page.goto(`/corpora/${id}/settings`);
      await page.getByTestId('settings-banner').fill('');
      await page.getByTestId('settings-save').click();
      await expect(page.getByTestId('settings-saved')).toBeVisible();
      await page.goto(`/corpora/${id}`);
      await expect(page.getByTestId('project-banner')).toHaveCount(0);
    } finally {
      await resetSettings(page.request, id);
    }
  });

  test('the project page links to the settings', async ({ page }) => {
    const id = await minpackId(page);
    await page.goto(`/corpora/${id}`);
    await page.getByTestId('open-project-settings').click();
    await expect(page.getByTestId('project-settings-page')).toBeVisible();
  });

  test('an Observer sees the settings read-only', async ({ page }) => {
    const id = await minpackId(page);
    await page.addInitScript(() => window.localStorage.setItem('astra.devPersona', 'observer'));
    await page.goto(`/corpora/${id}/settings`);
    await expect(page.getByTestId('settings-readonly')).toBeVisible();
    await expect(page.getByTestId('settings-save')).toBeDisabled();
    await expect(page.getByTestId('artefact-kind-ticket')).toBeDisabled();
  });
});
