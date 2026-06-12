import { clsx } from 'clsx';

type State = 'PARSED' | 'DRAFT' | 'IN_REVIEW' | 'SIGNED' | 'SCAFFOLDED' | 'COMMITTED' | string;

/**
 * ACE-style status pill with a coloured leading dot. Encapsulates the
 * routine / spec / scaffold state → colour mapping so the lane colours
 * stay consistent across every table cell in the product.
 */
export function StateBadge({ state, className }: { state: State; className?: string }) {
  const c = colours(state);
  return (
    <span className={clsx('pill ring-1', c.bg, c.text, c.ring, className)}>
      <span className={clsx('h-1.5 w-1.5 rounded-full', c.dot)} />
      {state}
    </span>
  );
}

function colours(state: State) {
  switch (state) {
    case 'COMMITTED':
      return { bg: 'bg-emerald-100', text: 'text-emerald-700', ring: 'ring-emerald-200', dot: 'bg-emerald-600' };
    case 'SIGNED':
      return { bg: 'bg-ace-50',     text: 'text-ace-700',     ring: 'ring-ace-100',     dot: 'bg-ace-600' };
    case 'SCAFFOLDED':
      return { bg: 'bg-amber-50',   text: 'text-amber-700',   ring: 'ring-amber-200',   dot: 'bg-amber-600' };
    case 'IN_REVIEW':
    case 'DRAFT':
      return { bg: 'bg-amber-50',   text: 'text-amber-700',   ring: 'ring-amber-200',   dot: 'bg-amber-500' };
    case 'PARSED':
    default:
      return { bg: 'bg-slate-100',  text: 'text-slate-600',   ring: 'ring-slate-200',   dot: 'bg-slate-400' };
  }
}
