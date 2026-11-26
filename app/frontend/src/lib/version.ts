/**
 * Build identity, shown in the rail footer tooltip and on System health. `commit` is the sha the image was built
 * from (the Dockerfile passes BUILD_SHA through as VITE_BUILD_SHA; `dev` on a local run) — the same value stamped on
 * `<html data-build>`, so the page and the bundle never disagree.
 */
export const buildInfo = {
  version: '0.1.0',
  commit: import.meta.env.VITE_BUILD_SHA || import.meta.env.VITE_COMMIT_SHA || 'dev',
  builtAt: import.meta.env.VITE_BUILT_AT ?? '',
} as const;
