/**
 * One-off: capture a screenshot of /subroutines?state=SIGNED so we can
 * splice it into the demo video where the original recording showed
 * "No matches" due to the action='question' sign-precondition bug.
 *
 * Not part of the regular suite — invoke explicitly.
 */
import { test } from '@playwright/test';

test('snap progress view', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => window.localStorage.setItem('astra.devPersona', 'engineer'));
  await page.goto('/subroutines?state=SIGNED');
  // Give the search query time to settle.
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(800);
  await page.screenshot({
    path: 'demo-output/progress-view-signed.png',
    fullPage: false,
  });
});
