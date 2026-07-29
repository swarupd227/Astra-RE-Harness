import { clsx } from 'clsx';
import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';

type Accent = 'indigo' | 'emerald' | 'amber' | 'teal' | 'violet' | 'rose' | 'orange' | 'slate';

// ACE-pattern: small icon box top-right, with a tinted background + a
// matching saturated icon. Reads cleanly on white card surfaces.
const accentClass: Record<Accent, string> = {
  indigo:  'bg-ace-50    text-ace-600',
  emerald: 'bg-emerald-50 text-emerald-600',
  amber:   'bg-amber-50  text-amber-600',
  teal:    'bg-teal-50   text-teal-600',
  violet:  'bg-violet-50 text-violet-600',
  rose:    'bg-rose-50   text-rose-600',
  orange:  'bg-brand-50  text-brand-600',
  slate:   'bg-slate-100 text-slate-500',
};

/**
 * ACE-style KPI tile. White card, uppercase tracking-wider label with
 * a tinted icon chip on the right, big tabular-nums value, optional
 * hint + target pill underneath. Used across Portfolio Dashboard,
 * Signature Health, Admin console.
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
        alert ? 'border-rose-300/70 ring-1 ring-rose-200/60' : 'hover:border-border',
        className,
      )}
    >
      <div className="mb-1 flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-ink-tertiary">
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
          alert ? 'text-rose-600' : 'text-ink-primary',
        )}
      >
        {value}
      </div>
      {(hint || target) && (
        <div className="mt-1 flex items-center justify-between gap-2">
          {hint && <span className="text-[11px] text-ink-tertiary">{hint}</span>}
          {target && (
            <span className="pill bg-ace-50 text-ace-700 ring-1 ring-ace-100">
              target {target}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
