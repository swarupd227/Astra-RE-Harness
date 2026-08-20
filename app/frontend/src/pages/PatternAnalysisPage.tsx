import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, Boxes, ChevronDown, ChevronRight, Loader2, Play, Wand2, XCircle } from 'lucide-react';
import { api, getPersona } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import type { ArchetypeProposal, PatternCluster } from '@/lib/api';

const RUNNING_STATES = new Set(['QUEUED', 'RUNNING']);

function clusterTone(memberCount: number): 'signed' | 'draft' | 'neutral' {
  if (memberCount >= 3) return 'signed';
  if (memberCount === 1) return 'neutral';
  return 'draft';
}

function proposalTone(state: string): 'signed' | 'draft' | 'failed' | 'neutral' {
  switch (state) {
    case 'PRODUCTION': return 'signed';
    case 'VERIFIED': return 'draft';
    case 'VERIFICATION_FAILED':
    case 'REJECTED': return 'failed';
    default: return 'neutral';
  }
}

function proposalLabel(state: string): string {
  switch (state) {
    case 'PRODUCTION': return 'Live';
    case 'VERIFIED': return 'Verified — awaiting approval';
    case 'VERIFICATION_FAILED': return 'Verification failed';
    case 'REJECTED': return 'Rejected';
    default: return state;
  }
}

/** Propose / review / approve panel for one cluster. Self-contained so its
    queries and mutations don't re-render the whole cluster list. */
function ArchetypeProposalSection({
  cluster,
  proposal,
  persona,
  onChanged,
}: {
  cluster: PatternCluster;
  proposal: ArchetypeProposal | undefined;
  persona: string;
  onChanged: () => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const [rejectReason, setRejectReason] = useState('');
  const [rejecting, setRejecting] = useState(false);

  const detail = useQuery({
    queryKey: ['archetype-proposal', proposal?.id],
    queryFn: () => api.getArchetypeProposal(proposal!.id),
    enabled: !!proposal && expanded,
  });

  const proposeMutation = useMutation({
    mutationFn: () => api.proposeArchetype(cluster.id),
    onSuccess: () => { setExpanded(true); onChanged(); },
  });

  const approveMutation = useMutation({
    mutationFn: () => api.approveArchetypeProposal(proposal!.id),
    onSuccess: onChanged,
  });

  const rejectMutation = useMutation({
    mutationFn: () => api.rejectArchetypeProposal(proposal!.id, rejectReason),
    onSuccess: () => { setRejecting(false); onChanged(); },
  });

  if (persona !== 'admin' && !proposal) return null;

  return (
    <div className="mt-3 border-t border-border-subtle pt-3">
      {!proposal && (
        <Button
          variant="secondary"
          onClick={() => proposeMutation.mutate()}
          disabled={proposeMutation.isPending}
          data-testid="propose-archetype"
        >
          {proposeMutation.isPending
            ? <Loader2 className="h-4 w-4 animate-spin" />
            : <Wand2 className="h-4 w-4" />}
          {proposeMutation.isPending ? 'Proposing + verifying…' : 'Propose archetype'}
        </Button>
      )}
      {proposeMutation.isError && (
        <p className="mt-2 text-xs text-rose-600">{(proposeMutation.error as Error).message}</p>
      )}

      {proposal && (
        <div className="space-y-2">
          <div className="flex items-center gap-2">
            <button
              onClick={() => setExpanded(v => !v)}
              className="flex items-center gap-1 text-sm text-ink-secondary hover:text-ink-primary"
            >
              {expanded ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
              <code className="text-xs">{proposal.proposedArchetypeId}</code>
            </button>
            <Badge tone={proposalTone(proposal.state)}>{proposalLabel(proposal.state)}</Badge>
            {proposal.testCount != null && (
              <span className="font-mono text-caption text-ink-tertiary">
                {(proposal.testCount ?? 0) - (proposal.testFailureCount ?? 0)}/{proposal.testCount} tests
              </span>
            )}
            {persona === 'admin' && proposal.state === 'VERIFIED' && (
              <div className="ml-auto flex items-center gap-2">
                <Button
                  variant="secondary"
                  onClick={() => approveMutation.mutate()}
                  disabled={approveMutation.isPending}
                  data-testid="approve-archetype-proposal"
                >
                  {approveMutation.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : null}
                  Approve — go live
                </Button>
                <Button variant="secondary" onClick={() => setRejecting(v => !v)}>Reject</Button>
              </div>
            )}
          </div>

          {rejecting && (
            <div className="flex items-center gap-2">
              <input
                value={rejectReason}
                onChange={e => setRejectReason(e.target.value)}
                placeholder="Why is this proposal being rejected?"
                className="flex-1 rounded border border-border-subtle bg-raised px-2 py-1 text-sm"
              />
              <Button
                variant="secondary"
                onClick={() => rejectMutation.mutate()}
                disabled={!rejectReason.trim() || rejectMutation.isPending}
              >
                Confirm reject
              </Button>
            </div>
          )}

          {expanded && detail.data && (
            <div className="space-y-2 rounded border border-border-subtle bg-raised p-3">
              <p className="text-sm text-ink-secondary">{detail.data.description}</p>
              <p className="font-mono text-caption text-ink-tertiary">
                Matches: {detail.data.matches.join(', ') || '—'}
              </p>
              {detail.data.state === 'VERIFICATION_FAILED' && detail.data.compileLog && (
                <pre className="max-h-64 overflow-auto whitespace-pre-wrap rounded bg-slate-900 p-3 font-mono text-xs text-slate-200">
                  {detail.data.compileLog}
                </pre>
              )}
              <div className="space-y-1">
                {detail.data.files.map(f => (
                  <details key={f.path} className="rounded border border-border-subtle">
                    <summary className="cursor-pointer px-2 py-1 font-mono text-xs text-ink-secondary">
                      {f.path}
                    </summary>
                    <pre className="max-h-96 overflow-auto whitespace-pre-wrap p-2 font-mono text-xs text-ink-primary">
                      {f.content}
                    </pre>
                  </details>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

export function PatternAnalysisPage() {
  const { id = '' } = useParams();
  const qc = useQueryClient();
  const persona = getPersona();
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [runError, setRunError] = useState<string | null>(null);
  // Full re-extraction re-runs the LLM over every routine in the corpus —
  // hours and real money on a large one. Arm it with a second click.
  const [confirmForce, setConfirmForce] = useState(false);

  const corpus = useQuery({
    queryKey: ['corpus', id],
    queryFn: () => api.getCorpus(id),
    enabled: !!id,
  });

  const clustersQuery = useQuery({
    queryKey: ['pattern-clusters', id],
    queryFn: () => api.listPatternClusters(id),
    enabled: !!id,
  });

  const proposalsQuery = useQuery({
    queryKey: ['archetype-proposals', id],
    queryFn: () => api.listArchetypeProposals(id),
    enabled: !!id,
  });

  // Incremental by default: extract only routines that have never been
  // extracted, then re-cluster. Forcing re-extracts routines that already
  // have specs, which is only wanted after an extract-prompt change.
  const runMutation = useMutation({
    mutationFn: (force: boolean) => api.runPatternAnalysis(id, { force }),
    onSuccess: (result) => {
      setRunError(null);
      setConfirmForce(false);
      setActiveRunId(result.runId);
    },
  });

  const runStatus = useQuery({
    queryKey: ['pattern-analysis-run', activeRunId],
    queryFn: () => api.getPatternAnalysisRun(activeRunId!),
    enabled: !!activeRunId,
    refetchInterval: (query) => {
      const s = query.state.data?.state;
      return s && RUNNING_STATES.has(s) ? 4000 : false;
    },
  });

  useEffect(() => {
    const s = runStatus.data?.state;
    if (s === 'SUCCEEDED' || s === 'PARTIAL' || s === 'FAILED') {
      if (s === 'FAILED') setRunError(runStatus.data?.errorSummary ?? 'Pattern analysis failed.');
      qc.invalidateQueries({ queryKey: ['pattern-clusters', id] });
      setActiveRunId(null);
    }
  }, [runStatus.data?.state, runStatus.data?.errorSummary, id, qc]);

  const refetchProposals = () => {
    qc.invalidateQueries({ queryKey: ['archetype-proposals', id] });
    qc.invalidateQueries({ queryKey: ['archetype-proposal'] });
  };

  if (corpus.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }
  if (corpus.isError) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load project" message={corpus.error.message} />
      </div>
    );
  }

  const c = corpus.data;
  const isRunning = runMutation.isPending || !!activeRunId;
  const liveState = runStatus.data?.state;
  const liveSummary = runStatus.data?.summary;
  const lastRun = clustersQuery.data?.run;
  const clusters = clustersQuery.data?.clusters ?? [];
  const totalRoutines = clusters.reduce((sum, cl) => sum + cl.memberCount, 0);
  const singletons = clusters.filter(cl => cl.memberCount === 1).length;
  const coreClusters = clusters.filter(cl => cl.memberCount > 1).length;
  // The API returns proposals newest-first; keep only the first (most
  // recent) one seen per cluster. Building the Map from a plain .map()
  // would let a later, older entry silently overwrite the newest one.
  const proposalsByCluster = new Map<string, ArchetypeProposal>();
  for (const p of proposalsQuery.data?.data ?? []) {
    if (!proposalsByCluster.has(p.patternClusterId)) proposalsByCluster.set(p.patternClusterId, p);
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup">
      <header className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-3">
          <Link
            to={`/corpora/${id}`}
            className="rounded p-1 text-ink-tertiary transition-colors hover:text-ink-primary"
          >
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <div>
            <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
              Pattern Analysis
            </p>
            <h1 className="mt-1 text-display font-semibold text-ink-primary">{c.name}</h1>
            <p className="mt-2 font-mono text-caption text-ink-tertiary">
              How many distinct behavioural patterns does this corpus contain, and which
              routines share one — the question that determines how many archetypes a
              migration engagement needs to build.
            </p>
          </div>
        </div>

        {persona === 'admin' && (
          <div className="flex shrink-0 items-center gap-2">
            <Button
              variant="secondary"
              onClick={() => runMutation.mutate(false)}
              disabled={isRunning}
              data-testid="run-pattern-analysis"
              title="Extracts any routine that has no spec yet, then re-groups the corpus. Routines already extracted are reused."
            >
              {isRunning ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
              {lastRun ? 'Re-run analysis' : 'Run analysis'}
            </Button>
            {lastRun && (
              <button
                type="button"
                onClick={() => (confirmForce ? runMutation.mutate(true) : setConfirmForce(true))}
                onBlur={() => setConfirmForce(false)}
                disabled={isRunning}
                data-testid="force-pattern-analysis"
                title="Discards every existing spec and re-runs the LLM over the whole corpus. Hours on a large project — only needed after an extraction-prompt change."
                className="rounded border border-border-subtle px-2.5 py-1.5 font-mono text-caption text-ink-tertiary transition-colors hover:border-rose-400 hover:text-rose-600 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {confirmForce ? 'Confirm full re-extraction' : 'Re-extract all specs…'}
              </button>
            )}
          </div>
        )}
      </header>

      {runError && (
        <div className="flex items-start gap-3 rounded border border-rose-500/30 bg-rose-500/10 px-4 py-3">
          <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-rose-500" />
          <span className="flex-1 text-sm text-rose-700 dark:text-rose-400">{runError}</span>
          <button onClick={() => setRunError(null)} className="shrink-0 text-xs text-rose-500 hover:text-rose-700">
            Dismiss
          </button>
        </div>
      )}

      {isRunning && (
        <Card>
          <CardBody className="flex items-center gap-3">
            <Loader2 className="h-4 w-4 shrink-0 animate-spin text-ink-tertiary" />
            <div>
              <p className="text-body font-medium text-ink-primary">
                {liveState === 'RUNNING' ? liveSummary ?? 'Running…' : 'Queued…'}
              </p>
              <p className="mt-0.5 font-mono text-caption text-ink-tertiary">
                Stage 1 extracts every un-extracted routine's spec; stage 2 sends the whole
                corpus to Claude in one call to group routines into shared patterns.
              </p>
            </div>
          </CardBody>
        </Card>
      )}

      {!isRunning && clustersQuery.isPending && (
        <Skeleton className="h-64 w-full" />
      )}

      {!isRunning && !clustersQuery.isPending && !lastRun && (
        <Card>
          <CardBody>
            <p className="text-body text-ink-secondary">
              No pattern analysis has run for this corpus yet. Run it to bulk-extract every
              routine's spec and see how many distinct patterns this codebase actually
              contains.
            </p>
          </CardBody>
        </Card>
      )}

      {!isRunning && lastRun && (
        <>
          <div className="grid grid-cols-3 gap-4">
            <Card>
              <CardBody>
                <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">Routines analysed</p>
                <p className="mt-1 text-h-lg font-semibold text-ink-primary">{totalRoutines}</p>
              </CardBody>
            </Card>
            <Card>
              <CardBody>
                <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">Core patterns (2+ routines)</p>
                <p className="mt-1 text-h-lg font-semibold text-ink-primary">{coreClusters}</p>
              </CardBody>
            </Card>
            <Card>
              <CardBody>
                <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">Singletons (long tail)</p>
                <p className="mt-1 text-h-lg font-semibold text-ink-primary">{singletons}</p>
              </CardBody>
            </Card>
          </div>

          <Card>
            <CardHeader
              title="Last run"
              description={lastRun.summary}
            />
            <CardBody className="font-mono text-caption text-ink-tertiary">
              {lastRun.completedAt && `Completed ${new Date(lastRun.completedAt).toLocaleString()}`}
              {lastRun.triggeredBy && ` · triggered by ${lastRun.triggeredBy}`}
            </CardBody>
          </Card>

          <div className="space-y-4">
            {clusters.map((cluster: PatternCluster) => (
              <Card key={cluster.id}>
                <CardHeader
                  title={
                    <span className="flex items-center gap-2">
                      <Boxes className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                      {cluster.label}
                    </span>
                  }
                  description={cluster.suggestedArchetypeName && (
                    <code className="text-xs">{cluster.suggestedArchetypeName}</code>
                  )}
                  action={
                    <Badge tone={clusterTone(cluster.memberCount)}>
                      {cluster.memberCount} routine{cluster.memberCount === 1 ? '' : 's'}
                    </Badge>
                  }
                />
                <CardBody className="space-y-3">
                  <p className="text-body text-ink-secondary">{cluster.rationale}</p>
                  {cluster.claimKindSignature && (
                    <p className="font-mono text-caption text-ink-tertiary">
                      Claim-kind signature: {cluster.claimKindSignature}
                    </p>
                  )}
                  <div className="flex flex-wrap gap-2">
                    {cluster.members.map(m => (
                      <Link
                        key={m.subroutineId}
                        to={`/subroutines/${m.subroutineId}`}
                        className="rounded border border-border-subtle px-2 py-1 font-mono text-xs text-ink-secondary transition-colors hover:border-brand hover:text-ink-primary"
                      >
                        {m.subroutineName}
                      </Link>
                    ))}
                  </div>

                  <ArchetypeProposalSection
                    cluster={cluster}
                    proposal={proposalsByCluster.get(cluster.id)}
                    persona={persona}
                    onChanged={refetchProposals}
                  />
                </CardBody>
              </Card>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
