import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { AlertTriangle, ArrowRight, Boxes, FileCode, GitCommit, Sparkles } from 'lucide-react';
import { api } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { Button } from '@/components/Button';
import { MonacoSource } from '@/components/MonacoSource';
import { TargetSelector } from '@/components/TargetSelector';
import { ConfirmModal } from '@/components/ConfirmModal';
import { WorkflowRail, nextStepFor } from '@/components/WorkflowRail';
import { useTargetStack } from '@/hooks/useTargetStack';
import { prettySchema, prettyStack } from '@/lib/targetStacks';
import { formatState } from '@/lib/labels';

export function SubroutineDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const sub = useQuery({
    queryKey: ['subroutine', id],
    queryFn: () => api.getSubroutine(id),
    enabled: !!id,
  });
  const source = useQuery({
    queryKey: ['subroutine-source', id],
    queryFn: () => api.getSubroutineSource(id),
    enabled: !!id,
  });
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const [confirmReextract, setConfirmReextract] = useState(false);

  if (sub.isPending || source.isPending) {
    return (
      <div className="mx-auto max-w-[1400px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-96" />
        <Skeleton className="h-[520px] w-full" />
      </div>
    );
  }
  if (sub.isError) {
    return (
      <div className="mx-auto max-w-[1400px] p-6 lg:p-10">
        <ErrorBlock
          title="Could not load routine"
          message={sub.error.message}
          onRetry={() => sub.refetch()}
        />
      </div>
    );
  }

  const s = sub.data;
  // Phase 10.1.b.1 — language-aware help text now reads the schema
  // straight off the subroutine row (Phase 9.2.c originally inferred
  // it from the corpus name regex; SubroutineEndpoints has projected
  // `sourceLanguage` since ingest, the frontend type just omitted it).
  const help = helpForSchemaId(s.sourceLanguage);
  const next = nextStepFor(s.state);
  // Any state past PARSED means a spec exists whose reviews re-extraction
  // would discard.
  const hasExistingSpec = ['DRAFT', 'IN_REVIEW', 'SIGNED', 'SCAFFOLDED'].includes(s.state);

  return (
    <div className="mx-auto max-w-[1400px] space-y-6 p-6 lg:p-10 fadeup">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
            <Link to={`/projects/${s.corpus.id}`} className="hover:text-ink-primary">
              {s.corpus.name}
            </Link>
            <span className="mx-1 text-ink-tertiary">›</span>
            <span className="text-ink-secondary">{s.file.relativePath}</span>
          </p>
          <h1 className="mt-2 font-mono text-display font-semibold text-ink-primary">
            {s.name}
          </h1>
          <p className="mt-1 font-mono text-caption text-ink-tertiary">{s.signature}</p>
        </div>
        <div className="flex items-center gap-2">
          <Badge tone={badgeToneForState(s.state)}>{formatState(s.state)}</Badge>
          {(s.state === 'DRAFT' || s.state === 'IN_REVIEW' || s.state === 'SIGNED') && (
            <Link to={s.state === 'DRAFT' ? `/subroutines/${s.id}/spec` : `/subroutines/${s.id}/review`}>
              <Button variant="secondary" size="md">
                {s.state === 'DRAFT' ? 'View draft spec' : s.state === 'SIGNED' ? 'View signed spec' : 'Open review'}
                <ArrowRight className="h-4 w-4" />
              </Button>
            </Link>
          )}
          <Button
            // Re-extraction replaces the draft and drops every claim review
            // taken against it — it used to fire straight off this button.
            variant={hasExistingSpec ? 'secondary' : 'primary'}
            size="md"
            onClick={() =>
              hasExistingSpec
                ? setConfirmReextract(true)
                : navigate(`/subroutines/${s.id}/extract`)
            }
            data-testid="extract-cta"
          >
            <Sparkles className="h-4 w-4" />
            {hasExistingSpec ? 'Re-extract spec' : 'Extract spec'}
          </Button>
        </div>
      </header>

      {/* Where this routine is in the workflow, and what happens next. */}
      <div className="flex flex-wrap items-center justify-between gap-x-6 gap-y-2 rounded-md border border-border-subtle bg-raised px-4 py-3">
        <WorkflowRail state={s.state} persona={whoami.data?.persona as any} />
        <p className="text-caption text-ink-secondary">
          Next: <strong className="text-ink-primary">{next.action}</strong>
          <span className="text-ink-tertiary"> · {next.ownerLabel}</span>
        </p>
      </div>

      {help && (
        <div
          className={`rounded-md border-l-4 ${help.borderClass} bg-raised px-4 py-3`}
          data-testid={`extraction-help-${help.id}`}
        >
          <p className="flex flex-wrap items-baseline gap-2 text-body text-ink-secondary">
            <span className={`text-caption font-medium uppercase tracking-wider ${help.labelTextClass}`}>
              {help.label}
            </span>
            <span className="text-ink-tertiary">·</span>
            <span>{help.body}</span>
          </p>
        </div>
      )}

      <div className="grid gap-6 lg:grid-cols-[1fr_320px]">
        <Card className="overflow-hidden">
          <CardHeader
            title={
              <span className="flex items-center gap-2">
                <FileCode className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                <span className="font-mono text-body">{s.file.relativePath}</span>
              </span>
            }
            description={`Lines ${s.lineStart}–${s.lineEnd} · ${s.file.lineCount} total`}
            action={
              <span className="font-mono text-[11px] text-ink-tertiary">
                <GitCommit className="mr-1 inline h-3 w-3" aria-hidden="true" />
                {s.file.fileHash.slice(0, 19)}…
              </span>
            }
          />
          <CardBody className="p-0">
            <MonacoSource
              value={source.data!.content}
              height={560}
              highlightLine={s.lineStart}
            />
          </CardBody>
        </Card>

        <aside className="space-y-4">
          <NextStepCard subroutineId={s.id} state={s.state} />
          <TargetStackCard sourceLanguage={s.sourceLanguage} />
          <StructurePanel sub={s} />
          <MigrationContextPanel subroutineId={s.id} />
        </aside>
      </div>

      <ConfirmModal
        open={confirmReextract}
        title="Re-extract this spec?"
        body={`${s.name} already has a spec. Re-extracting calls the model again and replaces it.`}
        consequences={[
          'Every claim decision taken on the current draft is discarded.',
          s.state === 'SIGNED' || s.state === 'SCAFFOLDED'
            ? 'This spec is signed — the signature covers the current claims, not the new ones.'
            : 'The routine returns to DRAFT and must be routed to the SME again.',
          'The new extraction is billed as a fresh model call.',
        ]}
        confirmLabel="Re-extract"
        onConfirm={() => navigate(`/subroutines/${s.id}/extract`)}
        onClose={() => setConfirmReextract(false)}
        testId="reextract-confirm"
      />
    </div>
  );
}

/**
 * Target stack, chosen here rather than only after sign-off.
 *
 * The picker used to live exclusively on the spec-review surface, gated on
 * SIGNED + engineer — the last possible moment, in the least likely place, so
 * "which stack will this become?" had no answer while you were still deciding
 * whether to migrate the routine at all.
 */
function TargetStackCard({ sourceLanguage }: { sourceLanguage: string | null }) {
  const { targetStack, setTargetStack, overriddenFrom, savedOverridesRecommended } =
    useTargetStack(sourceLanguage);
  return (
    <Card>
      <CardHeader
        title={
          <span className="flex items-center gap-2">
            <Boxes className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
            Target stack
          </span>
        }
        description={`Generated code for this routine will be ${prettyStack(targetStack)}.`}
      />
      <CardBody className="space-y-2">
        <TargetSelector
          value={targetStack}
          onChange={setTargetStack}
          sourceLanguage={sourceLanguage}
          compact
        />
        {overriddenFrom && (
          <p
            className="flex items-start gap-1.5 rounded-md border border-status-scaffolded/40 bg-[#F2E5C2]/40 px-2.5 py-2 text-caption text-ink-primary"
            data-testid="target-overridden-notice"
          >
            <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-status-scaffolded" aria-hidden="true" />
            <span>
              Your saved target <strong className="font-mono">{prettyStack(overriddenFrom)}</strong> has no
              production archetype for {prettySchema(sourceLanguage ?? '')} — using{' '}
              <strong className="font-mono">{prettyStack(targetStack)}</strong>.
            </span>
          </p>
        )}
        {savedOverridesRecommended && (
          <p className="text-caption text-ink-tertiary" data-testid="target-saved-notice">
            Using your saved target. Recommended here:{' '}
            <strong className="font-mono text-ink-secondary">{prettyStack(savedOverridesRecommended)}</strong>.{' '}
            <button
              type="button"
              onClick={() => setTargetStack(savedOverridesRecommended)}
              className="underline decoration-dotted underline-offset-2 hover:text-accent"
              data-testid="use-recommended-target"
            >
              Use recommended
            </button>
          </p>
        )}
      </CardBody>
    </Card>
  );
}

function StructurePanel({
  sub,
}: {
  sub: NonNullable<Awaited<ReturnType<typeof api.getSubroutine>>>;
}) {
  return (
    <Card>
      <CardHeader title="Structure" />
      <CardBody className="space-y-5">
        <Group title="COMMON blocks" items={sub.commonBlockRefs ?? []} />
        <Group title="Calls" items={sub.calledSubroutines ?? []} />
        <Group
          title="ISAM reads"
          items={sub.ioPatterns?.isam_reads ?? []}
        />
        <Group
          title="ISAM writes"
          items={sub.ioPatterns?.isam_writes ?? []}
        />
        <Group
          title="Notifications"
          items={sub.ioPatterns?.notifications ?? []}
        />
      </CardBody>
    </Card>
  );
}

function Group({ title, items }: { title: string; items: string[] }) {
  if (!items || items.length === 0) {
    return (
      <div>
        <h4 className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">{title}</h4>
        <p className="mt-1 text-caption text-ink-tertiary">—</p>
      </div>
    );
  }
  return (
    <div>
      <h4 className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">{title}</h4>
      <ul className="mt-1.5 flex flex-wrap gap-1.5">
        {items.map((item) => (
          <li key={item}>
            <span className="inline-flex items-center rounded-sm border border-border-subtle bg-canvas px-1.5 py-0.5 font-mono text-caption text-ink-primary">
              {item}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

/**
 * Up-next card. Was hardcoded to "Live extraction · Not yet extracted"
 * regardless of state, so a signed or scaffolded routine still advertised the
 * first step. Now it reads the lifecycle state and links to the surface where
 * the next action actually lives.
 */
function NextStepCard({ subroutineId, state }: { subroutineId: string; state: string }) {
  const next = nextStepFor(state);
  return (
    <Card className="border-accent/40 bg-accent-muted/40">
      <CardBody>
        <p className="text-caption font-medium uppercase tracking-wider text-accent">
          {next.done ? 'Done' : 'Up next'}
        </p>
        <p className="mt-2 text-body font-semibold text-ink-primary">{next.action}</p>
        <p className="mt-1 text-caption text-ink-secondary">{next.hint}</p>
        <Link
          to={next.href(subroutineId)}
          className="mt-3 inline-flex items-center gap-1.5 text-caption font-medium text-accent hover:underline"
          data-testid="next-step-cta"
        >
          {next.done ? 'Open it' : 'Go there'}
          <ArrowRight className="h-3 w-3" aria-hidden="true" />
        </Link>
        <p className="mt-2 text-caption text-ink-tertiary">
          Owned by <strong className="text-ink-secondary">{next.ownerLabel}</strong>
        </p>
      </CardBody>
    </Card>
  );
}

function badgeToneForState(state: string): 'draft' | 'review' | 'signed' | 'scaffolded' | 'neutral' {
  switch (state) {
    case 'DRAFT': return 'draft';
    case 'IN_REVIEW': return 'review';
    case 'SIGNED': return 'signed';
    case 'SCAFFOLDED': return 'scaffolded';
    default: return 'neutral';
  }
}

// ─── Phase 8.0.c — Migration context sidebar panel ─────────────────
// Three cards stacked: wave assignment (only if a plan exists),
// migration readiness classification, blast-radius summary. All
// three fetch in parallel via react-query. 404s render as compact
// "n/a" states rather than error blocks so the panel doesn't
// dominate the page when no migration plan exists yet.

function MigrationContextPanel({ subroutineId }: { subroutineId: string }) {
  const wave = useQuery({
    queryKey: ['wave-assignment', subroutineId],
    queryFn: () => api.getWaveAssignment(subroutineId),
    retry: false,
  });
  const readiness = useQuery({
    queryKey: ['migration-readiness', subroutineId],
    queryFn: () => api.getMigrationReadiness(subroutineId),
    retry: false,
  });
  const blast = useQuery({
    queryKey: ['blast-radius', subroutineId],
    queryFn: () => api.getBlastRadius(subroutineId),
    retry: false,
  });

  return (
    <div className="space-y-3" data-testid="migration-context-panel">
      <WaveCard q={wave} />
      <ReadinessCard q={readiness} />
      <BlastRadiusCard q={blast} />
    </div>
  );
}

function WaveCard({ q }: { q: ReturnType<typeof useQuery<Awaited<ReturnType<typeof api.getWaveAssignment>>>> }) {
  const noPlan = q.isError; // 404 = no plan for this corpus
  return (
    <Card data-testid="wave-assignment-card">
      <CardHeader title="Wave assignment" description="From the current migration plan." />
      <CardBody className="space-y-2">
        {q.isPending && <Skeleton className="h-5 w-32" />}
        {noPlan && (
          <p className="font-mono text-caption text-ink-tertiary">
            No migration plan for this corpus yet.
          </p>
        )}
        {q.data && (
          <div className="space-y-1">
            <div className="flex items-center gap-2">
              <Badge tone={q.data.planStatus === 'approved' ? 'success' : 'review'}>
                {q.data.planStatus}
              </Badge>
              <span className="font-mono text-body-sm text-ink-primary">
                Wave {q.data.waveNumber} of {q.data.totalWaves}
              </span>
            </div>
            <p className="text-body-sm text-ink-secondary">{q.data.waveName}</p>
            <p className="font-mono text-caption text-ink-tertiary">{q.data.strategyName}</p>
          </div>
        )}
      </CardBody>
    </Card>
  );
}

function ReadinessCard({ q }: { q: ReturnType<typeof useQuery<Awaited<ReturnType<typeof api.getMigrationReadiness>>>> }) {
  return (
    <Card data-testid="migration-readiness-card">
      <CardHeader title="Migration readiness" description="Based on this routine's dependencies." />
      <CardBody className="space-y-2">
        {q.isPending && <Skeleton className="h-5 w-40" />}
        {q.isError && (
          <p className="font-mono text-caption text-ink-tertiary">Could not classify.</p>
        )}
        {q.data && (
          <>
            <Badge tone={readinessTone(q.data.classification)}>{q.data.classification}</Badge>
            <ul className="space-y-1 text-body-sm text-ink-secondary">
              {q.data.reasons.map((r) => <li key={r}>{r}</li>)}
            </ul>
            <p className="font-mono text-caption text-ink-tertiary">
              callees: {q.data.calleeCount} · callers: {q.data.callerCount}
              {q.data.sharedBlockNames.length > 0 && (
                <> · shared: {q.data.sharedBlockNames.map((b) => `/${b}/`).join(', ')}</>
              )}
            </p>
            {q.data.blockingRoutineIds.length > 0 && (
              <p className="font-mono text-caption text-amber-700">
                Blocked by {q.data.blockingRoutineIds.length} routine{q.data.blockingRoutineIds.length === 1 ? '' : 's'}
              </p>
            )}
          </>
        )}
      </CardBody>
    </Card>
  );
}

function readinessTone(c: string): 'success' | 'review' | 'failed' | 'neutral' {
  switch (c) {
    case 'safe-leaf':
    case 'safe-with-deps-done': return 'success';
    case 'coordinated-only': return 'review';
    case 'blocked-on-deps': return 'failed';
    default: return 'neutral';
  }
}

function BlastRadiusCard({ q }: { q: ReturnType<typeof useQuery<Awaited<ReturnType<typeof api.getBlastRadius>>>> }) {
  return (
    <Card data-testid="blast-radius-card">
      <CardHeader title="Blast radius" description="Routines that depend on this one." />
      <CardBody className="space-y-2">
        {q.isPending && <Skeleton className="h-5 w-32" />}
        {q.isError && (
          <p className="font-mono text-caption text-ink-tertiary">Could not compute.</p>
        )}
        {q.data && (
          <>
            <p className="font-mono text-body-sm text-ink-primary">
              {q.data.transitiveCallerCount} downstream
              {q.data.directCallerCount > 0 && (
                <> ({q.data.directCallerCount} direct)</>
              )}
              {q.data.sharedStorageConsumerCount > 0 && (
                <> · {q.data.sharedStorageConsumerCount} shared-storage</>
              )}
            </p>
            {q.data.affected.length === 0 && (
              <p className="text-body-sm text-ink-secondary">
                Nothing in this corpus depends on this routine.
              </p>
            )}
            {q.data.affected.length > 0 && (
              <details>
                <summary className="cursor-pointer text-body-sm text-accent hover:underline">
                  Show {q.data.affected.length} affected routine{q.data.affected.length === 1 ? '' : 's'}
                </summary>
                <ul className="mt-2 space-y-1 font-mono text-caption">
                  {q.data.affected.map((a) => (
                    <li key={a.id} className="flex items-center justify-between gap-2">
                      <Link to={`/subroutines/${a.id}`} className="truncate text-accent hover:underline">
                        {a.name}
                      </Link>
                      <span className="flex items-center gap-1 text-ink-tertiary">
                        <Badge tone={badgeToneForState(a.state)}>{a.state}</Badge>
                        <span title={a.reason}>{a.reason.startsWith('direct') ? '↰' : a.reason === 'shared-storage' ? '↔' : '↑'}</span>
                      </span>
                    </li>
                  ))}
                </ul>
              </details>
            )}
          </>
        )}
      </CardBody>
    </Card>
  );
}

// ──────────────────────────────────────────────────────────────────────
// Phase 9.2.c — language-aware extraction help text. Brief blurbs per
// language, calibrated to what reviewers can expect from the extractor
// on that schema. Shorter for Delphi (cleaner mapping vs .NET / Java);
// longer + cautionary for C++ (more open questions expected because of
// templates, ownership and UB).
// ──────────────────────────────────────────────────────────────────────

type ExtractionHelp = {
  id: string;
  label: string;
  /** Plain-English copy describing what the extractor will look for. */
  body: string;
  /** Tailwind classes for the bordered banner. */
  borderClass: string;
  labelTextClass: string;
};

const HELP_FORTRAN: ExtractionHelp = {
  id: 'fortran-f77',
  label: 'Fortran extraction',
  body: 'The extractor will look for invariants, section contracts, I/O side effects, and edge cases. COMMON-block reads, IMPLICIT typing surprises, and BLOCK DATA initialisers are first-class concerns; expect 3–6 invariants and 1–3 open questions on a typical routine.',
  borderClass: 'border-l-[#6366F1]',
  labelTextClass: 'text-[#3730A3]',
};
const HELP_COBOL: ExtractionHelp = {
  id: 'cobol',
  label: 'COBOL extraction',
  body: 'The extractor will look for invariants, section contracts, and I/O side effects with focus on VSAM keys, CICS interactions, and PIC field truncation. EVALUATE WHEN OTHER fall-through and copybook redefines are common open questions.',
  borderClass: 'border-l-[#14B8A6]',
  labelTextClass: 'text-[#0F766E]',
};
const HELP_DELPHI: ExtractionHelp = {
  id: 'delphi',
  label: 'Delphi extraction',
  body: 'Cleanest mapping of the four — the curated RTL table (per ADR-025) means object lifetimes, properties, and events translate to .NET / Java with few open questions. Expect a tight extraction surface; SME review is usually short.',
  borderClass: 'border-l-[#10B981]',
  labelTextClass: 'text-[#065F46]',
};
const HELP_CPP: ExtractionHelp = {
  id: 'cpp',
  label: 'C++ extraction — expect more open questions',
  body: 'Templates, ownership models, undefined behaviour, and noexcept contracts each get their own claim kind. Many routines surface 2–3 open questions per extraction because the source under-specifies ownership or relies on UB that does not map cleanly. Per ADR-027, the Java target may suggest a JNA fallback when template constraints exceed what generics can express.',
  borderClass: 'border-l-[#F59E0B]',
  labelTextClass: 'text-[#92400E]',
};
const HELP_VB6: ExtractionHelp = {
  id: 'vb6',
  label: 'VB6 extraction — flag the swallows + the COM',
  body: 'On Error Resume Next blocks become on_error_handler claims; the extractor surfaces narrow Err.Number filters as Open Questions because most "intentional" swallows turn out to mask real errors at scale. Every CreateObject / GetObject site becomes a com_interop_contract claim — the customer must source modern equivalents during Discovery, not the harness. Default-property access (rs!Field, txtCustomer in a String context) explodes into typed reads in the .NET 10 port.',
  borderClass: 'border-l-[#0EA5E9]',
  labelTextClass: 'text-[#075985]',
};

function helpForSchemaId(schemaId: string | null): ExtractionHelp | null {
  switch (schemaId) {
    case 'fortran-f77': return HELP_FORTRAN;
    case 'cobol':       return HELP_COBOL;
    case 'delphi':      return HELP_DELPHI;
    case 'cpp':         return HELP_CPP;
    case 'vb6':         return HELP_VB6;
    default:            return null;
  }
}
