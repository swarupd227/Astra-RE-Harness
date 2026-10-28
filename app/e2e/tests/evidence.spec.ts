/**
 * Evidence: the citable pieces of a project (WS7 K2).
 *
 *   - Ingesting code indexes each routine as a chunk with a short id (SRC-xxxxxxxx); ids are stable
 *     across a re-sync for the same file and locator, and an id that no longer exists says so.
 *   - Attached text is indexed in windows; replacing keeps the ids of unchanged windows, removing drops
 *     them, a re-sync carries them forward under the same ids.
 *   - List / search / filter / page; resolve many ids at once (tolerating brackets and case); detail
 *     with the surrounding lines and the neighbouring chunks.
 *   - A project that predates evidence is indexed the first time it is read.
 *   - Artefact bytes are served with Range support; source code is not.
 *   - UI: the evidence page, the panel a citation opens (cited lines marked, Esc closes and returns
 *     focus), several sources read together or one at a time, a deep link, an unresolved citation,
 *     and the panel fits at 1280x720, 1920x1080 and on a phone.
 */
import { expect, test, type Page } from '@playwright/test';
import {
  API_BASE,
  FORTRAN,
  FORTRAN_TWO,
  JSON_ENG,
  attach,
  byPath,
  listEvidence,
  newProject,
  numberedLines,
  summary,
  type EvidenceRow,
} from './helpers/projects';

const PREFIX = 'evidence-e2e';

async function getEvidence(page: Page, id: string, shortId: string, persona = 'engineer') {
  const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/evidence/${encodeURIComponent(shortId)}`, {
    headers: { 'X-Dev-Persona': persona },
  });
  return { status: res.status(), body: await res.json() };
}

async function twoRoutines(page: Page, kinds: string[] = []) {
  const id = await newProject(page, kinds, { source: FORTRAN_TWO, path: 'two.f', prefix: PREFIX });
  const list = await listEvidence(page, id);
  const [alpha, beta] = list.data;
  return { id, alpha: alpha!, beta: beta! };
}

const linesOf = (text: string, from: number, to: number) => text.split('\n').slice(from - 1, to).join('\n');

test.describe('Evidence · API', () => {
  test('ingesting code indexes each routine as citable evidence', async ({ page }) => {
    const { id, alpha, beta } = await twoRoutines(page);
    const list = await listEvidence(page, id);
    expect(list.total).toBe(2);
    expect(list.kinds).toEqual([{ kind: 'code', label: 'Source code', count: 2 }]);
    expect([alpha.label, beta.label]).toEqual(['ALPHA', 'BETA']);
    for (const e of list.data) {
      expect(e.shortId).toMatch(/^SRC-[0-9a-f]{8}$/);
      expect(e.kind).toBe('code');
      expect(e.trustTier).toBe('structured');
      expect(e.path).toBe('two.f');
      expect(e.locator).toBe(`L${e.lineStart}-${e.lineEnd}`);
    }
    expect(alpha.shortId).not.toBe(beta.shortId);
  });

  test('a code chunk opens with its own lines marked in context, and its neighbours', async ({ page }) => {
    const { id, alpha, beta } = await twoRoutines(page);
    const a = (await getEvidence(page, id, alpha.shortId)).body;
    expect(a.text).toBe(linesOf(FORTRAN_TWO, alpha.lineStart!, alpha.lineEnd!));
    expect(a.view.highlightStart).toBe(alpha.lineStart);
    expect(a.view.highlightEnd).toBe(alpha.lineEnd);
    expect(a.view.startLine).toBe(1);
    expect(a.view.text.split('\n')).toHaveLength(a.view.endLine - a.view.startLine + 1);
    expect(a.structured.routine).toBe('ALPHA');
    expect(a.structured.subroutineId).toMatch(/^[0-9a-f-]{36}$/);
    expect(a.previous).toBeNull();
    expect(a.next).toBe(beta.shortId);

    const b = (await getEvidence(page, id, beta.shortId)).body;
    expect(b.previous).toBe(alpha.shortId);
    expect(b.next).toBeNull();
  });

  test('ids are stable across a re-sync; a routine that moves gets a new id and the old one says it is gone', async ({ page }) => {
    const { id, alpha, beta } = await twoRoutines(page);
    const resync = (content: string) =>
      page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
        headers: JSON_ENG,
        data: { files: [{ path: 'two.f', content }] },
      });

    expect((await resync(FORTRAN_TWO)).status()).toBe(200);
    const same = await listEvidence(page, id);
    expect(same.data.map((e) => e.shortId)).toEqual([alpha.shortId, beta.shortId]);

    // A comment line on top moves both routines down one line: new locators, new ids.
    expect((await resync('C     moved down a line\n' + FORTRAN_TWO)).status()).toBe(200);
    const moved = await listEvidence(page, id);
    expect(moved.total).toBe(2);
    expect(moved.data.map((e) => e.shortId)).not.toContain(alpha.shortId);

    const gone = await getEvidence(page, id, alpha.shortId);
    expect(gone.status).toBe(404);
    expect(gone.body.error.code).toBe('evidence.not_found');
    expect(gone.body.error.message).toContain(alpha.shortId);
  });

  test('a malformed id is a 400, an unknown one a 404, an unknown project a 404', async ({ page }) => {
    const { id } = await twoRoutines(page);
    const bad = await getEvidence(page, id, 'nonsense');
    expect(bad.status).toBe(400);
    expect(bad.body.error.code).toBe('evidence.invalid_id');
    expect((await getEvidence(page, id, 'SRC-00000000')).status).toBe(404);

    const nil = '00000000-0000-0000-0000-000000000000';
    expect((await getEvidence(page, nil, 'SRC-00000000')).status).toBe(404);
    expect((await page.request.get(`${API_BASE}/api/v1/corpora/${nil}/evidence`)).status()).toBe(404);
  });

  test('resolve answers many ids at once, tolerates brackets and case, and names the missing', async ({ page }) => {
    const { id, alpha, beta } = await twoRoutines(page);
    const post = (data: unknown) =>
      page.request.post(`${API_BASE}/api/v1/corpora/${id}/evidence/resolve`, { headers: JSON_ENG, data });

    const bracketed = `[${beta.shortId.toLowerCase()}]`;
    const res = await post({ ids: [alpha.shortId, bracketed, 'SRC-00000000', 'nonsense'] });
    expect(res.status()).toBe(200);
    const body = await res.json();
    expect(body.data[alpha.shortId].label).toBe('ALPHA');
    expect(body.data[bracketed].label).toBe('BETA');
    expect(body.data['SRC-00000000']).toBeNull();
    expect(body.data['nonsense']).toBeNull();
    expect(body.found).toBe(2);
    expect(body.missing.sort()).toEqual(['SRC-00000000', 'nonsense']);

    const detailed = await (await post({ ids: [alpha.shortId], detail: true })).json();
    expect(detailed.data[alpha.shortId].text).toContain('SUBROUTINE ALPHA');

    expect((await post({})).status()).toBe(400);
    expect((await post({ ids: [] })).status()).toBe(400);
    expect((await post({ ids: Array.from({ length: 201 }, (_, i) => `SRC-${i.toString(16).padStart(8, '0')}`) })).status()).toBe(400);
    expect((await post({ ids: Array.from({ length: 13 }, (_, i) => `SRC-${i.toString(16).padStart(8, '0')}`), detail: true })).status()).toBe(400);
  });

  test('attached text is indexed in windows; replacing keeps unchanged ids; shrinking and removing drop the rest', async ({ page }) => {
    const id = await newProject(page, ['sql'], { prefix: PREFIX });
    const path = 'db/ddl.sql';
    const sql80 = numberedLines(80, 'SELECT');

    const first = await attach(id, [{ path, data: sql80 }]);
    expect(byPath(first.body.files, path).chunks).toBe(2);

    const sql = await listEvidence(page, id, { kind: 'sql' });
    expect(sql.total).toBe(2);
    expect(sql.data.map((e) => e.locator)).toEqual(['L1-40', 'L41-80']);
    expect(sql.data.map((e) => e.label)).toEqual(['ddl.sql · lines 1–40', 'ddl.sql · lines 41–80']);
    for (const e of sql.data) {
      expect(e.shortId).toMatch(/^SQL-[0-9a-f]{8}$/);
      expect(e.trustTier).toBe('structured');
    }
    const [w1, w2] = sql.data as [EvidenceRow, EvidenceRow];

    const d1 = (await getEvidence(page, id, w1.shortId)).body;
    expect(d1.text).toBe(linesOf(sql80, 1, 40));
    expect(d1.view).toMatchObject({ highlightStart: 1, highlightEnd: 40 });

    // Change a line in the second window only.
    const edited = sql80.replace('SELECT 70', 'SELECT 70 -- changed');
    const replaced = await attach(id, [{ path, data: edited }]);
    expect(byPath(replaced.body.files, path).status).toBe('replaced');
    const after = await listEvidence(page, id, { kind: 'sql' });
    expect(after.data.map((e) => e.shortId)).toEqual([w1.shortId, w2.shortId]);
    expect((await getEvidence(page, id, w1.shortId)).body.text).toBe(linesOf(sql80, 1, 40));
    expect((await getEvidence(page, id, w2.shortId)).body.text).toContain('SELECT 70 -- changed');

    // Shorter file: the second window shrinks to L41-50 (a new locator, so a new id), the old one is gone.
    await attach(id, [{ path, data: numberedLines(50, 'SELECT') }]);
    const shrunk = await listEvidence(page, id, { kind: 'sql' });
    expect(shrunk.data.map((e) => e.locator)).toEqual(['L1-40', 'L41-50']);
    expect((await getEvidence(page, id, w2.shortId)).status).toBe(404);

    // Removing the file removes its evidence.
    const files = (await summary(page, id)).kinds.find((k: { id: string }) => k.id === 'sql').files;
    expect(files[0].chunkCount).toBe(2);
    await page.request.delete(`${API_BASE}/api/v1/corpora/${id}/artefacts?path=${encodeURIComponent(path)}`, { headers: JSON_ENG });
    const none = await listEvidence(page, id, { kind: 'sql' });
    expect(none.total).toBe(0);
    expect(none.kinds.map((k) => k.kind)).toEqual(['code']);
  });

  test('search, kind and path filters, and paging', async ({ page }) => {
    const { id, alpha } = await twoRoutines(page, ['sql', 'ticket']);
    await attach(id, [
      { path: 'db/ddl.sql', data: numberedLines(100, 'SELECT col') }, // 3 windows
      { path: 'tickets/inc.csv', data: 'number,short_description\nINC-777,Interface fails after midnight\n' },
    ]);

    const all = await listEvidence(page, id);
    expect(all.total).toBe(6);
    expect(all.kinds.map((k) => [k.kind, k.count])).toEqual([['code', 2], ['sql', 3], ['ticket', 1]]);
    // Files in path order: db/…, tickets/…, two.f.
    expect(all.data.map((e) => e.kind)).toEqual(['sql', 'sql', 'sql', 'ticket', 'code', 'code']);

    expect((await listEvidence(page, id, { kind: 'sql' })).total).toBe(3);
    expect((await listEvidence(page, id, { path: 'db/ddl.sql' })).total).toBe(3);

    const byText = await listEvidence(page, id, { q: 'midnight' });
    expect(byText.data.map((e) => e.kind)).toEqual(['ticket']);
    expect((await listEvidence(page, id, { q: 'alpha' })).data.map((e) => e.shortId)).toEqual([alpha.shortId]);
    expect((await listEvidence(page, id, { q: alpha.shortId.toLowerCase() })).total).toBe(1);

    // The facet counts follow the search but ignore the kind filter.
    const facets = await listEvidence(page, id, { q: 'ddl.sql', kind: 'code' });
    expect(facets.total).toBe(0);
    expect(facets.kinds.map((k) => [k.kind, k.count])).toEqual([['sql', 3]]);

    // LIKE wildcards in the search are text, not patterns.
    // (Five underscores would match any five characters if they were a pattern; as text they match nothing.)
    expect((await listEvidence(page, id, { q: '%' })).total).toBe(0);
    expect((await listEvidence(page, id, { q: '_____' })).total).toBe(0);

    const p1 = await listEvidence(page, id, { limit: 2, offset: 0 });
    const p2 = await listEvidence(page, id, { limit: 2, offset: 2 });
    const p3 = await listEvidence(page, id, { limit: 2, offset: 4 });
    const p4 = await listEvidence(page, id, { limit: 2, offset: 6 });
    expect([p1, p2, p3].map((p) => p.data.length)).toEqual([2, 2, 2]);
    expect(p4.data).toHaveLength(0);
    expect(p4.total).toBe(6);
    expect(new Set([...p1.data, ...p2.data, ...p3.data].map((e) => e.shortId)).size).toBe(6);
  });

  test('evidence of attached files survives a re-sync under the same ids', async ({ page }) => {
    const id = await newProject(page, ['sql'], { prefix: PREFIX });
    await attach(id, [{ path: 'db/ddl.sql', data: numberedLines(80, 'SELECT') }]);
    const before = await listEvidence(page, id, { kind: 'sql' });

    const resync = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
      headers: JSON_ENG,
      data: { files: [{ path: 'e2eart.f', content: FORTRAN + 'C     changed\n' }] },
    });
    expect(resync.status()).toBe(200);

    const after = await listEvidence(page, id, { kind: 'sql' });
    expect(after.data.map((e) => e.shortId)).toEqual(before.data.map((e) => e.shortId));
    // The carried chunk belongs to the carried file in the new version, so it resolves with its text.
    const detail = (await getEvidence(page, id, after.data[1]!.shortId)).body;
    expect(detail.text).toContain('SELECT 80');
    expect(after.data[0]!.fileId).not.toBe(before.data[0]!.fileId);
  });

  test('a project that predates evidence is indexed the first time it is read', async ({ page }) => {
    const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
    const minpack = corpora.data.find((c: { name: string }) => c.name.includes('MINPACK'));
    test.skip(!minpack, 'MINPACK demo project missing.');
    const list = await listEvidence(page, minpack.id, { limit: 5 });
    expect(list.total).toBeGreaterThan(0);
    for (const e of list.data) expect(e.shortId).toMatch(/^SRC-[0-9a-f]{8}$/);
  });

  test('anyone can read evidence, including an Observer', async ({ page }) => {
    const { id, alpha } = await twoRoutines(page);
    expect((await listEvidence(page, id, {}, 'observer')).total).toBe(2);
    expect((await getEvidence(page, id, alpha.shortId, 'observer')).status).toBe(200);
  });

  test('artefact bytes are served with range support; source code is not served', async ({ page }) => {
    const id = await newProject(page, ['audio'], { prefix: PREFIX });
    const bytes = Buffer.from(Array.from({ length: 777 }, (_, i) => i % 256));
    await attach(id, [{ path: 'kt/clip.mp3', data: bytes }]);
    const fileId = (await summary(page, id)).kinds.find((k: { id: string }) => k.id === 'audio').files[0].id;
    const url = `${API_BASE}/api/v1/artefacts/${fileId}/content`;

    const whole = await fetch(url);
    expect(whole.status).toBe(200);
    expect(whole.headers.get('content-type')).toBe('audio/mpeg');
    expect(whole.headers.get('accept-ranges')).toBe('bytes');
    expect(Buffer.from(await whole.arrayBuffer()).equals(bytes)).toBe(true);

    const part = await fetch(url, { headers: { Range: 'bytes=10-19' } });
    expect(part.status).toBe(206);
    expect(part.headers.get('content-range')).toBe('bytes 10-19/777');
    expect(Buffer.from(await part.arrayBuffer()).equals(bytes.subarray(10, 20))).toBe(true);

    const tail = await fetch(url, { headers: { Range: 'bytes=-5' } });
    expect(tail.status).toBe(206);
    expect(Buffer.from(await tail.arrayBuffer()).equals(bytes.subarray(772))).toBe(true);

    expect((await fetch(url, { headers: { Range: 'bytes=900-' } })).status).toBe(416);
    expect((await fetch(url, { headers: { 'X-Dev-Persona': 'observer' } })).status).toBe(200);

    const corpus = await page.request.get(`${API_BASE}/api/v1/corpora/${id}`).then((r) => r.json());
    const codeFileId = corpus.latestVersion.files.find((f: { kind: string }) => f.kind === 'code').id;
    expect((await fetch(`${API_BASE}/api/v1/artefacts/${codeFileId}/content`)).status).toBe(404);
    expect((await fetch(`${API_BASE}/api/v1/artefacts/00000000-0000-0000-0000-000000000000/content`)).status).toBe(404);
  });
});

test.describe('Evidence · UI', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      if (window.sessionStorage.getItem('e2e-seeded')) return;
      window.sessionStorage.setItem('e2e-seeded', '1');
      window.localStorage.setItem('astra.devPersona', 'engineer');
    });
  });

  /** Two routines, two windows of SQL, one ticket: five pieces of evidence in three kinds. */
  async function seed(page: Page) {
    const { id, alpha, beta } = await twoRoutines(page, ['sql', 'ticket']);
    await attach(id, [
      { path: 'db/ddl.sql', data: numberedLines(80, 'SELECT col') },
      { path: 'tickets/inc.csv', data: 'number,short_description\nINC-777,Interface fails after midnight\n' },
    ]);
    const all = await listEvidence(page, id);
    const sql = all.data.filter((e) => e.kind === 'sql');
    const ticket = all.data.find((e) => e.kind === 'ticket')!;
    return { id, alpha, beta, sql, ticket };
  }

  const row = (page: Page, shortId: string) => page.locator(`[data-testid="evidence-row"][data-short-id="${shortId}"]`);

  test('the page lists what can be cited, filters by kind and searches', async ({ page }) => {
    const { id } = await seed(page);
    await page.goto(`/corpora/${id}/evidence`);
    await expect(page.getByTestId('evidence-row')).toHaveCount(5);
    await expect(page.getByTestId('evidence-count')).toHaveText('Showing 5 of 5');
    await expect(page.getByTestId('evidence-kind-all')).toContainText('5');
    await expect(page.getByTestId('evidence-kind-sql')).toContainText('2');

    await page.getByTestId('evidence-kind-sql').click();
    await expect(page.getByTestId('evidence-row')).toHaveCount(2);
    await page.getByTestId('evidence-kind-sql').click();
    await expect(page.getByTestId('evidence-row')).toHaveCount(5);

    await page.getByTestId('evidence-search').fill('midnight');
    await expect(page.getByTestId('evidence-row')).toHaveCount(1);
    await expect(page.getByTestId('evidence-row')).toContainText('inc.csv');

    await page.getByTestId('evidence-search').fill('zzzz-nothing');
    await expect(page.getByTestId('evidence-no-match')).toBeVisible();
  });

  test('a citation opens the evidence with the cited lines marked; Escape closes and returns focus', async ({ page }) => {
    const { id, alpha } = await seed(page);
    await page.goto(`/corpora/${id}/evidence`);

    const chip = row(page, alpha.shortId).getByTestId('citation-chip');
    await chip.click();

    const drawer = page.getByTestId('evidence-drawer');
    await expect(drawer).toBeVisible();
    await expect(drawer.getByTestId('evidence-drawer-count')).toHaveText('1 source');
    const card = drawer.getByTestId('evidence-card');
    await expect(card).toHaveAttribute('data-short-id', alpha.shortId);
    await expect(card).toContainText('ALPHA');
    await expect(card).toContainText('two.f');

    const code = card.getByTestId('evidence-code');
    await expect(code).toBeVisible();
    const marked = code.locator('[data-marked="true"]');
    await expect(marked).toHaveCount(alpha.lineEnd! - alpha.lineStart! + 1);
    await expect(marked.first()).toContainText('SUBROUTINE ALPHA');
    await expect(card.getByTestId('evidence-open-routine')).toBeVisible();

    await page.keyboard.press('Escape');
    await expect(drawer).toHaveCount(0);
    await expect(chip).toBeFocused();
  });

  test('the cited lines are scrolled into view inside the code box, whichever card they are in', async ({ page }) => {
    const { id, alpha, sql } = await seed(page);
    await page.goto(`/corpora/${id}/evidence?open=${alpha.shortId},${sql[0]!.shortId},${sql[1]!.shortId}`);
    const cards = page.getByTestId('evidence-drawer').getByTestId('evidence-card');
    await expect(cards).toHaveCount(3);

    // Second SQL window cites lines 41-80; the box shows the context before it, so line 41 is part-way down.
    for (const [i, firstCited] of [[0, alpha.lineStart!], [1, 1], [2, 41]] as const) {
      const box = cards.nth(i).getByTestId('evidence-code');
      const line = box.locator(`[data-line="${firstCited}"]`);
      await expect(line).toHaveAttribute('data-marked', 'true');
      const inside = await box.evaluate((b, n) => {
        const el = b.querySelector(`[data-line="${n}"]`) as HTMLElement;
        const a = b.getBoundingClientRect();
        const r = el.getBoundingClientRect();
        return r.top >= a.top - 1 && r.bottom <= a.bottom + 1;
      }, firstCited);
      expect(inside, `line ${firstCited} of card ${i} is inside its box`).toBe(true);
    }
  });

  test('opening evidence needs nothing from outside the app and its API (it works offline)', async ({ page }) => {
    const { id, alpha, sql } = await seed(page);
    const local = new Set(['localhost', '127.0.0.1', '[::1]']);
    const outside: string[] = [];
    page.on('request', (r) => {
      const u = new URL(r.url());
      if (u.protocol.startsWith('http') && !local.has(u.hostname)) outside.push(r.url());
    });

    await page.goto(`/corpora/${id}/evidence?open=${alpha.shortId},${sql[0]!.shortId}`);
    const code = page.getByTestId('evidence-drawer').getByTestId('evidence-code');
    await expect(code).toHaveCount(2);
    // Wait for the SQL grammar chunk, so a late request for it would be seen.
    await expect(page.getByTestId('evidence-drawer').locator('[class*="hljs-"]').first()).toBeVisible();
    await page.waitForTimeout(500);
    expect(outside, `requests outside the app: ${outside.join(', ')}`).toEqual([]);
  });

  test('the code view is coloured from the bundle, per language', async ({ page }) => {
    const { id, sql } = await seed(page);
    await page.goto(`/corpora/${id}/evidence`);
    await row(page, sql[0]!.shortId).getByTestId('citation-chip').click();
    // The SQL grammar is a lazy chunk; once it has loaded the keywords carry highlight classes.
    await expect(page.getByTestId('evidence-drawer').locator('[class*="hljs-"]').first()).toBeVisible();
  });

  test('picked sources open together, then one at a time with tabs and neighbours', async ({ page }) => {
    const { id, alpha, sql, ticket } = await seed(page);
    await page.goto(`/corpora/${id}/evidence`);

    await expect(page.getByTestId('evidence-show-together')).toBeDisabled();
    for (const s of [alpha, sql[0]!, ticket]) await row(page, s.shortId).getByTestId('evidence-pick').check();
    await expect(page.getByTestId('evidence-show-together')).toContainText('(3)');
    await page.getByTestId('evidence-show-together').click();

    const drawer = page.getByTestId('evidence-drawer');
    await expect(drawer.getByTestId('evidence-drawer-count')).toHaveText('3 sources');
    const cards = drawer.getByTestId('evidence-card');
    await expect(cards).toHaveCount(3);
    await expect(cards).toHaveText([/ALPHA/, /ddl\.sql · lines 1–40/, /inc\.csv/]);
    await expect(drawer.locator('[data-kind="code"]')).toHaveCount(1);
    await expect(drawer.locator('[data-kind="sql"]')).toHaveCount(1);
    await expect(drawer.locator('[data-kind="ticket"]')).toHaveCount(1);
    // The three sources side by side: the routine, the SQL window and the ticket's own words.
    await expect(drawer.locator('[data-kind="ticket"]').getByTestId('evidence-code')).toContainText('Interface fails after midnight');

    await drawer.getByTestId('evidence-mode-one').click();
    await expect(cards).toHaveCount(1);
    await expect(drawer.getByRole('tab')).toHaveCount(3);
    await drawer.getByRole('tab', { name: new RegExp(sql[0]!.shortId) }).click();
    await expect(cards).toHaveAttribute('data-short-id', sql[0]!.shortId);

    await drawer.getByTestId('evidence-next').click();
    await expect(cards).toHaveAttribute('data-short-id', sql[1]!.shortId);
    await drawer.getByTestId('evidence-previous').click();
    await expect(cards).toHaveAttribute('data-short-id', sql[0]!.shortId);

    await drawer.getByTestId('evidence-mode-stack').click();
    await expect(cards).toHaveCount(3);
  });

  test('a link opens sources on arrival, and a citation that does not resolve says so', async ({ page }) => {
    const { id, alpha } = await seed(page);
    await page.goto(`/corpora/${id}/evidence?open=${alpha.shortId},SRC-00000000`);

    const drawer = page.getByTestId('evidence-drawer');
    await expect(drawer).toBeVisible();
    await expect(drawer.getByTestId('evidence-drawer-count')).toHaveText('2 sources');
    await expect(drawer.getByTestId('evidence-card')).toHaveCount(1);
    const missing = drawer.getByTestId('evidence-unresolved');
    await expect(missing).toBeVisible();
    await expect(missing).toContainText('SRC-00000000');
    await expect(missing).toContainText('cannot be resolved');
  });

  test('a file in the Sources card links to its evidence', async ({ page }) => {
    const { id } = await seed(page);
    await page.goto(`/corpora/${id}`);
    await page.getByTestId('artefact-kind-tile-sql').getByRole('button').click();
    const link = page.getByTestId('artefact-files-sql').getByTestId('artefact-file-evidence');
    await expect(link).toHaveText('2 chunks');
    await link.click();
    await expect(page.getByTestId('evidence-path-filter')).toContainText('db/ddl.sql');
    await expect(page.getByTestId('evidence-row')).toHaveCount(2);
    await page.getByTestId('evidence-path-filter').getByRole('button').click();
    await expect(page.getByTestId('evidence-row')).toHaveCount(5);
  });

  test('an Observer can browse and open evidence', async ({ page }) => {
    const { id, alpha } = await seed(page);
    await page.addInitScript(() => window.localStorage.setItem('astra.devPersona', 'observer'));
    await page.goto(`/corpora/${id}/evidence`);
    await row(page, alpha.shortId).getByTestId('citation-chip').click();
    await expect(page.getByTestId('evidence-card')).toHaveAttribute('data-short-id', alpha.shortId);
  });

  for (const vp of [
    { name: '1280x720', width: 1280, height: 720 },
    { name: '1920x1080', width: 1920, height: 1080 },
    { name: 'a phone', width: 390, height: 844 },
  ]) {
    test(`the panel fits on ${vp.name} and the page never scrolls sideways`, async ({ page }) => {
      const { id, alpha, sql, ticket } = await seed(page);
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto(`/corpora/${id}/evidence?open=${[alpha.shortId, sql[0]!.shortId, ticket.shortId].join(',')}`);

      const drawer = page.getByTestId('evidence-drawer');
      await expect(drawer.getByTestId('evidence-card')).toHaveCount(3);
      const box = (await drawer.boundingBox())!;
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(vp.width + 1);
      expect(Math.round(box.height)).toBe(vp.height);

      const scrollWidth = await page.evaluate(() => document.documentElement.scrollWidth);
      expect(scrollWidth).toBeLessThanOrEqual(vp.width);

      await page.getByTestId('evidence-drawer-close').click();
      await expect(drawer).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(vp.width);
    });
  }
});
