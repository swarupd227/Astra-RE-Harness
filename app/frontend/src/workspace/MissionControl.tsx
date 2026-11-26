import { useState } from 'react';
import { clsx } from 'clsx';
import { Activity, ArrowRight, ChevronDown, ChevronRight, ClipboardCheck, Code2, Coins, Gauge, Layers, Loader2, Zap } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { FunnelCounts, Overview, OverviewProgramme } from '@/lib/conversations';
import { FunnelBar } from './FunnelBar';
import { StatePill } from './StatePill';
import { formatInt, formatLanguage, formatMoney, formatPercent, formatState, relativeTime } from './format';

/** "<1%" rather than a misleading "0%" when a few of many are done. */
function pct(n: number, total: number): string {
  const v = (n / total) * 100;
  return n > 0 && v < 1 ? '<1%' : `${Math.round(v)}%`;
}

const ACTIVE_RUN = new Set(['RUNNING', 'QUEUED', 'PENDING', 'IN_PROGRESS']);

/** The one sentence that tells a lead what to do next on a programme, read off its funnel. */
export function nextStep(c: FunnelCounts | undefined): string {
  if (!c || !c.total) return 'Nothing ingested yet — add source to begin';
  const done = c.scaffolded + c.committed;
  if (c.extracting > 0) return `Extracting specs for ${formatInt(c.extracting)} routine${c.extracting === 1 ? '' : 's'}`;
  if (c.inReview > 0) return `${formatInt(c.inReview)} spec${c.inReview === 1 ? '' : 's'} waiting for review`;
  if (c.draft > 0) return `${formatInt(c.draft)} draft spec${c.draft === 1 ? '' : 's'} to send for review`;
  if (c.signed > 0) return `${formatInt(c.signed)} signed spec${c.signed === 1 ? '' : 's'} ready to generate code`;
  if (c.parsed === c.total) return 'Survey the patterns, then extract specs';
  if (c.parsed > 0) return `${formatInt(c.parsed)} routine${c.parsed === 1 ? '' : 's'} still to extract`;
  if (done === c.total) return 'Every routine has generated code';
  return `${formatInt(done)} of ${formatInt(c.total)} routines have code`;
}

/**
 * Home: the estate at a glance — four numbers, the programmes you were last working on with their next step, and
 * every programme one click away. The conversation sits below it; you ask in the composer.
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
  const [allOpen, setAllOpen] = useState(false);
  const [usageOpen, setUsageOpen] = useState(false);

  if (isLoading && !overview) {
    return (
      <section data-testid="mission-control" aria-label="Home" className="mb-8 space-y-4" aria-busy="true">
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          {[0, 1, 2, 3].map((i) => (
            <div key={i} className="h-20 animate-pulse rounded-xl bg-sunken" />
          ))}
        </div>
        {[0, 1].map((i) => (
          <div key={i} className="h-24 animate-pulse rounded-xl bg-sunken" />
        ))}
      </section>
    );
  }
  if (error && !overview) {
    return (
      <section data-testid="mission-control" aria-label="Home" className="mb-8">
        <p className="rounded-xl border border-status-failed/40 bg-status-fail/10 px-4 py-3 text-caption text-ink-primary">
          Couldn’t load your programmes: {error.message}
        </p>
      </section>
    );
  }
  if (!overview) return null;

  const t = overview.totals;
  const verified = (t?.scaffolded ?? 0) + (t?.committed ?? 0);
  const running = overview.programmes.filter((p) => p.latestRun && ACTIVE_RUN.has(p.latestRun.state.toUpperCase())).length;
  const recent = [...overview.programmes]
    .sort((a, b) => (b.latestRun?.startedAt ?? '').localeCompare(a.latestRun?.startedAt ?? ''))
    .slice(0, 3);

  return (
    <section data-testid="mission-control" aria-label="Home" className="mb-8 space-y-6">
      <div>
        <h2 className="text-h-md font-semibold text-ink-primary">Your migration estate</h2>
        <p className="mt-0.5 text-caption text-ink-secondary">
          Where every programme stands, and what to do next. Ask the agents anything in the box below.
        </p>
      </div>

      {/* Four numbers that answer "how are we doing" */}
      <ul className="grid grid-cols-2 gap-3 lg:grid-cols-4" aria-label="Estate summary">
        <Kpi
          icon={Layers}
          label="Programmes"
          value={formatInt(overview.programmes.length)}
          sub={`${formatInt(t?.total)} routines`}
          to="/projects"
          testid="home-kpi-programmes"
        />
        <Kpi
          icon={Code2}
          label="Routines with code"
          value={formatInt(verified)}
          sub={t?.total ? `${pct(verified, t.total)} of all routines` : '—'}
          to="/scaffolds"
          testid="home-kpi-code"
        />
        <Kpi
          icon={ClipboardCheck}
          label="Waiting for review"
          value={formatInt(t?.inReview)}
          sub={t?.inReview ? 'Specs for an SME to sign' : 'Nothing waiting'}
          to="/my-reviews"
          tone={t?.inReview ? 'attention' : undefined}
          testid="home-kpi-review"
        />
        <Kpi
          icon={Loader2}
          label="Runs in progress"
          value={formatInt(running)}
          sub={running ? 'Agents are working' : 'All quiet'}
          tone={running ? 'working' : undefined}
          testid="home-kpi-runs"
        />
      </ul>

      {overview.programmes.length === 0 ? (
        <div className="rounded-xl border border-dashed border-line px-5 py-6 text-center">
          <p className="text-body font-medium text-ink-primary">No programmes yet</p>
          <p className="mt-1 text-caption text-ink-secondary">Upload source or connect a repository to start.</p>
          <Link
            to="/projects/new"
            className="mt-3 inline-flex items-center gap-1.5 rounded-md bg-volt px-3 py-1.5 text-caption font-semibold text-on-volt"
          >
            Add a programme <ArrowRight size={13} aria-hidden="true" />
          </Link>
        </div>
      ) : (
        <>
          {/* Continue where you left off */}
          <div>
            <h3 className="label mb-2">Continue where you left off</h3>
            <ul className="grid gap-3 lg:grid-cols-3" aria-label="Recent programmes">
              {recent.map((p) => (
                <RecentCard key={p.corpusId} p={p} />
              ))}
            </ul>
          </div>

          {/* Every programme, folded */}
          <div className="rounded-xl border border-line-subtle bg-raised">
            <button
              type="button"
              onClick={() => setAllOpen((v) => !v)}
              aria-expanded={allOpen}
              className="flex w-full items-center gap-3 px-4 py-3 text-left"
              data-testid="home-all-programmes-toggle"
            >
              <ChevronDown
                size={14}
                className={clsx('shrink-0 text-ink-tertiary transition-transform', !allOpen && '-rotate-90')}
                aria-hidden="true"
              />
              <span className="text-body font-medium text-ink-primary">All programmes</span>
              <span className="text-caption text-ink-secondary">{formatInt(overview.programmes.length)}</span>
              <span className="ml-auto hidden w-64 md:block">
                <FunnelBar counts={overview.totals} dense className="[&>div:last-child]:hidden" />
              </span>
            </button>
            {allOpen && (
              <div className="border-t border-line-subtle px-2 pb-2 pt-1">
                <div className="px-2 py-3">
                  <div className="mb-2 flex items-baseline justify-between text-caption">
                    <span className="text-ink-secondary">Across all programmes</span>
                    <span className="font-mono tabular-nums text-ink-primary">{formatInt(t?.total)} routines</span>
                  </div>
                  <FunnelBar counts={overview.totals} />
                </div>
                <ul className="divide-y divide-line-subtle" aria-label="Programmes">
                  {overview.programmes.map((p) => (
                    <ProgrammeRow key={p.corpusId} p={p} />
                  ))}
                </ul>
              </div>
            )}
          </div>
        </>
      )}

      {/* Model usage today — for whoever watches the bill; folded */}
      <div className="text-caption">
        <button
          type="button"
          onClick={() => setUsageOpen((v) => !v)}
          aria-expanded={usageOpen}
          className="inline-flex items-center gap-1 text-ink-secondary hover:text-ink-primary"
          data-testid="home-usage-toggle"
        >
          <ChevronRight size={12} className={clsx('transition-transform', usageOpen && 'rotate-90')} aria-hidden="true" />
          Model usage today
        </button>
        <ul
          className={clsx('mt-2 flex flex-wrap items-center gap-1.5', !usageOpen && 'sr-only')}
          aria-label="Telemetry today"
        >
          <Chip icon={Coins} label="spent" value={formatMoney(overview.telemetry?.costTodayUsd)} testid="telemetry-cost" />
          <Chip icon={Zap} label="calls" value={formatInt(overview.telemetry?.callsToday)} testid="telemetry-calls" />
          <Chip
            icon={Gauge}
            label="median latency"
            value={overview.telemetry?.p50LatencyMs != null ? `${formatInt(Math.round(overview.telemetry.p50LatencyMs))} ms` : '—'}
            testid="telemetry-p50"
          />
          <Chip icon={Activity} label="cache hits" value={formatPercent(overview.telemetry?.cacheHitRate)} testid="telemetry-cache" />
        </ul>
      </div>
    </section>
  );
}

function Kpi({
  icon: Icon,
  label,
  value,
  sub,
  to,
  tone,
  testid,
}: {
  icon: typeof Layers;
  label: string;
  value: string;
  sub: string;
  to?: string;
  tone?: 'attention' | 'working';
  testid: string;
}) {
  const body = (
    <>
      <div className="flex items-center gap-2 text-caption text-ink-secondary">
        <Icon
          size={14}
          className={clsx(
            'shrink-0',
            tone === 'attention' ? 'text-status-warn' : tone === 'working' ? 'text-volt-ink motion-safe:animate-spin' : 'text-ink-tertiary',
          )}
          aria-hidden="true"
        />
        {label}
      </div>
      <div className="mt-1 font-mono text-h-lg font-semibold tabular-nums text-ink-primary">{value}</div>
      <div className="truncate text-caption text-ink-secondary">{sub}</div>
    </>
  );
  const cls = clsx(
    'block h-full rounded-xl border bg-raised px-4 py-3 transition-colors',
    tone === 'attention' ? 'border-status-warn/40' : 'border-line-subtle',
    to && 'hover:border-line-strong hover:bg-sunken focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
  );
  return (
    <li data-testid={testid}>
      {to ? (
        <Link to={to} className={cls}>
          {body}
        </Link>
      ) : (
        <div className={cls}>{body}</div>
      )}
    </li>
  );
}

function RecentCard({ p }: { p: OverviewProgramme }) {
  return (
    <li>
      <Link
        to={`/w/${p.conversationId}`}
        className="group flex h-full flex-col gap-2.5 rounded-xl border border-line-subtle bg-raised px-4 py-3 transition-colors hover:border-line-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
        data-testid={`home-recent-${p.corpusId}`}
      >
        <div className="flex items-start gap-2">
          <div className="min-w-0 flex-1">
            <p className="truncate text-body font-medium text-ink-primary" title={p.name}>
              {p.name}
            </p>
            <p className="text-caption text-ink-secondary">
              {formatLanguage(p.sourceLanguage)} · {formatInt(p.counts?.total)} routines
              {p.latestRun ? ` · ${relativeTime(p.latestRun.startedAt)}` : ''}
            </p>
          </div>
          {p.latestRun && <StatePill state={p.latestRun.state} size="xs" />}
        </div>
        <FunnelBar counts={p.counts} dense className="[&>div:last-child]:hidden" />
        <p className="mt-auto flex items-center gap-1.5 text-caption font-medium text-ink-primary">
          <span className="truncate">{nextStep(p.counts)}</span>
          <ArrowRight
            size={13}
            className="shrink-0 text-ink-tertiary transition-transform group-hover:translate-x-0.5"
            aria-hidden="true"
          />
        </p>
      </Link>
    </li>
  );
}

function ProgrammeRow({ p }: { p: OverviewProgramme }) {
  return (
    <li>
      <Link
        to={`/w/${p.conversationId}`}
        data-testid={`mission-programme-${p.corpusId}`}
        className="group/row flex items-center gap-3 rounded-lg px-2 py-2.5 transition-colors hover:bg-sunken focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
      >
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="truncate text-body font-medium text-ink-primary" title={p.name}>
              {p.name}
            </span>
            <span className="shrink-0 text-micro text-ink-secondary">{formatLanguage(p.sourceLanguage)}</span>
          </div>
          <div className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-caption text-ink-secondary">
            <span className="font-mono tabular-nums">
              {formatInt(p.counts?.total)} routines · {formatInt(p.counts?.signed)} signed ·{' '}
              {formatInt((p.counts?.scaffolded ?? 0) + (p.counts?.committed ?? 0))} with code
            </span>
            <span className="truncate">· {nextStep(p.counts)}</span>
            {p.latestRun && (
              <span className="hidden truncate lg:inline">
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
      className="inline-flex items-center gap-1.5 rounded-full border border-line-subtle bg-sunken px-2.5 py-1 text-micro text-ink-secondary"
    >
      <Icon size={12} className="text-ink-tertiary" aria-hidden="true" />
      <span className="font-mono tabular-nums text-ink-primary">{value}</span>
      <span>{label}</span>
    </li>
  );
}
