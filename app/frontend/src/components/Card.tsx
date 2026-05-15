import { clsx } from 'clsx';
import type { HTMLAttributes, ReactNode } from 'react';

export function Card({
  children,
  className,
  interactive,
  ...rest
}: HTMLAttributes<HTMLDivElement> & { children: ReactNode; interactive?: boolean }) {
  return (
    <div
      className={clsx(
        'rounded-md border border-border-subtle bg-raised shadow-e1 transition-all duration-medium',
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
}: {
  title: ReactNode;
  description?: ReactNode;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={clsx(
        'flex items-start justify-between gap-4 border-b border-border-subtle p-6',
        className,
      )}
    >
      <div>
        <h3 className="text-h-md font-semibold text-ink-primary">{title}</h3>
        {description && (
          <p className="mt-1 text-body text-ink-secondary">{description}</p>
        )}
      </div>
      {action && <div>{action}</div>}
    </div>
  );
}
