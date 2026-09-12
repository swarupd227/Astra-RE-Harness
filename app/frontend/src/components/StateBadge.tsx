import { clsx } from 'clsx';
import { formatState } from '@/lib/labels';

type State = 'PARSED' | 'DRAFT' | 'IN_REVIEW' | 'SIGNED' | 'SCAFFOLDED' | 'COMMITTED' | string;

/**
 * Status pill with a coloured leading dot. Encapsulates the routine / spec /
 * scaffold state → colour mapping so the lane colours stay consistent across
 * every table cell in the product. Every value is a theme token.
 */
export function StateBadge({ state, className }: { state: State; className?: string }) {
  const c = colours(state);
  return (
    <span className={clsx('pill ring-1', c.bg, c.text, c.ring, className)}>
      <span className={clsx('h-1.5 w-1.5 rounded-full', c.dot)} />
      {formatState(state)}
    </span>
  );
}

function colours(state: State) {
  switch (state) {
    case 'COMMITTED':
      return { bg: 'bg-status-ok/10',   text: 'text-status-ok',   ring: 'ring-status-ok/25',   dot: 'bg-status-ok' };
    case 'SIGNED':
      return { bg: 'bg-status-info/10', text: 'text-status-info', ring: 'ring-status-info/25', dot: 'bg-status-info' };
    case 'SCAFFOLDED':
      return { bg: 'bg-status-warn/10', text: 'text-status-warn', ring: 'ring-status-warn/25', dot: 'bg-status-warn' };
    case 'IN_REVIEW':
    case 'DRAFT':
      return { bg: 'bg-status-warn/10', text: 'text-status-warn', ring: 'ring-status-warn/25', dot: 'bg-status-warn' };
    case 'PARSED':
    default:
      return { bg: 'bg-sunken',         text: 'text-ink-secondary', ring: 'ring-line',         dot: 'bg-ink-tertiary' };
  }
}
