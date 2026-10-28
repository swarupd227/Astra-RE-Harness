import type { ComponentType } from 'react';
import { Link } from 'react-router-dom';
import { EvidenceCode } from '@/evidence/EvidenceCode';
import type { EvidenceDetail } from '@/lib/api';

export type EvidenceBodyProps = { detail: EvidenceDetail };

/**
 * How the body of an evidence card is drawn, by artefact kind. A kind without an entry here gets the
 * default: the cited lines in a numbered code view, or the chunk's text as it is. A kind with a
 * richer view (a transcript with speakers and a play button, a ticket as a record) registers it here
 * and nothing else changes.
 */
export const EVIDENCE_VIEWS: Partial<Record<string, ComponentType<EvidenceBodyProps>>> = {};

export function DefaultEvidenceBody({ detail }: EvidenceBodyProps) {
  const routineId = typeof detail.structured?.subroutineId === 'string' ? detail.structured.subroutineId : null;
  return (
    <div className="space-y-3">
      {detail.view ? (
        <>
          <EvidenceCode
            text={detail.view.text}
            path={detail.path}
            startLine={detail.view.startLine}
            highlightStart={detail.view.highlightStart}
            highlightEnd={detail.view.highlightEnd}
          />
          {detail.view.truncated && (
            <p className="text-caption text-ink-tertiary">
              The cited range is long; the first lines are shown.
            </p>
          )}
        </>
      ) : detail.text ? (
        <pre
          className="max-h-[26rem] overflow-auto whitespace-pre-wrap break-words rounded-md border border-line-subtle bg-sunken p-3 font-mono text-caption leading-5 text-ink-primary"
          data-testid="evidence-text"
        >
          {detail.text}
        </pre>
      ) : (
        <p className="text-body text-ink-secondary">There is no text to show for this piece of evidence.</p>
      )}
      {routineId && (
        <Link
          to={`/subroutines/${routineId}`}
          className="inline-flex items-center gap-1 text-caption text-ink-secondary underline-offset-2 hover:text-ink-primary hover:underline focus-visible:outline-2 focus-visible:outline-volt"
          data-testid="evidence-open-routine"
        >
          Open this routine
        </Link>
      )}
    </div>
  );
}

export function EvidenceBody({ detail }: EvidenceBodyProps) {
  const View = EVIDENCE_VIEWS[detail.kind] ?? DefaultEvidenceBody;
  return <View detail={detail} />;
}
