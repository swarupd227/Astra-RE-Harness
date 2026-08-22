import { clsx } from 'clsx';
import type { ReactNode } from 'react';

type HeroTone = 'indigo' | 'emerald' | 'amber' | 'violet' | 'teal' | 'orange';

// Light-theme hero washes — subtle gradient that fades to white. The
// outer border picks up a faint tint of the page's accent colour so
// the hero reads as a coloured panel without dominating.
const toneClass: Record<HeroTone, string> = {
  indigo:  'bg-hero-indigo  border-ace-100',
  emerald: 'bg-hero-emerald border-emerald-100',
  amber:   'bg-hero-amber   border-amber-100',
  violet:  'bg-hero-violet  border-violet-100',
  teal:    'bg-hero-teal    border-teal-100',
  orange:  'bg-hero-orange  border-brand-100',
};

const eyebrowToneClass: Record<HeroTone, string> = {
  indigo:  'text-ace-700',
  emerald: 'text-emerald-700',
  amber:   'text-amber-700',
  violet:  'text-violet-700',
  teal:    'text-teal-700',
  orange:  'text-brand-700',
};

/**
 * Coloured page hero block. Renders an eyebrow (phase / breadcrumb),
 * a display-size title, and an optional lead paragraph + slot for
 * actions, on a subtle gradient backdrop keyed to a brand tone.
 *
 * Pages opt in by tone — the same component standardises the
 * spacing, type ramp and gradient choice across the product so the
 * colour story stays consistent.
 */
export function PageHero({
  eyebrow,
  title,
  lead,
  tone = 'indigo',
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
  return (
    <div
      className={clsx(
        'rounded-lg border px-5 py-5 lg:px-7 lg:py-6',
        toneClass[tone],
        className,
      )}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 space-y-1">
          {eyebrow && (
            <p
              className={clsx(
                'text-caption font-medium uppercase tracking-wider',
                eyebrowToneClass[tone],
              )}
            >
              {eyebrow}
            </p>
          )}
          <h1 className="text-display font-semibold text-ink-primary">{title}</h1>
          {lead && (
            <p className="max-w-3xl text-body-lg text-ink-secondary">{lead}</p>
          )}
        </div>
        {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
      </div>
      {children && <div className="mt-3">{children}</div>}
    </div>
  );
}
