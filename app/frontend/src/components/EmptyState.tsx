import { clsx } from 'clsx';
import type { ReactNode } from 'react';

export function EmptyState({
  illustration,
  title,
  description,
  action,
  className,
}: {
  illustration: ReactNode;
  title: string;
  description?: string;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={clsx(
        'flex flex-col items-center justify-center gap-4 px-6 py-12 text-center',
        className,
      )}
    >
      <div className="opacity-90">{illustration}</div>
      <div className="space-y-1">
        <h3 className="text-h-md font-semibold text-ink-primary">{title}</h3>
        {description && (
          <p className="mx-auto max-w-md text-body text-ink-secondary">{description}</p>
        )}
      </div>
      {action && <div className="mt-2">{action}</div>}
    </div>
  );
}
