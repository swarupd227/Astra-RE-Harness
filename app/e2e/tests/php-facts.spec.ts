/**
 * PHP read for the entity graph (PHP track P1 + P2).
 *
 *   - PHP is read with the real PHP grammar in the parser sidecar: calls resolve to the class they target and a
 *     script's own statements (a routes file, a front controller) stay a routine.
 *   - The sidecar states facts — routes served, tables read and written, settings read, includes — and the API
 *     stores them like the facts it reads from C#, so each one is citable evidence and the inventory counts them.
 *   - Routes served are counted apart from endpoints the code calls.
 *
 * Needs a parser sidecar with PHP (0.6+); against an older one the facts are absent and the spec says so.
 */
import { expect, test, type Page } from '@playwright/test';
import { API_BASE, JSON_ENG, getEvidence } from './helpers/projects';

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
  'use App\\Services\\TaxCalculator;',
  'use Illuminate\\Support\\Facades\\DB;',
  'class InvoiceController extends Controller',
  '{',
  '    public function __construct(private TaxCalculator $tax) {}',
  '    public function show($id)',
  '    {',
  "        return DB::selectOne('SELECT * FROM invoices WHERE id = ?', [$id]);",
  '    }',
  '    public function store($request)',
  '    {',
  "        $total = $this->tax->withTax($request->input('amount'), env('TAX_REGION'));",
  "        DB::insert('INSERT INTO invoices (total) VALUES (?)', [$total]);",
  "        return response()->json(['total' => $total], 201);",
  '    }',
  '}',
  '',
].join('\n');

const TAX = [
  '<?php',
  'namespace App\\Services;',
  'class TaxCalculator',
  '{',
  '    public function withTax($amount, $region)',
  '    {',
  "        return round($amount * ($region === 'EU' ? 1.21 : 1.07), 2);",
  '    }',
  '}',
  '',
].join('\n');

const LEGACY = [
  '<?php',
  "require_once 'db.php';",
  'function get_customer($id) {',
  '    $r = mysqli_query($GLOBALS[\'db\'], "SELECT * FROM customers WHERE id = " . intval($id));',
  '    return mysqli_fetch_assoc($r);',
  '}',
  "switch ($_REQUEST['action']) {",
  "    case 'getCustomer': echo json_encode(get_customer($_GET['id'])); break;",
  "    case 'ping': echo 'ok'; break;",
  '}',
  '',
].join('\n');

async function ingest(page: Page): Promise<string> {
  const res = await page.request.post(`${API_BASE}/api/v1/ingest/text`, {
    headers: JSON_ENG,
    data: {
      name: `php-facts-e2e-${Date.now()}-${Math.floor(Math.random() * 1e4)}`,
      files: [
        { path: 'routes/api.php', content: ROUTES },
        { path: 'app/Http/Controllers/InvoiceController.php', content: CONTROLLER },
        { path: 'app/Services/TaxCalculator.php', content: TAX },
        { path: 'legacy/api.php', content: LEGACY },
      ],
    },
  });
  expect(res.ok()).toBe(true);
  return (await res.json()).corpusId;
}

async function getJson<T = any>(page: Page, path: string, persona = 'engineer'): Promise<T> {
  const res = await page.request.get(`${API_BASE}${path}`, { headers: { 'X-Dev-Persona': persona } });
  expect(res.status(), path).toBe(200);
  return res.json();
}

test.describe('PHP read for the entity graph', () => {
  let phpSidecar = false;

  test.beforeAll(async ({ request }) => {
    const ready = await (await request.get(`${API_BASE}/health/ready`)).text();
    const m = ready.match(/astra-parser (\d+)\.(\d+)/);
    phpSidecar = !!m && (Number(m[1]) > 0 || Number(m[2]) >= 6);
  });

  test('calls resolve to the class they target, and a script keeps its own statements', async ({ page }) => {
    test.skip(!phpSidecar, 'needs parser sidecar 0.6+ (PHP-Parser)');
    const id = await ingest(page);
    const graph = await getJson(page, `/api/v1/corpora/${id}/dependency-graph`);
    const name = Object.fromEntries(graph.nodes.map((n: any) => [n.id, `${n.sourcePath}#${n.name}`]));
    const calls = (graph.edges ?? []).filter((e: any) => (e.type ?? 'call') === 'call')
      .map((e: any) => `${name[e.from ?? e.callerId]} -> ${name[e.to ?? e.calleeId]}`);
    expect(calls).toContain('app/Http/Controllers/InvoiceController.php#store -> app/Services/TaxCalculator.php#withTax');
    expect(calls).toContain('legacy/api.php#api -> legacy/api.php#get_customer');
    expect(graph.nodes.map((n: any) => `${n.sourcePath}#${n.name}`)).toContain('routes/api.php#api');
  });

  test('routes, tables and settings become entities, each citing the lines that show it', async ({ page }) => {
    test.skip(!phpSidecar, 'needs parser sidecar 0.6+ (PHP-Parser)');
    const id = await ingest(page);

    const routes = await getJson(page, `/api/v1/corpora/${id}/entities?type=route&limit=50`, 'observer');
    const names = routes.data.map((r: any) => r.name).sort();
    expect(names).toEqual([
      'ANY /legacy/api.php?action=getCustomer',
      'ANY /legacy/api.php?action=ping',
      'GET /invoices/{id}',
      'POST /invoices',
    ]);
    for (const r of routes.data) {
      expect(r.evidenceIds.length, r.name).toBeGreaterThan(0);
      for (const e of r.evidenceIds) expect((await getEvidence(page, id, e)).status).toBe(200);
    }

    const show = await getJson(page, `/api/v1/corpora/${id}/entities/route/${encodeURIComponent('GET /invoices/{id}')}`);
    expect(show.links.some((l: any) => l.relation === 'triggers' && l.otherName === 'InvoiceController')).toBe(true);

    const controller = await getJson(page, `/api/v1/corpora/${id}/entities/class/InvoiceController`);
    const rel = controller.links.map((l: any) => `${l.direction}:${l.relation}:${l.otherType}:${l.otherName}`);
    expect(rel).toContain('out:reads:table:invoices');
    expect(rel).toContain('out:writes:table:invoices');
    expect(rel).toContain('out:reads:config-key:TAX_REGION');

    const legacy = await getJson(page, `/api/v1/corpora/${id}/entities/script/${encodeURIComponent('legacy/api.php')}`);
    const legacyRel = legacy.links.map((l: any) => `${l.relation}:${l.otherName}`);
    expect(legacyRel).toContain('reads:customers');
    expect(legacyRel).toContain('references:db.php');
  });

  test('an admin can read a version\'s facts again in place, keeping its routines', async ({ page }) => {
    test.skip(!phpSidecar, 'needs parser sidecar 0.6+ (PHP-Parser)');
    const id = await ingest(page);
    const routinesBefore = (await getJson(page, `/api/v1/corpora/${id}/dependency-graph`)).nodes.map((n: any) => n.id).sort();

    const engineer = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/refresh-facts`, { headers: { 'X-Dev-Persona': 'engineer' } });
    expect(engineer.status()).toBe(403);
    const admin = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/refresh-facts`, { headers: { 'X-Dev-Persona': 'admin' }, timeout: 60_000 });
    expect(admin.status()).toBe(202);
    expect((await admin.json()).state).toBe('refreshing');

    await expect.poll(async () => {
      const inv = await getJson(page, `/api/v1/corpora/${id}/inventory`, 'observer');
      return inv.measures.find((m: any) => m.key === 'routes')?.found;
    }, { timeout: 120_000 }).toBe(4);
    const routinesAfter = (await getJson(page, `/api/v1/corpora/${id}/dependency-graph`)).nodes.map((n: any) => n.id).sort();
    expect(routinesAfter).toEqual(routinesBefore);
  });

  test('the inventory counts routes served apart from endpoints called', async ({ page }) => {
    test.skip(!phpSidecar, 'needs parser sidecar 0.6+ (PHP-Parser)');
    const id = await ingest(page);
    const inv = await getJson(page, `/api/v1/corpora/${id}/inventory`, 'observer');
    const measure = (key: string) => inv.measures.find((m: any) => m.key === key);
    expect(measure('routes')).toMatchObject({ label: 'HTTP routes served', found: 4 });
    expect(measure('endpoints').found).toBe(0);
    expect(measure('tables').found).toBe(2);
    expect(measure('config-keys').found).toBe(1);
    expect(inv.code.languages).toEqual([{ language: 'php', files: 4, routines: expect.any(Number) }]);
  });
});
