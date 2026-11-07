import { useState } from 'react';
import { Link } from 'react-router-dom';
import { EvidenceCode } from '@/evidence/EvidenceCode';
import type { EvidenceDetail } from '@/lib/api';

export type EvidenceBodyProps = { detail: EvidenceDetail };

/**
 * The body of an evidence card for a kind with no view of its own: the cited lines in a numbered code
 * view with the cited range marked, or the chunk's text as it is. Richer views build on it.
 */
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

/**
 * The file's own lines around the chunk, folded away: the kind's view comes first, the source on
 * request. The code is not mounted (or coloured) until it is opened, so a stack of several cards stays light.
 */
export function SourceLines({ detail }: EvidenceBodyProps) {
  const [open, setOpen] = useState(false);
  if (!detail.view) return null;
  const { highlightStart, highlightEnd } = detail.view;
  return (
    <details
      className="group rounded-md border border-line-subtle"
      data-testid="evidence-source-lines"
      onToggle={(e) => setOpen(e.currentTarget.open)}
    >
      <summary className="cursor-pointer select-none px-3 py-2 text-caption text-ink-secondary hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-volt">
        Show in the file{' '}
        <span className="font-mono text-ink-tertiary">
          (line{highlightEnd > highlightStart ? `s ${highlightStart}–${highlightEnd}` : ` ${highlightStart}`})
        </span>
      </summary>
      {open && (
        <div className="border-t border-line-subtle p-2">
          <EvidenceCode
            text={detail.view.text}
            path={detail.path}
            startLine={detail.view.startLine}
            highlightStart={highlightStart}
            highlightEnd={highlightEnd}
          />
        </div>
      )}
    </details>
  );
}

/** Reads a string out of a chunk's structured part without trusting its shape. */
export function textOf(v: unknown): string {
  return typeof v === 'string' ? v : typeof v === 'number' || typeof v === 'boolean' ? String(v) : '';
}
