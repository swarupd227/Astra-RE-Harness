/**
 * Attaching evidence to a project (WS7 K1b).
 *
 *   - POST /api/v1/corpora/{id}/artefacts answers per file: added | replaced | unchanged |
 *     rejected, and every rejection carries a stable code and a message. Nothing is dropped
 *     silently.
 *   - Kinds a project has not switched on, source code, unsafe paths, unknown types, empty,
 *     duplicate and non-UTF-8 files are all refused with a reason.
 *   - Re-attaching the same text (even with different line endings) is "unchanged"; changed
 *     text is "replaced". Binary files (audio) are stored as bytes.
 *   - GET lists per-kind counts and sizes; DELETE removes an artefact but never source code.
 *   - A code re-sync carries attached artefacts forward and keeps the code file count.
 *   - A zip ingest reports what it skipped instead of dropping files without a word.
 *   - Engineers, SMEs and Admins write; Observers read.
 *   - UI: Sources tiles, dropzone, per-file result list, remove with confirmation, read-only.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const JSON_ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

const FORTRAN = `\
      SUBROUTINE E2EART(X, Y)
      INTEGER X, Y
      Y = X + 1
      RETURN
      END
`;

const ALL_KINDS = ['sql', 'job', 'transcript', 'ticket', 'document', 'audio'];

type Item = { path: string; data: string | Buffer };
type FileResult = {
  path: string;
  kind: string | null;
  status: 'added' | 'replaced' | 'unchanged' | 'rejected';
  code: string | null;
  message: string | null;
  sizeBytes: number;
  ambiguous: boolean;
};

async function newProject(page: Page, kinds: string[] = []): Promise<string> {
  const res = await page.request.post(`${API_BASE}/api/v1/ingest/text`, {
    headers: JSON_ENG,
    data: { name: `artefacts-e2e-${Date.now()}-${Math.floor(Math.random() * 1e4)}`, files: [{ path: 'e2eart.f', content: FORTRAN }] },
  });
  expect(res.ok()).toBe(true);
  const { corpusId } = await res.json();
  if (kinds.length > 0) await setKinds(page, corpusId, kinds);
  return corpusId as string;
}

async function setKinds(page: Page, id: string, kinds: string[]) {
  const put = await page.request.put(`${API_BASE}/api/v1/corpora/${id}/settings`, {
    headers: JSON_ENG,
    data: {
      artefactKinds: kinds,
      pack: null,
      banner: null,
      metadata: { tier: null, knowledgeHolder: null, wave: null },
      expectedCounts: {},
      demo: false,
    },
  });
  expect(put.status()).toBe(200);
}

/** Multipart through Node's own fetch: Playwright's `multipart` option cannot repeat a field name. */
async function attach(id: string, items: Item[], opts: { persona?: string; kind?: string } = {}) {
  const fd = new FormData();
  for (const it of items) {
    const bytes = typeof it.data === 'string' ? Buffer.from(it.data, 'utf8') : it.data;
    fd.append('files', new Blob([bytes]), it.path.split('/').pop() ?? 'file');
    fd.append('path', it.path);
  }
  if (opts.kind) fd.append('kind', opts.kind);
  const res = await fetch(`${API_BASE}/api/v1/corpora/${id}/artefacts`, {
    method: 'POST',
    headers: { 'X-Dev-Persona': opts.persona ?? 'engineer' },
    body: fd,
  });
  return { status: res.status, body: await res.json() };
}

function byPath(files: FileResult[], path: string): FileResult {
  const f = files.find((x) => x.path === path);
  if (!f) throw new Error(`no result for ${path}; got ${files.map((x) => x.path).join(', ')}`);
  return f;
}

async function summary(page: Page, id: string, persona = 'engineer') {
  return page.request.get(`${API_BASE}/api/v1/corpora/${id}/artefacts`, { headers: { 'X-Dev-Persona': persona } }).then((r) => r.json());
}

/** A zip with every entry stored uncompressed, so no compression library is needed. */
function storedZip(entries: { name: string; data: string }[]): Buffer {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc32 = (b: Buffer) => {
    let c = 0xffffffff;
    for (const byte of b) c = crcTable[(c ^ byte) & 0xff]! ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const e of entries) {
    const name = Buffer.from(e.name, 'utf8');
    const data = Buffer.from(e.data, 'utf8');
    const crc = crc32(data);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(data.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(name.length, 26);
    locals.push(local, name, data);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(data.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(offset, 42);
    centrals.push(central, name);
    offset += 30 + name.length + data.length;
  }
  const centralBytes = Buffer.concat(centrals);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralBytes.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, centralBytes, end]);
}

test.describe('Artefacts · API', () => {
  test('a kind the project has not switched on is refused, naming the kind and where to enable it', async ({ page }) => {
    const id = await newProject(page);
    const { status, body } = await attach(id, [{ path: 'tickets/export.csv', data: 'number,short_description\nINC1,x\n' }]);
    expect(status).toBe(200);
    expect(body.counts).toEqual({ added: 0, replaced: 0, unchanged: 0, rejected: 1 });
    const f = byPath(body.files, 'tickets/export.csv');
    expect(f.status).toBe('rejected');
    expect(f.code).toBe('kind_not_enabled');
    expect(f.message).toContain('Tickets');
    expect(f.message).toContain('settings');
  });

  test('a mixed drop is added per kind; binary audio is stored as bytes; an ambiguous type is flagged', async ({ page }) => {
    const id = await newProject(page, ALL_KINDS);
    const audio = Buffer.from(Array.from({ length: 777 }, (_, i) => i % 256)); // includes bytes that are not valid text
    const { status, body } = await attach(id, [
      { path: 'tickets/export.csv', data: 'number,short_description\nINC1,x\nINC2,y\n' },
      { path: 'db/ddl.sql', data: 'CREATE TABLE T (ID INT);\n' },
      { path: 'kt/session.vtt', data: 'WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nHello\n' },
      { path: 'kt/session.mp3', data: audio },
      { path: 'batch/jobs.xml', data: '<JOBS><JOB name="A"/></JOBS>\n' },
    ]);
    expect(status).toBe(200);
    expect(body.counts).toEqual({ added: 5, replaced: 0, unchanged: 0, rejected: 0 });
    expect(byPath(body.files, 'tickets/export.csv').kind).toBe('ticket');
    expect(byPath(body.files, 'db/ddl.sql').kind).toBe('sql');
    expect(byPath(body.files, 'kt/session.vtt').kind).toBe('transcript');
    expect(byPath(body.files, 'kt/session.mp3').kind).toBe('audio');
    expect(byPath(body.files, 'kt/session.mp3').sizeBytes).toBe(777);
    const xml = byPath(body.files, 'batch/jobs.xml');
    expect(xml.kind).toBe('job'); // first in registry order…
    expect(xml.ambiguous).toBe(true); // …and flagged, because documents take .xml too

    const s = await summary(page, id);
    const kind = (k: string) => s.kinds.find((x: { id: string }) => x.id === k);
    expect(kind('ticket').fileCount).toBe(1);
    expect(kind('audio').totalBytes).toBe(777);
    expect(kind('audio').files[0].path).toBe('kt/session.mp3');
    expect(kind('code').fileCount).toBe(1);
    expect(kind('code').files).toEqual([]); // source code is listed on the project page
    expect(kind('code').core).toBe(true);
    expect(kind('sql').enabled).toBe(true);
  });

  test('the same text is unchanged (even with CRLF line endings), changed text is replaced', async ({ page }) => {
    const id = await newProject(page, ['ticket']);
    const path = 'tickets/export.csv';
    const first = await attach(id, [{ path, data: 'number,short_description\nINC1,x\n' }]);
    expect(byPath(first.body.files, path).status).toBe('added');

    const same = await attach(id, [{ path, data: 'number,short_description\nINC1,x\n' }]);
    expect(byPath(same.body.files, path).status).toBe('unchanged');

    const crlf = await attach(id, [{ path, data: 'number,short_description\r\nINC1,x\r\n' }]);
    expect(byPath(crlf.body.files, path).status).toBe('unchanged');

    const changed = await attach(id, [{ path, data: 'number,short_description\nINC1,x\nINC2,y\n' }]);
    expect(byPath(changed.body.files, path).status).toBe('replaced');
    expect(changed.body.counts.replaced).toBe(1);

    expect((await summary(page, id)).kinds.find((k: { id: string }) => k.id === 'ticket').fileCount).toBe(1);
  });

  test('every refusal carries a code and a reason', async ({ page }) => {
    const id = await newProject(page, ['sql', 'ticket', 'transcript']);
    const { status, body } = await attach(id, [
      { path: 'src/more.f', data: FORTRAN },
      { path: '../escape.sql', data: 'SELECT 1;' },
      { path: '/abs/path.sql', data: 'SELECT 1;' },
      { path: 'tool/setup.exe', data: 'MZ' },
      { path: 'db/empty.sql', data: '' },
      { path: 'db/twice.sql', data: 'SELECT 1;' },
      { path: 'db/twice.sql', data: 'SELECT 2;' },
      { path: 'tickets/bad.csv', data: Buffer.from([0xff, 0xfe, 0x41, 0x0a]) },
      { path: 'job/x.dtsx', data: '<x/>' },
    ]);
    expect(status).toBe(200);
    const code = (p: string) => body.files.filter((f: FileResult) => f.path === p).map((f: FileResult) => f.code);
    expect(code('src/more.f')).toEqual(['code_belongs_in_resync']);
    expect(code('../escape.sql')).toEqual(['unsafe_path']);
    expect(code('/abs/path.sql')).toEqual(['unsafe_path']);
    expect(code('tool/setup.exe')).toEqual(['unsupported_extension']);
    expect(code('db/empty.sql')).toEqual(['empty_file']);
    expect(code('db/twice.sql')).toEqual([null, 'duplicate_path_in_request']);
    expect(code('tickets/bad.csv')).toEqual(['not_utf8']);
    expect(code('job/x.dtsx')).toEqual(['kind_not_enabled']);
    for (const f of body.files.filter((x: FileResult) => x.status === 'rejected')) {
      expect(f.message, `${f.path} has no reason`).toBeTruthy();
    }
    // The one acceptable file of the duplicate pair went in.
    expect(body.counts.added).toBe(1);
  });

  test('an explicit kind must be switched on and must take the file type', async ({ page }) => {
    const id = await newProject(page, ['ticket', 'transcript']);
    const wrongType = await attach(id, [{ path: 'kt/notes.txt', data: 'a note' }], { kind: 'ticket' });
    expect(byPath(wrongType.body.files, 'kt/notes.txt').code).toBe('extension_not_accepted_by_kind');
    const off = await attach(id, [{ path: 'db/x.sql', data: 'SELECT 1;' }], { kind: 'sql' });
    expect(byPath(off.body.files, 'db/x.sql').code).toBe('kind_not_enabled');
    const unknown = await attach(id, [{ path: 'db/x.sql', data: 'SELECT 1;' }], { kind: 'wizardry' });
    expect(byPath(unknown.body.files, 'db/x.sql').code).toBe('unknown_kind');
    const ok = await attach(id, [{ path: 'kt/notes.txt', data: 'a note' }], { kind: 'transcript' });
    expect(byPath(ok.body.files, 'kt/notes.txt').status).toBe('added');
  });

  test('removing an artefact works once; source code and bad paths are refused', async ({ page }) => {
    const id = await newProject(page, ['sql']);
    await attach(id, [{ path: 'db/a.sql', data: 'SELECT 1;' }]);
    const del = (path: string, persona = 'engineer') =>
      page.request.delete(`${API_BASE}/api/v1/corpora/${id}/artefacts?path=${encodeURIComponent(path)}`, {
        headers: { 'X-Dev-Persona': persona },
      });

    expect((await del('db/a.sql', 'observer')).status()).toBe(403);
    expect((await del('db/a.sql')).status()).toBe(200);
    expect((await del('db/a.sql')).status()).toBe(404);
    const code = await del('e2eart.f');
    expect(code.status()).toBe(400);
    expect((await code.json()).error.code).toBe('artefact.is_code');
    expect((await del('../x')).status()).toBe(400);
    expect((await summary(page, id)).kinds.find((k: { id: string }) => k.id === 'code').fileCount).toBe(1);
  });

  test('Engineers, SMEs and Admins write; Observers read; empty and unknown requests are explained', async ({ page }) => {
    const id = await newProject(page, ['ticket']);
    const row = { path: 'tickets/a.csv', data: 'number\nINC1\n' };

    const obs = await attach(id, [row], { persona: 'observer' });
    expect(obs.status).toBe(403);
    expect(obs.body.error.code).toBe('auth.writer_required');
    expect((await summary(page, id, 'observer')).corpusId).toBe(id);

    expect((await attach(id, [row], { persona: 'sme' })).status).toBe(200);
    expect((await attach(id, [{ path: 'tickets/b.csv', data: 'number\nINC2\n' }], { persona: 'admin' })).status).toBe(200);

    const none = await fetch(`${API_BASE}/api/v1/corpora/${id}/artefacts`, {
      method: 'POST',
      headers: { 'X-Dev-Persona': 'engineer' },
      body: new FormData(),
    });
    expect(none.status).toBe(400);
    expect((await none.json()).error.code).toBe('attach.no_files');

    const nil = '00000000-0000-0000-0000-000000000000';
    expect((await attach(nil, [row])).status).toBe(404);
    expect((await page.request.get(`${API_BASE}/api/v1/corpora/${nil}/artefacts`)).status()).toBe(404);
  });

  test('a code re-sync keeps attached artefacts and the code file count', async ({ page }) => {
    const id = await newProject(page, ['ticket', 'sql']);
    await attach(id, [
      { path: 'tickets/export.csv', data: 'number\nINC1\n' },
      { path: 'db/ddl.sql', data: 'CREATE TABLE T (ID INT);\n' },
    ]);
    const before = await page.request.get(`${API_BASE}/api/v1/corpora/${id}`).then((r) => r.json());

    const resync = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
      headers: JSON_ENG,
      data: { files: [{ path: 'e2eart.f', content: FORTRAN + 'C     changed so the version differs\n' }] },
    });
    expect(resync.status()).toBe(200);
    const result = await resync.json();
    expect(result.carriedArtefacts).toBe(2);

    const after = await page.request.get(`${API_BASE}/api/v1/corpora/${id}`).then((r) => r.json());
    expect(after.latestVersion.id).not.toBe(before.latestVersion.id);
    expect(after.fileCount).toBe(1); // the hero count is source files only
    const kinds = after.latestVersion.files.map((f: { kind: string }) => f.kind).sort();
    expect(kinds).toEqual(['code', 'sql', 'ticket']);

    const s = await summary(page, id);
    expect(s.versionId).toBe(after.latestVersion.id);
    expect(s.kinds.find((k: { id: string }) => k.id === 'ticket').files[0].path).toBe('tickets/export.csv');

    // A second re-sync carries them again (they come from the version the project points at).
    const again = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
      headers: JSON_ENG,
      data: { files: [{ path: 'e2eart.f', content: FORTRAN }] },
    });
    expect((await again.json()).carriedArtefacts).toBe(2);
  });

  test('a zip ingest says what it left out', async ({ page }) => {
    const zip = storedZip([
      { name: 'src/e2ezip.f', data: FORTRAN },
      { name: 'README.md', data: '# hello' },
      { name: 'docs/notes.md', data: 'notes' },
      { name: 'img/logo.png', data: 'png' },
      { name: '../evil.f', data: FORTRAN },
    ]);
    const fd = new FormData();
    fd.append('name', `artefacts-zip-${Date.now()}`);
    fd.append('files', new Blob([zip]), 'bundle.zip');
    const res = await fetch(`${API_BASE}/api/v1/ingest/upload`, { method: 'POST', headers: { 'X-Dev-Persona': 'engineer' }, body: fd });
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.state).toBe('PARSED');
    expect(body.fileCount).toBe(1);
    const notes: string[] = body.warnings;
    const skipped = notes.find((n) => n.includes('not supported source code'));
    expect(skipped, notes.join(' | ')).toContain('Skipped 3 files');
    expect(skipped).toContain('.md ×2');
    expect(skipped).toContain('.png ×1');
    expect(notes.find((n) => n.includes('unsafe path'))).toContain('../evil.f');
  });
});

test.describe('Artefacts · UI', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      if (window.sessionStorage.getItem('e2e-seeded')) return;
      window.sessionStorage.setItem('e2e-seeded', '1');
      window.localStorage.setItem('astra.devPersona', 'engineer');
    });
  });

  test('with no optional kinds on, the page says so and links to the settings; source code is still counted', async ({ page }) => {
    const id = await newProject(page);
    await page.goto(`/corpora/${id}`);
    await expect(page.getByTestId('artefact-sources')).toBeVisible();
    await expect(page.getByTestId('artefact-none-enabled')).toBeVisible();
    await expect(page.getByTestId('artefact-dropzone')).toHaveCount(0);
    await expect(page.getByTestId('artefact-count-code')).toHaveText('1');
    await page.getByTestId('artefact-none-enabled').getByRole('link').click();
    await expect(page.getByTestId('project-settings-page')).toBeVisible();
  });

  test('dropping files shows what happened to each, the tiles update, and a file can be removed', async ({ page }) => {
    const id = await newProject(page, ['ticket', 'sql']);
    await page.goto(`/corpora/${id}`);
    await expect(page.getByTestId('artefact-dropzone')).toBeVisible();
    await expect(page.getByTestId('artefact-count-ticket')).toHaveText('0');

    await page.getByTestId('artefact-file-input').setInputFiles([
      { name: 'export.csv', mimeType: 'text/csv', buffer: Buffer.from('number,short_description\nINC1,x\n') },
      { name: 'ddl.sql', mimeType: 'application/sql', buffer: Buffer.from('CREATE TABLE T (ID INT);\n') },
      { name: 'setup.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('MZ') },
    ]);

    const results = page.getByTestId('artefact-results');
    await expect(results).toBeVisible();
    await expect(results).toContainText('Done, with some files not added');
    await expect(results.getByTestId('artefact-result-added')).toHaveCount(2);
    const refused = results.getByTestId('artefact-result-rejected');
    await expect(refused).toHaveCount(1);
    await expect(refused).toContainText('setup.exe');
    await expect(refused).toContainText('is not accepted');

    await expect(page.getByTestId('artefact-count-ticket')).toHaveText('1');
    await expect(page.getByTestId('artefact-count-sql')).toHaveText('1');

    // The routine list stays code-only: the attached files are not in it.
    await expect(page.getByText('Files in this version')).toBeVisible();
    await expect(page.getByTestId('artefact-file-row')).toHaveCount(0);

    // Open the tickets tile, then remove its file after confirming.
    await page.getByTestId('artefact-kind-tile-ticket').getByRole('button').click();
    const rows = page.getByTestId('artefact-files-ticket').getByTestId('artefact-file-row');
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toContainText('export.csv');
    await rows.first().getByTestId('artefact-remove').click();
    await rows.first().getByTestId('artefact-remove-confirm').click();
    await expect(page.getByTestId('artefact-count-ticket')).toHaveText('0');
    await expect(page.getByTestId('artefact-files-ticket')).toHaveCount(0);
  });

  test('the same file dropped again is reported as unchanged', async ({ page }) => {
    const id = await newProject(page, ['ticket']);
    await page.goto(`/corpora/${id}`);
    const file = { name: 'export.csv', mimeType: 'text/csv', buffer: Buffer.from('number\nINC1\n') };
    await page.getByTestId('artefact-file-input').setInputFiles([file]);
    await expect(page.getByTestId('artefact-result-added')).toHaveCount(1);
    await page.getByTestId('artefact-file-input').setInputFiles([file]);
    await expect(page.getByTestId('artefact-result-unchanged')).toHaveCount(1);
    await expect(page.getByTestId('artefact-count-ticket')).toHaveText('1');
  });

  test('the “Treat as” choice settles an ambiguous file type', async ({ page }) => {
    const id = await newProject(page, ['job', 'document']);
    await page.goto(`/corpora/${id}`);
    await page.getByTestId('artefact-kind-override').selectOption('document');
    await page.getByTestId('artefact-file-input').setInputFiles([
      { name: 'screens.xml', mimeType: 'application/xml', buffer: Buffer.from('<Screens/>\n') },
    ]);
    await expect(page.getByTestId('artefact-result-added')).toHaveCount(1);
    await expect(page.getByTestId('artefact-count-document')).toHaveText('1');
    await expect(page.getByTestId('artefact-count-job')).toHaveText('0');
  });

  test('an Observer sees the sources but cannot add or remove', async ({ page }) => {
    const id = await newProject(page, ['ticket']);
    await attach(id, [{ path: 'tickets/a.csv', data: 'number\nINC1\n' }]);
    await page.addInitScript(() => window.localStorage.setItem('astra.devPersona', 'observer'));
    await page.goto(`/corpora/${id}`);
    await expect(page.getByTestId('artefact-count-ticket')).toHaveText('1');
    await expect(page.getByTestId('artefact-readonly')).toBeVisible();
    await expect(page.getByTestId('artefact-dropzone')).toHaveCount(0);
    await page.getByTestId('artefact-kind-tile-ticket').getByRole('button').click();
    await expect(page.getByTestId('artefact-files-ticket')).toBeVisible();
    await expect(page.getByTestId('artefact-remove')).toHaveCount(0);
  });

  test('after a re-sync the project page still shows the attached evidence', async ({ page }) => {
    const id = await newProject(page, ['ticket']);
    await attach(id, [{ path: 'tickets/a.csv', data: 'number\nINC1\n' }]);
    const res = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
      headers: JSON_ENG,
      data: { files: [{ path: 'e2eart.f', content: FORTRAN + 'C     again\n' }] },
    });
    expect((await res.json()).carriedArtefacts).toBe(1);
    await page.goto(`/corpora/${id}`);
    await expect(page.getByTestId('artefact-count-ticket')).toHaveText('1');
    await expect(page.getByTestId('artefact-count-code')).toHaveText('1');
  });
});
