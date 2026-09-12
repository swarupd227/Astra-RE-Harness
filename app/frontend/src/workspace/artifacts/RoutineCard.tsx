import { useMemo } from 'react';
import { ArrowUpRight } from 'lucide-react';
import { Link } from 'react-router-dom';
import { StatePill } from '../StatePill';
import { arr, formatInt, formatLanguage, num, str } from '../format';
import type { ArtifactRenderProps } from './registry';

/**
 * `routine` — one routine's metadata; in the pane, its own source slice with
 * line numbers. A plain `<pre>` rather than Monaco: the slice is ≤ 400 lines,
 * the pane is dark-first, and MonacoSource is hard-wired to its light theme.
 */
export function RoutineCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const pane = size === 'pane';
  const name = str(p.name, 'Routine');
  const signature = str(p.signature);
  const path = str(p.path);
  const lineStart = num(p.lineStart);
  const lineEnd = num(p.lineEnd);
  const callees = arr<unknown>(p.callees).map((c) => str(c)).filter(Boolean);
  const callerCount = num(p.callerCount);
  const specId = str(p.specId) || null;
  const subroutineId = artifact.refId;
  const source = typeof p.source === 'string' ? p.source : null;

  const lines = useMemo(() => (source ? source.replace(/\r\n?/g, '\n').split('\n') : []), [source]);
  const gutterWidth = String((lineStart || 1) + Math.max(0, lines.length - 1)).length;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-body font-medium text-ink-primary">{name}</span>
        <StatePill state={str(p.state)} size="xs" />
        <span className="text-caption text-ink-tertiary">{formatLanguage(str(p.sourceLanguage) || null)}</span>
      </div>

      {signature && signature !== name && (
        <pre className="overflow-x-auto rounded-lg border border-line-subtle bg-sunken px-3 py-2 font-mono text-[13px] text-ink-secondary">
          {signature}
        </pre>
      )}

      <dl className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-caption sm:grid-cols-3">
        <div className="col-span-2 min-w-0 sm:col-span-3">
          <dt className="text-micro uppercase tracking-wide text-ink-tertiary">Location</dt>
          <dd className="truncate font-mono text-ink-secondary" title={path}>
            {path || '—'}
            {lineStart > 0 && (
              <span className="text-ink-tertiary">
                :{lineStart}
                {lineEnd > lineStart ? `–${lineEnd}` : ''}
              </span>
            )}
          </dd>
        </div>
        <div>
          <dt className="text-micro uppercase tracking-wide text-ink-tertiary">Lines</dt>
          <dd className="font-mono tabular-nums text-ink-secondary">
            {lineStart > 0 && lineEnd >= lineStart ? formatInt(lineEnd - lineStart + 1) : '—'}
          </dd>
        </div>
        <div>
          <dt className="text-micro uppercase tracking-wide text-ink-tertiary">Callers</dt>
          <dd className="font-mono tabular-nums text-ink-secondary">{formatInt(callerCount)}</dd>
        </div>
        <div>
          <dt className="text-micro uppercase tracking-wide text-ink-tertiary">Callees</dt>
          <dd className="font-mono tabular-nums text-ink-secondary">{formatInt(callees.length)}</dd>
        </div>
      </dl>

      {callees.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {(pane ? callees : callees.slice(0, 8)).map((c) => (
            <span key={c} className="rounded bg-sunken px-1.5 py-px font-mono text-micro text-ink-tertiary">
              {c}
            </span>
          ))}
          {!pane && callees.length > 8 && <span className="px-1 text-micro text-ink-tertiary">+{callees.length - 8}</span>}
        </div>
      )}

      <div className="flex flex-wrap gap-1.5">
        {specId && subroutineId && (
          <Link
            to={`/subroutines/${subroutineId}/review`}
            onClick={(e) => e.stopPropagation()}
            className="inline-flex items-center gap-1 rounded-md border border-line-subtle bg-sunken px-2 py-1 text-micro text-ink-secondary hover:border-line hover:text-ink-primary"
          >
            Spec review
            <ArrowUpRight size={11} aria-hidden="true" />
          </Link>
        )}
        {!specId && subroutineId && (
          <Link
            to={`/subroutines/${subroutineId}`}
            onClick={(e) => e.stopPropagation()}
            className="inline-flex items-center gap-1 rounded-md border border-line-subtle bg-sunken px-2 py-1 text-micro text-ink-secondary hover:border-line hover:text-ink-primary"
          >
            Routine detail
            <ArrowUpRight size={11} aria-hidden="true" />
          </Link>
        )}
      </div>

      {pane && source && lines.length > 0 && (
        <div className="overflow-hidden rounded-lg border border-line-subtle bg-codebg">
          <div className="flex items-center justify-between border-b border-line-subtle px-3 py-1.5 text-micro text-ink-tertiary">
            <span className="truncate font-mono">{path || name}</span>
            <span className="font-mono tabular-nums">{formatInt(lines.length)} lines</span>
          </div>
          <pre className="max-h-[60vh] overflow-auto p-3 font-mono text-[12.5px] leading-[1.6] text-sand-100">
            {lines.map((l, i) => (
              <div key={i} className="flex">
                <span
                  className="select-none pr-3 text-right tabular-nums text-sand-500"
                  style={{ minWidth: `${gutterWidth + 1}ch` }}
                  aria-hidden="true"
                >
                  {(lineStart || 1) + i}
                </span>
                <span className="whitespace-pre">{l || ' '}</span>
              </div>
            ))}
          </pre>
        </div>
      )}
      {!pane && source && (
        <div className="text-micro text-ink-tertiary">Open in the pane to read the source.</div>
      )}
    </div>
  );
}
