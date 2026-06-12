import { clsx } from 'clsx';
import type { ReactNode } from 'react';

type Tone =
  | 'neutral'
  | 'draft'
  | 'review'
  | 'signed'
  | 'scaffolded'
  | 'failed'
  | 'superseded'
  | 'success'
  | 'info'
  | 'brand';

// ACE-style pill: light-soft background + saturated text + thin ring.
// Reads cleanly on white cards. Use `tone="brand"` for primary-CTA
// accents in the brand orange.
const tones: Record<Tone, string> = {
  neutral:    'bg-slate-100 text-slate-700 ring-1 ring-slate-200',
  draft:      'bg-amber-50 text-amber-700 ring-1 ring-amber-200',
  review:     'bg-emerald-50 text-emerald-700 ring-1 ring-emerald-200',
  signed:     'bg-ace-50 text-ace-700 ring-1 ring-ace-100',
  scaffolded: 'bg-amber-50 text-amber-700 ring-1 ring-amber-200',
  failed:     'bg-rose-50 text-rose-700 ring-1 ring-rose-200',
  superseded: 'bg-slate-100 text-slate-500 ring-1 ring-slate-200 line-through',
  success:    'bg-emerald-50 text-emerald-700 ring-1 ring-emerald-200',
  info:       'bg-teal-50 text-teal-700 ring-1 ring-teal-200',
  brand:      'bg-brand-50 text-brand-700 ring-1 ring-brand-300/40',
};

export function Badge({
  tone = 'neutral',
  children,
  icon,
  className,
}: {
  tone?: Tone;
  children: ReactNode;
  icon?: ReactNode;
  className?: string;
}) {
  return (
    <span
      className={clsx(
        'inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] font-semibold tabular-nums',
        tones[tone],
        className,
      )}
    >
      {icon}
      {children}
    </span>
  );
}
