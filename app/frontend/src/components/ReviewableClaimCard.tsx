import { clsx } from 'clsx';
import { Check, Edit3, HelpCircle, X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { claimBodyText, type ClaimReview, type SpecClaim } from '@/lib/api';
import { Button } from '@/components/Button';

type Action = 'accept' | 'edit' | 'reject' | 'question';

export function ReviewableClaimCard({
  id,
  section,
  claim,
  review,
  readOnly,
  onAct,
  onCite,
  activeCitation,
}: {
  id: string;
  section: string;
  claim: SpecClaim;
  review?: ClaimReview;
  readOnly?: boolean;
  onAct?: (action: Action, payload?: { reason?: string; editedText?: string }) => Promise<void> | void;
  onCite?: (lines: string) => void;
  activeCitation?: string | null;
}) {
  const [busy, setBusy] = useState<Action | null>(null);
  const [editing, setEditing] = useState<null | { kind: 'edit' | 'reject' | 'question' }>(null);
  const [draft, setDraft] = useState('');
  const inputRef = useRef<HTMLTextAreaElement | null>(null);

  useEffect(() => {
    if (editing && inputRef.current) inputRef.current.focus();
  }, [editing]);

  const body = claimBodyText(claim);
  // scenario/rationale are only useful as extra context when they weren't
  // the source of the main body text — when description/statement/condition
  // already won out, they'd just duplicate it.
  const supplemental = claim.behavior
    ? `Behavior: ${claim.behavior}`
    : !claim.description && claim.scenario
      ? claim.scenario
      : !claim.description && !claim.statement && claim.rationale
        ? claim.rationale
        : null;
  const tone = stateToTone(review?.action);

  const accept = async () => {
    if (busy || readOnly) return;
    setBusy('accept');
    try { await onAct?.('accept'); } finally { setBusy(null); }
  };

  const startEdit = () => {
    setDraft(review?.editedText ?? body);
    setEditing({ kind: 'edit' });
  };
  const startReject = () => { setDraft(review?.reason ?? ''); setEditing({ kind: 'reject' }); };
  const cancelEdit = () => { setEditing(null); setDraft(''); };
  const submit = async () => {
    if (!editing) return;
    setBusy(editing.kind === 'edit' ? 'edit' : editing.kind === 'reject' ? 'reject' : 'question');
    try {
      if (editing.kind === 'edit') await onAct?.('edit', { editedText: draft });
      else if (editing.kind === 'reject') await onAct?.('reject', { reason: draft });
      else await onAct?.('question', { reason: draft });
      setEditing(null);
      setDraft('');
    } finally { setBusy(null); }
  };

  const showOriginal = review?.action === 'edit' && review.editedText;

  return (
    <article
      id={`claim-${id}`}
      className={clsx(
        'scroll-mt-24 rounded-md border bg-raised p-4 transition-all duration-medium motion-safe:animate-fade-in',
        tone === 'accent' && 'border-status-draft/40 bg-accent-muted/30',
        tone === 'success' && 'border-status-review/40 bg-[#DAEFE9]/30',
        tone === 'reject' && 'border-status-failed/40 bg-[#F4D8D7]/30',
        tone === 'q' && 'border-status-scaffolded/40 bg-[#FBF1D9]/40',
        !tone && 'border-border-subtle',
      )}
      aria-labelledby={`claim-${id}-heading`}
    >
      <header className="flex flex-wrap items-center gap-2">
        <span id={`claim-${id}-heading`} className="rounded-sm bg-sunken px-1.5 py-0.5 font-mono text-caption uppercase text-ink-secondary">
          {id}
        </span>
        {claim.confidence && (
          <span
            className={clsx(
              'rounded-sm px-1.5 py-0.5 font-mono text-caption uppercase',
              claim.confidence === 'high' && 'bg-[#DAEFE9] text-status-review',
              claim.confidence === 'medium' && 'bg-accent-muted text-status-draft',
              claim.confidence === 'low' && 'bg-[#F4D8D7] text-status-failed',
            )}
          >
            {claim.confidence}
          </span>
        )}
        {review && (
          <span
            className={clsx(
              'ml-auto rounded-sm px-1.5 py-0.5 font-mono text-caption uppercase',
              review.action === 'accept' && 'bg-[#DAEFE9] text-status-review',
              review.action === 'edit' && 'bg-accent-muted text-status-draft',
              review.action === 'reject' && 'bg-[#F4D8D7] text-status-failed',
              review.action === 'question' && 'bg-[#FBF1D9] text-status-scaffolded',
            )}
          >
            {review.action}
          </span>
        )}
      </header>

      <div className="mt-2">
        {review?.action === 'reject' ? (
          <p className="text-body text-ink-tertiary line-through">{body}</p>
        ) : (
          <p className="text-body text-ink-primary">{review?.editedText ?? body}</p>
        )}
        {showOriginal && (
          <details className="mt-1 text-caption text-ink-tertiary">
            <summary className="cursor-pointer hover:text-ink-secondary">View original LLM draft</summary>
            <p className="mt-1 italic">{body}</p>
          </details>
        )}
        {review?.reason && (
          <p className="mt-2 rounded-sm border-l-2 border-ink-tertiary bg-sunken/60 px-2 py-1 text-caption text-ink-secondary">
            {review.action === 'reject' ? 'Reason: ' : review.action === 'question' ? 'Question: ' : ''}
            {review.reason}
          </p>
        )}
        {supplemental && <p className="mt-1 text-caption italic text-ink-secondary">{supplemental}</p>}
      </div>

      {claim.citations && claim.citations.length > 0 && (
        <div className="mt-3 flex flex-wrap gap-1.5">
          {claim.citations.map((c, i) => (
            <button
              key={i}
              type="button"
              onClick={() => onCite?.(c.lines)}
              className={clsx(
                'inline-flex items-center gap-1 rounded-sm border px-1.5 py-0.5 font-mono text-[11px] transition-colors duration-fast hover:bg-accent-muted',
                activeCitation === c.lines
                  ? 'border-accent bg-accent-muted text-status-draft'
                  : 'border-border-subtle bg-canvas text-ink-secondary',
              )}
            >
              L{c.lines}
            </button>
          ))}
        </div>
      )}

      {!readOnly && (
        <>
          {!editing ? (
            <div className="mt-3 flex flex-wrap gap-2">
              <Button
                size="sm"
                variant="secondary"
                onClick={accept}
                loading={busy === 'accept'}
                className={review?.action === 'accept' ? 'border-status-review/60' : ''}
              >
                <Check className="h-4 w-4" /> Accept
              </Button>
              <Button size="sm" variant="ghost" onClick={startEdit}>
                <Edit3 className="h-4 w-4" /> Edit
              </Button>
              <Button size="sm" variant="ghost" onClick={startReject}>
                <X className="h-4 w-4" /> Reject
              </Button>
              {section === 'open_questions' && (
                // "Resolve in spec" is editing — the SME rewrites the open
                // question into a resolved claim. Uses the `edit` action so
                // the precondition logic accepts it as a final decision.
                // The `question` action is reserved for raising a NEW
                // question on a non-question claim.
                <Button size="sm" variant="ghost" onClick={startEdit}>
                  <HelpCircle className="h-4 w-4" /> Resolve in spec
                </Button>
              )}
            </div>
          ) : (
            <div className="mt-3 space-y-2">
              <textarea
                ref={inputRef}
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                rows={editing.kind === 'edit' ? 4 : 3}
                placeholder={
                  editing.kind === 'edit'
                    ? 'Refined claim text…'
                    : editing.kind === 'reject'
                      ? 'Reason for rejection (≥ 20 characters)…'
                      : 'Resolution / clarification…'
                }
                className="w-full rounded-md border border-border-subtle bg-raised p-2 font-mono text-body text-ink-primary focus:border-accent focus:outline-none"
              />
              {editing.kind === 'reject' && draft.length > 0 && draft.length < 20 && (
                <p className="text-caption text-status-failed">Reason must be at least 20 characters.</p>
              )}
              <div className="flex gap-2">
                <Button
                  size="sm"
                  variant="primary"
                  onClick={submit}
                  loading={busy != null}
                  disabled={
                    !draft.trim() ||
                    (editing.kind === 'reject' && draft.length < 20)
                  }
                >
                  Save
                </Button>
                <Button size="sm" variant="ghost" onClick={cancelEdit}>
                  Cancel
                </Button>
              </div>
            </div>
          )}
        </>
      )}
    </article>
  );
}

function stateToTone(action: string | undefined): 'accent' | 'success' | 'reject' | 'q' | null {
  switch (action) {
    case 'edit': return 'accent';
    case 'accept': return 'success';
    case 'reject': return 'reject';
    case 'question': return 'q';
    default: return null;
  }
}
