import { clsx } from 'clsx';
import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';

type Accent = 'indigo' | 'emerald' | 'amber' | 'teal' | 'violet' | 'rose' | 'orange' | 'slate';

// v1 accent names map onto the v2 status / sand tokens so the same call
// sites read correctly in both themes without a per-page change.
const accentClass: Record<Accent, string> = {
  indigo:  'bg-status-info/15 text-status-info',
  teal:    'bg-status-info/15 text-status-info',
  emerald: 'bg-status-ok/15 text-status-ok',
  amber:   'bg-status-warn/15 text-status-warn',
  orange:  'bg-status-warn/15 text-status-warn',
  violet:  'bg-sand-400/15 text-sand-400',
  rose:    'bg-status-fail/15 text-status-fail',
  slate:   'bg-sunken text-ink-tertiary',
};

/**
 * KPI tile. Raised card, sentence-case label with a tinted icon chip, big
 * tabular-nums value, optional hint + target pill underneath. Used across
 * Portfolio Dashboard, Signature Health, Admin console.
 */
export function KpiCard({
  label,
  value,
  hint,
  target,
  icon: Icon,
  accent = 'indigo',
  alert = false,
  className,
}: {
  label: ReactNode;
  value: ReactNode;
  hint?: ReactNode;
  /** Optional "target ≥80%" pill rendered on the right of the hint row. */
  target?: ReactNode;
  icon?: LucideIcon;
  accent?: Accent;
  alert?: boolean;
  className?: string;
}) {
  return (
    <div
      className={clsx(
        'card p-5 transition-colors duration-fast',
        alert ? 'border-status-fail/50 ring-1 ring-status-fail/30' : 'hover:border-line',
        className,
      )}
    >
      <div className="mb-1 flex items-center gap-2 text-caption font-semibold text-ink-tertiary">
        {Icon && (
          <span
            className={clsx(
              'inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-md',
              accentClass[accent],
            )}
          >
            <Icon size={12} />
          </span>
        )}
        <span className="truncate">{label}</span>
      </div>
      <div
        className={clsx(
          'mt-1 text-[28px] font-extrabold tabular-nums leading-tight tracking-tight',
          alert ? 'text-status-fail' : 'text-ink-primary',
        )}
      >
        {value}
      </div>
      {(hint || target) && (
        <div className="mt-1 flex items-center justify-between gap-2">
          {hint && <span className="text-caption text-ink-tertiary">{hint}</span>}
          {target && (
            <span className="pill bg-status-info/10 text-status-info ring-1 ring-status-info/25">
              target {target}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
