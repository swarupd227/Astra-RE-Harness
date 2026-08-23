import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Activity, BarChart3, Coins, Layers, Package } from 'lucide-react';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { api, type PortfolioActivity, type PortfolioCorpusRow } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Card, CardBody } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { PageHero } from '@/components/PageHero';
import { KpiCard } from '@/components/KpiCard';
import { ProgressRing } from '@/components/ProgressRing';

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
    <div className="mx-auto max-w-[1300px] space-y-6 p-6 lg:p-10 fadeup" data-testid="portfolio-dashboard-page">
      <PageHero
        tone="teal"
        title="Portfolio dashboard"
        lead="Routines, signature progress, spend, and recent activity across all projects."
      />

      {summary.isPending && <Skeleton className="h-40" />}
      {summary.isError && (
        <ErrorBlock title="Could not load portfolio summary" message={String(summary.error)} />
      )}

      {summary.data && (
        <>
          <TotalsRow s={summary.data} />
          <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_1fr]">
            <StateDistributionChart totals={summary.data.totals} />
            <PerCorpusProgressChart rows={summary.data.corpora} />
          </div>
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
  // Overall migration-progress score: 60% weight signed, 30% scaffolded, 10% committed.
  const score = t.totalRoutines === 0 ? 0 : Math.round(
    (t.signedCount / t.totalRoutines) * 60 +
    (t.scaffoldedCount / t.totalRoutines) * 30 +
    (t.committedCount / t.totalRoutines) * 10,
  );
  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-[180px_1fr]" data-testid="portfolio-totals">
      <Card className="bg-raised">
        <CardBody className="flex items-center justify-center p-4">
          <ProgressRing score={score} label="MIGRATED" />
        </CardBody>
      </Card>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        <KpiCard label="Corpora"    value={t.corpusCount}     icon={Package}   accent="indigo" />
        <KpiCard label="Routines"   value={t.totalRoutines}   icon={Layers}    accent="teal" />
        <KpiCard label="Signed"     value={`${t.signedCount} (${pct(t.signedCount)}%)`}     icon={BarChart3} accent="emerald" />
        <KpiCard label="Scaffolded" value={`${t.scaffoldedCount} (${pct(t.scaffoldedCount)}%)`} icon={BarChart3} accent="amber" />
        <KpiCard label="Committed"  value={`${t.committedCount} (${pct(t.committedCount)}%)`}  icon={BarChart3} accent="orange" />
      </div>
    </div>
  );
}

function CorporaTable({ rows }: { rows: PortfolioCorpusRow[] }) {
  return (
    <Card data-testid="portfolio-corpora-table">
      <CardBody className="space-y-3">
        <h2 className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
          Per-corpus rollup
        </h2>
        {rows.length === 0 && (
          <p className="text-body-sm text-ink-secondary">No corpora yet.</p>
        )}
        {rows.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-body-sm">
              <thead>
                <tr className="text-left text-caption font-medium uppercase tracking-wider text-ink-tertiary">
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
        <h2 className="flex items-center gap-2 text-caption font-medium uppercase tracking-wider text-ink-tertiary">
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
          Costs accumulate per model call. Runs against the mock provider report $0.
        </p>
      </CardBody>
    </Card>
  );
}

function RecentActivityCard({ activity }: { activity: PortfolioActivity[] }) {
  return (
    <Card data-testid="portfolio-recent-activity">
      <CardBody className="space-y-2">
        <h2 className="flex items-center gap-2 text-caption font-medium uppercase tracking-wider text-ink-tertiary">
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

// ── Recharts panels ────────────────────────────────────────────────

const STATE_COLORS = {
  parsed:     '#94a3b8',
  draft:      '#fbbf24',
  signed:     '#4f46e5',
  scaffolded: '#7c3aed',
  committed:  '#059669',
} as const;

function StateDistributionChart({
  totals,
}: {
  totals: {
    totalRoutines: number;
    signedCount: number;
    scaffoldedCount: number;
    committedCount: number;
    draftCount: number;
    parsedCount: number;
  };
}) {
  const data = [
    { name: 'Parsed',     value: totals.parsedCount,     fill: STATE_COLORS.parsed },
    { name: 'Draft',      value: totals.draftCount,      fill: STATE_COLORS.draft },
    { name: 'Signed',     value: totals.signedCount,     fill: STATE_COLORS.signed },
    { name: 'Scaffolded', value: totals.scaffoldedCount, fill: STATE_COLORS.scaffolded },
    { name: 'Committed',  value: totals.committedCount,  fill: STATE_COLORS.committed },
  ].filter((d) => d.value > 0);

  // Non-visual equivalent — the SVG pie is opaque to assistive tech.
  const chartLabel = `Routine state distribution across every corpus: ${data
    .map((d) => `${d.value} ${d.name.toLowerCase()}`)
    .join(', ')}.`;

  return (
    <div className="card p-5">
      <h2 className="label mb-3">Routine state distribution</h2>
      {data.length === 0 ? (
        <p className="text-[12px] text-ink-secondary">No routines yet.</p>
      ) : (
        <div style={{ width: '100%', height: 220 }} role="img" aria-label={chartLabel}>
          <ResponsiveContainer>
            <PieChart>
              <Pie
                data={data}
                dataKey="value"
                nameKey="name"
                innerRadius={48}
                outerRadius={84}
                stroke="#ffffff"
                strokeWidth={2}
                paddingAngle={2}
              >
                {data.map((d, i) => <Cell key={i} fill={d.fill} />)}
              </Pie>
              <Tooltip
                contentStyle={{
                  border: '1px solid #e2e8f0',
                  borderRadius: 8,
                  fontSize: 12,
                  boxShadow: '0 4px 12px rgba(16,24,40,.10)',
                }}
              />
              <Legend
                iconType="circle"
                wrapperStyle={{ fontSize: 11, paddingTop: 8 }}
              />
            </PieChart>
          </ResponsiveContainer>
        </div>
      )}
      <p className="mt-1 text-[11px] text-ink-tertiary">
        Aggregate across every corpus. Each routine is bucketed by its furthest-along state.
      </p>
    </div>
  );
}

function PerCorpusProgressChart({ rows }: { rows: PortfolioCorpusRow[] }) {
  const data = rows.map((r) => ({
    name: r.name.length > 26 ? `${r.name.slice(0, 24)}…` : r.name,
    Signed: r.counts.signed,
    Scaffolded: r.counts.scaffolded,
    Committed: r.counts.committed,
    Pending: Math.max(
      0,
      r.counts.total - r.counts.signed - r.counts.scaffolded - r.counts.committed,
    ),
  }));

  // Non-visual equivalent — summarise the stacked totals for assistive tech.
  const agg = data.reduce(
    (a, d) => ({
      signed: a.signed + d.Signed,
      scaffolded: a.scaffolded + d.Scaffolded,
      committed: a.committed + d.Committed,
      pending: a.pending + d.Pending,
    }),
    { signed: 0, scaffolded: 0, committed: 0, pending: 0 },
  );
  const chartLabel = `Per-corpus migration progress for ${data.length} ${
    data.length === 1 ? 'corpus' : 'corpora'
  }. Totals: ${agg.signed} signed, ${agg.scaffolded} scaffolded, ${agg.committed} committed, ${agg.pending} pending.`;

  return (
    <div className="card p-5">
      <h2 className="label mb-3">Per-corpus migration progress</h2>
      {data.length === 0 ? (
        <p className="text-[12px] text-ink-secondary">No corpora yet.</p>
      ) : (
        <div style={{ width: '100%', height: 220 }} role="img" aria-label={chartLabel}>
          <ResponsiveContainer>
            <BarChart data={data} margin={{ top: 4, right: 8, left: 0, bottom: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
              <XAxis dataKey="name" tick={{ fontSize: 10, fill: '#64748b' }} />
              <YAxis tick={{ fontSize: 10, fill: '#64748b' }} allowDecimals={false} />
              <Tooltip
                contentStyle={{
                  border: '1px solid #e2e8f0',
                  borderRadius: 8,
                  fontSize: 12,
                  boxShadow: '0 4px 12px rgba(16,24,40,.10)',
                }}
              />
              <Legend iconType="circle" wrapperStyle={{ fontSize: 11, paddingTop: 8 }} />
              <Bar dataKey="Signed"     stackId="a" fill="#4f46e5" />
              <Bar dataKey="Scaffolded" stackId="a" fill="#7c3aed" />
              <Bar dataKey="Committed"  stackId="a" fill="#059669" />
              <Bar dataKey="Pending"    stackId="a" fill="#cbd5e1" />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}
    </div>
  );
}
