import { useState } from 'react';
import { CheckCircle2, X, ShieldCheck, AlertTriangle } from 'lucide-react';
import { Button } from '@/components/Button';
import { useModalA11y } from '@/hooks/useModalA11y';
import type { SpecResponse } from '@/lib/api';

export function SignOffModal({
  spec,
  open,
  onClose,
  onConfirm,
  preconditionFailures,
}: {
  spec: SpecResponse | null;
  open: boolean;
  onClose: () => void;
  onConfirm: (sentence: string) => Promise<void>;
  preconditionFailures: string[];
}) {
  const [agreed, setAgreed] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const guardedClose = () => { if (!submitting) onClose(); };
  const dialogRef = useModalA11y<HTMLDivElement>(open, guardedClose);

  if (!open || !spec) return null;

  const sentence = `I have reviewed every claim and confirm this spec is accurate to the source as of version ${spec.sourceVersionId}`;

  const submit = async () => {
    setSubmitting(true);
    setError(null);
    try {
      await onConfirm(sentence);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSubmitting(false);
    }
  };

  const blocked = preconditionFailures.length > 0;

  return (
    <div
      ref={dialogRef}
      tabIndex={-1}
      className="fixed inset-0 z-50 flex items-center justify-center outline-none motion-safe:animate-fade-in"
      role="dialog"
      aria-modal="true"
      aria-labelledby="sign-modal-title"
      onClick={guardedClose}
    >
      <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
      <div
        className="relative w-[640px] max-w-[92vw] overflow-hidden rounded-lg border border-border-subtle bg-raised shadow-e3"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-start justify-between border-b border-border-subtle px-6 py-4">
          <div className="flex items-start gap-3">
            <span className="flex h-9 w-9 items-center justify-center rounded-md bg-accent-muted text-accent">
              <ShieldCheck className="h-5 w-5" aria-hidden="true" />
            </span>
            <div>
              <h2 id="sign-modal-title" className="text-h-md font-semibold text-ink-primary">
                Sign spec — irrevocable
              </h2>
              <p className="mt-1 text-caption text-ink-secondary">
                The signature is HSM-backed and bound to source version <span className="font-mono">{spec.sourceVersionId.slice(0, 8)}…</span>. There is no un-sign.
              </p>
            </div>
          </div>
          {!submitting && (
            <button
              type="button"
              onClick={onClose}
              className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary"
              aria-label="Close"
            >
              <X className="h-4 w-4" aria-hidden="true" />
            </button>
          )}
        </header>

        <div className="space-y-4 px-6 py-5">
          {/* Citation integrity check */}
          <section className="rounded-md border border-status-review/30 bg-[#DAEFE9]/30 p-3">
            <div className="flex items-start gap-2">
              <CheckCircle2 className="mt-0.5 h-4 w-4 text-status-review" aria-hidden="true" />
              <div>
                <p className="font-mono text-caption uppercase tracking-wider text-status-review">
                  Citation integrity
                </p>
                <p className="mt-1 text-caption text-ink-primary">
                  Every citation resolves to a valid source line range.
                </p>
              </div>
            </div>
          </section>

          {/* Preconditions */}
          {blocked && (
            <section className="rounded-md border border-status-failed/30 bg-[#F4D8D7]/30 p-3">
              <div className="flex items-start gap-2">
                <AlertTriangle className="mt-0.5 h-4 w-4 text-status-failed" aria-hidden="true" />
                <div>
                  <p className="font-mono text-caption uppercase tracking-wider text-status-failed">
                    Preconditions unmet
                  </p>
                  <ul className="mt-1 list-disc space-y-0.5 pl-5 text-caption text-ink-primary">
                    {preconditionFailures.slice(0, 6).map((f, i) => (
                      <li key={i} className="font-mono">{f}</li>
                    ))}
                    {preconditionFailures.length > 6 && (
                      <li className="text-ink-tertiary">…and {preconditionFailures.length - 6} more</li>
                    )}
                  </ul>
                </div>
              </div>
            </section>
          )}

          {/* Canonical sentence + checkbox */}
          <section className="rounded-md border border-border-subtle bg-canvas p-4">
            <p className="text-caption text-ink-tertiary">Canonical confirmation sentence</p>
            <p className="mt-1 text-body text-ink-primary">{sentence}</p>
            <label className="mt-3 flex cursor-pointer items-start gap-2">
              <input
                type="checkbox"
                className="mt-0.5"
                checked={agreed}
                onChange={(e) => setAgreed(e.target.checked)}
                disabled={blocked || submitting}
              />
              <span className="text-body text-ink-primary">
                I confirm the sentence above and accept that this signature is permanent.
              </span>
            </label>
          </section>

          {error && (
            <p className="rounded-md border border-status-failed/30 bg-[#F4D8D7]/30 p-2 text-caption text-status-failed">
              {error}
            </p>
          )}
        </div>

        <footer className="flex items-center justify-end gap-2 border-t border-border-subtle bg-canvas px-6 py-3">
          <Button variant="ghost" size="md" onClick={onClose} disabled={submitting}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="md"
            onClick={submit}
            loading={submitting}
            disabled={!agreed || blocked || submitting}
          >
            <ShieldCheck className="h-4 w-4" /> Sign spec
          </Button>
        </footer>
      </div>
    </div>
  );
}
