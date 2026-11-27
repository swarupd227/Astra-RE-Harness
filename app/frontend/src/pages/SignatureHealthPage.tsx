import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ShieldCheck, ShieldAlert, ArrowRight, RefreshCcw, BadgeCheck } from 'lucide-react';
import { clsx } from 'clsx';
import {
  Bar,
  BarChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { api, ApiError, type SignatureHealth } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { KpiCard } from '@/components/KpiCard';
import { useChartTheme } from '@/theme/charts';

/**
 * Signature Health portfolio — value-add #8.
 *
 * Cross-project board of every signed spec. Each row shows the routine,
 * the corpus it belongs to, the signer, and the drift state. Sorted
 * drift-first so the admin lands on the actionable rows.
 *
 * Wraps GET /api/v1/signature-health.
 */
export function SignatureHealthPage() {
  const queryClient = useQueryClient();
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  const q = useQuery({
    queryKey: ['signature-health-portfolio'],
    queryFn: api.getSignatureHealthPortfolio,
    staleTime: 30_000,
  });

  const [actionError, setActionError] = useState<string | null>(null);

  const reverify = useMutation({
    mutationFn: (specId: string) => api.reverifySpec(specId),
    onSuccess: () => {
      setActionError(null);
      queryClient.invalidateQueries({ queryKey: ['signature-health-portfolio'] });
    },
    onError: (e) => setActionError(e instanceof ApiError ? e.message : String(e)),
  });
  const reverifyAll = useMutation({
    mutationFn: () => api.reverifyAllDrifted(),
    onSuccess: () => {
      setActionError(null);
      queryClient.invalidateQueries({ queryKey: ['signature-health-portfolio'] });
    },
    onError: (e) => setActionError(e instanceof ApiError ? e.message : String(e)),
  });

  if (q.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-4 lg:p-6">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (q.isError || !q.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-4 lg:p-6">
        <ErrorBlock title="Could not load signature health" message={String(q.error)} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-4 lg:p-6 fadeup" data-testid="signature-health-page">
      <PageHero
        tone="emerald"
        eyebrow="Governance"
        title="Signature Health"
        lead="Signed specifications across all projects. A signature drifts when its project is re-synced after signing, and needs re-verification."
      />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <KpiCard testId="signature-stat-signed" label="Signed specs" value={q.data.totalSigned} icon={BadgeCheck} accent="indigo" />
        <KpiCard testId="signature-stat-healthy" label="Healthy"      value={q.data.totalSigned - q.data.drifted} icon={ShieldCheck} accent="emerald" />
        <KpiCard
          testId="signature-stat-drifted"
          label="Drifted"
          value={q.data.drifted}
          icon={ShieldAlert}
          accent={q.data.drifted > 0 ? 'rose' : 'slate'}
          alert={q.data.drifted > 0}
        />
      </div>
      <SigHealthByCorpusChart rows={q.data.rows} />

      {actionError && <ErrorBlock title="Re-verify failed" message={actionError} />}

      <Card>
        <CardHeader
          title="Portfolio"
          description="Items needing attention appear first."
          action={
            isAdmin && q.data.drifted > 0 && (
              <Button
                variant="primary"
                size="sm"
                onClick={() => {
                  if (window.confirm(`Re-verify all ${q.data.drifted} drifted specs? Each will be reset to IN_REVIEW for the SME to re-walk.`)) {
                    reverifyAll.mutate();
                  }
                }}
                loading={reverifyAll.isPending}
                data-testid="reverify-all"
              >
                <RefreshCcw className="h-4 w-4" />
                Re-verify all drifted
              </Button>
            )
          }
        />
        <CardBody className="p-0">
          {q.data.rows.length === 0 ? (
            <p className="px-6 py-4 text-body text-ink-secondary">
              No signed specs yet. Specs appear here once the SME signs them.
            </p>
          ) : (
            <div className="overflow-x-auto">
            <table className="w-full text-body">
              <thead className="bg-sunken/60 text-caption text-ink-tertiary">
                <tr>
                  <th className="px-4 py-2 text-left font-medium">Code unit</th>
                  <th className="px-4 py-2 text-left font-medium">Project</th>
                  <th className="px-4 py-2 text-left font-medium">Signer</th>
                  <th className="px-4 py-2 text-left font-medium">Signed at</th>
                  <th className="px-4 py-2 text-left font-medium">State</th>
                  <th className="px-4 py-2"></th>
                </tr>
              </thead>
              <tbody>
                {/* Drifted first: those are the ones that need someone; healthy ones follow, newest first. */}
                {[...q.data.rows]
                  .sort((x, y) => (x.state === 'drift' ? 0 : 1) - (y.state === 'drift' ? 0 : 1) || String(y.signedAt ?? '').localeCompare(String(x.signedAt ?? '')))
                  .map((row) => (
                  <PortfolioRow
                    key={row.specId}
                    row={row}
                    isAdmin={isAdmin}
                    onReverify={() => {
                      if (window.confirm(`Re-verify ${row.routineName}? The existing signature will be cleared and the spec returned to IN_REVIEW.`)) {
                        reverify.mutate(row.specId);
                      }
                    }}
                    reverifyPending={reverify.isPending && reverify.variables === row.specId}
                  />
                ))}
              </tbody>
            </table>
            </div>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

function PortfolioRow({
  row,
  isAdmin,
  onReverify,
  reverifyPending,
}: {
  row: SignatureHealth;
  isAdmin: boolean;
  onReverify: () => void;
  reverifyPending: boolean;
}) {
  const isDrift = row.state === 'drift';
  return (
    <tr
      className={clsx(
        'border-t border-line-subtle',
        isDrift && 'bg-status-fail/10',
      )}
      data-testid={`portfolio-row-${row.specId}`}
    >
      <td className="px-4 py-2 font-mono text-caption text-ink-primary">{row.routineName}</td>
      <td className="px-4 py-2 text-ink-secondary">{row.corpusName}</td>
      <td className="px-4 py-2 text-ink-secondary">{row.signerDisplay ?? '—'}</td>
      <td className="px-4 py-2 font-mono text-caption text-ink-tertiary">
        {row.signedAt ? new Date(row.signedAt).toISOString().slice(0, 16).replace('T', ' ') : '—'}
      </td>
      <td className="px-4 py-2">
        {isDrift ? (
          <Badge tone="failed">
            <ShieldAlert className="h-3 w-3" aria-hidden="true" />
            Drift · {row.driftAgeDays ?? 0}d
          </Badge>
        ) : (
          <Badge tone="signed">
            <ShieldCheck className="h-3 w-3" aria-hidden="true" />
            Healthy
          </Badge>
        )}
      </td>
      <td className="px-4 py-2">
        <div className="flex items-center justify-end gap-3">
          {isAdmin && isDrift && (
            <button
              type="button"
              onClick={onReverify}
              disabled={reverifyPending}
              className="inline-flex items-center gap-1 text-caption font-medium text-status-failed hover:underline disabled:opacity-50"
              data-testid={`reverify-${row.specId}`}
            >
              <RefreshCcw className="h-3.5 w-3.5" aria-hidden="true" />
              Re-verify
            </button>
          )}
          <Link
            to={`/subroutines/${row.subroutineId}/review`}
            className="inline-flex items-center gap-1 text-caption font-medium text-volt-ink hover:underline"
          >
            Open <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
          </Link>
        </div>
      </td>
    </tr>
  );
}

// ── Healthy-vs-drifted stacked bar by corpus ─────────────────────────
//
// Tucked into the KPI row's last column so the admin sees at a glance
// where the drift concentration is. Recharts ResponsiveContainer keeps
// it sized to whatever cell it lands in.
function SigHealthByCorpusChart({ rows }: { rows: SignatureHealth[] }) {
  const data = useMemo(() => {
    const byCorpus = new Map<string, { name: string; Healthy: number; Drifted: number }>();
    for (const r of rows) {
      const key = r.corpusName;
      if (!byCorpus.has(key)) {
        byCorpus.set(key, { name: shortName(r.corpusName), Healthy: 0, Drifted: 0 });
      }
      const bucket = byCorpus.get(key)!;
      if (r.state === 'drift') bucket.Drifted++;
      else bucket.Healthy++;
    }
    return Array.from(byCorpus.values());
  }, [rows]);

  const ct = useChartTheme();
  // Non-visual equivalent — summarise healthy vs drifted for assistive tech.
  const totalHealthy = data.reduce((n, d) => n + d.Healthy, 0);
  const totalDrifted = data.reduce((n, d) => n + d.Drifted, 0);
  const chartLabel = `Signature health by project across ${data.length} ${
    data.length === 1 ? 'project' : 'projects'
  }: ${totalHealthy} healthy, ${totalDrifted} drifted.`;
  // One bar per project, drifted first then largest: names stay readable instead of two clipped axis labels.
  const sorted = [...data].sort((a, b) => b.Drifted - a.Drifted || b.Healthy + b.Drifted - (a.Healthy + a.Drifted));

  return (
    <div className="card p-4">
      <p className="label mb-2">By project</p>
      {data.length === 0 ? (
        <p className="text-[12px] text-ink-secondary">No signed specs yet.</p>
      ) : (
        <div style={{ width: '100%', height: Math.max(90, sorted.length * 22 + 40) }} role="img" aria-label={chartLabel}>
          <ResponsiveContainer>
            <BarChart data={sorted} layout="vertical" margin={{ top: 0, right: 8, left: 0, bottom: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke={ct.grid} horizontal={false} />
              <XAxis type="number" tick={{ fontSize: 9, fill: ct.tick }} axisLine={{ stroke: ct.grid }} tickLine={{ stroke: ct.grid }} allowDecimals={false} />
              <YAxis type="category" dataKey="name" width={210} tick={{ fontSize: 10, fill: ct.tick }} axisLine={{ stroke: ct.grid }} tickLine={false} interval={0} />
              <Tooltip contentStyle={{ ...ct.tooltip, fontSize: 11 }} itemStyle={{ color: ct.tooltip.color }} cursor={{ fill: ct.grid, opacity: 0.4 }} />
              <Legend iconType="circle" formatter={ct.legendText} wrapperStyle={{ ...ct.legend, fontSize: 10, paddingTop: 0 }} />
              <Bar dataKey="Healthy" stackId="a" fill={ct.ok} />
              <Bar dataKey="Drifted" stackId="a" fill={ct.fail} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}
    </div>
  );
}

function shortName(name: string): string {
  return name.length > 34 ? `${name.slice(0, 32)}…` : name;
}
