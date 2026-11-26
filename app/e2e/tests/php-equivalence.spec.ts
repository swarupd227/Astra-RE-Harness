/**
 * PHP track P7 — EQUIVALENCE for a PHP routine and the Java/Spring package generated from it, vector by vector.
 *
 *   - A pure PHP routine (`resolveQty`) is extracted, signed and converted to java-spring (the mock scaffold provider
 *     answers with the canonical PHP catalog archetype, whose `QuantityResolver.resolveQty` is the Java side).
 *   - The equivalence gate runs the PHP source in the php sidecar and the Java method in the maven sidecar on the same
 *     inputs: vectors drawn from the signed spec plus cases an engineer adds in the request. Each vector is PASSED,
 *     FAILED or SKIPPED with its reason — here `qty = "0"` FAILS on purpose: PHP's empty("0") turns it into 1, the
 *     Java target keeps 0 (the archetype documents that as a deliberate fix), and the gate must say so.
 *   - A routine that queries a database is SKIPPED with that reason; no vector runs.
 *
 * Runs on the mock providers. Needs the php sidecar (Validation:PhpEndpoint) and the maven sidecar; when the API cannot
 * reach the php sidecar the run reports ERRORED and the spec is skipped.
 */
import { expect, test, type APIRequestContext } from '@playwright/test';
import { API_BASE, JSON_ENG } from './helpers/projects';

const ENG = { 'X-Dev-Persona': 'engineer' };
const SME = { 'X-Dev-Persona': 'sme' };
const T = 300_000;

const QTY = [
  '<?php',
  '$db = mysqli_connect("db", "u", "p");   // top-level: never runs in an equivalence run',
  '',
  'function resolveQty($input) {',
  "    $qty = $input['qty'] ?? 1;",
  '    if (empty($qty)) $qty = 1;',
  '    return (int)$qty;',
  '}',
  '',
  'function fetch_order($id) {',
  '    global $db;',
  '    return mysqli_query($db, "SELECT * FROM orders WHERE id = " . (int)$id);',
  '}',
  '',
].join('\n');

async function signAndScaffold(request: APIRequestContext, subroutineId: string): Promise<string> {
  await (await request.post(`${API_BASE}/api/v1/subroutines/${subroutineId}/extract`, { headers: ENG, data: {}, timeout: T })).text();
  const spec = await (await request.get(`${API_BASE}/api/v1/subroutines/${subroutineId}/spec`, { headers: ENG, timeout: T })).json();
  expect(spec?.id).toBeTruthy();
  await request.post(`${API_BASE}/api/v1/specs/${spec.id}/route`, { headers: ENG, data: { routingNote: 'e2e' }, timeout: T });
  for (const [section, claims] of Object.entries(spec.spec ?? {})) {
    if (!Array.isArray(claims)) continue;
    for (const claim of claims as Array<{ id?: string }>) {
      if (!claim?.id || section === 'inputs' || section === 'outputs') continue;
      const claimPath = `$.${section}[?(@.id=='${claim.id}')]`;
      const data = section === 'open_questions'
        ? { claimPath, action: 'edit', editedText: 'Resolved for the e2e fixture.' }
        : { claimPath, action: 'accept' };
      await request.post(`${API_BASE}/api/v1/specs/${spec.id}/claims/review`, { headers: SME, data, timeout: T });
    }
  }
  const sign = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/sign`, {
    headers: SME, data: { confirmation: 'I have reviewed every claim.' }, timeout: T,
  });
  expect(sign.ok(), await sign.text()).toBe(true);
  const gen = await request.post(`${API_BASE}/api/v1/specs/${spec.id}/scaffold?targetStack=java-spring`, { headers: ENG, data: {}, timeout: T });
  const genText = await gen.text();
  // The PHP → Java archetype the mock answers with is `preview`, which the scaffold route gates unless it is enabled.
  test.skip(genText.includes('scaffold.target_gated'), 'the PHP java-spring archetype is gated (preview) on this stack');
  const list = (await (await request.get(`${API_BASE}/api/v1/scaffolds?targetStack=java-spring`, { headers: ENG, timeout: T })).json()).data ?? [];
  const scaffold = list.find((s: { specId: string }) => s.specId === spec.id);
  expect(scaffold, 'a java-spring scaffold for the signed spec').toBeTruthy();
  return scaffold.id;
}

test.describe('PHP equivalence, vector by vector', () => {
  test.describe.configure({ mode: 'serial' });
  let routines: Record<string, string> = {};

  test.beforeAll(async ({ request }) => {
    test.setTimeout(T);
    const res = await request.post(`${API_BASE}/api/v1/ingest/text`, {
      headers: JSON_ENG,
      timeout: T,
      data: { name: `php-equivalence-e2e-${Date.now()}-${Math.floor(Math.random() * 1e4)}`, files: [{ path: 'src/qty.php', content: QTY }] },
    });
    expect(res.ok()).toBe(true);
    const corpusId = (await res.json()).corpusId;
    const detail = await (await request.get(`${API_BASE}/api/v1/corpora/${corpusId}`, { headers: ENG, timeout: T })).json();
    for (const f of detail.latestVersion.files) for (const s of f.subroutines ?? []) routines[s.name] = s.id;
  });

  test('resolveQty: PHP and Java agree on most vectors and the empty("0") difference fails, with its reason', async ({ request }) => {
    test.setTimeout(600_000);
    expect(routines.resolveQty, JSON.stringify(routines)).toBeTruthy();
    const scaffoldId = await signAndScaffold(request, routines.resolveQty);

    const res = await request.post(`${API_BASE}/api/v1/scaffolds/${scaffoldId}/validate/equivalence`, {
      headers: JSON_ENG,
      timeout: T,
      data: {
        vectors: [
          { label: 'a quantity', named: { qty: '3' } },
          { label: 'zero', named: { qty: '0' } },
          { label: 'not a number', named: { qty: 'abc' } },
          { label: 'absent', named: {} },
        ],
      },
    });
    expect(res.ok(), await res.text()).toBe(true);
    const run = await res.json();
    if (run.status === 'ERRORED' && /PHP sidecar|maven sidecar/.test(run.summary ?? '')) test.skip(true, run.summary);

    expect(run.stage).toBe('EQUIVALENCE');
    expect(run.status, run.summary).toBe('FAILED');
    const m = run.metrics;
    expect(m.mode).toBe('per-vector');
    expect(m.javaTarget).toMatchObject({ className: 'com.acme.catalog.QuantityResolver', method: 'resolveQty' });
    const byLabel = Object.fromEntries(m.vectors.map((v: any) => [v.label, v]));
    expect(byLabel['a quantity'].status, byLabel['a quantity'].reason).toBe('PASSED');
    expect(byLabel['absent'].status, byLabel['absent'].reason).toBe('PASSED');
    expect(byLabel['zero'].status).toBe('FAILED');
    expect(byLabel['zero'].diff).toBe('$: PHP 1 ≠ Java 0');
    expect(byLabel['not a number'].status).toBe('FAILED');
    expect(byLabel['not a number'].reason).toContain('IllegalArgumentException');
    // The spec's own inputs were tried too, after the engineer's cases.
    expect(m.vectors.some((v: any) => v.source !== 'engineer')).toBe(true);
    expect(m.failed).toBeGreaterThanOrEqual(2);
    expect(m.passed).toBeGreaterThanOrEqual(2);
    expect(run.summary).toContain('differ on');

    // The run is listed with the scaffold's validation runs, metrics and all.
    const runs = (await (await request.get(`${API_BASE}/api/v1/scaffolds/${scaffoldId}/validation`, { headers: ENG, timeout: T })).json()).runs;
    expect(runs.find((r: any) => r.id === run.id)?.metrics?.vectors?.length).toBe(m.vectors.length);
  });

  test('a routine that queries a database is skipped with that reason', async ({ request }) => {
    test.setTimeout(600_000);
    const scaffoldId = await signAndScaffold(request, routines.fetch_order);
    const run = await (await request.post(`${API_BASE}/api/v1/scaffolds/${scaffoldId}/validate/equivalence`, { headers: ENG, timeout: T })).json();
    expect(run.status).toBe('SKIPPED');
    expect(run.errorCode).toBe('equivalence.needs_database');
    expect(run.summary).toContain('mysqli_query');
  });
});
