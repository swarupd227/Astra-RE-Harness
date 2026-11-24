import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ExternalLink, Loader2, MonitorPlay, Square } from 'lucide-react';
import { api, getPersona, type PreviewInfo } from '@/lib/api';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Live preview of a generated React + Python package: the platform builds it and serves it in its own short-lived
 * container, so the working app can be shown next to its code. Renders nothing for other stacks or when previews
 * are not switched on. `PreviewButton` starts/stops; `PreviewPanel` shows progress and then the running app.
 */
export function usePreview(scaffoldId: string, targetPlatform: string) {
  const qc = useQueryClient();
  const eligible = targetPlatform.toLowerCase() === 'react-python';
  const enabled = useQuery({ queryKey: ['preview-enabled'], queryFn: api.previewEnabled, enabled: eligible, staleTime: 60_000 });
  const available = eligible && enabled.data?.enabled === true;

  const preview = useQuery({
    queryKey: ['preview', scaffoldId],
    queryFn: () => api.getPreview(scaffoldId),
    enabled: available,
    refetchInterval: (q) => {
      const d = q.state.data as PreviewInfo | null | undefined;
      return d && d.state === 'STARTING' ? 5000 : false;
    },
  });
  const start = useMutation({
    mutationFn: () => api.startPreview(scaffoldId),
    onSuccess: (d) => qc.setQueryData(['preview', scaffoldId], d),
  });
  const stop = useMutation({
    mutationFn: () => api.stopPreview(scaffoldId),
    onSuccess: () => qc.setQueryData(['preview', scaffoldId], null),
  });
  return { available, preview, start, stop };
}

type PreviewState = ReturnType<typeof usePreview>;

export function PreviewButton({ state }: { state: PreviewState }) {
  if (!state.available || getPersona() === 'observer') return null;
  const live = state.preview.data;
  const busy = state.start.isPending || (live?.state === 'STARTING');
  return (
    <Button
      variant="secondary"
      size="md"
      onClick={() => (live ? state.stop.mutate() : state.start.mutate())}
      loading={busy || state.stop.isPending}
      data-testid="preview-toggle"
    >
      {live ? <Square className="h-4 w-4" aria-hidden="true" /> : <MonitorPlay className="h-4 w-4" aria-hidden="true" />}
      {live ? 'Stop preview' : 'Preview app'}
    </Button>
  );
}

export function PreviewPanel({ state }: { state: PreviewState }) {
  if (!state.available) return null;
  const live = state.preview.data;
  const err = state.start.error ?? state.stop.error ?? state.preview.error;
  if (!live && !err) return null;

  return (
    <section className="border-b border-line-subtle bg-raised px-6 py-3" data-testid="preview-panel">
      {err && <ErrorBlock title="Preview" message={err instanceof Error ? err.message : String(err)} />}
      {live && live.state !== 'READY' && (
        <p className="flex items-center gap-2 text-body text-ink-secondary" data-testid="preview-progress">
          <Loader2 className={`h-4 w-4 ${live.state === 'FAILED' ? '' : 'animate-spin'}`} aria-hidden="true" />
          {live.state === 'FAILED'
            ? `The preview could not start. ${live.detail ?? ''}`
            : `${live.detail ?? 'Starting the preview'}. This takes about three minutes the first time.`}
        </p>
      )}
      {live && live.state === 'READY' && live.url && (
        <div className="space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-body text-ink-secondary">
              The generated app, running.
              {live.expiresAt && ` It closes itself at ${new Date(live.expiresAt).toLocaleTimeString()}.`}
            </p>
            <a
              href={live.url}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1.5 rounded-md border border-line-subtle bg-canvas px-3 py-1.5 font-mono text-caption text-ink-secondary hover:bg-sunken hover:text-ink-primary"
              data-testid="preview-open"
            >
              <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
              Open in a new tab
            </a>
          </div>
          <iframe
            src={live.url}
            title="Preview of the generated app"
            className="h-[460px] w-full rounded-md border border-line-subtle bg-sunken"
            data-testid="preview-frame"
          />
        </div>
      )}
    </section>
  );
}
