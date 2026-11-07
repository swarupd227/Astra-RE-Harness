/**
 * Evidence from the kinds of file a project keeps besides code (WS7 K3).
 *
 *   - config: one chunk per setting, located by JSON path, with the lines it sits on; a secret-looking
 *     value never enters a chunk.
 *   - tickets: one chunk per row, located by ticket number, multi-line values and all.
 *   - transcripts: one chunk per turn, located by time, with the turns around it; a recording of the same
 *     name plays it, one that does not says so and can be linked by hand; a transcript added later picks
 *     up a recording already there.
 *   - documents: Markdown, Word and XML are cut by heading or element; a Word file has no lines.
 *   - ids of unchanged pieces survive a replacement, order and context survive a re-sync.
 *   - UI: each kind has its own view in the evidence panel (a transcript plays from the cited moment and
 *     stops where it ends), and the Sources card shows what each recording plays with.
 */
import { expect, test, type Page } from '@playwright/test';
import {
  API_BASE,
  JSON_ENG,
  attach,
  byPath,
  getEvidence,
  linkAudio,
  listEvidence,
  newProject,
  summary,
  FORTRAN,
} from './helpers/projects';
import { docxBytes, wavBytes } from './helpers/files';

const PREFIX = 'adapters-e2e';

const CONFIG = `{
  "Ofac": {
    "BaseUrl": "https://ofac.example/screen",
    "TimeoutSeconds": 15
  },
  "Db": {
    "Password": "hunter2-not-for-chunks",
    "Connection": "Server=sql01;Password=hunter2-not-for-chunks;Timeout=5"
  },
  "Servers": ["a", "b"]
}
`;

const TICKETS =
  [
    'number,opened_at,priority,state,short_description,description,close_notes',
    "INC-T-0412,2026-03-02 02:11,2,Closed,TLR_EOD_POST fails with 2627,\"Violation of UNIQUE KEY constraint 'UQ_TLR_STAGE_TXN_REF'.",
    'Cannot insert duplicate key (TXN-77).",Deleted the duplicate and restarted from step 3',
    'INC-T-0413,2026-03-03 09:00,4,Open,Receipt printer jams,,',
  ].join('\n') + '\n';

const VTT = `WEBVTT

00:00:02.000 --> 00:00:05.000
<v Morgan Lee>How does the nightly chain restart?</v>

00:00:06.000 --> 00:00:08.500
<v Dale Henry>From the failed step, never from the top.</v>

00:00:10.000 --> 00:00:14.000
<v Morgan Lee>And if the post step fails with 2627?</v>

00:00:15.000 --> 00:00:19.000
<v Dale Henry>Delete the staged duplicate, then rerun from step three.</v>
`;

const TURNS = ['t=00:00:02-00:00:05', 't=00:00:06-00:00:09', 't=00:00:10-00:00:14', 't=00:00:15-00:00:19'];

const NOTES_MD = ['Intro paragraph.', '', '# Overview', 'The platform does X.', '', '## Users', 'Tellers.', '', '# Restart', 'Restart from the failed step.', ''].join('\n');

const NOTES_DOCX = () =>
  docxBytes([
    { h: 1, text: 'Overview' },
    { text: 'Teller Lite takes payments.' },
    { h: 2, text: 'Users' },
    { text: 'Tellers and supervisors.' },
    { h: 1, text: 'Restart' },
    { text: 'Restart from the failed step.' },
  ]);

function screensXml(count: number, widgets: number): string {
  const lines = ['<Module Name="Teller">', '  <Screens>'];
  for (let s = 1; s <= count; s++) {
    lines.push(`    <Screen Name="S${s}">`);
    for (let w = 1; w <= widgets; w++) lines.push(`      <Widget Id="w${w}" Type="Input"/>`);
    lines.push('    </Screen>');
  }
  lines.push('  </Screens>', '</Module>');
  return lines.join('\n') + '\n';
}

async function transcriptProject(page: Page, withAudio: boolean) {
  const id = await newProject(page, ['transcript', 'audio'], { prefix: PREFIX });
  await attach(id, [{ path: 'kt/session1.vtt', data: VTT }]);
  if (withAudio) await attach(id, [{ path: 'kt/session1.wav', data: wavBytes(30) }]);
  const turns = (await listEvidence(page, id, { kind: 'transcript' })).data;
  return { id, turns };
}

test.describe('Artefact adapters · API', () => {
  test('a configuration file is cited by setting, with its lines, and a secret never enters a chunk', async ({ page }) => {
    const id = await newProject(page, ['config'], { prefix: PREFIX });
    const path = 'config/appsettings.PROD.json';
    const r = await attach(id, [{ path, data: CONFIG }]);
    expect(byPath(r.body.files, path).chunks).toBe(5);

    const list = await listEvidence(page, id, { kind: 'config' });
    expect(list.data.map((e) => e.locator)).toEqual([
      'json:$.Ofac.BaseUrl',
      'json:$.Ofac.TimeoutSeconds',
      'json:$.Db.Password',
      'json:$.Db.Connection',
      'json:$.Servers',
    ]);
    expect(list.data.map((e) => e.label)).toEqual(['Ofac:BaseUrl', 'Ofac:TimeoutSeconds', 'Db:Password', 'Db:Connection', 'Servers']);
    for (const e of list.data) {
      expect(e.shortId).toMatch(/^CFG-[0-9a-f]{8}$/);
      expect(e.trustTier).toBe('structured');
    }

    const timeout = (await getEvidence(page, id, list.data[1]!.shortId)).body;
    expect(timeout.text).toBe('Ofac:TimeoutSeconds = 15');
    expect(timeout.structured).toMatchObject({ key: 'Ofac:TimeoutSeconds', path: '$.Ofac.TimeoutSeconds', value: '15', type: 'number', masked: false });
    expect(timeout.view).toMatchObject({ highlightStart: 4, highlightEnd: 4 });

    // What is kept for search and for models has no secret in it; the file itself is stored as given.
    // …and neither does the file view the panel shows around a setting (the stored file is untouched).
    for (const e of list.data) {
      const d = (await getEvidence(page, id, e.shortId)).body;
      expect(JSON.stringify([d.text, d.structured, d.label, d.view])).not.toContain('hunter2');
    }
    const around = (await getEvidence(page, id, list.data[2]!.shortId)).body.view.text as string;
    expect(around).toContain('"Password": "<<MASKED>>"');
    expect(around).toContain('Password=<<MASKED>>;Timeout=5');
    const stored = await fetch(`${API_BASE}/api/v1/artefacts/${list.data[0]!.fileId}/content`).then((r) => r.text());
    expect(stored).toContain('hunter2-not-for-chunks');
    const password = (await getEvidence(page, id, list.data[2]!.shortId)).body;
    expect(password.text).toBe('Db:Password = <<MASKED>>');
    expect(password.structured.masked).toBe(true);
    expect((await getEvidence(page, id, list.data[3]!.shortId)).body.text).toBe('Db:Connection = Server=sql01;Password=<<MASKED>>;Timeout=5');
    expect((await listEvidence(page, id, { q: 'hunter2' })).total).toBe(0);
    expect((await getEvidence(page, id, list.data[4]!.shortId)).body.text).toBe('Servers = [a, b]');
  });

  test('a ticket export is cited by ticket number, multi-line values and all', async ({ page }) => {
    const id = await newProject(page, ['ticket'], { prefix: PREFIX });
    const r = await attach(id, [{ path: 'tickets/export.csv', data: TICKETS }]);
    expect(byPath(r.body.files, 'tickets/export.csv').chunks).toBe(2);

    const list = await listEvidence(page, id, { kind: 'ticket' });
    expect(list.data.map((e) => e.locator)).toEqual(['row:INC-T-0412', 'row:INC-T-0413']);
    expect(list.data[0]!.label).toBe('INC-T-0412 · TLR_EOD_POST fails with 2627');
    expect(list.data[0]!.shortId).toMatch(/^TKT-[0-9a-f]{8}$/);
    expect(list.data[0]!.trustTier).toBe('narrative');

    // A name that has an underscore in it is found as text, not as a pattern.
    expect((await listEvidence(page, id, { q: 'UQ_TLR_STAGE_TXN_REF' })).data.map((e) => e.locator)).toEqual(['row:INC-T-0412']);
    expect((await listEvidence(page, id, { q: 'INC-T-0413' })).total).toBe(1);

    const d = (await getEvidence(page, id, list.data[0]!.shortId)).body;
    expect(d.text).toContain('Cannot insert duplicate key (TXN-77).');
    expect(d.text).toContain('close_notes: Deleted the duplicate and restarted from step 3');
    expect(d.structured).toMatchObject({ id: 'INC-T-0412', priority: '2', state: 'Closed', openedAt: '2026-03-02 02:11' });
    // Every column, in the order of the export (jsonb would scramble an object's keys, so it is a list).
    expect(d.structured.fields.map((f: { name: string }) => f.name)).toEqual([
      'number', 'opened_at', 'priority', 'state', 'short_description', 'description', 'close_notes',
    ]);
    expect([d.lineStart, d.lineEnd]).toEqual([2, 3]);
    expect(d.view).toMatchObject({ highlightStart: 2, highlightEnd: 3, startLine: 1 });
    expect(d.previous).toBeNull();
    expect(d.next).toBe(list.data[1]!.shortId);
  });

  test('a ticket export with no ticket number cites rows by position and says why', async ({ page }) => {
    const id = await newProject(page, ['ticket'], { prefix: PREFIX });
    const r = await attach(id, [{ path: 'tickets/plain.csv', data: 'title,state\nA,Open\nB,Closed\n' }]);
    const f = byPath(r.body.files, 'tickets/plain.csv');
    expect(f.chunks).toBe(2);
    expect(f.notes?.[0]).toContain('ticket number');
    expect((await listEvidence(page, id, { kind: 'ticket' })).data.map((e) => e.locator)).toEqual(['row:1', 'row:2']);
  });

  test('a transcript is cited by turn, with the turns around it; a recording of the same name plays it', async ({ page }) => {
    const id = await newProject(page, ['transcript', 'audio'], { prefix: PREFIX });
    const t = await attach(id, [{ path: 'kt/session1.vtt', data: VTT }]);
    const tf = byPath(t.body.files, 'kt/session1.vtt');
    expect(tf.chunks).toBe(4);
    expect(tf.notes ?? []).toEqual([]);

    const a = await attach(id, [{ path: 'kt/session1.wav', data: wavBytes(30) }]);
    expect(byPath(a.body.files, 'kt/session1.wav').notes).toEqual(['Plays with kt/session1.vtt.']);

    const list = await listEvidence(page, id, { kind: 'transcript' });
    expect(list.data.map((e) => e.locator)).toEqual(TURNS);
    for (const e of list.data) expect(e.shortId).toMatch(/^TRN-[0-9a-f]{8}$/);
    expect(list.data[1]!.label).toBe('Dale Henry · 0:06 · From the failed step, never from the top.');

    const d = (await getEvidence(page, id, list.data[1]!.shortId)).body;
    expect(d.text).toBe('Dale Henry: From the failed step, never from the top.');
    expect(d.structured).toMatchObject({ speaker: 'Dale Henry', startSeconds: 6, endSeconds: 8.5, start: '00:00:06' });
    expect(d.audio).toMatchObject({ path: 'kt/session1.wav', contentType: 'audio/wav' });
    expect(d.context.before.map((n: { locator: string }) => n.locator)).toEqual([TURNS[0]]);
    expect(d.context.after.map((n: { locator: string }) => n.locator)).toEqual([TURNS[2], TURNS[3]]);
    expect(d.previous).toBe(list.data[0]!.shortId);
    expect(d.next).toBe(list.data[2]!.shortId);
    expect(d.view).toMatchObject({ highlightStart: 6, highlightEnd: 7 });

    const first = (await getEvidence(page, id, list.data[0]!.shortId)).body;
    expect(first.context.before).toEqual([]);
    expect(first.previous).toBeNull();

    // The recording's bytes are served, so the panel can play them.
    const fileId = d.audio.fileId as string;
    const head = await fetch(`${API_BASE}/api/v1/artefacts/${fileId}/content`, { headers: { Range: 'bytes=0-3' } });
    expect(head.status).toBe(206);
    expect(Buffer.from(await head.arrayBuffer()).toString('latin1')).toBe('RIFF');
  });

  test('a recording whose name matches no transcript says so, and can be linked by hand', async ({ page }) => {
    const id = await newProject(page, ['transcript', 'audio'], { prefix: PREFIX });
    await attach(id, [{ path: 'kt/session1.vtt', data: VTT }]);
    const a = await attach(id, [{ path: 'recordings/clip.wav', data: wavBytes(5) }]);
    expect(byPath(a.body.files, 'recordings/clip.wav').notes?.[0]).toContain('No transcript has the same name');

    const audioFile = async () =>
      (await summary(page, id)).kinds.find((k: { id: string }) => k.id === 'audio').files[0] as { linkedTo: string | null };
    expect((await audioFile()).linkedTo).toBeNull();
    const turn = (await listEvidence(page, id, { kind: 'transcript' })).data[0]!.shortId;
    expect((await getEvidence(page, id, turn)).body.audio).toBeNull();

    expect((await linkAudio(page, id, 'recordings/clip.wav', 'kt/session1.vtt')).status).toBe(200);
    expect((await audioFile()).linkedTo).toBe('kt/session1.vtt');
    expect((await getEvidence(page, id, turn)).body.audio).toMatchObject({ path: 'recordings/clip.wav' });

    // It survives a re-sync of the code.
    const resync = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
      headers: JSON_ENG,
      data: { files: [{ path: 'e2eart.f', content: FORTRAN + 'C     again\n' }] },
    });
    expect(resync.status()).toBe(200);
    expect((await audioFile()).linkedTo).toBe('kt/session1.vtt');

    // Clearing the link goes back to matching by name.
    expect((await linkAudio(page, id, 'recordings/clip.wav', null)).status).toBe(200);
    expect((await audioFile()).linkedTo).toBeNull();
  });

  test('linking is refused for what cannot be linked, and for an Observer', async ({ page }) => {
    const id = await newProject(page, ['transcript', 'audio'], { prefix: PREFIX });
    await attach(id, [
      { path: 'kt/session1.vtt', data: VTT },
      { path: 'recordings/clip.wav', data: wavBytes(2) },
    ]);
    const notAudio = await linkAudio(page, id, 'kt/session1.vtt', 'kt/session1.vtt');
    expect([notAudio.status, notAudio.body.error.code]).toEqual([400, 'artefact.not_audio']);
    const notTranscript = await linkAudio(page, id, 'recordings/clip.wav', 'recordings/clip.wav');
    expect([notTranscript.status, notTranscript.body.error.code]).toEqual([400, 'artefact.transcript_not_found']);
    expect((await linkAudio(page, id, 'recordings/missing.wav', 'kt/session1.vtt')).status).toBe(404);
    expect((await linkAudio(page, id, '../escape.wav', 'kt/session1.vtt')).status).toBe(400);
    expect((await linkAudio(page, id, 'recordings/clip.wav', 'kt/session1.vtt', 'observer')).status).toBe(403);
    expect((await linkAudio(page, id, 'recordings/clip.wav', 'kt/session1.vtt', 'sme')).status).toBe(200);
  });

  test('a transcript added after its recording picks the recording up and says so', async ({ page }) => {
    const id = await newProject(page, ['transcript', 'audio'], { prefix: PREFIX });
    const a = await attach(id, [{ path: 'kt/later.wav', data: wavBytes(3) }]);
    expect(byPath(a.body.files, 'kt/later.wav').notes?.[0]).toContain('No transcript has the same name');

    const t = await attach(id, [{ path: 'kt/later.vtt', data: VTT }]);
    expect(byPath(t.body.files, 'kt/later.vtt').notes).toEqual(['A citation of this transcript can play kt/later.wav.']);
    const audio = (await summary(page, id)).kinds.find((k: { id: string }) => k.id === 'audio').files[0];
    expect(audio.linkedTo).toBe('kt/later.vtt');
  });

  test('a transcript with no timestamps is kept as line windows and says it cannot play', async ({ page }) => {
    const id = await newProject(page, ['transcript'], { prefix: PREFIX });
    const r = await attach(id, [{ path: 'kt/notes.txt', data: 'Dale said this.\nMorgan said that.\n' }]);
    const f = byPath(r.body.files, 'kt/notes.txt');
    expect(f.status).toBe('added');
    expect(f.chunks).toBe(1);
    expect(f.notes?.[0]).toContain('No timestamps');
    expect((await listEvidence(page, id, { kind: 'transcript' })).data[0]!.locator).toBe('L1-2');
  });

  test('replacing a transcript keeps the ids of the turns that did not change', async ({ page }) => {
    const { id, turns } = await transcriptProject(page, false);
    const moved = VTT.replace('00:00:15.000 --> 00:00:19.000', '00:00:16.000 --> 00:00:20.000');
    const r = await attach(id, [{ path: 'kt/session1.vtt', data: moved }]);
    expect(byPath(r.body.files, 'kt/session1.vtt').status).toBe('replaced');

    const after = (await listEvidence(page, id, { kind: 'transcript' })).data;
    expect(after.slice(0, 3).map((e) => e.shortId)).toEqual(turns.slice(0, 3).map((e) => e.shortId));
    expect(after[3]!.locator).toBe('t=00:00:16-00:00:20');
    expect(after[3]!.shortId).not.toBe(turns[3]!.shortId);
    expect((await getEvidence(page, id, turns[3]!.shortId)).status).toBe(404);
  });

  test('Markdown and Word notes are cut by heading; a Word file has no lines', async ({ page }) => {
    const id = await newProject(page, ['document'], { prefix: PREFIX });
    const r = await attach(id, [
      { path: 'docs/notes.md', data: NOTES_MD },
      { path: 'docs/Notes2019.docx', data: NOTES_DOCX() },
    ]);
    expect(byPath(r.body.files, 'docs/notes.md').chunks).toBe(4);
    expect(byPath(r.body.files, 'docs/Notes2019.docx').chunks).toBe(3);
    expect(byPath(r.body.files, 'docs/Notes2019.docx').kind).toBe('document');

    const md = await listEvidence(page, id, { path: 'docs/notes.md' });
    expect(md.data.map((e) => e.locator)).toEqual(['h=Preamble', 'h=Overview', 'h=Users', 'h=Restart']);
    const users = (await getEvidence(page, id, md.data[2]!.shortId)).body;
    expect(users.text).toBe('## Users\nTellers.');
    expect([users.lineStart, users.lineEnd]).toEqual([6, 7]);
    expect(users.view).toMatchObject({ highlightStart: 6, highlightEnd: 7 });
    expect(users.structured.path).toEqual(['Overview', 'Users']);
    expect(users.previous).toBe(md.data[1]!.shortId);

    const docx = await listEvidence(page, id, { path: 'docs/Notes2019.docx' });
    expect(docx.data.map((e) => e.locator)).toEqual(['h=Overview', 'h=Users', 'h=Restart']);
    for (const e of docx.data) {
      expect(e.shortId).toMatch(/^DOC-[0-9a-f]{8}$/);
      expect(e.lineStart).toBeNull();
    }
    const d = (await getEvidence(page, id, docx.data[1]!.shortId)).body;
    expect(d.text).toBe('Users\nTellers and supervisors.');
    expect(d.view).toBeNull();
    expect(d.structured).toMatchObject({ heading: 'Users', level: 2, path: ['Overview', 'Users'] });
    // Position, not lines, orders a Word file: neighbours follow the document.
    expect(d.previous).toBe(docx.data[0]!.shortId);
    expect(d.next).toBe(docx.data[2]!.shortId);
    expect(d.context.before.map((n: { locator: string }) => n.locator)).toEqual(['h=Overview']);
    expect(d.context.after.map((n: { locator: string }) => n.locator)).toEqual(['h=Restart']);
  });

  test('an XML export is cut by element, each cited by its path and shown in the file', async ({ page }) => {
    const id = await newProject(page, ['document'], { prefix: PREFIX });
    const xml = screensXml(3, 30);
    const r = await attach(id, [{ path: 'src/web/Teller.oml.xml', data: xml }]);
    expect(byPath(r.body.files, 'src/web/Teller.oml.xml').kind).toBe('document');

    const list = await listEvidence(page, id, { kind: 'document' });
    expect(list.data.map((e) => e.locator)).toEqual([
      'xpath:/Module',
      'xpath:/Module/Screens',
      'xpath:/Module/Screens/Screen[1]',
      'xpath:/Module/Screens/Screen[2]',
      'xpath:/Module/Screens/Screen[3]',
    ]);
    const second = list.data[3]!;
    expect(second.label).toBe('Screen S2');
    const d = (await getEvidence(page, id, second.shortId)).body;
    expect(d.text.split('\n')[0]).toBe('    <Screen Name="S2">');
    expect(d.view).toMatchObject({ highlightStart: d.lineStart, highlightEnd: d.lineEnd });
    expect(d.structured).toMatchObject({ element: 'Screen', xpath: '/Module/Screens/Screen[2]', attributes: { Name: 'S2' } });
  });

  test('a file an adapter cannot read is kept as line windows with a reason, never refused', async ({ page }) => {
    const id = await newProject(page, ['config', 'document'], { prefix: PREFIX });
    const r = await attach(id, [
      { path: 'config/broken.json', data: '{ "A": \n' },
      { path: 'docs/broken.xml', data: '<a>\n<b>\n</a>\n' },
      { path: 'docs/not-a-zip.docx', data: Buffer.from('this is not a zip') },
    ]);
    expect(byPath(r.body.files, 'config/broken.json')).toMatchObject({ status: 'added', chunks: 1 });
    expect(byPath(r.body.files, 'config/broken.json').notes?.[0]).toContain('not valid JSON');
    expect(byPath(r.body.files, 'docs/broken.xml')).toMatchObject({ status: 'added', chunks: 1 });
    expect(byPath(r.body.files, 'docs/broken.xml').notes?.[0]).toContain('not well-formed');
    expect(byPath(r.body.files, 'docs/not-a-zip.docx')).toMatchObject({ status: 'added', chunks: 0 });
    expect(byPath(r.body.files, 'docs/not-a-zip.docx').notes?.[0]).toContain('not a readable Word document');
  });

  test('order, context and the recording survive a re-sync, under the same ids', async ({ page }) => {
    const id = await newProject(page, ['transcript', 'audio', 'document'], { prefix: PREFIX });
    await attach(id, [
      { path: 'kt/session1.vtt', data: VTT },
      { path: 'kt/session1.wav', data: wavBytes(30) },
      { path: 'docs/Notes2019.docx', data: NOTES_DOCX() },
    ]);
    const before = await listEvidence(page, id, { limit: 50 });

    const resync = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
      headers: JSON_ENG,
      data: { files: [{ path: 'e2eart.f', content: FORTRAN + 'C     changed\n' }] },
    });
    expect(resync.status()).toBe(200);

    const after = await listEvidence(page, id, { limit: 50 });
    const artefacts = (l: typeof before) => l.data.filter((e) => e.kind !== 'code').map((e) => e.shortId);
    expect(artefacts(after)).toEqual(artefacts(before));

    const turn = after.data.find((e) => e.locator === TURNS[1])!;
    const d = (await getEvidence(page, id, turn.shortId)).body;
    expect(d.audio).toMatchObject({ path: 'kt/session1.wav' });
    expect(d.context.after).toHaveLength(2);
    const heading = after.data.find((e) => e.locator === 'h=Users')!;
    const h = (await getEvidence(page, id, heading.shortId)).body;
    expect([h.previous, h.next]).toEqual([
      after.data.find((e) => e.locator === 'h=Overview')!.shortId,
      after.data.find((e) => e.locator === 'h=Restart')!.shortId,
    ]);
  });
});

test.describe('Artefact adapters · UI', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      if (window.sessionStorage.getItem('e2e-seeded')) return;
      window.sessionStorage.setItem('e2e-seeded', '1');
      window.localStorage.setItem('astra.devPersona', 'engineer');
    });
  });

  test('a transcript opens as a conversation and plays from the cited moment to where it ends', async ({ page }) => {
    const { id, turns } = await transcriptProject(page, true);
    await page.goto(`/corpora/${id}/evidence?open=${turns[1]!.shortId}`);

    const drawer = page.getByTestId('evidence-drawer');
    const view = drawer.getByTestId('transcript-view');
    await expect(view).toBeVisible();
    const current = view.getByTestId('transcript-turn-current');
    await expect(current).toContainText('Dale Henry');
    await expect(current).toContainText('From the failed step, never from the top.');
    // One turn before the cited one, two after.
    await expect(view.getByTestId('transcript-turn')).toHaveCount(3);
    await expect(view.getByTestId('transcript-turn').first()).toContainText('How does the nightly chain restart?');

    const audio = view.getByTestId('transcript-audio');
    await expect(audio).toHaveAttribute('src', /\/api\/v1\/artefacts\/[0-9a-f-]+\/content$/);
    await expect(view.getByTestId('transcript-play-moment')).toContainText('0:06');

    await view.getByTestId('transcript-play-moment').click();
    const time = () => audio.evaluate((a: HTMLAudioElement) => a.currentTime);
    await expect.poll(time, { timeout: 8000 }).toBeGreaterThanOrEqual(6);
    // It stops by itself at the end of the turn (8.5 s), not at the end of the recording.
    await expect.poll(() => audio.evaluate((a: HTMLAudioElement) => a.paused && a.currentTime > 8), { timeout: 10_000 }).toBe(true);
    const stoppedAt = await time();
    expect(stoppedAt).toBeGreaterThanOrEqual(8.4);
    expect(stoppedAt).toBeLessThan(10);
    await expect(view.getByTestId('transcript-audio-problem')).toHaveCount(0);

    // The time beside another turn plays from there.
    await view.getByTestId('transcript-turn-time').first().click();
    await expect.poll(async () => (await time()) >= 2 && (await time()) < 6, { timeout: 8000 }).toBe(true);
  });

  test('without a recording the panel says there is nothing to play', async ({ page }) => {
    const { id, turns } = await transcriptProject(page, false);
    await page.goto(`/corpora/${id}/evidence?open=${turns[0]!.shortId}`);
    const view = page.getByTestId('evidence-drawer').getByTestId('transcript-view');
    await expect(view.getByTestId('transcript-no-audio')).toBeVisible();
    await expect(view.getByTestId('transcript-audio')).toHaveCount(0);
    await expect(view.getByTestId('transcript-turn-current')).toContainText('How does the nightly chain restart?');
    // The turn's own lines in the file are one click away.
    await view.getByTestId('evidence-source-lines').getByText('Show in the file').click();
    await expect(view.getByTestId('evidence-code').locator('[data-marked="true"]').first()).toContainText('00:00:02.000 --> 00:00:05.000');
  });

  test('a ticket opens as a record, with its row in the file on request', async ({ page }) => {
    const id = await newProject(page, ['ticket'], { prefix: PREFIX });
    await attach(id, [{ path: 'tickets/export.csv', data: TICKETS }]);
    const first = (await listEvidence(page, id, { kind: 'ticket' })).data[0]!;
    await page.goto(`/corpora/${id}/evidence?open=${first.shortId}`);

    const view = page.getByTestId('evidence-drawer').getByTestId('ticket-view');
    await expect(view.getByTestId('ticket-id')).toHaveText('INC-T-0412');
    await expect(view).toContainText('P2');
    await expect(view).toContainText('Closed');
    await expect(view.getByTestId('ticket-fields')).toContainText('short_description');
    await expect(view.getByTestId('ticket-fields')).toContainText("Violation of UNIQUE KEY constraint 'UQ_TLR_STAGE_TXN_REF'.");
    await expect(view.getByTestId('ticket-fields')).toContainText('close_notes');
    // The columns read in the order of the export.
    await expect(view.getByTestId('ticket-fields').locator('dt')).toHaveText([
      'number', 'opened_at', 'priority', 'state', 'short_description', 'description', 'close_notes',
    ]);
    await expect(view.getByTestId('evidence-code')).toHaveCount(0);

    await view.getByText('Show in the file').click();
    await expect(view.getByTestId('evidence-code').locator('[data-marked="true"]')).toHaveCount(2);
  });

  test('a setting opens as a name and value; a secret says it is not kept', async ({ page }) => {
    const id = await newProject(page, ['config'], { prefix: PREFIX });
    await attach(id, [{ path: 'config/appsettings.PROD.json', data: CONFIG }]);
    const list = (await listEvidence(page, id, { kind: 'config' })).data;

    await page.goto(`/corpora/${id}/evidence?open=${list[1]!.shortId},${list[2]!.shortId}`);
    const cards = page.getByTestId('evidence-drawer').getByTestId('evidence-card');
    await expect(cards).toHaveCount(2);

    const timeout = cards.nth(0);
    await expect(timeout.getByTestId('config-key')).toHaveText('Ofac:TimeoutSeconds');
    await expect(timeout.getByTestId('config-value')).toHaveText('15');
    await expect(timeout.getByTestId('config-masked')).toHaveCount(0);
    const marked = timeout.getByTestId('evidence-code').locator('[data-marked="true"]');
    await expect(marked).toHaveCount(1);
    await expect(marked).toContainText('"TimeoutSeconds": 15');

    const password = cards.nth(1);
    await expect(password.getByTestId('config-value')).toHaveText('<<MASKED>>');
    await expect(password.getByTestId('config-masked')).toBeVisible();
    // Nor does the file around it show the secret.
    await expect(password.getByTestId('evidence-code')).toContainText('"Password": "<<MASKED>>"');
    await expect(page.getByTestId('evidence-drawer')).not.toContainText('hunter2');
  });

  test('a section of a Word file opens under its headings, as text', async ({ page }) => {
    const id = await newProject(page, ['document'], { prefix: PREFIX });
    await attach(id, [{ path: 'docs/Notes2019.docx', data: NOTES_DOCX() }]);
    const users = (await listEvidence(page, id, { kind: 'document' })).data.find((e) => e.locator === 'h=Users')!;
    await page.goto(`/corpora/${id}/evidence?open=${users.shortId}`);

    const view = page.getByTestId('evidence-drawer').getByTestId('document-view');
    await expect(view.getByTestId('document-path')).toContainText('Overview');
    await expect(view.getByTestId('document-path')).toContainText('Users');
    await expect(view.getByTestId('evidence-text')).toContainText('Tellers and supervisors.');
    await expect(view.getByTestId('evidence-code')).toHaveCount(0);
  });

  test('the Sources card shows what each recording plays with, and a person can link one that does not', async ({ page }) => {
    const id = await newProject(page, ['transcript', 'audio'], { prefix: PREFIX });
    await attach(id, [{ path: 'kt/session1.vtt', data: VTT }]);
    await page.goto(`/corpora/${id}`);

    // Dropping a recording with no matching transcript says so under the file.
    await page.getByTestId('artefact-file-input').setInputFiles([
      { name: 'clip.wav', mimeType: 'audio/wav', buffer: wavBytes(3) },
    ]);
    const note = page.getByTestId('artefact-results').getByTestId('artefact-result-note');
    await expect(note).toContainText('No transcript has the same name');

    await page.getByTestId('artefact-kind-tile-audio').getByRole('button').click();
    const row = page.getByTestId('artefact-files-audio').getByTestId('artefact-file-row');
    await expect(row.getByTestId('artefact-audio-unlinked')).toBeVisible();

    await row.getByTestId('artefact-audio-select').selectOption('kt/session1.vtt');
    await expect(row.getByTestId('artefact-audio-linked')).toContainText('kt/session1.vtt');
    await expect(row.getByTestId('artefact-audio-unlinked')).toHaveCount(0);

    await row.getByTestId('artefact-audio-select').selectOption('');
    await expect(row.getByTestId('artefact-audio-unlinked')).toBeVisible();
  });

  test('an Observer sees what a recording plays with but cannot change it', async ({ page }) => {
    const id = await newProject(page, ['transcript', 'audio'], { prefix: PREFIX });
    await attach(id, [
      { path: 'kt/session1.vtt', data: VTT },
      { path: 'kt/session1.wav', data: wavBytes(2) },
    ]);
    await page.addInitScript(() => window.localStorage.setItem('astra.devPersona', 'observer'));
    await page.goto(`/corpora/${id}`);
    await page.getByTestId('artefact-kind-tile-audio').getByRole('button').click();
    const row = page.getByTestId('artefact-files-audio').getByTestId('artefact-file-row');
    await expect(row.getByTestId('artefact-audio-linked')).toContainText('kt/session1.vtt');
    await expect(row.getByTestId('artefact-audio-select')).toHaveCount(0);
  });

  test('the transcript panel fits at 1280x720 and on a phone, and the page never scrolls sideways', async ({ page }) => {
    const { id, turns } = await transcriptProject(page, true);
    for (const vp of [{ width: 1280, height: 720 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(vp);
      await page.goto(`/corpora/${id}/evidence?open=${turns[3]!.shortId}`);
      const drawer = page.getByTestId('evidence-drawer');
      await expect(drawer.getByTestId('transcript-view')).toBeVisible();
      const box = (await drawer.boundingBox())!;
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.x + box.width).toBeLessThanOrEqual(vp.width + 1);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      expect(await drawer.getByTestId('transcript-view').evaluate((el) => el.scrollWidth <= el.clientWidth)).toBe(true);
    }
  });
});
