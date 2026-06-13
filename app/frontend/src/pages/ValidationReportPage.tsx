import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  ArrowLeft,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  CircleAlert,
  CircleDashed,
  FlaskConical,
  Hammer,
  Loader2,
  RefreshCw,
  ShieldCheck,
  XCircle,
} from 'lucide-react';
import {
  api,
  type FalsifyingMetrics,
  type ValidationRun,
  type ValidationStage,
  type ValidationStatus,
} from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { ProviderSettingsCard } from '@/components/ProviderSettingsCard';

/**
 * Phase #2d — Post-migration validation report card.
 *
 * Surfaces three stages (COMPILE, TEST_PACK, EQUIVALENCE) for a scaffold
 * as separate badges with drill-down to the latest run's metrics + log.
 * Engineers can trigger each stage from this surface; the result is
 * persisted as a ValidationRun row server-side and the page re-fetches.
 *
 * This is the page the commit gate (Validation:CommitGateRequired)
 * points engineers at when their commit is blocked — so each stage
 * has a clear "run this now" CTA right next to its current verdict.
 */
export function ValidationReportPage() {
  const { id = '' } = useParams<{ id: string }>(); // scaffold id
  const queryClient = useQueryClient();

  const scaffold = useQuery({
    queryKey: ['scaffold', id],
    queryFn: () => api.getScaffold(id),
    enabled: !!id,
  });

  const runs = useQuery({
    queryKey: ['validation', id],
    queryFn: () => api.listValidationRuns(id),
    enabled: !!id,
  });

  const refetchRuns = () => queryClient.invalidateQueries({ queryKey: ['validation', id] });

  // ── Mutations: trigger each stage. Test-pack also regenerates first. ──
  const compile = useMutation({
    mutationFn: () => api.validateCompile(id),
    onSuccess: refetchRuns,
  });
  const testPack = useMutation({
    mutationFn: async () => {
      await api.generateTestPack(id);
      return api.validateTestPack(id);
    },
    onSuccess: refetchRuns,
  });
  const equivalence = useMutation({
    mutationFn: () => api.validateEquivalence(id),
    onSuccess: refetchRuns,
  });
  // Phase 9.3.b — 4th gate.
  const falsifying = useMutation({
    mutationFn: () => api.validateFalsifying(id),
    onSuccess: refetchRuns,
  });

  if (scaffold.isPending || runs.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <Skeleton className="h-48" />
          <Skeleton className="h-48" />
          <Skeleton className="h-48" />
          <Skeleton className="h-48" />
        </div>
      </div>
    );
  }
  if (scaffold.isError) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock
          title="Could not load scaffold"
          message={scaffold.error.message}
          onRetry={() => scaffold.refetch()}
        />
      </div>
    );
  }

  const sc = scaffold.data;
  const allRuns = runs.data?.runs ?? [];
  const latestByStage = (stage: ValidationStage): ValidationRun | undefined =>
    allRuns.find((r) => r.stage === stage);

  const compileRun = latestByStage('COMPILE');
  const testPackRun = latestByStage('TEST_PACK');
  const equivalenceRun = latestByStage('EQUIVALENCE');
  const falsifyingRun = latestByStage('FALSIFYING');

  const overall = computeOverall([compileRun, testPackRun, equivalenceRun, falsifyingRun]);

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <Link
            to={`/scaffolds/${id}`}
            className="inline-flex items-center gap-1 text-caption text-ink-tertiary hover:text-ink-secondary"
          >
            <ArrowLeft className="h-3.5 w-3.5" aria-hidden="true" /> Back to scaffold
          </Link>
          <p className="mt-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
            Phase #2 · Post-migration validation
          </p>
          <h1 className="mt-1 text-display font-semibold text-ink-primary">
            Validation report
          </h1>
          <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
            Four independent gates run against the generated scaffold:
            buildability, claim-mapped test coverage, cross-runtime
            equivalence to the original source, and a property-based
            search for falsifying inputs against the signed spec. All
            four must be green before the scaffold can be committed.
          </p>
        </div>
        <OverallBadge verdict={overall} />
      </header>

      <ProviderSettingsCard />

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <StageCard
          title="Compile"
          subtitle="dotnet build against the generated package"
          icon={<Hammer className="h-5 w-5" aria-hidden="true" />}
          run={compileRun}
          onRun={() => compile.mutate()}
          isRunning={compile.isPending}
          errorMessage={compile.error?.message}
          runLabel="Run compile"
        />
        <StageCard
          title="Test pack"
          subtitle="generated xUnit fixtures per signed claim + dotnet test"
          icon={<ShieldCheck className="h-5 w-5" aria-hidden="true" />}
          run={testPackRun}
          onRun={() => testPack.mutate()}
          isRunning={testPack.isPending}
          errorMessage={testPack.error?.message}
          runLabel="Regenerate + run"
        />
        <StageCard
          title="Equivalence"
          subtitle="reference compile + run vs. scaffold output, byte-compared"
          icon={<CheckCircle2 className="h-5 w-5" aria-hidden="true" />}
          run={equivalenceRun}
          onRun={() => equivalence.mutate()}
          isRunning={equivalence.isPending}
          errorMessage={equivalence.error?.message}
          runLabel="Run equivalence"
        />
        <StageCard
          title="Falsifying"
          subtitle="Hypothesis-driven counterexample search per signed-spec invariant"
          icon={<FlaskConical className="h-5 w-5" aria-hidden="true" />}
          run={falsifyingRun}
          onRun={() => falsifying.mutate()}
          isRunning={falsifying.isPending}
          errorMessage={falsifying.error?.message}
          runLabel="Run 4th gate"
        />
      </div>

      <Card>
        <CardHeader
          title="All runs"
          description={`${allRuns.length} validation run${allRuns.length === 1 ? '' : 's'} on scaffold ${sc.id.slice(0, 8)}`}
        />
        <CardBody className="p-0">
          {allRuns.length === 0 ? (
            <div className="px-6 py-8 text-center text-body text-ink-tertiary">
              No validation runs yet. Use the cards above to start.
            </div>
          ) : (
            <ul className="divide-y divide-border-subtle">
              {allRuns.map((r) => <RunRow key={r.id} run={r} />)}
            </ul>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Per-stage card with verdict, summary, metrics, drill-in to log
// ────────────────────────────────────────────────────────────────────────

function StageCard({
  title,
  subtitle,
  icon,
  run,
  onRun,
  isRunning,
  errorMessage,
  runLabel,
}: {
  title: string;
  subtitle: string;
  icon: React.ReactNode;
  run: ValidationRun | undefined;
  onRun: () => void;
  isRunning: boolean;
  errorMessage: string | undefined;
  runLabel: string;
}) {
  const [expanded, setExpanded] = useState(false);
  return (
    <Card
      className={`border-l-2 ${edgeColorClass(run?.status)} transition-colors duration-fast`}
      data-testid={`validation-card-${title.toLowerCase().replace(/\s+/g, '-')}`}
    >
      <CardBody className="space-y-4">
        <div className="flex items-start justify-between gap-3">
          <div className="flex items-start gap-3">
            <span className="mt-0.5 flex h-9 w-9 items-center justify-center rounded-md bg-accent-muted text-accent">
              {icon}
            </span>
            <div>
              <h2 className="text-h-md font-semibold text-ink-primary">{title}</h2>
              <p className="mt-0.5 text-caption text-ink-tertiary">{subtitle}</p>
            </div>
          </div>
          <StatusGlyph status={run?.status} />
        </div>

        <div>
          <StatusBadge status={run?.status} />
          <p className="mt-2 text-body text-ink-secondary">
            {run?.summary ?? 'No run yet. Trigger one to see the verdict.'}
          </p>
          {run?.completedAt && (
            <p className="mt-1 font-mono text-caption text-ink-tertiary">
              Last run {new Date(run.completedAt).toLocaleString()}
            </p>
          )}
        </div>

        {run?.metrics && (
          <button
            type="button"
            onClick={() => setExpanded((e) => !e)}
            className="-mx-2 flex w-full items-center justify-between rounded px-2 py-1 text-caption text-ink-secondary hover:bg-sunken"
          >
            <span>{expanded ? 'Hide metrics' : 'Show metrics'}</span>
            {expanded
              ? <ChevronDown className="h-3.5 w-3.5" aria-hidden="true" />
              : <ChevronRight className="h-3.5 w-3.5" aria-hidden="true" />}
          </button>
        )}
        {expanded && run?.metrics && (
          run.stage === 'FALSIFYING'
            ? <FalsifyingDetail metrics={run.metrics as unknown as FalsifyingMetrics} />
            : (
              <pre className="overflow-x-auto rounded-sm bg-sunken px-3 py-2 font-mono text-caption text-ink-secondary">
                {JSON.stringify(run.metrics, null, 2)}
              </pre>
            )
        )}

        {errorMessage && (
          <p className="rounded-sm bg-[#F4D8D7] px-3 py-2 text-caption text-status-failed">
            {errorMessage}
          </p>
        )}

        <div className="flex items-center justify-between gap-2">
          <Button
            variant={run?.status === 'PASSED' ? 'ghost' : 'primary'}
            onClick={onRun}
            disabled={isRunning}
            loading={isRunning}
          >
            <RefreshCw className="h-4 w-4" aria-hidden="true" />
            {runLabel}
          </Button>
          {run?.id && <LogLink runId={run.id} />}
        </div>
      </CardBody>
    </Card>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Phase 9.3.d — Falsifying-gate drilldown
// ────────────────────────────────────────────────────────────────────────

/**
 * Per-claim breakdown of a FALSIFYING ValidationRun's metrics. The 4th
 * gate's verdict means little without seeing which claims were exercised
 * and which (if any) were falsified — this panel substitutes for the
 * generic JSON.stringify dump that the other 3 gates fall back to.
 *
 * Shadow-mode runs (v1) carry `mode: "shadow"` in their metrics; we
 * surface this as a small inline badge so engineers know the gate
 * exercised Hypothesis end-to-end but did NOT yet compare ref vs
 * candidate binaries. v1.1 will swap in real comparison and drop the
 * shadow indicator.
 */
function FalsifyingDetail({ metrics }: { metrics: FalsifyingMetrics }) {
  const perClaim = metrics.perClaim ?? [];
  const isShadow = metrics.mode === 'shadow';
  return (
    <div className="space-y-3 rounded-sm bg-sunken px-3 py-3">
      <div className="flex flex-wrap items-center gap-2 text-caption">
        {isShadow ? <ShadowModeBadge /> : <LiveModeBadge />}
        <span className="font-mono text-ink-secondary">
          {metrics.claimsExercised} claim{metrics.claimsExercised === 1 ? '' : 's'} exercised
        </span>
        <span className="text-ink-tertiary">·</span>
        <span className="font-mono text-ink-secondary">
          {metrics.totalExamplesTried} total examples
        </span>
        {typeof metrics.totalElapsedMs === 'number' && (
          <>
            <span className="text-ink-tertiary">·</span>
            <span className="font-mono text-ink-secondary">
              {Math.round(metrics.totalElapsedMs / 100) / 10}s
            </span>
          </>
        )}
      </div>

      {perClaim.length === 0 ? (
        <p className="text-caption text-ink-tertiary">
          No claims carry generatorHints on this signed spec. Re-extract with a
          property-test-aware prompt to populate the 4th gate.
        </p>
      ) : (
        <ul className="space-y-2">
          {perClaim.map((c) => <FalsifyingClaimRow key={c.claimId} claim={c} />)}
        </ul>
      )}
    </div>
  );
}

function FalsifyingClaimRow({ claim }: { claim: FalsifyingMetrics['perClaim'][number] }) {
  const isFalsified = claim.falsifying !== null;
  const isSkipped = claim.skipReason !== null;
  const tone: 'success' | 'failed' | 'neutral' | 'draft' =
    isFalsified ? 'failed' :
    isSkipped ? 'neutral' :
    claim.timedOut ? 'draft' :
    'success';
  const verdict =
    isFalsified ? 'Falsifying example found' :
    isSkipped ? `Skipped · ${claim.skipReason}` :
    claim.timedOut ? `Timed out at ${claim.examplesTried} examples` :
    `${claim.examplesTried} examples · no counterexample`;

  return (
    <li
      className="rounded-sm border border-border-subtle bg-raised px-3 py-2"
      data-testid={`falsifying-claim-${claim.claimId}`}
    >
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={tone}>{claim.claimId}</Badge>
        <span className="font-mono text-caption text-ink-secondary">{verdict}</span>
        {claim.elapsedMs > 0 && (
          <span className="ml-auto font-mono text-caption text-ink-tertiary">
            {Math.round(claim.elapsedMs)}ms
            {claim.callbackErrors > 0 && ` · ${claim.callbackErrors} callback errors`}
          </span>
        )}
      </div>
      {isFalsified && (
        <div className="mt-2 space-y-1">
          <p className="text-caption text-ink-tertiary">Minimal counterexample:</p>
          <pre className="overflow-x-auto rounded-sm bg-sunken px-2 py-1 font-mono text-caption text-ink-primary">
            {JSON.stringify(claim.falsifying, null, 2)}
          </pre>
          {(claim.refOutput || claim.candOutput) && (
            <div className="mt-2 grid grid-cols-1 gap-2 sm:grid-cols-2">
              <div>
                <p className="text-caption text-ink-tertiary">Reference output</p>
                <pre className="overflow-x-auto rounded-sm bg-sunken px-2 py-1 font-mono text-caption text-ink-primary">
                  {claim.refOutput ?? '(none)'}
                </pre>
              </div>
              <div>
                <p className="text-caption text-ink-tertiary">Candidate output</p>
                <pre className="overflow-x-auto rounded-sm bg-sunken px-2 py-1 font-mono text-caption text-status-failed">
                  {claim.candOutput ?? '(none)'}
                </pre>
              </div>
            </div>
          )}
        </div>
      )}
    </li>
  );
}

function ShadowModeBadge() {
  return (
    <span
      className="inline-flex items-center gap-1 rounded-sm bg-accent-muted px-2 py-0.5 font-mono text-caption text-status-draft"
      title="Sidecar exercised Hypothesis end-to-end, but candidate-binary comparison is not yet wired. Verdicts are based on the reference path only."
      data-testid="falsifying-shadow-mode"
    >
      shadow mode v1
    </span>
  );
}

/**
 * Phase 9.5 — shown on FALSIFYING runs whose metrics.mode === 'live'.
 * Live mode means the validator pre-compiled both a reference binary
 * and a candidate binary, then drove both with each Hypothesis-generated
 * input and compared outputs. Falsifying examples in this mode are
 * real semantic disagreements, not artefacts of the harness.
 */
function LiveModeBadge() {
  return (
    <span
      className="inline-flex items-center gap-1 rounded-sm bg-[#DAEFE9] px-2 py-0.5 font-mono text-caption text-status-review"
      title="Validator compiled both a reference binary and the candidate, then drove both with each Hypothesis-generated input and compared outputs. Falsifiers in this mode are real semantic disagreements."
      data-testid="falsifying-live-mode"
    >
      live mode
    </span>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Run history row + log fetcher
// ────────────────────────────────────────────────────────────────────────

function RunRow({ run }: { run: ValidationRun }) {
  return (
    <li className="flex flex-wrap items-center gap-3 px-6 py-3">
      <StatusBadge status={run.status} />
      <span className="font-mono text-caption text-ink-tertiary">
        {run.stage}
      </span>
      <span className="min-w-0 flex-1 truncate text-body text-ink-secondary">
        {run.summary}
      </span>
      <span className="font-mono text-caption text-ink-tertiary">
        {new Date(run.startedAt).toLocaleString()}
      </span>
      <LogLink runId={run.id} />
    </li>
  );
}

function LogLink({ runId }: { runId: string }) {
  const [open, setOpen] = useState(false);
  const log = useQuery({
    queryKey: ['validation-log', runId],
    queryFn: () => api.getValidationRunLog(runId),
    enabled: open,
  });
  return (
    <>
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="text-caption font-medium text-accent hover:underline"
      >
        View log
      </button>
      {open && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center"
          role="dialog"
          aria-modal="true"
          aria-labelledby="validation-log-title"
          onClick={() => setOpen(false)}
        >
          <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
          <div
            className="relative max-h-[80vh] w-[900px] max-w-[92vw] overflow-hidden rounded-lg border border-border-subtle bg-raised shadow-e3"
            onClick={(e) => e.stopPropagation()}
          >
            <header className="flex items-center justify-between border-b border-border-subtle px-6 py-3">
              <h3 id="validation-log-title" className="text-h-sm font-semibold text-ink-primary">Validation log</h3>
              <button
                type="button"
                onClick={() => setOpen(false)}
                className="text-caption text-ink-tertiary hover:text-ink-primary"
              >
                Close
              </button>
            </header>
            <div className="max-h-[68vh] overflow-auto bg-sunken px-6 py-4">
              {log.isPending ? (
                <Skeleton className="h-64 w-full" />
              ) : log.isError ? (
                <p className="text-body text-status-failed">{log.error.message}</p>
              ) : (
                <pre className="whitespace-pre-wrap font-mono text-caption text-ink-secondary">
                  {log.data}
                </pre>
              )}
            </div>
          </div>
        </div>
      )}
    </>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Status helpers
// ────────────────────────────────────────────────────────────────────────

function StatusGlyph({ status }: { status: ValidationStatus | undefined }) {
  if (!status) return <CircleDashed className="h-5 w-5 text-ink-tertiary" aria-hidden="true" />;
  if (status === 'RUNNING')
    return <Loader2 className="h-5 w-5 animate-spin text-accent" aria-hidden="true" />;
  if (status === 'PASSED')
    return <CheckCircle2 className="h-5 w-5 text-status-review" aria-hidden="true" />;
  if (status === 'FAILED')
    return <XCircle className="h-5 w-5 text-status-failed" aria-hidden="true" />;
  if (status === 'ERRORED')
    return <CircleAlert className="h-5 w-5 text-status-failed" aria-hidden="true" />;
  return <CircleDashed className="h-5 w-5 text-ink-tertiary" aria-hidden="true" />;
}

function StatusBadge({ status }: { status: ValidationStatus | undefined }) {
  if (!status) return <Badge tone="neutral">No run yet</Badge>;
  if (status === 'PASSED') return <Badge tone="success">Passed</Badge>;
  if (status === 'FAILED') return <Badge tone="failed">Failed</Badge>;
  if (status === 'ERRORED') return <Badge tone="failed">Errored</Badge>;
  if (status === 'RUNNING') return <Badge tone="draft">Running</Badge>;
  return <Badge tone="neutral">{status}</Badge>;
}

function edgeColorClass(status: ValidationStatus | undefined): string {
  if (status === 'PASSED') return 'border-l-status-review';
  if (status === 'FAILED' || status === 'ERRORED') return 'border-l-status-failed';
  if (status === 'RUNNING') return 'border-l-status-draft';
  return 'border-l-transparent';
}

function OverallBadge({
  verdict,
}: {
  verdict: 'all-green' | 'partial' | 'red' | 'none';
}) {
  if (verdict === 'all-green')
    return (
      <span className="inline-flex items-center gap-2 rounded-md bg-[#DAEFE9] px-4 py-2 text-h-sm font-semibold text-status-review">
        <CheckCircle2 className="h-5 w-5" aria-hidden="true" /> All 4 gates green — commit-ready
      </span>
    );
  if (verdict === 'red')
    return (
      <span className="inline-flex items-center gap-2 rounded-md bg-[#F4D8D7] px-4 py-2 text-h-sm font-semibold text-status-failed">
        <XCircle className="h-5 w-5" aria-hidden="true" /> Blocked — fix the red gates
      </span>
    );
  if (verdict === 'partial')
    return (
      <span className="inline-flex items-center gap-2 rounded-md bg-accent-muted px-4 py-2 text-h-sm font-semibold text-status-draft">
        <CircleDashed className="h-5 w-5" aria-hidden="true" /> Some gates pending
      </span>
    );
  return (
    <span className="inline-flex items-center gap-2 rounded-md bg-sunken px-4 py-2 text-h-sm font-semibold text-ink-secondary">
      <CircleDashed className="h-5 w-5" aria-hidden="true" /> No runs yet
    </span>
  );
}

function computeOverall(
  runs: (ValidationRun | undefined)[],
): 'all-green' | 'partial' | 'red' | 'none' {
  if (runs.every((r) => !r)) return 'none';
  if (runs.some((r) => r && (r.status === 'FAILED' || r.status === 'ERRORED'))) return 'red';
  if (runs.every((r) => r && r.status === 'PASSED')) return 'all-green';
  return 'partial';
}
