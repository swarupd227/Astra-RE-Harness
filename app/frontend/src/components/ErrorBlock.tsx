import { AlertTriangle } from 'lucide-react';
import { Link } from 'react-router-dom';
import { Button } from './Button';
import { ApiError } from '@/lib/api';

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

/**
 * A project page whose project did not load. A 404 is not a failure to retry: the project is gone or the link points at
 * something else (a conversation id, say), so say that and offer the project list instead of "Request failed (404)".
 */
export function ProjectLoadError({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const notFound = error instanceof ApiError ? error.status === 404 : /\(404\)/.test(String((error as Error)?.message ?? error));
  if (!notFound) {
    return <ErrorBlock title="Could not load project" message={(error as Error)?.message ?? String(error)} onRetry={onRetry} />;
  }
  return (
    <div role="alert" className="rounded-lg border border-line-subtle bg-raised p-6" data-testid="project-not-found">
      <h3 className="text-h-md font-semibold text-ink-primary">Project not found</h3>
      <p className="mt-1 text-body text-ink-secondary">
        It may have been removed, or this link points at something else.
      </p>
      <Link to="/projects" className="mt-4 inline-block text-caption font-medium text-ink-link hover:underline">
        All projects →
      </Link>
    </div>
  );
}
