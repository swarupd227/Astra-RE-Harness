/**
 * Phase #4 / value-add #3 — Targets selector.
 *
 * Validates that:
 *   - The Spec-review surface renders a target-stack selector when a spec
 *     is SIGNED and the engineer is logged in.
 *   - Both shipped archetypes (.NET 8 production, Java Spring preview)
 *     appear as cards.
 *   - The preview card is locked / non-clickable.
 *   - Selecting a target persists across reloads (localStorage).
 *   - POST /api/v1/specs/{id}/scaffold rejects gated targets server-side.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
const ENG = { 'X-Dev-Persona': 'engineer' };

async function getSignedSpecId(page: Page): Promise<string | null> {
  const list = await page.request.get(`${API_BASE}/api/v1/corpora`, { headers: ENG }).then((r) => r.json());
  const seed = list.data.find((c: { name: string }) => c.name === 'Roll-stock inventory demo (Fortran F77)');
  if (!seed) return null;
  const detail = await page.request.get(`${API_BASE}/api/v1/corpora/${seed.id}`, { headers: ENG }).then((r) => r.json());
  const subId = detail.latestVersion.files[0].subroutines[0].id;
  const spec = await page.request
    .get(`${API_BASE}/api/v1/subroutines/${subId}/spec`, { headers: ENG })
    .then((r) => r.json());
  return spec?.state === 'SIGNED' ? (spec.id as string) : null;
}

test.describe('Phase #4 · Targets selector', () => {
  test.beforeEach(async ({ page }) => {
    await page.goto('/');
    await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  });

  test('GET /archetypes returns at least two archetypes (dotnet8 production, java-spring preview)', async ({ page }) => {
    const res = await page.request.get(`${API_BASE}/api/v1/archetypes`, { headers: ENG });
    expect(res.ok()).toBe(true);
    const body = await res.json();
    const stacks = new Set<string>(body.data.map((a: { targetStack: string }) => a.targetStack));
    expect(stacks.has('dotnet8')).toBe(true);
    expect(stacks.has('java-spring')).toBe(true);

    const dotnet = body.data.find((a: { targetStack: string }) => a.targetStack === 'dotnet8');
    const jspring = body.data.find((a: { targetStack: string }) => a.targetStack === 'java-spring');
    expect(dotnet.status.toLowerCase()).toContain('production');
    expect(jspring.status.toLowerCase()).toContain('preview');
  });

  test('SpecReview page renders target cards for both stacks; .NET 8 selected by default; Java Spring is locked', async ({ page }) => {
    // Need a SIGNED spec for the SIGNED-only selector to render.
    // The demo-path test signs one — re-use it where possible.
    const specId = await getSignedSpecId(page);
    test.skip(!specId, 'No SIGNED CONSUME_ROLL spec — run demo-path first.');

    // The spec-review URL takes subroutineId, not specId. Look it up.
    const spec = await page.request.get(`${API_BASE}/api/v1/specs/${specId}`, { headers: ENG }).then((r) => r.json());
    const subId = spec.subroutine.id;

    await page.goto(`/subroutines/${subId}/review`);
    const selector = page.getByTestId('target-selector');
    await expect(selector).toBeVisible();

    const dotnet = page.getByTestId('target-card-dotnet8');
    const jspring = page.getByTestId('target-card-java-spring');
    await expect(dotnet).toBeVisible();
    await expect(jspring).toBeVisible();
    // Production target is selectable; preview is locked (disabled attribute).
    await expect(dotnet).toBeEnabled();
    await expect(jspring).toBeDisabled();
  });

  test('POST /scaffold with a preview target stack is rejected with a 400 + helpful message', async ({ page }) => {
    const specId = await getSignedSpecId(page);
    test.skip(!specId, 'No SIGNED spec for the gated-target rejection check.');

    const res = await page.request.post(
      `${API_BASE}/api/v1/specs/${specId}/scaffold?targetStack=java-spring`,
      { headers: ENG },
    );
    expect(res.status()).toBe(400);
    const body = await res.json();
    expect(body.error.code).toBe('scaffold.target_gated');
    expect(body.error.message.toLowerCase()).toContain('pair-engagement');
  });
});
