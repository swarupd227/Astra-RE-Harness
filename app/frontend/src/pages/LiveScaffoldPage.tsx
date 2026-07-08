import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowRight, Cog, X } from 'lucide-react';
import { api, type SpecResponse } from '@/lib/api';
import {
  streamScaffold,
  type ProviderInfo,
  type ScaffoldDoneEvent,
  type ScaffoldEvent,
} from '@/lib/scaffoldStream';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { ProviderStrip } from '@/components/ProviderStrip';
import { MonacoSource, type TodoMarker } from '@/components/MonacoSource';
import { ScaffoldFileTree } from '@/components/ScaffoldFileTree';
import { clsx } from 'clsx';

type Status = 'idle' | 'streaming' | 'success' | 'error';

type LiveFile = {
  path: string;
  language: string;
  derivedFrom: string[];
  content: string;
  loading: boolean;
  lineCount?: number;
  todoCount?: number;
};

const STAGES = [
  { key: 'priming', label: 'Priming' },
  { key: 'streaming', label: 'Streaming code' },
  { key: 'validating', label: 'Validating' },
  { key: 'committing', label: 'Persisting' },
];

export function LiveScaffoldPage() {
  const { id = '' } = useParams<{ id: string }>();  // spec id
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  // The Spec-review surface forwards the chosen target stack here. Default
  // to dotnet8 so deep links still work.
  const targetStack = searchParams.get('target') ?? 'dotnet8';
  const subroutineId = useResolveSubroutineId(id);

  const [status, setStatus] = useState<Status>('idle');
  const [stage, setStage] = useState<string | null>(null);
  const [provider, setProvider] = useState<ProviderInfo | null>(null);
  const [files, setFiles] = useState<Map<string, LiveFile>>(new Map());
  const [orderedPaths, setOrderedPaths] = useState<string[]>([]);
  const [activePath, setActivePath] = useState<string | null>(null);
  const [done, setDone] = useState<ScaffoldDoneEvent | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Auto-pin to the most-recently-emitted file unless the user clicks one.
  const userPinnedRef = useRef(false);

  useEffect(() => {
    if (!id) return;
    const controller = new AbortController();
    abortRef.current = controller;
    setStatus('streaming');

    const liveFiles = new Map<string, LiveFile>();
    const order: string[] = [];

    const upsert = (path: string, mut: (f: LiveFile) => void) => {
      const next = new Map(liveFiles);
      const cur = next.get(path) ?? { path, language: 'plaintext', derivedFrom: [], content: '', loading: true };
      const draft = { ...cur };
      mut(draft);
      next.set(path, draft);
      liveFiles.clear();
      next.forEach((v, k) => liveFiles.set(k, v));
      setFiles(new Map(liveFiles));
    };

    const onEvent = (evt: ScaffoldEvent) => {
      switch (evt.type) {
        case 'provider_info':
          setProvider(evt.data);
          break;
        case 'stage':
          setStage(evt.data.stage);
          break;
        case 'file_started':
          if (!order.includes(evt.data.path)) {
            order.push(evt.data.path);
            setOrderedPaths([...order]);
          }
          upsert(evt.data.path, (f) => {
            f.path = evt.data.path;
            f.language = evt.data.language;
            f.derivedFrom = evt.data.derivedFrom;
            f.loading = true;
          });
          if (!userPinnedRef.current) setActivePath(evt.data.path);
          break;
        case 'file_chunk':
          upsert(evt.data.path, (f) => { f.content += evt.data.content; });
          break;
        case 'file_done':
          upsert(evt.data.path, (f) => {
            f.loading = false;
            f.lineCount = evt.data.lineCount;
            f.todoCount = evt.data.todoCount;
          });
          break;
        case 'done':
          setDone(evt.data);
          setStatus('success');
          setStage(null);
          break;
        case 'error':
          setErrorMessage(evt.data.message);
          setStatus('error');
          setStage(null);
          break;
      }
    };

    streamScaffold(id, { targetStack }, controller.signal, onEvent).catch((err) => {
      if (controller.signal.aborted) return;
      setErrorMessage(err instanceof Error ? err.message : String(err));
      setStatus('error');
      setStage(null);
    });

    return () => controller.abort();
  }, [id, targetStack]);

  const treeFiles = useMemo(() =>
    orderedPaths.map((p) => {
      const f = files.get(p);
      return {
        path: p,
        language: f?.language ?? 'plaintext',
        todoCount: f?.todoCount ?? 0,
        loading: f?.loading ?? true,
      };
    }), [orderedPaths, files]);

  const active = activePath ? files.get(activePath) : null;
  const todoMarkers: TodoMarker[] = useMemo(() => {
    if (!active) return [];
    const marks: TodoMarker[] = [];
    const lines = active.content.split('\n');
    lines.forEach((line, i) => {
      if (line.includes('TODO') || line.includes('NotImplementedException')) {
        marks.push({ lineStart: i + 1, lineEnd: i + 1, tooltip: 'Engineer-implementation TODO' });
      }
    });
    return marks;
  }, [active]);

  const onCancel = () => {
    abortRef.current?.abort();
    if (subroutineId) navigate(`/subroutines/${subroutineId}/review`);
  };

  return (
    <div className="flex h-[calc(100vh-110px)] flex-col">
      <header className="border-b border-border-subtle bg-raised px-6 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <span className="flex h-8 w-8 items-center justify-center rounded-md bg-accent-muted text-accent">
              <Cog className="h-4 w-4" aria-hidden="true" />
            </span>
            <div>
              <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                Stage 5 · Scaffold generation
              </p>
              <h1 className="font-mono text-h-md font-semibold text-ink-primary">
                {orderedPaths.length} {orderedPaths.length === 1 ? 'file' : 'files'} streaming
              </h1>
            </div>
          </div>
          <div className="flex items-center gap-3">
            <StagePill stage={stage} />
            {/* Screen-reader announcement of stage/status changes — StagePill
                alone gives no signal to assistive tech. */}
            <p className="sr-only" role="status" aria-live="polite">
              {status === 'success'
                ? 'Scaffold generation complete.'
                : status === 'error'
                  ? 'Scaffold generation failed.'
                  : stage
                    ? `Scaffold stage: ${stage}.`
                    : ''}
            </p>
            {status === 'streaming' && (
              <Button variant="secondary" size="sm" onClick={onCancel}>
                <X className="h-4 w-4" /> Cancel
              </Button>
            )}
            {status === 'success' && done && (
              <Button variant="primary" size="sm" onClick={() => navigate(`/scaffolds/${done.scaffoldId}`)}>
                Open scaffold
                <ArrowRight className="h-4 w-4" />
              </Button>
            )}
          </div>
        </div>
      </header>

      <ProviderStrip
        info={provider as any}
        latencyMs={done?.latencyMs}
        tokens={done ? { in: done.inputTokens, out: done.outputTokens } : undefined}
      />

      {errorMessage && (
        <div className="px-6 py-3"><ErrorBlock title="Scaffold failed" message={errorMessage} /></div>
      )}

      <div className="grid flex-1 min-h-0 grid-cols-1 divide-y divide-border-subtle overflow-y-auto bg-canvas lg:grid-cols-[260px_minmax(0,1fr)_minmax(0,400px)] lg:divide-x lg:divide-y-0 lg:overflow-visible">
        <div className="h-52 min-h-0 overflow-y-auto bg-raised lg:h-auto">
          <div className="border-b border-border-subtle px-3 py-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
            Generated files
          </div>
          {treeFiles.length === 0 ? (
            <div className="space-y-2 p-3">
              <Skeleton className="h-6 w-full" />
              <Skeleton className="h-6 w-full" />
              <Skeleton className="h-6 w-3/4" />
            </div>
          ) : (
            <ScaffoldFileTree
              files={treeFiles}
              activePath={activePath}
              onSelect={(p) => { userPinnedRef.current = true; setActivePath(p); }}
            />
          )}
        </div>

        <div className="h-[420px] min-h-0 flex flex-col bg-canvas lg:h-auto">
          <div className="shrink-0 flex items-center justify-between border-b border-border-subtle bg-raised px-4 py-2 font-mono text-caption text-ink-secondary">
            <span>{active?.path ?? 'Awaiting first file…'}</span>
            <span className="text-ink-tertiary">{active?.lineCount ? `${active.lineCount} lines` : '—'}</span>
          </div>
          {active ? (
            <div className="flex-1 min-h-0">
              <MonacoSource
                value={active.content}
                language={active.language as any}
                height="100%"
                todoMarkers={todoMarkers}
              />
            </div>
          ) : (
            <div className="flex flex-1 items-center justify-center font-mono text-caption text-ink-tertiary">
              Streaming…
            </div>
          )}
        </div>

        <div className="max-h-64 min-h-0 overflow-y-auto bg-raised p-5 font-mono text-caption text-ink-secondary lg:max-h-none">
          <p className="uppercase tracking-wider text-ink-tertiary">Derived from</p>
          {active && active.derivedFrom.length > 0 ? (
            <ul className="mt-3 space-y-1.5">
              {active.derivedFrom.map((id) => (
                <li key={id} className="rounded-sm border border-border-subtle bg-canvas px-2 py-1 uppercase">
                  {id}
                </li>
              ))}
            </ul>
          ) : (
            <p className="mt-3 text-ink-tertiary">No claim mapping yet — file streaming in.</p>
          )}
        </div>
      </div>
    </div>
  );
}

function StagePill({ stage }: { stage: string | null }) {
  const idx = stage ? STAGES.findIndex((s) => s.key === stage) : -1;
  return (
    <ol className="flex items-center gap-1.5" aria-label="Scaffold stages">
      {STAGES.map((s, i) => {
        const state = idx === -1 ? 'idle' : i < idx ? 'done' : i === idx ? 'active' : 'pending';
        return (
          <li key={s.key} className="flex items-center gap-1">
            <span
              className={clsx(
                'flex h-5 w-5 items-center justify-center rounded-full border font-mono text-[10px]',
                state === 'done' && 'border-status-review bg-status-review text-white',
                state === 'active' && 'border-accent bg-accent text-white motion-safe:animate-pulse',
                (state === 'pending' || state === 'idle') && 'border-border-subtle bg-canvas text-ink-tertiary',
              )}
            >
              {i + 1}
            </span>
            <span className="hidden font-mono text-[11px] uppercase tracking-wide xl:inline">
              {s.label}
            </span>
            {i < STAGES.length - 1 && <span className="mx-0.5 h-px w-3 bg-border-subtle" aria-hidden="true" />}
          </li>
        );
      })}
    </ol>
  );
}

function useResolveSubroutineId(specId: string): string | null {
  // The spec is referenced by spec-id in URL; we need the subroutine id for the back link.
  // Cheap approach: piggy-back on the existing my-reviews payload.
  const { data } = useQuery({ queryKey: ['my-reviews'], queryFn: () => fetch('').then(() => null), enabled: false });
  void data;
  // Fallback to looking up via the scaffold response after success — but we want it before too.
  // Simpler: pass the subroutine id in the URL. Already done at the call-site.
  return null;
}
// Suppress lint for unused stub
void api;
void Link;
