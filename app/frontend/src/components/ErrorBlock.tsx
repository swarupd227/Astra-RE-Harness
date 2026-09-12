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
      className="rounded-lg border border-status-fail/40 bg-status-fail/10 p-6"
    >
      <div className="flex items-start gap-3">
        <AlertTriangle className="mt-0.5 h-5 w-5 text-status-fail" aria-hidden="true" />
        <div className="flex-1">
          <h3 className="text-h-md font-semibold text-status-fail">{title}</h3>
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
