import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright config for the Astra Appendix-A demo-path E2E.
 *
 * Targets the running Docker Compose stack at http://127.0.0.1:35173 by default —
 * use `BASE_URL` to point elsewhere. The tests assume:
 *   - the backend has the seed corpus (`Roll-stock inventory demo`)
 *   - the API is at $API_BASE (default http://127.0.0.1:38080)
 *
 * The 4-minute Appendix A flow is single-test in v1; we add per-stage
 * granularity (extraction-only, sign-only, scaffold-only) in B.6 if the
 * full path becomes too coarse to debug.
 */
// RECORD_DEMO=1 flips trace/video to always-on so the walkthrough run
// produces a clean artifact even when the test passes. Default config
// stays "retain on failure" so day-to-day CI doesn't waste disk.
const recordDemo = process.env.RECORD_DEMO === '1';

export default defineConfig({
  testDir: './tests',
  // recordDemo runs the full HYBRD1 walk + validation report + 5-routine
  // montage + dashboard shot — ~12 min wall clock at real-Anthropic speeds.
  // Headroom up to 20 min so a slow Anthropic call doesn't time-out the entire
  // record-cycle. Non-record runs stay at 4 min.
  timeout: recordDemo ? 1_200_000 : 240_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: [
    ['list'],
    ['html', { open: 'never', outputFolder: 'playwright-report' }],
  ],
  use: {
    baseURL: process.env.BASE_URL ?? 'http://127.0.0.1:35173',
    trace: recordDemo ? 'on' : 'retain-on-failure',
    screenshot: 'only-on-failure',
    // Record at native viewport resolution so the demo plays sharp at
    // fullscreen. Defaults to 800x500 which looks pixelated when blown
    // up on a projector or LinkedIn post.
    video: recordDemo
      ? { mode: 'on', size: { width: 1600, height: 1000 } }
      : 'retain-on-failure',
    actionTimeout: recordDemo ? 30_000 : 8_000,
    // The deployed Azure environment has occasionally shown elevated
    // latency (observed >5s just for the base HTML response) — 15s wasn't
    // enough margin once the full SPA bundle load is added on top.
    navigationTimeout: 45_000,
    // RECORD_DEMO: slow every action by 600ms so a stakeholder can watch
    // each click land. The test will naturally take ~1 min longer; that's
    // the cost of stakeholder-readable pacing.
    launchOptions: recordDemo ? { slowMo: 600 } : {},
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1600, height: 1000 } },
    },
  ],
});
