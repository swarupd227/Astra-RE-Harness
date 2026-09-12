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

// Pill: 10% tint of the status colour + the status colour as text + a thin
// ring. Every value is a theme token — in the dark shell the status colours
// are the bright pastels, inside the light legacy wrapper the palette swaps
// them for their darker, AA-readable siblings automatically.
const tones: Record<Tone, string> = {
  neutral:    'bg-sunken text-ink-secondary ring-1 ring-line',
  draft:      'bg-status-warn/10 text-status-warn ring-1 ring-status-warn/25',
  review:     'bg-status-ok/10 text-status-ok ring-1 ring-status-ok/25',
  signed:     'bg-status-info/10 text-status-info ring-1 ring-status-info/25',
  scaffolded: 'bg-status-warn/10 text-status-warn ring-1 ring-status-warn/25',
  failed:     'bg-status-fail/10 text-status-fail ring-1 ring-status-fail/25',
  superseded: 'bg-sunken text-ink-tertiary ring-1 ring-line line-through',
  success:    'bg-status-ok/10 text-status-ok ring-1 ring-status-ok/25',
  info:       'bg-status-info/10 text-status-info ring-1 ring-status-info/25',
  brand:      'bg-volt/10 text-volt-ink ring-1 ring-volt/30',
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
