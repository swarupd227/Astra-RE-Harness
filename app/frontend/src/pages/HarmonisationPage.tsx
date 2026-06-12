import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Play, X } from 'lucide-react';
import { clsx } from 'clsx';
import {
  api,
  ApiError,
  type HarmonisationFinding,
  type HarmonisationRunSummary,
} from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Card, CardBody } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';

/**
 * Phase 7.1 — Cross-routine harmonisation page.
 *
 * Lets admins drive the corpus-wide consistency pass and curate the
 * resulting findings. The pass takes every SIGNED spec, sends them to
 * the LLM in one call, and surfaces contradictions across them
 * (callee-IO drift, COMMON-layout drift, terminology drift, missing
 * invariants, duplicate open questions).
 *
 * Findings are SUGGESTIONS — the SME marks each open / accepted /
 * dismissed. The status mutations are audit-logged.
 */
export function HarmonisationPage() {
  const queryClient = useQueryClient();
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';
  const corpora = useQuery({ queryKey: ['corpora'], queryFn: api.listCorpora });
  const [selectedCorpusId, setSelectedCorpusId] = useState<string | null>(null);
  const [openRunId, setOpenRunId] = useState<string | null>(null);

  const corpusId =
    selectedCorpusId ?? corpora.data?.data[0]?.id ?? null;

  const runs = useQuery({
    queryKey: ['harmonisation-runs', corpusId],
    queryFn: () => (corpusId ? api.listHarmonisationRuns(corpusId, 50) : Promise.resolve({ data: [] })),
    enabled: !!corpusId,
    staleTime: 0,
  });

  const runPass = useMutation({
    mutationFn: () => (corpusId ? api.runHarmonisation(corpusId) : Promise.reject(new Error('no corpus'))),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['harmonisation-runs', corpusId] }),
  });

  if (corpora.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-40" />
      </div>
    );
  }
  if (corpora.isError || !corpora.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load corpora" message={String(corpora.error)} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup" data-testid="harmonisation-page">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase 7.1 · Cross-routine consistency
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">Harmonisation</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Sends every SIGNED spec in a corpus to the LLM in a single
          call and surfaces inconsistencies across them — callee-IO
          drift, COMMON-layout drift, terminology drift, missing
          invariants, duplicate open questions. Findings are
          suggestions; the SME confirms or dismisses each.
        </p>
      </header>

      <Card>
        <CardBody className="flex flex-wrap items-end justify-between gap-4">
          <label className="block">
            <span className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
              Corpus
            </span>
            <select
              className="mt-1 rounded border border-border-subtle bg-raised px-2 py-1 text-body-sm"
              value={corpusId ?? ''}
              onChange={(e) => setSelectedCorpusId(e.target.value)}
              data-testid="harmonisation-corpus-select"
            >
              {corpora.data.data.map((c: { id: string; name: string }) => (
                <option key={c.id} value={c.id}>{c.name}</option>
              ))}
            </select>
          </label>
          {isAdmin && (
            <Button
              variant="primary"
              size="sm"
              onClick={() => runPass.mutate()}
              disabled={runPass.isPending || !corpusId}
              data-testid="harmonisation-run"
            >
              <Play className={clsx('h-4 w-4', runPass.isPending && 'animate-pulse')} />
              {runPass.isPending ? 'Running…' : 'Run harmonisation pass'}
            </Button>
          )}
        </CardBody>
      </Card>

      {runPass.isError && (
        <ErrorBlock
          title="Harmonisation pass failed"
          message={runPass.error instanceof ApiError ? runPass.error.message : String(runPass.error)}
        />
      )}

      <section data-testid="harmonisation-run-list">
        <h2 className="mb-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Recent runs
        </h2>
        {runs.isPending && <Skeleton className="h-24" />}
        {!runs.isPending && (runs.data?.data.length ?? 0) === 0 && (
          <Card>
            <CardBody className="text-center text-ink-secondary">
              No harmonisation passes yet for this corpus. Click <strong>Run harmonisation pass</strong>.
            </CardBody>
          </Card>
        )}
        <div className="space-y-2">
          {runs.data?.data.map((r) => (
            <RunCard key={r.id} run={r} onOpen={() => setOpenRunId(r.id)} />
          ))}
        </div>
      </section>

      {openRunId && (
        <RunDrawer
          runId={openRunId}
          isAdmin={isAdmin}
          onClose={() => setOpenRunId(null)}
        />
      )}
    </div>
  );
}

function RunCard({ run, onOpen }: { run: HarmonisationRunSummary; onOpen: () => void }) {
  const tone =
    run.status === 'COMPLETED' ? 'success' :
    run.status === 'FAILED' ? 'failed' :
    'review';
  return (
    <Card interactive onClick={onOpen} data-testid={`harmonisation-run-${run.id}`}>
      <CardBody className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="flex items-center gap-2">
            <Badge tone={tone}>{run.status}</Badge>
            <span className="font-mono text-caption text-ink-tertiary">
              {new Date(run.completedAt).toLocaleString()}
            </span>
            <Badge tone="neutral">{run.modelName}</Badge>
          </div>
          <p className="mt-1 text-body text-ink-primary">{run.summary}</p>
          <p className="text-body-sm text-ink-secondary">
            {run.specCount} signed specs · {run.findingCount} findings · prompt {run.promptId}@{run.promptVersion}
          </p>
        </div>
        <div className="text-right font-mono text-caption text-ink-tertiary">
          <div>input: {run.inputTokens.toLocaleString()} tok</div>
          <div>cache hit: {run.cacheReadTokens.toLocaleString()} tok</div>
          <div>output: {run.outputTokens.toLocaleString()} tok</div>
        </div>
      </CardBody>
    </Card>
  );
}

function RunDrawer({
  runId,
  isAdmin,
  onClose,
}: {
  runId: string;
  isAdmin: boolean;
  onClose: () => void;
}) {
  const detail = useQuery({
    queryKey: ['harmonisation-run', runId],
    queryFn: () => api.getHarmonisationRun(runId),
  });
  return (
    <div
      className="fixed inset-0 z-50 flex items-stretch justify-end bg-black/40"
      role="dialog"
      data-testid="harmonisation-run-drawer"
      onClick={onClose}
    >
      <div
        className="flex h-full w-full max-w-2xl flex-col overflow-y-auto bg-raised shadow-xl"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="sticky top-0 z-10 flex items-center justify-between border-b border-border-subtle bg-raised p-4">
          <h2 className="font-mono text-body-sm text-ink-secondary">Run {runId.slice(0, 8)}…</h2>
          <button
            className="rounded p-1 hover:bg-sunken"
            onClick={onClose}
            aria-label="Close"
          >
            <X className="h-4 w-4" />
          </button>
        </header>
        {detail.isPending && (
          <div className="space-y-3 p-4">
            <Skeleton className="h-6 w-64" />
            <Skeleton className="h-32" />
          </div>
        )}
        {detail.isError && (
          <ErrorBlock title="Could not load run" message={String(detail.error)} />
        )}
        {detail.data && (
          <div className="space-y-4 p-4">
            <Card>
              <CardBody>
                <p className="text-body text-ink-primary">{detail.data.run.summary}</p>
                <p className="mt-1 font-mono text-caption text-ink-tertiary">
                  {detail.data.run.specCount} signed specs · {detail.data.run.modelName} · prompt {detail.data.run.promptId}@{detail.data.run.promptVersion}
                </p>
              </CardBody>
            </Card>
            {detail.data.findings.length === 0 && (
              <Card>
                <CardBody className="text-center text-ink-secondary">
                  No findings — the corpus is consistent.
                </CardBody>
              </Card>
            )}
            {detail.data.findings.map((f) => (
              <FindingCard key={f.id} finding={f} isAdmin={isAdmin} onMutated={() => detail.refetch()} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

function FindingCard({
  finding,
  isAdmin,
  onMutated,
}: {
  finding: HarmonisationFinding;
  isAdmin: boolean;
  onMutated: () => void;
}) {
  const [note, setNote] = useState(finding.adminNote ?? '');
  const update = useMutation({
    mutationFn: (status: 'open' | 'accepted' | 'dismissed') =>
      api.updateHarmonisationFinding(finding.id, { status, adminNote: note }),
    onSuccess: () => onMutated(),
  });
  const severityTone =
    finding.severity === 'high' ? 'failed' :
    finding.severity === 'medium' ? 'scaffolded' :
    'neutral';
  const statusTone =
    finding.status === 'accepted' ? 'success' :
    finding.status === 'dismissed' ? 'superseded' :
    'review';
  return (
    <Card data-testid={`harmonisation-finding-${finding.id}`}>
      <CardBody>
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone={severityTone}>{finding.severity}</Badge>
          <Badge tone="neutral">{finding.category}</Badge>
          <Badge tone={statusTone}>{finding.status}</Badge>
        </div>
        <p className="mt-2 text-body-lg font-semibold text-ink-primary">{finding.title}</p>
        <pre className="mt-2 whitespace-pre-wrap rounded bg-sunken p-2 font-mono text-body-sm">
          {finding.detail}
        </pre>
        {finding.affectedSpecIds.length > 0 && (
          <p className="mt-2 font-mono text-caption text-ink-tertiary">
            affects: {finding.affectedSpecIds.join(', ')}
          </p>
        )}
        {isAdmin && (
          <div className="mt-3 space-y-2">
            <textarea
              className="w-full rounded border border-border-subtle bg-raised p-2 font-mono text-body-sm"
              rows={2}
              placeholder="Optional admin note…"
              value={note}
              onChange={(e) => setNote(e.target.value)}
            />
            <div className="flex gap-2">
              <Button size="sm" variant="primary" onClick={() => update.mutate('accepted')} disabled={update.isPending}>
                Accept
              </Button>
              <Button size="sm" variant="ghost" onClick={() => update.mutate('dismissed')} disabled={update.isPending}>
                Dismiss
              </Button>
              <Button size="sm" variant="ghost" onClick={() => update.mutate('open')} disabled={update.isPending}>
                Reopen
              </Button>
            </div>
          </div>
        )}
      </CardBody>
    </Card>
  );
}
