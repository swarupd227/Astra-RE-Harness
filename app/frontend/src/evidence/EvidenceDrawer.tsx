import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ChevronLeft, ChevronRight, Layers, Rows3, ScrollText, X } from 'lucide-react';
import { clsx } from 'clsx';
import { ApiError, api } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { EvidenceBody } from '@/evidence/evidenceViews';
import { KindIcon, kindOfShortId } from '@/evidence/kinds';

export type EvidenceRequest = {
  corpusId: string;
  /** Everything the statement cites; more than one opens the stacked view. */
  ids: string[];
  /** The one the reader clicked. */
  focus?: string;
};

/**
 * The panel a citation opens. One id shows that piece of evidence; several show everything the
 * statement rests on together (a transcript line, the ticket, the SQL) so the reader can see the
 * sources agree, or where they do not. It floats over the page rather than pushing it, so the
 * statement stays in view, and it never takes the page's scroll.
 */
export function EvidenceDrawer({ request, onClose }: { request: EvidenceRequest; onClose: () => void }) {
  const ids = useMemo(() => [...new Set(request.ids)], [request.ids]);
  const multi = ids.length > 1;
  const [mode, setMode] = useState<'stack' | 'one'>(multi ? 'stack' : 'one');
  const [current, setCurrent] = useState(request.focus && ids.includes(request.focus) ? request.focus : ids[0]!);
  const closeButton = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    closeButton.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const shown = mode === 'stack' ? ids : [current];

  return (
    <>
      {/* On narrow screens the panel covers the page, so a tap outside closes it. */}
      <div className="fixed inset-0 z-[59] bg-black/50 lg:hidden" onClick={onClose} aria-hidden="true" />
      <aside
        role="dialog"
        aria-label="Evidence"
        data-testid="evidence-drawer"
        className="fixed inset-y-0 right-0 z-[60] flex w-full max-w-[36rem] flex-col border-l border-line bg-canvas text-ink-primary shadow-xl"
      >
        <header className="flex h-12 shrink-0 items-center gap-2 border-b border-line-subtle bg-raised px-3">
          <ScrollText className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
          <h2 className="text-body font-medium">Evidence</h2>
          <span className="text-caption text-ink-tertiary" data-testid="evidence-drawer-count">
            {multi ? `${ids.length} sources` : '1 source'}
          </span>
          <div className="flex-1" />
          {multi && (
            <div className="flex items-center rounded-md border border-line-subtle p-0.5" role="group" aria-label="Layout">
              <ModeButton active={mode === 'stack'} onClick={() => setMode('stack')} testid="evidence-mode-stack">
                <Layers className="h-3.5 w-3.5" aria-hidden="true" />
                All together
              </ModeButton>
              <ModeButton active={mode === 'one'} onClick={() => setMode('one')} testid="evidence-mode-one">
                <Rows3 className="h-3.5 w-3.5" aria-hidden="true" />
                One at a time
              </ModeButton>
            </div>
          )}
          <button
            ref={closeButton}
            type="button"
            onClick={onClose}
            aria-label="Close evidence"
            className="inline-flex h-8 w-8 items-center justify-center rounded-md text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            data-testid="evidence-drawer-close"
          >
            <X className="h-4 w-4" aria-hidden="true" />
          </button>
        </header>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-4" data-testid="evidence-drawer-body">
          {multi && mode === 'one' && (
            <div className="flex flex-wrap gap-1.5" role="tablist" aria-label="Sources">
              {ids.map((id) => (
                <button
                  key={id}
                  type="button"
                  role="tab"
                  aria-selected={id === current}
                  onClick={() => setCurrent(id)}
                  className={clsx(
                    'inline-flex items-center gap-1 rounded-md border px-2 py-1 font-mono text-micro focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
                    id === current ? 'border-volt text-ink-primary' : 'border-line-subtle text-ink-secondary hover:border-line',
                  )}
                >
                  <KindIcon kind={kindOfShortId(id)} />
                  {id}
                </button>
              ))}
            </div>
          )}
          {shown.map((id) => (
            <EvidenceCard
              key={id}
              corpusId={request.corpusId}
              shortId={id}
              onNavigate={mode === 'one' ? setCurrent : undefined}
            />
          ))}
        </div>
      </aside>
    </>
  );
}

function ModeButton({
  active,
  onClick,
  testid,
  children,
}: {
  active: boolean;
  onClick: () => void;
  testid: string;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      data-testid={testid}
      className={clsx(
        'inline-flex h-7 items-center gap-1 rounded px-2 text-micro focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
        active ? 'bg-sunken text-ink-primary' : 'text-ink-tertiary hover:text-ink-primary',
      )}
    >
      {children}
    </button>
  );
}

function EvidenceCard({
  corpusId,
  shortId,
  onNavigate,
}: {
  corpusId: string;
  shortId: string;
  /** Present in one-at-a-time mode: lets the reader step to the neighbouring chunk in the same file. */
  onNavigate?: (id: string) => void;
}) {
  const detail = useQuery({
    queryKey: ['evidence', corpusId, shortId],
    queryFn: () => api.getEvidence(corpusId, shortId),
    staleTime: 60_000,
    retry: (count, err) => !(err instanceof ApiError && (err.status === 404 || err.status === 400)) && count < 2,
  });

  if (detail.isPending) return <Skeleton className="h-48 w-full" />;

  if (detail.isError) {
    const missing = detail.error instanceof ApiError && detail.error.status === 404;
    return missing ? (
      <section
        className="rounded-lg border border-status-warn/40 bg-status-warn/10 p-4"
        data-testid="evidence-unresolved"
        data-short-id={shortId}
      >
        <p className="font-mono text-caption text-ink-primary">{shortId}</p>
        <p className="mt-1 text-body text-ink-primary">This citation cannot be resolved.</p>
        <p className="mt-1 text-caption text-ink-secondary">{detail.error.message}</p>
      </section>
    ) : (
      <ErrorBlock title={`Could not load ${shortId}`} message={detail.error.message} onRetry={() => void detail.refetch()} />
    );
  }

  const d = detail.data;
  return (
    <section
      className="rounded-lg border border-line-subtle bg-raised"
      data-testid="evidence-card"
      data-short-id={d.shortId}
      data-kind={d.kind}
    >
      <header className="flex items-start gap-3 border-b border-line-subtle px-4 py-3">
        <KindIcon kind={d.kind} className="mt-1 text-ink-secondary" />
        <div className="min-w-0 flex-1">
          <h3 className="truncate text-body font-medium text-ink-primary" title={d.label}>{d.label}</h3>
          <p className="mt-0.5 flex flex-wrap items-center gap-x-2 font-mono text-micro text-ink-tertiary">
            <span className="text-ink-secondary">{d.shortId}</span>
            <span className="min-w-0 truncate" title={d.path}>{d.path}</span>
            <span>{d.locator}</span>
          </p>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          <Badge tone="neutral">{d.kindLabel}</Badge>
          <Badge tone={d.trustTier === 'structured' ? 'info' : 'neutral'}>
            {d.trustTier === 'structured' ? 'Structured' : 'Narrative'}
          </Badge>
        </div>
      </header>
      <div className="p-4">
        <EvidenceBody detail={d} />
      </div>
      {onNavigate && (d.previous || d.next) && (
        <footer className="flex items-center justify-between gap-2 border-t border-line-subtle px-3 py-2">
          <Button variant="ghost" size="sm" disabled={!d.previous} onClick={() => d.previous && onNavigate(d.previous)} data-testid="evidence-previous">
            <ChevronLeft className="h-4 w-4" aria-hidden="true" />
            Previous in file
          </Button>
          <Button variant="ghost" size="sm" disabled={!d.next} onClick={() => d.next && onNavigate(d.next)} data-testid="evidence-next">
            Next in file
            <ChevronRight className="h-4 w-4" aria-hidden="true" />
          </Button>
        </footer>
      )}
    </section>
  );
}
