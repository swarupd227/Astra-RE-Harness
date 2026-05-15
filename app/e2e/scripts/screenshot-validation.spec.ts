/**
 * One-off: screenshot the validation report card surface for the demo
 * deck. Not part of the regular suite — invoke explicitly:
 *   npx playwright test scripts/screenshot-validation.spec.ts
 */
import { test } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

test('capture validation report (all gates green)', async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto('/');
  await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));

  const list = await page.request.get(`${API_BASE}/api/v1/corpora`, {
    headers: { 'X-Dev-Persona': 'engineer' },
  }).then((r) => r.json());
  const seed = list.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`, {
    headers: { 'X-Dev-Persona': 'engineer' },
  }).then((r) => r.json());
  const subId = detail.latestVersion.files[0].subroutines[0].id;
  const spec = await page.request.get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, {
    headers: { 'X-Dev-Persona': 'engineer' },
  }).then((r) => r.json());
  const scResp = await page.request.get(`${API_BASE}/api/v1/specs/${spec.id}/scaffold`, {
    headers: { 'X-Dev-Persona': 'engineer' },
  });
  const sc = await scResp.json();

  await page.goto(`/scaffolds/${sc.id}/validation`);
  await page.waitForLoadState('networkidle');
  await page.screenshot({
    path: 'demo-output/validation-report.png',
    fullPage: true,
  });
});
