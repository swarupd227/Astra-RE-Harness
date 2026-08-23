/**
 * Human labels for the values the API stores as enums.
 *
 * These states are persisted as SCREAMING_SNAKE_CASE and were being
 * rendered straight into badges, so users read "IN_REVIEW" and
 * "SCAFFOLDED" — database vocabulary on screen. This converts them at
 * the point of display only; the stored values are untouched, so
 * filters, API calls and test selectors keep working on the raw value.
 */

// Cases the generic rule would get wrong or render awkwardly.
const EXPLICIT: Record<string, string> = {
  IN_REVIEW: 'In review',
  VERIFICATION_FAILED: 'Verification failed',
  NOT_STARTED: 'Not started',
  IN_PROGRESS: 'In progress',
  PRODUCTION: 'Live',
};

/** "IN_REVIEW" → "In review"; "SCAFFOLDED" → "Scaffolded". */
export function formatState(state: string | null | undefined): string {
  if (!state) return '—';
  const key = state.toUpperCase();
  if (EXPLICIT[key]) return EXPLICIT[key];
  // Only reshape values that are actually enum-shaped — anything already
  // written for humans passes through untouched.
  if (!/^[A-Z0-9_]+$/.test(state)) return state;
  return state.charAt(0) + state.slice(1).toLowerCase().replace(/_/g, ' ');
}
