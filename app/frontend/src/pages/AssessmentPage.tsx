/**
 * `/projects/:id/assessment` (+ `/corpora/:id/assessment`) — the 10-minute
 * Assessment as a page: the assessment card at pane size on top, the
 * section's rendered markdown underneath. With nothing assessed yet, an
 * Admin can run it here and watch the run card until it lands.
 *
 * Dark-first (v2 tokens), no legacy wrapper. `?fixture=1` in dev renders
 * the Increment 2 card gallery instead of loading anything.
 */
import { useCallback, useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { ArrowLeft, ClipboardList, MessageSquare, Play, RefreshCw } from 'lucide-react';
import { api, ApiError, getPersona } from '@/lib/api';
import { assessmentApi, conversationsApi, openRunStream, type Artifact } from '@/lib/conversations';
import { renderArtifact } from '@/workspace/artifacts/registry';
import { Markdown } from '@/workspace/Markdown';
import { AgentAvatar } from '@/workspace/AgentAvatar';
import { absoluteTime, isTerminalRunState, relativeTime, str } from '@/workspace/format';
import { CardGallery } from '@/workspace/dev/CardGallery';

type RunHandle = { id: string; startedAt: string };

export function AssessmentPage() {
  const { id = '' } = useParams<{ id: string }>();
  const [search] = useSearchParams();
  const qc = useQueryClient();
  const persona = getPersona();
  const fixture = import.meta.env.DEV && search.get('fixture') === '1';

  const assessment = useQuery({
    queryKey: ['assessment', id],
    queryFn: () => assessmentApi.get(id),
    enabled: !!id && !fixture,
    retry: (count, err) => !(err instanceof ApiError && err.status === 404) && count < 2,
    staleTime: 30_000,
  });
  const notFound = assessment.isError && assessment.error instanceof ApiError && assessment.error.status === 404;

  // Name the programme even before an assessment exists.
  const corpus = useQuery({
    queryKey: ['corpus', id],
    queryFn: () => api.getCorpus(id),
    enabled: !!id && !fixture,
    staleTime: 5 * 60_000,
  });
  const thread = useQuery({
    queryKey: ['conversations', 'corpus', id],
    queryFn: () => conversationsApi.list(id),
    enabled: !!id && !fixture,
    staleTime: 5 * 60_000,
    retry: false,
  });
  const threadId = thread.data?.data?.find((c) => c.kind === 'programme')?.id ?? null;

  // ── Running it ──────────────────────────────────────────────────────
  const [run, setRun] = useState<RunHandle | null>(null);
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState<string | null>(null);

  const start = useCallback(async () => {
    if (!id || starting) return;
    setStarting(true);
    setStartError(null);
    try {
      const res = await assessmentApi.run(id);
      setRun({ id: res.runId, startedAt: new Date().toISOString() });
    } catch (e) {
      setStartError(e instanceof Error ? e.message : 'Could not start the assessment.');
    } finally {
      setStarting(false);
    }
  }, [id, starting]);

  // Follow the run to know when to refetch (the card follows it for display).
  useEffect(() => {
    if (!run) return;
    let finished = false;
    const finish = () => {
      if (finished) return;
      finished = true;
      void qc.invalidateQueries({ queryKey: ['assessment', id] });
      void qc.invalidateQueries({ queryKey: ['conversation'] });
      // Give the section write a beat to land, then drop the run card.
      window.setTimeout(() => setRun(null), 1200);
    };
    const close = openRunStream(
      run.id,
      0,
      (evt) => {
        if (evt.type === 'done') finish();
        if (evt.type === 'state' && isTerminalRunState(str(evt.data?.state))) finish();
      },
      finish,
    );
    // Belt and braces: poll in case the stream is cut.
    const poll = window.setInterval(() => void qc.invalidateQueries({ queryKey: ['assessment', id] }), 10_000);
    return () => {
      close();
      window.clearInterval(poll);
    };
  }, [run, id, qc]);

  // Once the assessment appears, the run card has done its job.
  useEffect(() => {
    if (assessment.data && run) setRun(null);
  }, [assessment.data, run]);

  const card: Artifact | null = assessment.data
    ? { kind: 'assessment', refId: id, props: assessment.data.card ?? {} }
    : null;
  const runCard: Artifact | null = run
    ? {
        kind: 'runProgress',
        refId: run.id,
        props: {
          kind: 'assessment',
          corpusId: id,
          label: `Assessment · ${corpus.data?.name ?? 'programme'}`,
          agent: 'discovery',
          state: 'RUNNING',
          startedAt: run.startedAt,
        },
      }
    : null;

  const name = str(assessment.data?.card?.corpusName) || corpus.data?.name || 'Programme';
  const generatedAt = assessment.data?.section?.generatedAt ?? null;
  const canRun = persona === 'admin';

  return (
    <div className="min-h-full bg-canvas text-ink-primary" data-testid="assessment-page">
      <header className="border-b border-line-subtle bg-raised/60 px-6 py-4">
        <div className="mx-auto flex max-w-[1100px] flex-wrap items-center gap-3">
          <Link
            to={`/projects/${id}`}
            className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            aria-label="Back to project"
          >
            <ArrowLeft size={16} aria-hidden="true" />
          </Link>
          <span className="inline-flex h-8 w-8 items-center justify-center rounded-lg bg-sunken text-ink-secondary" aria-hidden="true">
            <ClipboardList size={16} />
          </span>
          <div className="min-w-0">
            <p className="text-micro font-medium uppercase tracking-wide text-ink-tertiary">Assessment</p>
            <h1 className="truncate text-h-md font-semibold text-ink-primary" data-testid="assessment-title">
              {fixture ? 'Card gallery (fixtures)' : name}
            </h1>
          </div>
          <div className="ml-auto flex flex-wrap items-center gap-2 text-caption">
            {generatedAt && (
              <span className="text-ink-tertiary" title={absoluteTime(generatedAt)}>
                assessed {relativeTime(generatedAt)}
              </span>
            )}
            {threadId && (
              <Link
                to={`/w/${threadId}`}
                className="inline-flex items-center gap-1.5 rounded-md border border-line-subtle px-2.5 py-1.5 text-ink-secondary hover:border-line hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                data-testid="assessment-open-thread"
              >
                <MessageSquare size={13} aria-hidden="true" />
                Programme thread
              </Link>
            )}
            {!fixture && assessment.data && canRun && !run && (
              <button
                type="button"
                onClick={() => void start()}
                disabled={starting}
                data-testid="assessment-rerun"
                className="inline-flex items-center gap-1.5 rounded-md border border-line-subtle px-2.5 py-1.5 text-ink-secondary hover:border-line hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt disabled:opacity-50"
              >
                <RefreshCw size={13} aria-hidden="true" />
                {starting ? 'Starting…' : 'Run again'}
              </button>
            )}
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-[1100px] space-y-6 px-6 py-6">
        {fixture ? (
          <CardGallery />
        ) : assessment.isPending && !notFound ? (
          <div className="space-y-3" aria-busy="true" data-testid="assessment-loading">
            <div className="h-6 w-64 animate-pulse rounded bg-sunken" />
            <div className="h-40 w-full animate-pulse rounded-xl bg-sunken" />
            <div className="h-64 w-full animate-pulse rounded-xl bg-sunken" />
          </div>
        ) : assessment.isError && !notFound ? (
          <div className="rounded-xl border border-status-fail/40 bg-status-fail/10 px-4 py-3 text-caption" data-testid="assessment-error">
            <p className="text-ink-primary">Couldn’t load the assessment.</p>
            <p className="mt-1 text-ink-secondary">{(assessment.error as Error).message}</p>
            <button
              type="button"
              onClick={() => void assessment.refetch()}
              className="mt-2 inline-flex items-center gap-1.5 rounded-md border border-line px-2.5 py-1 text-ink-secondary hover:border-line-strong hover:text-ink-primary"
            >
              <RefreshCw size={12} aria-hidden="true" />
              Try again
            </button>
          </div>
        ) : card ? (
          <>
            <section className="rounded-2xl border border-line-subtle bg-raised p-5" aria-label="Assessment summary">
              {renderArtifact(card, { size: 'pane' })}
            </section>
            {runCard && (
              <section className="rounded-2xl border border-line-subtle bg-raised p-5" aria-label="Assessment run">
                {renderArtifact(runCard, { size: 'pane' })}
              </section>
            )}
            {assessment.data?.section?.renderedMarkdown ? (
              <section className="rounded-2xl border border-line-subtle bg-raised p-6" aria-label="Assessment report" data-testid="assessment-report">
                <Markdown>{assessment.data.section.renderedMarkdown}</Markdown>
              </section>
            ) : (
              <p className="text-caption text-ink-tertiary">The written report has not been rendered yet.</p>
            )}
          </>
        ) : (
          <section
            className="flex flex-col items-start gap-4 rounded-2xl border border-dashed border-line bg-raised/40 px-6 py-8"
            data-testid="assessment-empty"
          >
            <div className="flex items-center gap-3">
              <AgentAvatar agent="discovery" size="lg" working={!!run} />
              <div>
                <p className="text-body font-medium text-ink-primary">No assessment yet for {name}.</p>
                <p className="text-caption text-ink-secondary">
                  The 10-minute Assessment reads the inventory, dependency graph, survey digests and patterns, then
                  writes an effort and risk view with a recommended conversion mode.
                </p>
              </div>
            </div>

            {runCard ? (
              <div className="w-full rounded-xl border border-line-subtle bg-raised p-4" data-testid="assessment-run">
                {renderArtifact(runCard, { size: 'pane' })}
              </div>
            ) : canRun ? (
              <button
                type="button"
                onClick={() => void start()}
                disabled={starting}
                data-testid="assessment-run-button"
                className="inline-flex items-center gap-2 rounded-lg bg-volt px-4 py-2 text-body font-semibold text-on-volt transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt focus-visible:ring-offset-2 focus-visible:ring-offset-canvas disabled:cursor-not-allowed disabled:opacity-50"
              >
                <Play size={15} aria-hidden="true" />
                {starting ? 'Starting…' : 'Run assessment'}
              </button>
            ) : (
              <p className="text-caption text-ink-tertiary" data-testid="assessment-run-blocked">
                Running the assessment is the <strong className="text-ink-secondary">Admin</strong>’s step — switch
                persona in the menu at the top right, or ask Astra in the programme thread.
              </p>
            )}
            {startError && <p className="text-caption text-status-fail">{startError}</p>}
          </section>
        )}
      </main>
    </div>
  );
}
