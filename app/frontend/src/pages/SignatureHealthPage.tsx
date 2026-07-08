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
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (q.isError || !q.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load signature health" message={String(q.error)} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup" data-testid="signature-health-page">
      <PageHero
        tone="emerald"
        eyebrow="Phase #4 · Signature health"
        title="Signature Health"
        lead={`Every signed spec across every project. A spec is "healthy" when the corpus hasn't been re-ingested since signing — i.e. the source revision the signature is bound to is still the latest. Drift means a newer revision exists and the spec needs to be re-verified.`}
      />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_1fr_1fr_2fr]">
        <KpiCard label="Signed specs" value={q.data.totalSigned} icon={BadgeCheck} accent="indigo" />
        <KpiCard label="Healthy"      value={q.data.totalSigned - q.data.drifted} icon={ShieldCheck} accent="emerald" />
        <KpiCard
          label="Drifted"
          value={q.data.drifted}
          icon={ShieldAlert}
          accent={q.data.drifted > 0 ? 'rose' : 'slate'}
          alert={q.data.drifted > 0}
        />
        <SigHealthByCorpusChart rows={q.data.rows} />
      </div>

      {actionError && <ErrorBlock title="Re-verify failed" message={actionError} />}

      <Card>
        <CardHeader
          title="Portfolio"
          description="Drift-first ordering so actionable rows surface up."
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
                  <th className="px-4 py-2 text-left font-medium">Routine</th>
                  <th className="px-4 py-2 text-left font-medium">Project</th>
                  <th className="px-4 py-2 text-left font-medium">Signer</th>
                  <th className="px-4 py-2 text-left font-medium">Signed at</th>
                  <th className="px-4 py-2 text-left font-medium">State</th>
                  <th className="px-4 py-2"></th>
                </tr>
              </thead>
              <tbody>
                {q.data.rows.map((row) => (
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
        'border-t border-border-subtle',
        isDrift && 'bg-[#F4D8D7]/20',
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
            className="inline-flex items-center gap-1 text-caption font-medium text-accent hover:underline"
          >
            Open <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
          </Link>
        </div>
      </td>
    </tr>
  );
}

function StatCard({
  label,
  value,
  tone,
}: {
  label: string;
  value: number;
  tone: 'success' | 'failed' | 'neutral';
}) {
  const colour =
    tone === 'success' ? 'text-status-review' :
    tone === 'failed'  ? 'text-status-failed' :
    'text-ink-primary';
  return (
    <Card>
      <CardBody>
        <div className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</div>
        <div className={clsx('mt-1 text-display font-semibold', colour)}>{value}</div>
      </CardBody>
    </Card>
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

  // Non-visual equivalent — summarise healthy vs drifted for assistive tech.
  const totalHealthy = data.reduce((n, d) => n + d.Healthy, 0);
  const totalDrifted = data.reduce((n, d) => n + d.Drifted, 0);
  const chartLabel = `Signature health by corpus across ${data.length} ${
    data.length === 1 ? 'corpus' : 'corpora'
  }: ${totalHealthy} healthy, ${totalDrifted} drifted.`;

  return (
    <div className="card p-4">
      <p className="label mb-2">By corpus</p>
      {data.length === 0 ? (
        <p className="text-[12px] text-ink-secondary">No signed specs yet.</p>
      ) : (
        <div style={{ width: '100%', height: 130 }} role="img" aria-label={chartLabel}>
          <ResponsiveContainer>
            <BarChart data={data} margin={{ top: 0, right: 4, left: -16, bottom: -4 }}>
              <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
              <XAxis dataKey="name" tick={{ fontSize: 9, fill: '#64748b' }} />
              <YAxis tick={{ fontSize: 9, fill: '#64748b' }} allowDecimals={false} />
              <Tooltip
                contentStyle={{
                  border: '1px solid #e2e8f0',
                  borderRadius: 8,
                  fontSize: 11,
                  boxShadow: '0 4px 12px rgba(16,24,40,.10)',
                }}
              />
              <Legend iconType="circle" wrapperStyle={{ fontSize: 10 }} />
              <Bar dataKey="Healthy" stackId="a" fill="#059669" />
              <Bar dataKey="Drifted" stackId="a" fill="#e11d48" />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}
    </div>
  );
}

function shortName(name: string): string {
  return name.length > 22 ? `${name.slice(0, 20)}…` : name;
}
