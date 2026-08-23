import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { ArrowRight, AlertTriangle, X, Sparkles } from 'lucide-react';
import { api } from '@/lib/api';
import {
  applyPatch,
  streamExtraction,
  type ExtractionEvent,
  type ProviderInfo,
  type DoneEvent,
} from '@/lib/extractionStream';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { ProviderStrip } from '@/components/ProviderStrip';
import { StageIndicator } from '@/components/StageIndicator';
import { MonacoSource, type Citation } from '@/components/MonacoSource';
import {
  StreamingSpecPanel,
  type DraftSpec,
} from '@/components/StreamingSpecPanel';

type Status = 'idle' | 'streaming' | 'success' | 'error';

export function LiveExtractionPage() {
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

  const [status, setStatus] = useState<Status>('idle');
  const [stage, setStage] = useState<string | null>(null);
  const [provider, setProvider] = useState<ProviderInfo | null>(null);
  const [draft, setDraft] = useState<DraftSpec>({});
  const [done, setDone] = useState<DoneEvent | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [warnings, setWarnings] = useState<{ code: string; message: string }[]>([]);
  const [activeCitation, setActiveCitation] = useState<string | null>(null);
  const [activeLine, setActiveLine] = useState<number | undefined>(undefined);
  // Live token preview during real-provider streaming. The mock provider
  // never emits `token` events; the real Anthropic adapter emits one per
  // content_block_delta. Tracking these here gives the user a visible
  // "Claude is writing" feel while the JSON buffer fills (since patches
  // only land at the very end of a real call).
  const [streamedText, setStreamedText] = useState('');
  const [tokenChunkCount, setTokenChunkCount] = useState(0);
  const abortRef = useRef<AbortController | null>(null);
  const streamPreviewRef = useRef<HTMLPreElement | null>(null);
  // StrictMode dev double-mounts: without this guard, two extract POSTs fire
  // and the first one's abort/revert clobbers the second's state, leaving
  // the spec stuck at PARSED with no draft. The ref is per-page-load so a
  // navigation back to this route correctly re-runs the effect.
  const extractionStartedRef = useRef(false);

  useEffect(() => {
    if (!id || !sub.data || !source.data) return;
    // StrictMode dev double-mount: the cleanup-then-remount sequence can
    // race two extract POSTs against each other server-side; the first
    // request's abort triggers a state revert that clobbers the second
    // request's progress. Gate with a ref so only the first effect actually
    // initiates the extraction.
    if (extractionStartedRef.current) return;
    extractionStartedRef.current = true;

    const controller = new AbortController();
    abortRef.current = controller;
    setStatus('streaming');
    const draftDoc: Record<string, unknown> = {};

    const onEvent = (evt: ExtractionEvent) => {
      switch (evt.type) {
        case 'provider_info':
          setProvider(evt.data);
          break;
        case 'stage':
          setStage(evt.data.stage);
          break;
        case 'patch':
          applyPatch(draftDoc, evt.data);
          setDraft({ ...draftDoc } as DraftSpec);
          break;
        case 'token': {
          // Real providers stream text chunks before the JSON parses into
          // patches. Surface them so the user has a visible "alive" signal
          // during the 30-60s LLM time. Mock provider never emits these,
          // so its UX (patch-by-patch) is unchanged.
          const chunk = evt.data.text ?? '';
          if (chunk) {
            setStreamedText((prev) => prev + chunk);
            setTokenChunkCount((n) => n + 1);
          }
          break;
        }
        case 'citation_pulse': {
          setActiveCitation(evt.data.lines);
          const start = parseRangeStart(evt.data.lines);
          if (start) setActiveLine(start);
          // Hold the active state for 1.8s to match the CSS animation
          // duration in index.css. After it expires the base styles take
          // over (still highlighted, just not pulsing).
          window.setTimeout(() => {
            setActiveCitation((cur) => (cur === evt.data.lines ? null : cur));
          }, 1800);
          break;
        }
        case 'warning':
          setWarnings((w) => [...w, evt.data]);
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

    streamExtraction(id, controller.signal, onEvent).catch((err) => {
      if (controller.signal.aborted) return;
      setErrorMessage(err instanceof Error ? err.message : String(err));
      setStatus('error');
      setStage(null);
    });

    // Deliberately do NOT abort on effect cleanup. StrictMode dev mounts the
    // effect twice; aborting on the first cleanup killed the only in-flight
    // request. The user-initiated Cancel button still calls abortRef.abort()
    // directly. Server-side BestEffortRevertExtractingAsync handles any real
    // client disconnect (navigation away) on its own.
  }, [id, sub.data, source.data]);

  const citations: Citation[] = useMemo(() => {
    const all: Citation[] = [];
    const collect = (lines: string, tone?: 'accent') => {
      const r = parseRange(lines);
      if (r) all.push({ lineStart: r[0], lineEnd: r[1], tone });
    };
    draft.invariants?.forEach((i) =>
      i.citations?.forEach((c) => collect(c.lines, activeCitation === c.lines ? 'accent' : undefined)),
    );
    draft.side_effects?.forEach((s) =>
      s.citations?.forEach((c) => collect(c.lines, activeCitation === c.lines ? 'accent' : undefined)),
    );
    draft.edge_cases?.forEach((e) =>
      e.citations?.forEach((c) => collect(c.lines, activeCitation === c.lines ? 'accent' : undefined)),
    );
    return all;
  }, [draft, activeCitation]);

  if (sub.isPending || source.isPending) {
    return (
      <div className="mx-auto max-w-[1600px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-12 w-96" />
        <Skeleton className="h-[600px] w-full" />
      </div>
    );
  }
  if (sub.isError || source.isError) {
    return (
      <div className="mx-auto max-w-[1400px] p-6 lg:p-10">
        <ErrorBlock title="Could not load routine" message={String(sub.error ?? source.error)} />
      </div>
    );
  }

  const s = sub.data;

  const onCancel = () => {
    abortRef.current?.abort();
    navigate(`/subroutines/${s.id}`);
  };

  const onCite = (lines: string) => {
    setActiveCitation(lines);
    const start = parseRangeStart(lines);
    if (start) setActiveLine(start);
  };

  return (
    <div className="flex h-[calc(100vh-110px)] flex-col">
      {/* Header */}
      <header className="border-b border-border-subtle bg-raised px-6 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <span className="flex h-8 w-8 items-center justify-center rounded-md bg-accent-muted text-accent">
              <Sparkles className="h-4 w-4" aria-hidden="true" />
            </span>
            <div>
              <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
                Extraction in progress
              </p>
              <h1 className="font-mono text-h-md font-semibold text-ink-primary">
                {s.name}
              </h1>
            </div>
          </div>
          <div className="flex items-center gap-3">
            <StageIndicator currentStage={stage} />
            {/* Screen-reader announcement of stage/status changes — the visual
                StageIndicator alone gives no signal to assistive tech. */}
            <p className="sr-only" role="status" aria-live="polite">
              {status === 'success'
                ? 'Extraction complete.'
                : status === 'error'
                  ? 'Extraction failed.'
                  : stage
                    ? `Extraction stage: ${stage}.`
                    : ''}
            </p>
            {status === 'streaming' && (
              <Button variant="secondary" size="sm" onClick={onCancel}>
                <X className="h-4 w-4" />
                Cancel
              </Button>
            )}
            {status === 'success' && (
              <Button
                variant="primary"
                size="sm"
                onClick={() => navigate(`/subroutines/${s.id}/spec`)}
              >
                View draft spec
                <ArrowRight className="h-4 w-4" />
              </Button>
            )}
          </div>
        </div>
      </header>

      <ProviderStrip
        info={provider}
        latencyMs={done?.latencyMs}
        tokens={done ? { in: done.inputTokens, out: done.outputTokens } : undefined}
      />

      {/* Live token feed — visible only during a real-provider stream where
          patches are buffered until the JSON parses at end-of-stream. Hidden
          on the mock provider (which emits zero `token` events) and after
          the run finishes so the structured spec is the focus. */}
      {status === 'streaming' && tokenChunkCount > 0 && (
        <div className="border-b border-border-subtle bg-sunken px-6 py-2">
          <div className="flex items-center justify-between gap-3 text-caption text-ink-secondary">
            <span className="flex items-center gap-2">
              <span className="inline-block h-2 w-2 rounded-full bg-accent motion-safe:animate-pulse" aria-hidden="true" />
              <span className="font-mono uppercase tracking-wider">Claude is writing</span>
            </span>
            <span className="font-mono">
              {tokenChunkCount.toLocaleString()} chunks · {streamedText.length.toLocaleString()} chars
            </span>
          </div>
          <pre
            ref={(el) => {
              streamPreviewRef.current = el;
              if (el) el.scrollTop = el.scrollHeight;
            }}
            className="mt-2 max-h-24 overflow-y-auto whitespace-pre-wrap break-words rounded-sm bg-canvas px-3 py-2 font-mono text-caption text-ink-secondary"
            aria-live="polite"
            data-testid="claude-stream-preview"
          >
            {streamedText.length > 1200 ? '…' + streamedText.slice(-1200) : streamedText}
          </pre>
        </div>
      )}

      {/* Body — two-pane */}
      <div className="grid flex-1 min-h-0 grid-cols-1 divide-y divide-border-subtle overflow-y-auto bg-canvas lg:grid-cols-[minmax(0,1fr)_minmax(0,560px)] lg:divide-x lg:divide-y-0 lg:overflow-visible">
        {/* Streaming spec */}
        <div className="h-[420px] min-h-0 overflow-y-auto bg-raised lg:h-auto">
          {status === 'idle' && (
            <div className="flex h-full items-center justify-center text-ink-tertiary">
              <p className="font-mono text-caption">Connecting to provider…</p>
            </div>
          )}
          {status !== 'idle' && (
            <>
              {warnings.length > 0 && (
                <div className="border-b border-status-scaffolded/40 bg-[#FBF1D9] px-6 py-3">
                  {warnings.map((w, i) => (
                    <p key={i} className="flex items-start gap-2 text-caption text-status-scaffolded">
                      <AlertTriangle className="mt-0.5 h-3.5 w-3.5" aria-hidden="true" />
                      <span className="font-mono uppercase">{w.code}</span>
                      <span className="text-ink-primary">{w.message}</span>
                    </p>
                  ))}
                </div>
              )}
              {status === 'error' && errorMessage && (
                <div className="p-6">
                  <ErrorBlock title="Extraction failed" message={errorMessage} />
                </div>
              )}
              <StreamingSpecPanel
                draft={draft}
                onCitationClick={onCite}
                activeCitation={activeCitation}
              />
            </>
          )}
        </div>

        {/* Source pane */}
        <div className="h-[420px] min-h-0 flex flex-col bg-canvas lg:h-auto">
          <div className="shrink-0 flex items-center justify-between border-b border-border-subtle bg-raised px-4 py-2 font-mono text-caption text-ink-secondary">
            <span>{s.file.relativePath}</span>
            <span className="text-ink-tertiary">{source.data!.lineCount} lines</span>
          </div>
          <div className="flex-1 min-h-0">
            <MonacoSource
              value={source.data!.content}
              height="100%"
              citations={citations}
              highlightLine={activeLine}
            />
          </div>
        </div>
      </div>
    </div>
  );
}

function parseRange(s: string): [number, number] | null {
  const m = /^(\d+)\s*[-–]\s*(\d+)$/.exec(s.trim());
  if (m) return [Number(m[1]), Number(m[2])];
  const single = /^(\d+)/.exec(s.trim());
  if (single) return [Number(single[1]), Number(single[1])];
  return null;
}

function parseRangeStart(s: string): number | null {
  const r = parseRange(s);
  return r ? r[0] : null;
}
