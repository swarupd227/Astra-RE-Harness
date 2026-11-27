import { clsx } from 'clsx';
import type { FunnelCounts } from '@/lib/conversations';
import { formatInt, num, obj } from './format';

// The same words as the project page's filter chips and Home ("Not started", "With code"), not engine states.
export const FUNNEL_STAGES: Array<{ key: keyof Omit<FunnelCounts, 'total'>; label: string; swatch: string }> = [
  { key: 'parsed', label: 'Not started', swatch: 'bg-sand-500' },
  { key: 'extracting', label: 'Extracting', swatch: 'bg-status-info/70' },
  { key: 'draft', label: 'Draft', swatch: 'bg-status-warn/60' },
  { key: 'inReview', label: 'In review', swatch: 'bg-status-warn' },
  { key: 'signed', label: 'Signed', swatch: 'bg-status-info' },
  { key: 'scaffolded', label: 'With code', swatch: 'bg-status-ok/70' },
  { key: 'committed', label: 'Committed to Git', swatch: 'bg-status-ok' },
];

export function normaliseCounts(v: unknown): FunnelCounts {
  const o = obj(v);
  const c: FunnelCounts = {
    parsed: num(o.parsed),
    extracting: num(o.extracting),
    draft: num(o.draft),
    inReview: num(o.inReview),
    signed: num(o.signed),
    scaffolded: num(o.scaffolded),
    committed: num(o.committed),
    total: num(o.total),
  };
  if (!c.total) {
    c.total = c.parsed + c.extracting + c.draft + c.inReview + c.signed + c.scaffolded + c.committed;
  }
  return c;
}

/**
 * Horizontal segmented funnel: parsed ▸ extracting ▸ draft ▸ in review ▸
 * signed ▸ scaffolded ▸ committed. Numbers underneath, one column per stage.
 */
export function FunnelBar({
  counts,
  dense = false,
  layout = 'row',
  className,
}: {
  counts: FunnelCounts | unknown;
  dense?: boolean;
  /** `list`: one stage per line in two columns, for narrow hosts (the artifact pane). */
  layout?: 'row' | 'list';
  className?: string;
}) {
  const c = normaliseCounts(counts);
  const total = Math.max(1, c.total);
  return (
    <div className={clsx('space-y-2', className)}>
      <div
        className="flex h-2 w-full overflow-hidden rounded-full bg-sunken ring-1 ring-inset ring-line-subtle"
        role="img"
        aria-label={FUNNEL_STAGES.map((s) => `${s.label} ${c[s.key]}`).join(', ')}
      >
        {FUNNEL_STAGES.map((s) => {
          const v = c[s.key];
          if (v <= 0) return null;
          return (
            <div
              key={s.key}
              className={clsx('h-full transition-[width] duration-slow', s.swatch)}
              style={{ width: `${(v / total) * 100}%` }}
              title={`${s.label}: ${formatInt(v)}`}
            />
          );
        })}
      </div>
      <div
        className={clsx(
          'grid gap-x-2',
          layout === 'list'
            ? 'grid-cols-2 gap-x-6 gap-y-1.5 text-caption'
            : dense
              ? 'grid-cols-7 text-micro'
              : 'grid-cols-4 text-caption sm:grid-cols-7',
        )}
      >
        {FUNNEL_STAGES.map((s) => (
          <div key={s.key} className="flex min-w-0 items-baseline gap-1.5">
            <span className={clsx('h-1.5 w-1.5 shrink-0 rounded-full', s.swatch)} aria-hidden="true" />
            <span className={clsx('truncate', layout === 'list' ? 'text-ink-secondary' : 'text-ink-tertiary')}>{s.label}</span>
            <span className="ml-auto font-mono tabular-nums text-ink-primary">{formatInt(c[s.key])}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
