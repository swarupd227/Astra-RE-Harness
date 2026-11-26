/**
 * The dev persona switcher, as the specs that walk Engineer → SME → Engineer use it.
 *
 * Picking a persona reloads the page. "Network idle" is no way to wait for that: the conversation's
 * event stream stays open, so the network may never go quiet. These helpers wait for the reload
 * itself and then for something a person would see.
 */
import { expect, type Page } from '@playwright/test';

const LABEL = { engineer: 'Engineer', sme: 'SME', observer: 'Observer', admin: 'Admin' } as const;

export async function switchPersona(page: Page, persona: keyof typeof LABEL) {
  const label = LABEL[persona];
  // The trigger is the only menu button in the shell.
  await page.locator('button[aria-haspopup="menu"]').click();
  const reloaded = page.waitForEvent('load');
  // The accessible name is "<Label> <tagline>".
  await page.getByRole('menuitemradio', { name: new RegExp(`^${label}\\s`) }).click();
  await reloaded;
  await expect(page.getByRole('button', { name: `Persona: ${label}`, exact: true })).toBeVisible();
}

/**
 * On spec review as the SME: waits until the claim cards offer their decisions. They render once the
 * spec and the signed-in persona have both loaded; counting cards before that finds none.
 */
export async function waitForReviewableClaims(page: Page) {
  await expect(
    page.locator('article[id^="claim-"]').first().getByRole('button', { name: /^(Accept|Resolve in spec)$/ }),
  ).toBeVisible();
}

/** On a signed spec as the Engineer: opens its generated code, generating it first if there is none. */
export async function openGeneratedCode(page: Page, generateTimeout = 30_000) {
  const generate = page.getByRole('link', { name: /Generate scaffold/ });
  const openExisting = page.getByRole('link', { name: /Open scaffold/ });
  // The header says "Checking for generated code…" until it knows which of the two to offer.
  await expect(generate.or(openExisting)).toBeVisible();
  if (await openExisting.isVisible()) {
    await openExisting.click();
    return;
  }
  await generate.click();
  const open = page.getByRole('button', { name: /Open scaffold/ });
  await expect(open).toBeVisible({ timeout: generateTimeout });
  await open.click();
}
