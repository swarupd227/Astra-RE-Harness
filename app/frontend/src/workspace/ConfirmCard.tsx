import { clsx } from 'clsx';
import { Check, ShieldAlert, X } from 'lucide-react';
import type { PendingAction } from '@/lib/conversations';
import { personaClasses, personaLabel, pretty } from './format';

/**
 * A state-changing action the agent wants to take. Nothing happens until
 * the user confirms; "Not now" declines and the agent carries on.
 */
export function ConfirmCard({
  action,
  busy = false,
  disabled = false,
  onConfirm,
  onDecline,
}: {
  action: PendingAction;
  busy?: boolean;
  disabled?: boolean;
  onConfirm: () => void;
  onDecline: () => void;
}) {
  const locked = busy || disabled;
  return (
    <div
      className="rounded-xl border border-volt/40 bg-raised p-4 shadow-glow"
      role="group"
      aria-label="Confirm action"
      data-testid="confirm-card"
    >
      <div className="flex items-start gap-3">
        <span className="mt-0.5 inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-volt/15 text-volt">
          <ShieldAlert size={15} aria-hidden="true" />
        </span>
        <div className="min-w-0 flex-1 space-y-2">
          {/* The summary arrives as plain text (no markdown) — render it verbatim. */}
          <p className="whitespace-pre-wrap text-body text-ink-primary [overflow-wrap:anywhere]" data-testid="confirm-summary">
            {action.summary || 'Astra wants to take an action.'}
          </p>
          <div className="flex flex-wrap items-center gap-2 text-caption">
            <span className="rounded-md border border-line-subtle bg-sunken px-1.5 py-0.5 font-mono text-[12.5px] text-ink-secondary">
              {action.toolName}
            </span>
            {action.requiredPersona && (
              <span
                className={clsx(
                  'rounded-full border px-2 py-0.5 text-micro font-medium',
                  personaClasses(action.requiredPersona),
                )}
                title="Persona required to run this"
              >
                {personaLabel(action.requiredPersona)} required
              </span>
            )}
          </div>
          {action.input && Object.keys(action.input).length > 0 && (
            <details className="group/details">
              <summary className="cursor-pointer select-none text-micro text-ink-tertiary hover:text-ink-secondary">
                Show input
              </summary>
              <pre className="mt-1.5 max-h-56 overflow-auto rounded-lg border border-line-subtle bg-codebg p-3 font-mono text-[12px] leading-[1.5] text-sand-100">
                {pretty(action.input)}
              </pre>
            </details>
          )}
        </div>
      </div>
      <div className="mt-3 flex flex-wrap items-center justify-end gap-2">
        <button
          type="button"
          data-testid="decline-action"
          disabled={locked}
          onClick={onDecline}
          className="inline-flex items-center gap-1.5 rounded-lg border border-line px-3 py-1.5 text-caption font-medium text-ink-secondary transition-colors hover:border-line-strong hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt disabled:cursor-not-allowed disabled:opacity-50"
        >
          <X size={13} aria-hidden="true" />
          Not now
        </button>
        <button
          type="button"
          data-testid="confirm-action"
          disabled={locked}
          onClick={onConfirm}
          className="inline-flex items-center gap-1.5 rounded-lg bg-volt px-3.5 py-1.5 text-caption font-semibold text-on-volt transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt focus-visible:ring-offset-2 focus-visible:ring-offset-raised disabled:cursor-not-allowed disabled:opacity-50"
        >
          <Check size={13} aria-hidden="true" />
          {busy ? 'Confirming…' : 'Confirm'}
        </button>
      </div>
    </div>
  );
}

/** The small resolved line shown once an action was confirmed or declined. */
export function ResolvedActionLine({ action }: { action: PendingAction }) {
  const confirmed = action.state === 'confirmed';
  return (
    <div
      className="flex items-center gap-2 text-caption text-ink-tertiary"
      data-testid="resolved-action"
      data-state={action.state}
    >
      <span
        className={clsx(
          'inline-flex h-4 w-4 items-center justify-center rounded-full',
          confirmed ? 'bg-status-ok/15 text-status-ok' : 'bg-sunken text-ink-tertiary',
        )}
        aria-hidden="true"
      >
        {confirmed ? <Check size={10} /> : <X size={10} />}
      </span>
      <span>
        {confirmed ? 'Confirmed' : 'Declined'}
        {' · '}
        <span className="font-mono text-[12px]">{action.toolName}</span>
      </span>
    </div>
  );
}
