import { clsx } from 'clsx';
import { Activity, ChevronRight, Coins, Gauge, Zap } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { Overview } from '@/lib/conversations';
import { FunnelBar } from './FunnelBar';
import { StatePill } from './StatePill';
import { formatInt, formatLanguage, formatMoney, formatPercent, formatState, relativeTime } from './format';

/**
 * The global thread's pinned block: cross-programme funnel, one row per
 * programme (click → its thread), and today's telemetry.
 */
export function MissionControl({
  overview,
  isLoading = false,
  error,
}: {
  overview: Overview | undefined;
  isLoading?: boolean;
  error?: Error | null;
}) {
  return (
    <section
      data-testid="mission-control"
      aria-label="Mission Control"
      className="mb-8 rounded-2xl border border-line-subtle bg-raised"
    >
      <header className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-line-subtle px-5 py-3.5">
        <div className="min-w-0">
          <h2 className="text-h-sm font-semibold text-ink-primary">Mission Control</h2>
          <p className="text-caption text-ink-tertiary">Every programme, one thread.</p>
        </div>
        {overview && (
          <ul className="ml-auto flex flex-wrap items-center gap-1.5" aria-label="Telemetry today">
            <Chip icon={Coins} label="today" value={formatMoney(overview.telemetry?.costTodayUsd)} testid="telemetry-cost" />
            <Chip icon={Zap} label="calls" value={formatInt(overview.telemetry?.callsToday)} testid="telemetry-calls" />
            <Chip
              icon={Gauge}
              label="p50"
              value={overview.telemetry?.p50LatencyMs != null ? `${formatInt(Math.round(overview.telemetry.p50LatencyMs))} ms` : '—'}
              testid="telemetry-p50"
            />
            <Chip icon={Activity} label="cache" value={formatPercent(overview.telemetry?.cacheHitRate)} testid="telemetry-cache" />
          </ul>
        )}
      </header>

      <div className="px-5 py-4">
        {isLoading && !overview ? (
          <div className="space-y-3" aria-hidden="true">
            <div className="h-2 w-full animate-pulse rounded-full bg-sunken" />
            <div className="grid grid-cols-7 gap-2">
              {Array.from({ length: 7 }).map((_, i) => (
                <div key={i} className="h-3 animate-pulse rounded bg-sunken" />
              ))}
            </div>
            <div className="mt-2 space-y-2">
              {[0, 1, 2].map((i) => (
                <div key={i} className="h-10 animate-pulse rounded-lg bg-sunken" />
              ))}
            </div>
          </div>
        ) : error && !overview ? (
          <p className="text-caption text-status-fail">Couldn’t load the overview: {error.message}</p>
        ) : overview ? (
          <div className="space-y-4">
            <div>
              <div className="mb-2 flex items-baseline justify-between text-caption">
                <span className="text-ink-secondary">Across all programmes</span>
                <span className="font-mono tabular-nums text-ink-primary">
                  {formatInt(overview.totals?.total)} routines
                </span>
              </div>
              <FunnelBar counts={overview.totals} />
            </div>

            {overview.programmes.length === 0 ? (
              <p className="text-caption text-ink-tertiary">
                No programmes yet.{' '}
                <Link to="/projects/new" className="text-ink-link hover:underline">
                  Add the first one
                </Link>{' '}
                and it will show up here as a thread.
              </p>
            ) : (
              <ul className="-mx-2 divide-y divide-line-subtle" aria-label="Programmes">
                {overview.programmes.map((p) => (
                  <li key={p.corpusId}>
                    <Link
                      to={`/w/${p.conversationId}`}
                      data-testid={`mission-programme-${p.corpusId}`}
                      className="group/row flex items-center gap-3 rounded-lg px-2 py-2.5 transition-colors hover:bg-sunken focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                    >
                      <div className="min-w-0 flex-1">
                        <div className="flex items-center gap-2">
                          <span className="truncate text-body font-medium text-ink-primary">{p.name}</span>
                          <span className="shrink-0 text-micro text-ink-tertiary">{formatLanguage(p.sourceLanguage)}</span>
                        </div>
                        <div className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-micro text-ink-tertiary">
                          <span className="font-mono tabular-nums">
                            {formatInt(p.counts?.total)} routines · {formatInt(p.counts?.signed)} signed ·{' '}
                            {formatInt(p.counts?.committed)} committed
                          </span>
                          {p.latestRun && (
                            <span className="truncate">
                              · {formatState(p.latestRun.kind)} {relativeTime(p.latestRun.startedAt)}
                            </span>
                          )}
                        </div>
                      </div>
                      {p.latestRun ? (
                        <StatePill state={p.latestRun.state} size="xs" />
                      ) : (
                        <span className="text-micro text-ink-tertiary">no runs</span>
                      )}
                      <ChevronRight
                        size={14}
                        className="shrink-0 text-ink-tertiary transition-transform group-hover/row:translate-x-0.5"
                        aria-hidden="true"
                      />
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </div>
        ) : null}
      </div>
    </section>
  );
}

function Chip({
  icon: Icon,
  label,
  value,
  testid,
}: {
  icon: typeof Coins;
  label: string;
  value: string;
  testid: string;
}) {
  return (
    <li
      data-testid={testid}
      className={clsx(
        'inline-flex items-center gap-1.5 rounded-full border border-line-subtle bg-sunken px-2.5 py-1 text-micro text-ink-secondary',
      )}
    >
      <Icon size={12} className="text-ink-tertiary" aria-hidden="true" />
      <span className="font-mono tabular-nums text-ink-primary">{value}</span>
      <span>{label}</span>
    </li>
  );
}
