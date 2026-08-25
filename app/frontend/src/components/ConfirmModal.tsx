import { AlertTriangle, X } from 'lucide-react';
import { clsx } from 'clsx';
import { Button } from '@/components/Button';
import { useModalA11y } from '@/hooks/useModalA11y';

/**
 * Confirmation dialog for actions that throw work away.
 *
 * Re-extraction discards every claim review on the current draft, and
 * regenerating a scaffold overwrites the package and staleness-marks its
 * validation runs — both used to fire straight off a primary button with no
 * warning at all. `consequences` lists what is lost, in the user's terms.
 */
export function ConfirmModal({
  open,
  title,
  body,
  consequences,
  confirmLabel,
  tone = 'danger',
  onConfirm,
  onClose,
  children,
  confirmDisabled,
  testId,
}: {
  open: boolean;
  title: string;
  body: string;
  consequences?: string[];
  confirmLabel: string;
  tone?: 'danger' | 'neutral';
  onConfirm: () => void;
  onClose: () => void;
  /** Optional extra controls rendered above the footer (e.g. a target picker). */
  children?: React.ReactNode;
  confirmDisabled?: boolean;
  testId?: string;
}) {
  const dialogRef = useModalA11y<HTMLDivElement>(open, onClose);
  if (!open) return null;

  return (
    <div
      ref={dialogRef}
      tabIndex={-1}
      className="fixed inset-0 z-50 flex items-center justify-center outline-none motion-safe:animate-fade-in"
      role="dialog"
      aria-modal="true"
      aria-labelledby="confirm-modal-title"
      onClick={onClose}
      data-testid={testId ?? 'confirm-modal'}
    >
      <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
      <div
        className="relative w-[560px] max-w-[92vw] overflow-hidden rounded-lg border border-border-subtle bg-raised shadow-e3"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-start justify-between border-b border-border-subtle px-6 py-4">
          <div className="flex items-start gap-3">
            <span
              className={clsx(
                'flex h-9 w-9 items-center justify-center rounded-md',
                tone === 'danger' ? 'bg-[#F4D8D7]/60 text-status-failed' : 'bg-accent-muted text-accent',
              )}
            >
              <AlertTriangle className="h-5 w-5" aria-hidden="true" />
            </span>
            <div>
              <h2 id="confirm-modal-title" className="text-h-md font-semibold text-ink-primary">
                {title}
              </h2>
              <p className="mt-1 text-caption text-ink-secondary">{body}</p>
            </div>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary"
            aria-label="Close"
          >
            <X className="h-4 w-4" aria-hidden="true" />
          </button>
        </header>

        {(consequences?.length || children) && (
          <div className="space-y-4 px-6 py-5">
            {consequences && consequences.length > 0 && (
              <ul className="list-disc space-y-1 rounded-md border border-border-subtle bg-canvas p-4 pl-8 text-body text-ink-primary">
                {consequences.map((c) => (
                  <li key={c}>{c}</li>
                ))}
              </ul>
            )}
            {children}
          </div>
        )}

        <footer className="flex items-center justify-end gap-2 border-t border-border-subtle bg-canvas px-6 py-3">
          <Button variant="ghost" size="md" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant={tone === 'danger' ? 'destructive' : 'primary'}
            size="md"
            onClick={onConfirm}
            disabled={confirmDisabled}
            data-testid="confirm-modal-confirm"
          >
            {confirmLabel}
          </Button>
        </footer>
      </div>
    </div>
  );
}
