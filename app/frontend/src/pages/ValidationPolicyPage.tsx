import { useQuery } from '@tanstack/react-query';
import { CheckCircle2, ShieldAlert, SlidersHorizontal } from 'lucide-react';
import { api, type ValidationGate } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Validation Policy — value-add #7 in the Nous platform pitch.
 *
 * Read-only today. Shows the three independent gates (compile / test
 * pack / equivalence), their coverage thresholds, retry policy, and the
 * commit-blocking guarantee. Phase D adds the per-project override
 * surface; for the demo the canonical policy is the asset and that's
 * what this page surfaces.
 */
export function ValidationPolicyPage() {
  const q = useQuery({
    queryKey: ['validation-policy'],
    queryFn: api.getValidationPolicy,
    staleTime: 5 * 60_000,
  });

  if (q.isPending) {
    return (
      <div className="mx-auto max-w-[1100px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (q.isError || !q.data) {
    return (
      <div className="mx-auto max-w-[1100px] p-6 lg:p-10">
        <ErrorBlock title="Could not load validation policy" message={String(q.error)} />
      </div>
    );
  }
  const p = q.data;

  return (
    <div className="mx-auto max-w-[1100px] space-y-6 p-6 lg:p-10" data-testid="validation-policy-page">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase #4 · Validation policy
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">Validation Policy</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Three independent gates run against every generated scaffold.
          The commit-to-Git action is blocked until each required gate
          reports PASSED on the same scaffold revision.
        </p>
      </header>

      <Card>
        <CardHeader
          title={
            <span className="inline-flex items-center gap-2">
              <SlidersHorizontal className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
              Commit gate
            </span>
          }
          description={p.commitGate.description}
          action={
            <Badge tone={p.commitGate.requireAllGreen ? 'success' : 'neutral'}>
              {p.commitGate.requireAllGreen ? 'All-green required' : 'Permissive'}
            </Badge>
          }
        />
        <CardBody>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3 text-caption">
            <Meta label="Scope" value={p.scope} />
            <Meta label="Policy version" value={p.version} />
            <Meta label="Owned by" value={p.ownedBy} />
          </div>
        </CardBody>
      </Card>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        {p.gates.map((g) => (
          <GateCard key={g.id} gate={g} />
        ))}
      </div>

      <Card>
        <CardHeader
          title={
            <span className="inline-flex items-center gap-2">
              <ShieldAlert className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
              Retry & flake policy
            </span>
          }
          description={p.retryDefaults.note}
        />
        <CardBody>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 text-caption">
            <Meta label="Auto-retry count" value={String(p.retryDefaults.autoRetryCount)} />
            <Meta label="Transient flake window" value={p.retryDefaults.transientFlakeWindow} />
          </div>
        </CardBody>
      </Card>
    </div>
  );
}

function GateCard({ gate }: { gate: ValidationGate }) {
  return (
    <Card data-testid={`policy-gate-${gate.id}`}>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <CheckCircle2 className="h-4 w-4 text-status-review" aria-hidden="true" />
            {gate.label}
          </span>
        }
        action={
          <Badge tone={gate.required ? 'success' : 'neutral'}>
            {gate.required ? 'Required' : 'Optional'}
          </Badge>
        }
      />
      <CardBody className="space-y-3 text-body">
        <p className="text-ink-secondary">{gate.description}</p>
        {gate.coverageThreshold && (
          <Meta label="Coverage threshold" value={gate.coverageThreshold} />
        )}
        <Meta label="Retry policy" value={gate.retryPolicy} />
        <Meta label="Blocks commit on failure" value={gate.blockingCommitOnFailure} />
      </CardBody>
    </Card>
  );
}

function Meta({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</div>
      <div className="mt-0.5 text-body text-ink-primary">{value}</div>
    </div>
  );
}
