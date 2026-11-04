/**
 * Reduced motion. The shell kills CSS animation/transition durations under
 * `prefers-reduced-motion: reduce` (index.css) and wraps the app in
 * <MotionConfig reducedMotion="user"> for framer-motion (main.tsx). This pins
 * the CSS half to what a user sees, with a control: the same page with motion
 * allowed must still animate, or the assertion below would pass vacuously.
 */
import { expect, test, type Page } from '@playwright/test';

const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';

async function corpusId(page: Page): Promise<string> {
  const corpora = await page.request.get(`${API_BASE}/api/v1/corpora`).then((r) => r.json());
  return corpora.data[0].id;
}

// `.fadeup` is the page-entry animation (300 ms) on every artifact view.
async function entryAnimation(page: Page, reducedMotion: 'reduce' | 'no-preference') {
  // emulateMedia, not `test.use({ reducedMotion })`: the use option left the
  // preference off in this setup, which the assertions below would have caught.
  await page.emulateMedia({ reducedMotion });
  const id = await corpusId(page);
  await page.goto(`/corpora/${id}/board`);
  await expect(page.locator('.fadeup').first()).toBeVisible();
  return page.evaluate(() => {
    const el = document.querySelector('.fadeup')!;
    const css = getComputedStyle(el);
    const infiniteRunning = document
      .getAnimations()
      .filter((a) => {
        const t = a.effect?.getTiming?.();
        return a.playState === 'running' && t && t.iterations === Infinity && Number(t.duration) > 0;
      }).length;
    const prefersReduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
    return { prefersReduce, duration: css.animationDuration, infiniteRunning };
  });
}

test.describe('Reduced motion · prefers reduce', () => {
  test('the page-entry animation is off and nothing loops', async ({ page }) => {
    const s = await entryAnimation(page, 'reduce');
    expect(s.prefersReduce, 'the emulated preference is on').toBe(true);
    expect(s.duration).toBe('0s');
    expect(s.infiniteRunning).toBe(0);
  });
});

test.describe('Reduced motion · control', () => {
  test('with motion allowed the same page still animates in', async ({ page }) => {
    const s = await entryAnimation(page, 'no-preference');
    expect(s.prefersReduce, 'the control has the preference off').toBe(false);
    expect(s.duration).not.toBe('0s');
  });
});
