/**
 * Small, dependency-free formatting helpers for the Workspace. Everything
 * here is defensive: artifact props come straight off the wire and a card
 * must never crash on a missing field.
 */

// ─── Defensive accessors ─────────────────────────────────────────────

export function str(v: unknown, fallback = ''): string {
  return typeof v === 'string' ? v : v == null ? fallback : String(v);
}

export function num(v: unknown, fallback = 0): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback;
}

export function numOrNull(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

export function arr<T = unknown>(v: unknown): T[] {
  return Array.isArray(v) ? (v as T[]) : [];
}

export function bool(v: unknown, fallback = false): boolean {
  return typeof v === 'boolean' ? v : fallback;
}

/** Strings only, dropping empties — for `drivers: string[]` style props. */
export function strs(v: unknown): string[] {
  return arr(v).map((x) => str(x)).filter(Boolean);
}

export function obj(v: unknown): Record<string, unknown> {
  return v && typeof v === 'object' && !Array.isArray(v) ? (v as Record<string, unknown>) : {};
}

export function pretty(v: unknown): string {
  try {
    return JSON.stringify(v ?? {}, null, 2);
  } catch {
    return String(v);
  }
}

// ─── Time ────────────────────────────────────────────────────────────

/** "just now" · "2 min ago" · "3 h ago" · "yesterday" · "12 Sep". */
export function relativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '';
  const diff = Math.max(0, now - t);
  const s = Math.round(diff / 1000);
  if (s < 45) return 'just now';
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} h ago`;
  const d = Math.round(h / 24);
  if (d === 1) return 'yesterday';
  if (d < 7) return `${d} d ago`;
  return new Date(t).toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

export function absoluteTime(iso: string | null | undefined): string {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '';
  return new Date(t).toLocaleString();
}

export function formatEta(seconds?: number | null): string | null {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return null;
  if (seconds < 60) return `~${Math.max(1, Math.round(seconds))}s left`;
  return `~${Math.ceil(seconds / 60)} min left`;
}

export function formatDuration(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms)) return '';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.round(ms / 60_000)} min`;
}

// ─── Numbers ─────────────────────────────────────────────────────────

export function formatInt(n: number | null | undefined): string {
  if (n == null || !Number.isFinite(n)) return '—';
  return n.toLocaleString();
}

export function formatMoney(usd: number | null | undefined): string {
  if (usd == null || !Number.isFinite(usd)) return '—';
  if (usd < 0.01 && usd > 0) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(2)}`;
}

export function formatPercent(ratio: number | null | undefined): string {
  if (ratio == null || !Number.isFinite(ratio)) return '—';
  return `${Math.round(ratio * 100)}%`;
}

// ─── Vocabulary ──────────────────────────────────────────────────────

const LANGUAGES: Record<string, string> = {
  'fortran-f77': 'Fortran 77',
  fortran: 'Fortran',
  cobol: 'COBOL',
  delphi: 'Delphi',
  cpp: 'C++',
  vb6: 'VB6',
  csharp: 'C#',
  java: 'Java',
  typescript: 'TypeScript',
};

export function formatLanguage(lang: string | null | undefined): string {
  if (!lang) return '—';
  return LANGUAGES[lang.toLowerCase()] ?? lang;
}

const STATE_LABELS: Record<string, string> = {
  IN_REVIEW: 'In review',
  VERIFICATION_FAILED: 'Verification failed',
  NOT_STARTED: 'Not started',
  IN_PROGRESS: 'In progress',
};

/** "IN_REVIEW" → "In review"; anything already human passes through. */
export function formatState(state: string | null | undefined): string {
  if (!state) return '—';
  const key = state.toUpperCase();
  if (STATE_LABELS[key]) return STATE_LABELS[key];
  if (!/^[A-Z0-9_-]+$/.test(state)) return state;
  return state.charAt(0) + state.slice(1).toLowerCase().replace(/[_-]/g, ' ');
}

export type Tone = 'ok' | 'warn' | 'fail' | 'info' | 'neutral';

export function stateTone(state: string | null | undefined): Tone {
  const s = (state ?? '').toUpperCase().replace(/[\s-]/g, '_');
  if (!s) return 'neutral';
  if (/FAIL|ERROR|CANCEL|REJECT|DRIFT|BLOCKED/.test(s)) return 'fail';
  if (/SIGNED|COMMITTED|COMPLETED|PASS|SUCCEEDED|OK|ACCEPT|HEALTHY|APPROVED|VERIFIED|BUILT/.test(s)) return 'ok';
  if (/RUNNING|EXTRACTING|QUEUED|PENDING|IN_PROGRESS|PARSED|PLANNED/.test(s)) return 'info';
  if (/DRAFT|REVIEW|SCAFFOLDED|PAUSED|WARN|QUESTION|EDIT|UNSIGNED/.test(s)) return 'warn';
  return 'neutral';
}

export const TONE_DOT: Record<Tone, string> = {
  ok: 'bg-status-ok',
  warn: 'bg-status-warn',
  fail: 'bg-status-fail',
  info: 'bg-status-info',
  neutral: 'bg-sand-400',
};

export const TONE_TEXT: Record<Tone, string> = {
  ok: 'text-status-ok',
  warn: 'text-status-warn',
  fail: 'text-status-fail',
  info: 'text-status-info',
  neutral: 'text-ink-tertiary',
};

export const TONE_BORDER: Record<Tone, string> = {
  ok: 'border-status-ok/40',
  warn: 'border-status-warn/40',
  fail: 'border-status-fail/40',
  info: 'border-status-info/40',
  neutral: 'border-line',
};

export const TONE_BG: Record<Tone, string> = {
  ok: 'bg-status-ok/10',
  warn: 'bg-status-warn/10',
  fail: 'bg-status-fail/10',
  info: 'bg-status-info/10',
  neutral: 'bg-sunken',
};

const PERSONA_TEXT: Record<string, string> = {
  engineer: 'text-persona-engineer border-persona-engineer/40 bg-persona-engineer/10',
  sme: 'text-persona-sme border-persona-sme/40 bg-persona-sme/10',
  observer: 'text-persona-observer border-persona-observer/40 bg-persona-observer/10',
  admin: 'text-persona-admin border-persona-admin/40 bg-persona-admin/10',
};

export function personaClasses(persona: string | null | undefined): string {
  return PERSONA_TEXT[(persona ?? '').toLowerCase()] ?? 'text-ink-secondary border-line bg-sunken';
}

export function personaLabel(persona: string | null | undefined): string {
  if (!persona) return '';
  const p = persona.toLowerCase();
  if (p === 'sme') return 'SME';
  return p.charAt(0).toUpperCase() + p.slice(1);
}

/** Terminal run states — a runProgress card stops subscribing once here. */
export function isTerminalRunState(state: string | null | undefined): boolean {
  const s = (state ?? '').toUpperCase();
  return /COMPLETED|FAILED|CANCELLED|CANCELED|SUCCEEDED|ERROR|DONE/.test(s);
}
