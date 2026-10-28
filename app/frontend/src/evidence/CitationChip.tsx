import { AlertTriangle, Layers } from 'lucide-react';
import { clsx } from 'clsx';
import { useEvidence } from '@/evidence/EvidenceProvider';
import { KindIcon, kindOfShortId } from '@/evidence/kinds';
import { useEvidenceSummary } from '@/evidence/useEvidenceSummary';

/**
 * A citation: the short id of one piece of evidence, as a button. It says what the evidence is
 * (an icon for the kind, the label on hover), and opens it. A citation that does not resolve in
 * the project's current version is shown as such rather than hidden: a statement must not look
 * supported when its source is gone.
 *
 * `siblings` is everything the same statement cites; opening any chip then shows them together.
 */
export function CitationChip({
  corpusId,
  id,
  siblings,
  showLabel = false,
  className,
}: {
  corpusId: string;
  id: string;
  siblings?: string[];
  /** Show the evidence's label instead of its id. */
  showLabel?: boolean;
  className?: string;
}) {
  const { open } = useEvidence();
  const summary = useEvidenceSummary(corpusId, id);
  const unresolved = summary.isSuccess && summary.data === null;
  const s = summary.data ?? undefined;

  const description = unresolved
    ? `${id} cannot be resolved in this version of the project`
    : s
      ? `${s.label} — ${s.path} ${s.locator}`
      : id;

  return (
    <button
      type="button"
      onClick={() => open({ corpusId, ids: siblings && siblings.length > 0 ? siblings : [id], focus: id })}
      title={description}
      aria-label={`Open evidence ${id}${s ? `: ${s.label}` : ''}${unresolved ? ' (cannot be resolved)' : ''}`}
      data-testid="citation-chip"
      data-short-id={id}
      data-resolved={unresolved ? 'false' : s ? 'true' : undefined}
      className={clsx(
        'inline-flex max-w-full items-center gap-1 rounded-md border px-1.5 py-0.5 align-baseline font-mono text-micro transition-colors duration-fast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
        unresolved
          ? 'border-status-warn/40 bg-status-warn/10 text-status-warn'
          : 'border-line bg-sunken text-ink-secondary hover:border-line-strong hover:text-ink-primary',
        className,
      )}
    >
      {unresolved ? (
        <AlertTriangle className="h-3 w-3 shrink-0" aria-hidden="true" />
      ) : (
        <KindIcon kind={s?.kind ?? kindOfShortId(id)} className="h-3 w-3" />
      )}
      <span className="truncate">{showLabel && s ? s.label : id}</span>
    </button>
  );
}

/**
 * Every citation behind one statement, plus one button that opens them all together. With a single
 * source it is just the chip.
 */
export function CitationList({ corpusId, ids, className }: { corpusId: string; ids: string[]; className?: string }) {
  const { open } = useEvidence();
  const unique = [...new Set(ids)];
  if (unique.length === 0) return null;
  return (
    <span className={clsx('inline-flex flex-wrap items-center gap-1 align-baseline', className)} data-testid="citation-list">
      {unique.map((id) => (
        <CitationChip key={id} corpusId={corpusId} id={id} siblings={unique} />
      ))}
      {unique.length > 1 && (
        <button
          type="button"
          onClick={() => open({ corpusId, ids: unique })}
          className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-micro text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          data-testid="citation-show-all"
        >
          <Layers className="h-3 w-3" aria-hidden="true" />
          All {unique.length} sources
        </button>
      )}
    </span>
  );
}
