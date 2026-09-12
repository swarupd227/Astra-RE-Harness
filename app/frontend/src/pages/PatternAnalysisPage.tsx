import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import {
  ArrowLeft, Boxes, ChevronDown, ChevronRight, Loader2, Pause, Play, Square, Wand2, XCircle,
} from 'lucide-react';
import { api, getPersona, API_BASE } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { PageHero } from '@/components/PageHero';
import type { ArchetypeProposal, PatternAnalysisProgress, PatternCluster } from '@/lib/api';

const RUNNING_STATES = new Set(['QUEUED', 'RUNNING']);
const TERMINAL_STATES = new Set(['SUCCEEDED', 'PARTIAL', 'FAILED', 'CANCELLED']);

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

function formatEta(seconds?: number | null): string | null {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return null;
  if (seconds < 60) return `~${Math.max(1, Math.round(seconds))}s left`;
  return `~${Math.ceil(seconds / 60)} min left`;
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
    <div className="mt-3 border-t border-line-subtle pt-3">
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
        <p className="mt-2 text-xs text-status-fail">{(proposeMutation.error as Error).message}</p>
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
                className="flex-1 rounded border border-line bg-raised px-2 py-1 text-sm text-ink-primary placeholder:text-ink-tertiary"
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
            <div className="space-y-2 rounded border border-line-subtle bg-raised p-3">
              <p className="text-sm text-ink-secondary">{detail.data.description}</p>
              <p className="font-mono text-caption text-ink-tertiary">
                Matches: {detail.data.matches.join(', ') || '—'}
              </p>
              {detail.data.state === 'VERIFICATION_FAILED' && detail.data.compileLog && (
                <pre className="max-h-64 overflow-auto whitespace-pre-wrap rounded bg-codebg p-3 font-mono text-xs text-sand-100">
                  {detail.data.compileLog}
                </pre>
              )}
              <div className="space-y-1">
                {detail.data.files.map(f => (
                  <details key={f.path} className="rounded border border-line-subtle">
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
  // A forced run discards the corpus's survey digests and re-surveys every
  // routine — minutes on a large corpus, but still real money. Arm it with
  // a second click.
  const [confirmForce, setConfirmForce] = useState(false);
  const [logLines, setLogLines] = useState<string[]>([]);
  const [progress, setProgress] = useState<PatternAnalysisProgress | null>(null);
  const [stageLabel, setStageLabel] = useState<string | null>(null);

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

  // Runs survive the page: on load, re-attach to a run that is still
  // going, or surface one that was paused / interrupted so it can be
  // resumed. (Previously a reload silently forgot a 2-hour run.)
  const runsQuery = useQuery({
    queryKey: ['pattern-analysis-runs', id],
    queryFn: () => api.listPatternAnalysisRuns(id, 5),
    enabled: !!id,
  });
  const latestRun = runsQuery.data?.data?.[0];
  useEffect(() => {
    if (!latestRun || activeRunId) return;
    if (RUNNING_STATES.has(latestRun.state)) setActiveRunId(latestRun.id);
  }, [latestRun, activeRunId]);
  const resumableRun = !activeRunId && latestRun?.state === 'RESUMABLE' ? latestRun : null;

  const startRun = (result: { runId: string }) => {
    setRunError(null);
    setConfirmForce(false);
    setLogLines([]);
    setProgress(null);
    setStageLabel(null);
    setActiveRunId(result.runId);
  };

  // Incremental by default: survey only routines with no digest yet, then
  // re-cluster. Forcing re-surveys routines that already have a digest,
  // which is only wanted after a survey-prompt change.
  const runMutation = useMutation({
    mutationFn: (force: boolean) => api.runPatternAnalysis(id, { force }),
    onSuccess: startRun,
  });
  const resumeMutation = useMutation({
    mutationFn: (runId: string) => api.resumePatternAnalysisRun(runId),
    onSuccess: (_r, runId) => startRun({ runId }),
  });
  const pauseMutation = useMutation({
    mutationFn: (runId: string) => api.pausePatternAnalysisRun(runId),
  });
  const cancelMutation = useMutation({
    mutationFn: (runId: string) => api.cancelPatternAnalysisRun(runId),
  });

  // Live progress off the structured run-event stream: `log` events keep
  // the terminal readable, `progress` drives the bar + ETA, `stage`
  // names the phase. EventSource reconnects with Last-Event-ID on its own,
  // so a dropped connection replays what it missed.
  useEffect(() => {
    if (!activeRunId) return;
    const es = new EventSource(`${API_BASE}/api/v1/pattern-analysis/runs/${activeRunId}/logs`);
    es.onmessage = (e: MessageEvent) => {
      try {
        const { message } = JSON.parse(e.data) as { message?: string };
        if (message) setLogLines((prev) => [...prev.slice(-400), message]);
      } catch { /* ignore malformed events */ }
    };
    es.addEventListener('progress', (e) => {
      try {
        const { data } = JSON.parse((e as MessageEvent).data) as { data: PatternAnalysisProgress };
        if (data) setProgress(data);
      } catch { /* ignore */ }
    });
    es.addEventListener('stage', (e) => {
      try {
        const { data } = JSON.parse((e as MessageEvent).data) as { data?: { label?: string; step?: number; of?: number } };
        if (data?.label) setStageLabel(`${data.label}${data.step && data.of ? ` (${data.step}/${data.of})` : ''}`);
        setProgress(null);
      } catch { /* ignore */ }
    });
    es.addEventListener('done', () => es.close());
    es.onerror = () => { /* EventSource retries by itself */ };
    return () => es.close();
  }, [activeRunId]);

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
    if (!s) return;
    if (TERMINAL_STATES.has(s) || s === 'RESUMABLE') {
      if (s === 'FAILED') setRunError(runStatus.data?.errorSummary ?? 'Pattern analysis failed.');
      qc.invalidateQueries({ queryKey: ['pattern-clusters', id] });
      qc.invalidateQueries({ queryKey: ['pattern-analysis-runs', id] });
      setActiveRunId(null);
      setProgress(null);
      setStageLabel(null);
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
  const isRunning = runMutation.isPending || resumeMutation.isPending || !!activeRunId;
  const liveState = runStatus.data?.state;
  const liveSummary = runStatus.data?.summary;
  const stopRequested = pauseMutation.isPending || cancelMutation.isPending || !!runStatus.data?.cancelRequested;
  const lastRun = clustersQuery.data?.run;
  const clusters = clustersQuery.data?.clusters ?? [];
  const totalRoutines = clusters.reduce((sum, cl) => sum + cl.memberCount, 0);
  const singletons = clusters.filter(cl => cl.memberCount === 1).length;
  const coreClusters = clusters.filter(cl => cl.memberCount > 1).length;
  const percent = progress && progress.total > 0 ? Math.min(100, Math.round((progress.done / progress.total) * 100)) : null;
  const eta = formatEta(progress?.etaSeconds);
  // The API returns proposals newest-first; keep only the first (most
  // recent) one seen per cluster. Building the Map from a plain .map()
  // would let a later, older entry silently overwrite the newest one.
  const proposalsByCluster = new Map<string, ArchetypeProposal>();
  for (const p of proposalsQuery.data?.data ?? []) {
    if (!proposalsByCluster.has(p.patternClusterId)) proposalsByCluster.set(p.patternClusterId, p);
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup">
      <PageHero
        eyebrow={
          <span className="inline-flex items-center gap-2">
            <Link
              to={`/corpora/${id}`}
              className="rounded text-ink-tertiary transition-colors hover:text-ink-primary"
              aria-label="Back to project"
            >
              <ArrowLeft className="h-4 w-4" />
            </Link>
            Pattern Analysis
          </span>
        }
        title={c.name}
        lead="How many distinct behavioural patterns does this corpus contain, and which routines share one — the question that determines how many archetypes a migration engagement needs to build."
        actions={persona === 'admin' && (
          <>
            <Button
              variant="secondary"
              onClick={() => runMutation.mutate(false)}
              disabled={isRunning}
              data-testid="run-pattern-analysis"
              title="Surveys any routine not yet covered (minutes, not hours), then regroups the project. Existing digests and specs are reused."
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
                title="Discards this project's survey digests and re-surveys every routine. A few minutes on a large project; signed specs are never touched."
                className="rounded-lg border border-line-subtle px-2.5 py-1.5 font-mono text-caption text-ink-tertiary transition-colors hover:border-status-fail hover:text-status-fail disabled:cursor-not-allowed disabled:opacity-50"
              >
                {confirmForce ? 'Confirm full re-survey' : 'Re-survey all routines…'}
              </button>
            )}
          </>
        )}
      />

      {runError && (
        <div className="flex items-start gap-3 rounded-lg border border-status-fail/30 bg-status-fail/10 px-4 py-3">
          <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-status-fail" />
          <span className="flex-1 text-sm text-status-fail">{runError}</span>
          <button onClick={() => setRunError(null)} className="shrink-0 text-xs text-status-fail hover:underline">
            Dismiss
          </button>
        </div>
      )}

      {resumableRun && (
        <div
          className="flex flex-wrap items-center gap-3 rounded-lg border border-status-warn/30 bg-status-warn/10 px-4 py-3"
          data-testid="pattern-analysis-resumable"
        >
          <Pause className="h-4 w-4 shrink-0 text-status-warn" />
          <div className="flex-1">
            <p className="text-sm font-medium text-ink-primary">A previous run is paused</p>
            <p className="font-mono text-caption text-ink-tertiary">
              {resumableRun.summary ?? 'Interrupted — completed digests are kept.'}
            </p>
          </div>
          {persona === 'admin' && (
            <>
              <Button
                variant="secondary"
                onClick={() => resumeMutation.mutate(resumableRun.id)}
                disabled={resumeMutation.isPending}
                data-testid="resume-pattern-analysis"
              >
                {resumeMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
                Resume
              </Button>
              <button
                type="button"
                onClick={() => cancelMutation.mutate(resumableRun.id, {
                  onSuccess: () => qc.invalidateQueries({ queryKey: ['pattern-analysis-runs', id] }),
                })}
                className="font-mono text-caption text-ink-tertiary hover:text-status-fail"
              >
                Discard
              </button>
            </>
          )}
        </div>
      )}

      {isRunning && (
        <Card>
          <CardBody className="space-y-3">
            <div className="flex items-center gap-3">
              <Loader2 className="h-4 w-4 shrink-0 animate-spin text-ink-tertiary" />
              <div className="flex-1">
                <p className="text-body font-medium text-ink-primary">
                  {stopRequested
                    ? 'Stopping after the current routines…'
                    : liveState === 'RUNNING' ? liveSummary ?? 'Running…' : 'Queued…'}
                </p>
                <p className="mt-0.5 font-mono text-caption text-ink-tertiary">
                  {stageLabel ?? 'Surveying each routine, then grouping them into shared patterns.'}
                  {progress && percent != null && (
                    <>
                      {' · '}{progress.done}/{progress.total}
                      {progress.propagated > 0 && ` (+${progress.propagated} propagated)`}
                      {progress.failed > 0 && `, ${progress.failed} failed`}
                      {eta && ` · ${eta}`}
                    </>
                  )}
                </p>
              </div>
              {persona === 'admin' && activeRunId && (
                <div className="flex shrink-0 items-center gap-2">
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => pauseMutation.mutate(activeRunId)}
                    disabled={stopRequested}
                    title="Stop now and keep every digest written so far; resume later."
                    data-testid="pause-pattern-analysis"
                  >
                    <Pause className="h-3.5 w-3.5" /> Pause
                  </Button>
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => cancelMutation.mutate(activeRunId)}
                    disabled={stopRequested}
                    title="Stop and mark the run cancelled. Digests already written are kept."
                    data-testid="cancel-pattern-analysis"
                  >
                    <Square className="h-3.5 w-3.5" /> Cancel
                  </Button>
                </div>
              )}
            </div>
            {percent != null && (
              <div
                className="h-1.5 w-full overflow-hidden rounded-full bg-sunken"
                role="progressbar"
                aria-valuenow={percent}
                aria-valuemin={0}
                aria-valuemax={100}
                data-testid="pattern-analysis-progress"
              >
                <div
                  className="h-full rounded-full bg-volt transition-[width] duration-500"
                  style={{ width: `${percent}%` }}
                />
              </div>
            )}
          </CardBody>
          {logLines.length > 0 && (
            <div
              className="max-h-48 overflow-y-auto border-t border-line-subtle bg-sunken px-4 py-2 font-mono text-[11px] leading-relaxed text-ink-tertiary"
              data-testid="pattern-analysis-log"
            >
              {logLines.map((line, i) => (
                <div key={i} className="whitespace-pre-wrap">{line}</div>
              ))}
            </div>
          )}
        </Card>
      )}

      {!isRunning && clustersQuery.isPending && (
        <Skeleton className="h-64 w-full" />
      )}

      {!isRunning && !clustersQuery.isPending && !lastRun && !resumableRun && (
        <Card>
          <CardBody>
            <p className="text-body text-ink-secondary">
              No pattern analysis has run for this corpus yet. Run it to survey every
              routine (minutes, not hours) and see how many distinct patterns this
              codebase actually contains.
            </p>
          </CardBody>
        </Card>
      )}

      {!isRunning && lastRun && (
        <>
          <div className="grid grid-cols-3 gap-4">
            <Card>
              <CardBody>
                <p className="label">Routines analysed</p>
                <p className="mt-1 text-h-lg font-semibold text-ink-primary">{totalRoutines}</p>
              </CardBody>
            </Card>
            <Card>
              <CardBody>
                <p className="label">Core patterns (2+ routines)</p>
                <p className="mt-1 text-h-lg font-semibold text-ink-primary">{coreClusters}</p>
              </CardBody>
            </Card>
            <Card>
              <CardBody>
                <p className="label">Singletons (long tail)</p>
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
              {lastRun.stagesRequested && ` · stages: ${lastRun.stagesRequested}`}
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
                        className="rounded border border-line-subtle px-2 py-1 font-mono text-xs text-ink-secondary transition-colors hover:border-line-strong hover:text-ink-primary"
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
