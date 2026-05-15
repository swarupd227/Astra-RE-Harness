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
  | 'success';

const tones: Record<Tone, string> = {
  neutral: 'bg-sunken text-ink-secondary border-border-subtle',
  draft: 'bg-accent-muted text-status-draft border-accent/40',
  review: 'bg-[#DAEFE9] text-status-review border-status-review/40',
  signed: 'bg-[#DCE6F5] text-status-signed border-status-signed/40',
  scaffolded: 'bg-[#F2E5C2] text-status-scaffolded border-status-scaffolded/40',
  failed: 'bg-[#F4D8D7] text-status-failed border-status-failed/40',
  superseded: 'bg-sunken text-status-superseded border-border line-through',
  success: 'bg-[#DAEFE9] text-status-review border-status-review/40',
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
        'inline-flex items-center gap-1 rounded-sm border px-2 py-0.5 text-caption font-medium',
        tones[tone],
        className,
      )}
    >
      {icon}
      {children}
    </span>
  );
}
