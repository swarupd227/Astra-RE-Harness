/**
 * PHP track P3 — a spec is written for the stack the routine is going to, and is told which routes it serves.
 *
 *   - With no target in the project's settings, a PHP routine is extracted with the Java/Spring prompt (PHP's
 *     default target); naming `dotnet10` in the settings switches the next extraction to the .NET 10 prompt.
 *   - The PHP schema carries the web kinds a REST migration must keep: HTTP contracts, authorization rules, data
 *     access.
 *   - Extraction is told the routes a routine serves, read from the project's route table (a routes file for a
 *     controller method, a front controller's switch for a function it calls) — `routes_attached` on the stream.
 *
 * Runs on the mock provider (the mock writes a fixed spec; what is pinned is which prompt was chosen and what
 * context was attached). The routes need a parser sidecar with PHP (0.6+).
 */
import { expect, test, type APIRequestContext } from '@playwright/test';
import { API_BASE, JSON_ENG } from './helpers/projects';

const ENG = { 'X-Dev-Persona': 'engineer' };

const ROUTES = [
  '<?php',
  'use App\\Http\\Controllers\\InvoiceController;',
  "Route::get('/invoices/{id}', [InvoiceController::class, 'show']);",
  "Route::post('/invoices', [InvoiceController::class, 'store']);",
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
  '    public function store($request)',
  '    {',
  "        DB::insert('INSERT INTO invoices (total) VALUES (?)', [$request->input('total')]);",
  "        return response()->json(['ok' => true], 201);",
  '    }',
  '}',
  '',
].join('\n');

const LEGACY = [
  '<?php',
  'function get_customer($id) {',
  '    return mysqli_query($GLOBALS[\'db\'], "SELECT * FROM customers WHERE id = " . $id);',
  '}',
  "switch ($_GET['action']) {",
  "    case 'getCustomer': echo json_encode(get_customer($_GET['id'])); break;",
  '}',
  '',
].join('\n');

type Event = { type: string; data: any };

async function extract(request: APIRequestContext, subroutineId: string): Promise<Event[]> {
  const res = await request.post(`${API_BASE}/api/v1/subroutines/${subroutineId}/extract`, { headers: ENG, timeout: 120_000 });
  expect(res.status()).toBe(200);
  // Server-sent events: an `event: <type>` line, then `data: <json payload>`.
  const events: Event[] = [];
  let type = 'message';
  for (const line of (await res.text()).split('\n')) {
    if (line.startsWith('event:')) type = line.slice(6).trim();
    else if (line.startsWith('data:')) {
      try {
        events.push({ type, data: JSON.parse(line.slice(5).trim()) });
      } catch {
        /* a non-JSON keep-alive */
      }
      type = 'message';
    }
  }
  return events;
}

const one = (events: Event[], type: string) => events.find((e) => e.type === type);

test.describe('PHP extraction is written for its target', () => {
  test.describe.configure({ mode: 'serial' });
  let corpusId = '';
  let routines: Record<string, string> = {};
  let phpSidecar = false;

  test.beforeAll(async ({ request }) => {
    const ready = await (await request.get(`${API_BASE}/health/ready`)).text();
    const m = ready.match(/astra-parser (\d+)\.(\d+)/);
    phpSidecar = !!m && (Number(m[1]) > 0 || Number(m[2]) >= 6);

    const res = await request.post(`${API_BASE}/api/v1/ingest/text`, {
      headers: JSON_ENG,
      timeout: 120_000,
      data: {
        name: `php-extract-e2e-${Date.now()}-${Math.floor(Math.random() * 1e4)}`,
        files: [
          { path: 'routes/api.php', content: ROUTES },
          { path: 'app/Http/Controllers/InvoiceController.php', content: CONTROLLER },
          { path: 'legacy/api.php', content: LEGACY },
        ],
      },
    });
    expect(res.ok()).toBe(true);
    corpusId = (await res.json()).corpusId;
    const detail = await (await request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG, timeout: 120_000 })).json();
    routines = {};
    for (const f of detail.latestVersion.files)
      for (const s of f.subroutines ?? []) routines[`${f.relativePath ?? f.path}#${s.name}`] = s.id;
  });

  test('the PHP schema has the web kinds', async ({ request }) => {
    const php = await (await request.get(`${API_BASE}/api/v1/spec-schemas/php`, { headers: ENG, timeout: 120_000 })).json();
    const ids = php.claimKinds.map((k: any) => k.id);
    expect(ids).toEqual(expect.arrayContaining(['httpContract', 'authorizationRule', 'dataAccess']));
  });

  test('with no target set, a PHP handler is extracted for Java/Spring and told the route it serves', async ({ request }) => {
    const id = routines['app/Http/Controllers/InvoiceController.php#show'];
    expect(id, JSON.stringify(Object.keys(routines))).toBeTruthy();
    const events = await extract(request, id);
    expect(one(events, 'provider_info')?.data).toMatchObject({ promptTemplateId: 'php-extract-java-spring', promptTemplateVersion: 'v2.0' });
    expect(one(events, 'done'), JSON.stringify(events.filter((e) => e.type === 'error'))).toBeTruthy();
    if (!phpSidecar) test.skip(true, 'routes need parser sidecar 0.6+ (PHP-Parser)');
    expect(one(events, 'routes_attached')?.data.routes).toEqual(['GET /invoices/{id}']);
  });

  test('a function a front controller calls is told the action it handles', async ({ request }) => {
    test.skip(!phpSidecar, 'routes need parser sidecar 0.6+ (PHP-Parser)');
    const events = await extract(request, routines['legacy/api.php#get_customer']);
    expect(one(events, 'routes_attached')?.data.routes).toEqual(['ANY /legacy/api.php?action=getCustomer']);
  });

  test('naming dotnet10 as the project target switches the prompt, and an unknown target is refused', async ({ request }) => {
    const settingsUrl = `${API_BASE}/api/v1/corpora/${corpusId}/settings`;
    const current = await (await request.get(settingsUrl, { headers: ENG, timeout: 120_000 })).json();

    const bad = await request.put(settingsUrl, { headers: JSON_ENG, timeout: 120_000, data: { ...current, targetStack: 'cobol' } });
    expect(bad.status()).toBe(400);
    expect((await bad.json()).error.message).toContain('targetStack');

    const ok = await request.put(settingsUrl, { headers: JSON_ENG, timeout: 120_000, data: { ...current, targetStack: 'dotnet10' } });
    expect(ok.ok()).toBe(true);
    expect((await (await request.get(settingsUrl, { headers: ENG, timeout: 120_000 })).json()).targetStack).toBe('dotnet10');

    const events = await extract(request, routines['app/Http/Controllers/InvoiceController.php#store']);
    expect(one(events, 'provider_info')?.data).toMatchObject({ promptTemplateId: 'php-extract-dotnet10', promptTemplateVersion: 'v2.0' });
    if (phpSidecar) expect(one(events, 'routes_attached')?.data.routes).toEqual(['POST /invoices']);
  });
});
