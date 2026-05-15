import { AlertTriangle } from 'lucide-react';
import { Button } from './Button';

export function ErrorBlock({
  title,
  message,
  onRetry,
}: {
  title: string;
  message?: string;
  onRetry?: () => void;
}) {
  return (
    <div
      role="alert"
      className="rounded-md border border-status-failed/40 bg-[#F4D8D7]/40 p-6"
    >
      <div className="flex items-start gap-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 text-status-failed" aria-hidden="true" />
        <div className="flex-1">
          <h3 className="text-h-md font-semibold text-status-failed">{title}</h3>
          {message && <p className="mt-1 text-body text-ink-primary">{message}</p>}
          {onRetry && (
            <div className="mt-4">
              <Button variant="secondary" size="sm" onClick={onRetry}>
                Retry
              </Button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
