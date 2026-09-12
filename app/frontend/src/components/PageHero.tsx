import { clsx } from 'clsx';
import type { ReactNode } from 'react';

type HeroTone = 'indigo' | 'emerald' | 'amber' | 'violet' | 'teal' | 'orange';

/**
 * Compact page header — eyebrow, title, a one-line subtitle, and the page's
 * actions on the right. This replaced the v1 gradient hero: in the dark-first
 * shell the conversation is the hero, and an artifact view just needs to say
 * what it is and hand you the controls.
 *
 * `tone` is accepted for source compatibility with the v1 call sites but no
 * longer paints anything — volt is the single accent and is reserved for
 * agent-working / primary CTA / focus.
 */
export function PageHero({
  eyebrow,
  title,
  lead,
  tone: _tone,
  actions,
  children,
  className,
}: {
  eyebrow?: ReactNode;
  title: ReactNode;
  lead?: ReactNode;
  tone?: HeroTone;
  actions?: ReactNode;
  children?: ReactNode;
  className?: string;
}) {
  void _tone;
  return (
    <header className={clsx('border-b border-line-subtle pb-4', className)}>
      <div className="flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
        <div className="min-w-0 flex-1 space-y-1">
          {eyebrow && <p className="label">{eyebrow}</p>}
          <h1 className="text-h-lg font-semibold tracking-tight text-ink-primary">{title}</h1>
          {lead && <p className="max-w-3xl text-body text-ink-secondary">{lead}</p>}
        </div>
        {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
      </div>
      {children && <div className="mt-3">{children}</div>}
    </header>
  );
}
