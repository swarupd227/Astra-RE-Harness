import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Activity, BarChart3, Coins, Layers, Package } from 'lucide-react';
import { api, type PortfolioActivity, type PortfolioCorpusRow } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Card, CardBody } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';

/**
 * Phase 8.0.d — Portfolio dashboard.
 *
 * Cross-corpus program-manager view. Admin-only because LLM cost +
 * spend by corpus typically should not surface in engineer/SME
 * dashboards. v1 has no charts (no charting library installed and
 * not enough historical data to plot a meaningful burn-down yet).
 * When 90+ days of state-transition events have landed we'll add
 * recharts and a stacked-area wave-completion view.
 */
export function PortfolioDashboardPage() {
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const summary = useQuery({
    queryKey: ['portfolio-summary'],
    queryFn: api.getPortfolioSummary,
    enabled: whoami.data?.persona === 'admin',
    retry: false,
  });

  if (whoami.isPending) {
    return (
      <div className="mx-auto max-w-[1300px] p-6">
        <Skeleton className="h-8 w-72" />
      </div>
    );
  }
  if (whoami.data?.persona !== 'admin') {
    return (
      <div className="mx-auto max-w-[1300px] p-6 lg:p-10">
        <ErrorBlock
          title="Admin only"
          message="The portfolio dashboard surfaces cross-corpus aggregates including LLM cost. Switch persona from the top-right menu to continue."
        />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1300px] space-y-6 p-6 lg:p-10" data-testid="portfolio-dashboard-page">
      <header className="space-y-1">
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase 8.0 · portfolio migration planning
        </p>
        <h1 className="text-display font-semibold text-ink-primary">Portfolio dashboard</h1>
        <p className="max-w-3xl text-body-lg text-ink-secondary">
          Cross-corpus aggregates: routines, signature progress, LLM spend, recent activity.
          The numbers below are live — they recompute on every load from the source rows.
        </p>
      </header>

      {summary.isPending && <Skeleton className="h-40" />}
      {summary.isError && (
        <ErrorBlock title="Could not load portfolio summary" message={String(summary.error)} />
      )}

      {summary.data && (
        <>
          <TotalsRow s={summary.data} />
          <CorporaTable rows={summary.data.corpora} />
          <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
            <LlmCostCard t={summary.data.llmTotals} />
            <RecentActivityCard activity={summary.data.recent} />
          </div>
        </>
      )}
    </div>
  );
}

function TotalsRow({
  s,
}: {
  s: { totals: { corpusCount: number; totalRoutines: number; signedCount: number; scaffoldedCount: number; committedCount: number; draftCount: number; parsedCount: number } };
}) {
  const t = s.totals;
  const pct = (n: number) => (t.totalRoutines === 0 ? 0 : Math.round((n / t.totalRoutines) * 100));
  const tiles = [
    { label: 'Corpora', value: t.corpusCount, icon: Package },
    { label: 'Routines', value: t.totalRoutines, icon: Layers },
    { label: 'Signed', value: `${t.signedCount} (${pct(t.signedCount)}%)`, icon: BarChart3 },
    { label: 'Scaffolded', value: `${t.scaffoldedCount} (${pct(t.scaffoldedCount)}%)`, icon: BarChart3 },
    { label: 'Committed', value: `${t.committedCount} (${pct(t.committedCount)}%)`, icon: BarChart3 },
  ];
  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-5" data-testid="portfolio-totals">
      {tiles.map((t) => {
        const Icon = t.icon;
        return (
          <Card key={t.label}>
            <CardBody className="space-y-1">
              <div className="flex items-center gap-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                <Icon className="h-3.5 w-3.5" aria-hidden="true" />
                {t.label}
              </div>
              <p className="text-display font-semibold text-ink-primary">{t.value}</p>
            </CardBody>
          </Card>
        );
      })}
    </div>
  );
}

function CorporaTable({ rows }: { rows: PortfolioCorpusRow[] }) {
  return (
    <Card data-testid="portfolio-corpora-table">
      <CardBody className="space-y-3">
        <h2 className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Per-corpus rollup
        </h2>
        {rows.length === 0 && (
          <p className="text-body-sm text-ink-secondary">No corpora yet.</p>
        )}
        {rows.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-body-sm">
              <thead>
                <tr className="text-left font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                  <th className="py-2">Name</th>
                  <th className="py-2">Plan</th>
                  <th className="py-2 text-right">Routines</th>
                  <th className="py-2 text-right">Signed</th>
                  <th className="py-2 text-right">Scaffolded</th>
                  <th className="py-2 text-right">Committed</th>
                  <th className="py-2">Last activity</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.corpusId} className="border-t border-border-subtle">
                    <td className="py-2">
                      <Link to={`/corpora/${r.corpusId}`} className="text-accent hover:underline">
                        {r.name}
                      </Link>
                      <p className="font-mono text-caption text-ink-tertiary">
                        {r.fileCount} files · {r.totalLoc.toLocaleString()} LOC
                      </p>
                    </td>
                    <td className="py-2">
                      {r.planStatus ? (
                        <Link to={`/corpora/${r.corpusId}/migration-plan`} className="hover:underline">
                          <Badge tone={r.planStatus === 'approved' ? 'success' : 'review'}>
                            {r.planStatus} · {r.totalWaves} waves
                          </Badge>
                        </Link>
                      ) : (
                        <span className="font-mono text-caption text-ink-tertiary">no plan</span>
                      )}
                    </td>
                    <td className="py-2 text-right font-mono">{r.counts.total}</td>
                    <td className="py-2 text-right font-mono">
                      {r.counts.signed}
                      <span className="ml-1 text-ink-tertiary">
                        ({r.counts.total === 0 ? 0 : Math.round((r.counts.signed / r.counts.total) * 100)}%)
                      </span>
                    </td>
                    <td className="py-2 text-right font-mono">{r.counts.scaffolded}</td>
                    <td className="py-2 text-right font-mono">{r.counts.committed}</td>
                    <td className="py-2 font-mono text-caption text-ink-tertiary">
                      {new Date(r.lastActivity).toLocaleString()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardBody>
    </Card>
  );
}

function LlmCostCard({
  t,
}: {
  t: { callCount: number; inputTokens: number; outputTokens: number; costUsd: number };
}) {
  return (
    <Card data-testid="portfolio-llm-totals">
      <CardBody className="space-y-2">
        <h2 className="flex items-center gap-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          <Coins className="h-3.5 w-3.5" />
          LLM spend (platform-wide)
        </h2>
        <div className="grid grid-cols-2 gap-3 font-mono text-body-sm">
          <div>
            <p className="text-ink-tertiary">calls</p>
            <p className="text-body-lg text-ink-primary">{t.callCount.toLocaleString()}</p>
          </div>
          <div>
            <p className="text-ink-tertiary">total cost</p>
            <p className="text-body-lg text-ink-primary">${t.costUsd.toFixed(4)}</p>
          </div>
          <div>
            <p className="text-ink-tertiary">input tokens</p>
            <p className="text-ink-primary">{t.inputTokens.toLocaleString()}</p>
          </div>
          <div>
            <p className="text-ink-tertiary">output tokens</p>
            <p className="text-ink-primary">{t.outputTokens.toLocaleString()}</p>
          </div>
        </div>
        <p className="text-caption text-ink-tertiary">
          Mock-provider runs report $0. Real-provider totals accumulate per LlmCall row.
        </p>
      </CardBody>
    </Card>
  );
}

function RecentActivityCard({ activity }: { activity: PortfolioActivity[] }) {
  return (
    <Card data-testid="portfolio-recent-activity">
      <CardBody className="space-y-2">
        <h2 className="flex items-center gap-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          <Activity className="h-3.5 w-3.5" />
          Recent activity ({activity.length})
        </h2>
        {activity.length === 0 && (
          <p className="text-body-sm text-ink-secondary">No audit events yet.</p>
        )}
        <ul className="space-y-1 font-mono text-caption">
          {activity.slice(0, 12).map((e, i) => (
            <li key={`${e.occurredAt}-${i}`} className="flex items-center justify-between gap-2">
              <span className="text-ink-secondary">{e.eventType}</span>
              <span className="text-ink-tertiary">
                {e.actorPersona} · {new Date(e.occurredAt).toLocaleTimeString()}
              </span>
            </li>
          ))}
        </ul>
      </CardBody>
    </Card>
  );
}
