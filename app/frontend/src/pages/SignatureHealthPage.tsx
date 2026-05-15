import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ShieldCheck, ShieldAlert, ArrowRight, RefreshCcw } from 'lucide-react';
import { clsx } from 'clsx';
import { api, ApiError, type SignatureHealth } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

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
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10" data-testid="signature-health-page">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase #4 · Signature health
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">Signature Health</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Every signed spec across every project. A spec is "healthy" when
          the corpus hasn't been re-ingested since signing — i.e. the source
          revision the signature is bound to is still the latest. Drift
          means a newer revision exists and the spec needs to be re-verified.
        </p>
      </header>

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
        <StatCard label="Signed specs" value={q.data.totalSigned} tone="neutral" />
        <StatCard label="Healthy" value={q.data.totalSigned - q.data.drifted} tone="success" />
        <StatCard label="Drifted" value={q.data.drifted} tone={q.data.drifted > 0 ? 'failed' : 'neutral'} />
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
