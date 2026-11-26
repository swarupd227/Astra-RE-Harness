/**
 * PHP track P4 — a PHP web routine becomes a real Spring Boot REST service, and the project's target is a sentence.
 *
 *   - "Migrate this project to Java Spring" pauses on a Confirm card; confirming sets the project's target, and
 *     "use the default target" clears it.
 *   - A PHP handler, extracted, reviewed and signed, is generated with no target named: the default is Java/Spring
 *     (the language's default target) and the archetype the production `spring-rest-service` (not the preview
 *     catalog one, which used to block every PHP routine).
 *   - The package compiles and its tests pass in the maven sidecar, offline: MockMvc contract tests, @JdbcTest on H2.
 *
 * Runs on the mock providers (the mock scaffold returns the archetype as it is). The gates need the maven sidecar
 * with the Spring JDBC/validation/H2 warm-up; without it they are reported, not faked.
 */
import { expect, test, type APIRequestContext } from '@playwright/test';
import { API_BASE, JSON_ENG } from './helpers/projects';

const ENG = { 'X-Dev-Persona': 'engineer' };
const SME = { 'X-Dev-Persona': 'sme' };
const T = 300_000;

const ROUTES = [
  '<?php',
  'use App\\Http\\Controllers\\InvoiceController;',
  "Route::get('/invoices/{id}', [InvoiceController::class, 'show']);",
  '',
].join('\n');

const CONTROLLER = [
  '<?php',
  'namespace App\\Http\\Controllers;',
  'use Illuminate\\Support\\Facades\\DB;',
  'class InvoiceController extends Controller',
  '{',
  '    public function show($id)',
  '    {',
  "        $row = DB::selectOne('SELECT * FROM invoices WHERE id = ?', [$id]);",
  '        if ($row === null) abort(404);',
  '        return response()->json($row);',
  '    }',
  '}',
  '',
].join('\n');

type Event = { type: string; data: any };

async function sse(res: Awaited<ReturnType<APIRequestContext['post']>>): Promise<Event[]> {
  const events: Event[] = [];
  let type = 'message';
  for (const line of (await res.text()).split('\n')) {
    if (line.startsWith('event:')) type = line.slice(6).trim();
    else if (line.startsWith('data:')) {
      try {
        events.push({ type, data: JSON.parse(line.slice(5).trim()) });
      } catch {
        /* keep-alive */
      }
      type = 'message';
    }
  }
  return events;
}

async function ingest(request: APIRequestContext): Promise<string> {
  const res = await request.post(`${API_BASE}/api/v1/ingest/text`, {
    headers: JSON_ENG,
    data: {
      name: `php-spring-e2e-${Date.now()}-${Math.floor(Math.random() * 1e4)}`,
      files: [
        { path: 'routes/api.php', content: ROUTES },
        { path: 'app/Http/Controllers/InvoiceController.php', content: CONTROLLER },
      ],
    },
  });
  expect(res.ok()).toBe(true);
  return (await res.json()).corpusId;
}

async function settings(request: APIRequestContext, corpusId: string) {
  return (await request.get(`${API_BASE}/api/v1/corpora/${corpusId}/settings`, { headers: ENG, timeout: T })).json();
}

/** Say something in the programme's thread and confirm the action it proposes; returns the proposal. */
async function sayAndConfirm(request: APIRequestContext, threadId: string, text: string) {
  const said = await sse(await request.post(`${API_BASE}/api/v1/conversations/${threadId}/messages`, {
    headers: JSON_ENG, data: { text }, timeout: T,
  }));
  const proposal = said.find((e) => e.type === 'message' && e.data?.pendingAction)?.data;
  expect(proposal, JSON.stringify(said.map((e) => e.type))).toBeTruthy();
  const confirmed = await sse(await request.post(
    `${API_BASE}/api/v1/conversations/${threadId}/messages/${proposal.id}/confirm`, { headers: ENG, timeout: T }));
  expect(confirmed.find((e) => e.type === 'tool_result')?.data?.ok, JSON.stringify(confirmed)).toBe(true);
  return proposal.pendingAction;
}

test.describe('PHP to Spring Boot', () => {
  test('the migration target is a sentence, behind a Confirm card', async ({ request }) => {
    const corpusId = await ingest(request);
    const threads = await (await request.get(`${API_BASE}/api/v1/conversations?corpusId=${corpusId}`, { headers: ENG, timeout: T })).json();
    const thread = threads.data.find((c: any) => c.corpusId === corpusId) ?? threads.data[0];
    expect(thread?.id).toBeTruthy();

    const action = await sayAndConfirm(request, thread.id, 'Migrate this project to .NET 8');
    expect(action.toolName).toBe('set_target_stack');
    expect(action.summary).toContain('dotnet8');
    expect((await settings(request, corpusId)).targetStack).toBe('dotnet8');

    await sayAndConfirm(request, thread.id, 'use the default target');
    expect((await settings(request, corpusId)).targetStack ?? null).toBeNull();
  });

  test('a signed PHP handler is generated as a Spring REST service that builds and passes its tests', async ({ request }) => {
    test.setTimeout(900_000);
    const corpusId = await ingest(request);
    const detail = await (await request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG })).json();
    const show = detail.latestVersion.files
      .flatMap((f: any) => (f.subroutines ?? []).map((s: any) => ({ ...s, path: f.relativePath })))
      .find((s: any) => s.path.endsWith('InvoiceController.php') && s.name === 'show');
    expect(show).toBeTruthy();

    // Extract (mock), review every claim of every section, sign.
    await (await request.post(`${API_BASE}/api/v1/subroutines/${show.id}/extract`, { headers: ENG, timeout: T })).text();
    const spec = await (await request.get(`${API_BASE}/api/v1/subroutines/${show.id}/spec`, { headers: ENG })).json();
    expect(spec.id).toBeTruthy();
    await request.post(`${API_BASE}/api/v1/specs/${spec.id}/route`, { headers: JSON_ENG, data: { routingNote: 'e2e' }, timeout: T });
    for (const [section, claims] of Object.entries<any>(spec.spec ?? {})) {
      if (!Array.isArray(claims) || ['inputs', 'outputs'].includes(section)) continue;
      for (const claim of claims) {
        if (!claim?.id) continue;
        const claimPath = `$.${section}[?(@.id=='${claim.id}')]`;
        const data = section === 'open_questions'
          ? { claimPath, action: 'edit', editedText: 'Resolved for the e2e run.' }
          : { claimPath, action: 'accept' };
        await request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, { headers: { ...SME, 'Content-Type': 'application/json' }, data, timeout: T });
      }
    }
    const sign = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/sign`, {
      headers: { ...SME, 'Content-Type': 'application/json' }, data: { confirmation: 'I have reviewed every claim.' }, timeout: T,
    });
    expect(sign.ok(), await sign.text()).toBe(true);

    // Generate with no target named: PHP's default, Java/Spring, and the production REST archetype.
    const generated = await sse(await request.post(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, { headers: ENG, timeout: T }));
    const errors = generated.filter((e) => e.type === 'error');
    expect(errors, JSON.stringify(errors)).toEqual([]);
    const scaffolds = (await (await request.get(`${API_BASE}/api/v1/scaffolds`, { headers: ENG })).json()).data;
    const scaffold = scaffolds.find((s: any) => s.specId === spec.id);
    expect(scaffold).toBeTruthy();
    expect(scaffold.targetPlatform).toBe('java-spring');
    const pkg = await (await request.get(`${API_BASE}/api/v1/scaffolds/${scaffold.id}`, { headers: ENG })).json();
    const paths: string[] = pkg.files.map((f: any) => f.path);
    // The production REST archetype: a controller, a JdbcTemplate repository, contract and H2 tests.
    expect(paths.some((p) => p.endsWith('Controller.java'))).toBe(true);
    expect(paths.some((p) => p.endsWith('RepositoryTests.java'))).toBe(true);
    expect(paths).toContain('src/main/resources/schema.sql');

    // The gates, in the maven sidecar.
    const compile = await (await request.post(`${API_BASE}/api/v1/scaffolds/${scaffold.id}/validate/compile`, { headers: ENG, timeout: T })).json();
    expect(compile.status, JSON.stringify(compile)).toBe('PASSED');
    const tests = await (await request.post(`${API_BASE}/api/v1/scaffolds/${scaffold.id}/validate/test-pack`, { headers: ENG, timeout: T })).json();
    expect(tests.status, JSON.stringify(tests)).toBe('PASSED');
  });
});
