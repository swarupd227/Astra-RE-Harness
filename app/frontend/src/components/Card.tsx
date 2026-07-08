import { clsx } from 'clsx';
import type { HTMLAttributes, ReactNode } from 'react';

export function Card({
  children,
  className,
  interactive,
  ...rest
}: HTMLAttributes<HTMLDivElement> & { children: ReactNode; interactive?: boolean }) {
  // Uses the shared `.card` utility (defined in index.css) so every
  // card across the app shares one source of truth for surface, border,
  // radius and shadow. `interactive` adds the lift-on-hover affordance.
  return (
    <div
      className={clsx(
        'card transition-all duration-medium',
        interactive && 'cursor-pointer hover:-translate-y-0.5 hover:shadow-e2',
        className,
      )}
      {...rest}
    >
      {children}
    </div>
  );
}

export function CardBody({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return <div className={clsx('p-6', className)}>{children}</div>;
}

export function CardHeader({
  title,
  description,
  action,
  className,
  titleAs: TitleTag = 'h3',
}: {
  title: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
  className?: string;
  /** Heading level for the title — defaults to h3. Set h2 for top-level page
      sections so the document outline doesn't skip a level under the page h1. */
  titleAs?: 'h2' | 'h3';
}) {
  return (
    <div
      className={clsx(
        'flex items-start justify-between gap-4 border-b border-border-subtle p-6',
        className,
      )}
    >
      <div>
        <TitleTag className="text-h-md font-semibold text-ink-primary">{title}</TitleTag>
        {description && (
          <p className="mt-1 text-body text-ink-secondary">{description}</p>
        )}
      </div>
      {action && <div>{action}</div>}
    </div>
  );
}
