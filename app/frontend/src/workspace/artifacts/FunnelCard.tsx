import { clsx } from 'clsx';
import { FunnelBar, normaliseCounts } from '../FunnelBar';
import { StatePill } from '../StatePill';
import { formatInt, formatLanguage, formatState, num, obj, relativeTime, str } from '../format';
import type { ArtifactRenderProps } from './registry';

/**
 * `funnel` — one programme's routines by stage. Props:
 * `{ corpusName, sourceLanguage, counts, digests?, clusters?, latestRun? }`.
 */
export function FunnelCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const counts = normaliseCounts(p.counts);
  const digests = p.digests ? obj(p.digests) : null;
  const clusters = typeof p.clusters === 'number' ? p.clusters : null;
  const run = p.latestRun ? obj(p.latestRun) : null;
  const pane = size === 'pane';

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className={clsx('font-medium text-ink-primary', pane ? 'text-h-sm' : 'text-body')}>
          {str(p.corpusName, 'Programme')}
        </span>
        <span className="text-caption text-ink-tertiary">{formatLanguage(str(p.sourceLanguage) || null)}</span>
        <span className="ml-auto font-mono text-caption tabular-nums text-ink-secondary">
          {formatInt(counts.total)} routines
        </span>
      </div>

      <FunnelBar counts={counts} dense={!pane} />

      {(digests || clusters != null) && (
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-caption text-ink-secondary">
          {digests && (
            <span>
              Digests{' '}
              <span className="font-mono tabular-nums text-ink-primary">
                {formatInt(num(digests.surveyed))}/{formatInt(num(digests.total))}
              </span>{' '}
              surveyed
              {num(digests.propagated) > 0 && (
                <>
                  {' · '}
                  <span className="font-mono tabular-nums text-ink-primary">{formatInt(num(digests.propagated))}</span>{' '}
                  propagated
                </>
              )}
              {num(digests.trivial) > 0 && (
                <>
                  {' · '}
                  <span className="font-mono tabular-nums text-ink-primary">{formatInt(num(digests.trivial))}</span>{' '}
                  trivial
                </>
              )}
            </span>
          )}
          {clusters != null && (
            <span>
              <span className="font-mono tabular-nums text-ink-primary">{formatInt(clusters)}</span> pattern
              {clusters === 1 ? '' : 's'}
            </span>
          )}
        </div>
      )}

      {run && (
        <div className="flex flex-wrap items-center gap-2 rounded-lg border border-line-subtle bg-sunken px-3 py-2 text-caption">
          <span className="text-ink-secondary">Latest run</span>
          <span className="font-mono text-ink-primary">{formatState(str(run.kind))}</span>
          <StatePill state={str(run.state)} size="xs" />
          <span className="text-ink-tertiary">{relativeTime(str(run.startedAt) || null)}</span>
          {str(run.summary) && (
            <span className={clsx('min-w-0 text-ink-secondary', pane ? 'basis-full' : 'truncate basis-full')}>
              {str(run.summary)}
            </span>
          )}
        </div>
      )}
    </div>
  );
}
