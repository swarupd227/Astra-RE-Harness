import { clsx } from 'clsx';

type Status = 'ok' | 'down' | 'pending';

const tones: Record<Status, string> = {
  ok: 'bg-status-review',
  down: 'bg-status-failed',
  pending: 'bg-status-superseded',
};

/**
 * A small status dot. When `pulse` is true (e.g. while a probe is in flight)
 * the dot emits an animated ring so the user can see the system is checking.
 */
export function DependencyDot({
  status,
  pulse,
  size = 8,
  className,
}: {
  status: Status;
  pulse?: boolean;
  size?: number;
  className?: string;
}) {
  return (
    <span
      className={clsx('relative inline-flex shrink-0', className)}
      style={{ width: size, height: size }}
    >
      {pulse && (
        <span
          className={clsx(
            'absolute inset-0 rounded-full opacity-60 motion-safe:animate-ping',
            tones[status],
          )}
          aria-hidden="true"
        />
      )}
      <span
        className={clsx('relative inline-block rounded-full', tones[status])}
        style={{ width: size, height: size }}
      />
    </span>
  );
}
