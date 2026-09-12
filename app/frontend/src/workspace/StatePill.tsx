import { clsx } from 'clsx';
import { formatState, stateTone, TONE_BORDER, TONE_DOT, TONE_TEXT, type Tone } from './format';

/** Compact state badge: coloured dot + human label, driven by status tokens. */
export function StatePill({
  state,
  tone,
  label,
  className,
  size = 'sm',
}: {
  state?: string | null;
  tone?: Tone;
  label?: string;
  className?: string;
  size?: 'xs' | 'sm';
}) {
  const t = tone ?? stateTone(state);
  return (
    <span
      className={clsx(
        'inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full border font-medium',
        size === 'xs' ? 'px-1.5 py-px text-micro' : 'px-2 py-0.5 text-micro',
        TONE_BORDER[t],
        TONE_TEXT[t],
        className,
      )}
      data-state={state ?? undefined}
    >
      <span className={clsx('h-1.5 w-1.5 rounded-full', TONE_DOT[t])} aria-hidden="true" />
      {label ?? formatState(state)}
    </span>
  );
}
