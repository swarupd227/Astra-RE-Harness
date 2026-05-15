/**
 * Build identity. Wired into the LeftNav footer and the System page.
 * Replaced at build time once we have a real CI pipeline; for now the
 * commit hash and timestamp are placeholders.
 */
export const buildInfo = {
  version: '0.1.0',
  phase: 'A',
  phaseLabel: 'Foundations',
  commit: import.meta.env.VITE_COMMIT_SHA ?? 'local-dev',
  builtAt: import.meta.env.VITE_BUILT_AT ?? '2026-05-06',
} as const;
