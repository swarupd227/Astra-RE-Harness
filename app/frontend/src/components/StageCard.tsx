import type { ComponentType } from 'react';
import { clsx } from 'clsx';
import { ChevronRight, Lock } from 'lucide-react';
import { PhaseChip, type PhaseId } from './PhaseChip';

export type StageProps = {
  index: number;
  name: string;
  tagline: string;
  icon: ComponentType<{ className?: string }>;
  phase: PhaseId;
  active?: boolean;
};

export function StageCard({
  index,
  name,
  tagline,
  icon: Icon,
  phase,
  active,
}: StageProps) {
  return (
    <article
      className={clsx(
        'group relative flex h-full min-w-0 flex-col gap-3 rounded-md border bg-raised p-4 shadow-e1 transition-all duration-medium',
        active
          ? 'border-accent/60 shadow-e2 ring-1 ring-accent/20'
          : 'border-border-subtle hover:-translate-y-px hover:shadow-e2',
      )}
    >
      <header className="flex items-start justify-between gap-2">
        <span className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
          Stage {String(index).padStart(2, '0')}
        </span>
        <PhaseChip phase={phase} active={active} />
      </header>

      <div className="flex items-center gap-2">
        <span
          className={clsx(
            'flex h-9 w-9 items-center justify-center rounded-md',
            active ? 'bg-accent-muted text-accent' : 'bg-sunken text-ink-secondary',
          )}
        >
          <Icon className="h-5 w-5" />
        </span>
        <h3 className="text-h-sm font-semibold text-ink-primary">{name}</h3>
      </div>

      <p className="text-caption leading-relaxed text-ink-secondary">{tagline}</p>

      <footer className="mt-auto flex items-center justify-between text-caption text-ink-tertiary">
        <span className="inline-flex items-center gap-1 font-mono">
          <Lock className="h-3 w-3" aria-hidden="true" />
          locked
        </span>
        <ChevronRight className="h-3.5 w-3.5 opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
      </footer>
    </article>
  );
}
