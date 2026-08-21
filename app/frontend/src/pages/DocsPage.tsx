import { useState, useEffect, useRef } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import {
  ArrowLeft, ChevronDown, ChevronRight, Download, FileText,
  Loader2, Play, ShieldCheck, XCircle, CheckCircle2,
} from 'lucide-react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { api, getPersona, API_BASE } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { MermaidBlock } from '@/components/MermaidBlock';

const KIND_ORDER = [
  'overview', 'module', 'routine-summary',
  'data-dictionary', 'glossary', 'interface', 'business-rules',
  'diagram', 'sequence-diagram', 'dependency-diagram',
  // Phase B - requirements pack (as-is behaviour)
  'capability-map', 'process-flow', 'functional-requirement', 'nfr',
];

const KIND_LABELS: Record<string, string> = {
  'overview':           'Overview',
  'module':             'Modules',
  'routine-summary':    'Routines',
  'data-dictionary':    'Data Dictionary',
  'glossary':           'Glossary',
  'interface':          'Interfaces',
  'business-rules':     'Business Rules',
  'diagram':            'Diagrams',
  'sequence-diagram':   'Sequence Diagrams',
  'dependency-diagram': 'Dependency Diagrams',
  'capability-map':        'Capability Map',
  'process-flow':          'Process Flows',
  'functional-requirement':'Functional Requirements',
  'nfr':                   'Non-Functional',
};

const REQUIREMENTS_STAGES = ['capability-map', 'process-flow', 'functional-requirement', 'nfr'];

const STATE_LABELS: Record<string, string> = {
  'SIGNED':    'Signed',
  'IN_REVIEW': 'In Review',
  'DRAFT':     'Draft',
  'REJECTED':  'Rejected',
};

type StateTone = 'draft' | 'review' | 'signed' | 'neutral' | 'failed';

function stateTone(state: string): StateTone {
  switch (state) {
    case 'SIGNED':    return 'signed';
    case 'IN_REVIEW': return 'review';
    case 'DRAFT':     return 'draft';
    case 'REJECTED':  return 'failed';
    default:          return 'neutral';
  }
}

function stateLabel(state: string) {
  return STATE_LABELS[state] ?? state;
}

function sectionLabel(s: {
  moduleName?: string;
  subroutineName?: string;
  subroutineId?: string;
  id: string;
}): string {
  return s.moduleName ?? s.subroutineName ?? (s.subroutineId ? s.subroutineId.slice(0, 8) : s.id.slice(0, 8));
}

export function DocsPage() {
  const { id = '' } = useParams();
  const qc = useQueryClient();
  const persona = getPersona();

  const [selectedKind, setSelectedKind] = useState<string>(KIND_ORDER[0]);
  const [selectedSectionId, setSelectedSectionId] = useState<string | null>(null);
  const [sectionPage, setSectionPage] = useState(1);
  const [exportLoading, setExportLoading] = useState<string | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [logLines, setLogLines] = useState<string[]>([]);
  const [logExpanded, setLogExpanded] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const logEndRef = useRef<HTMLDivElement>(null);

  const handleExport = async (format: string) => {
    if (exportLoading) return;
    setExportLoading(format);
    setExportError(null);
    try {
      const { blob, filename } = await api.exportDocs(id, format);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } catch (err) {
      setExportError((err as Error).message ?? 'Export failed');
    } finally {
      setExportLoading(null);
    }
  };

  const corpus = useQuery({
    queryKey: ['corpus', id],
    queryFn: () => api.getCorpus(id),
    enabled: !!id,
  });

  const summary = useQuery({
    queryKey: ['docs-summary', id],
    queryFn: () => api.getDocSummary(id),
    enabled: !!id,
  });

  const sections = useQuery({
    queryKey: ['doc-sections', id, selectedKind, sectionPage],
    queryFn: () => api.listDocSections(id, { kind: selectedKind, page: sectionPage, pageSize: 50 }),
    enabled: !!id && !!selectedKind,
  });

  const sectionDetail = useQuery({
    queryKey: ['doc-section', selectedSectionId],
    queryFn: () => api.getDocSection(selectedSectionId!),
    enabled: !!selectedSectionId,
  });

  const invalidateDocs = () => {
    qc.invalidateQueries({ queryKey: ['docs-summary', id] });
    qc.invalidateQueries({ queryKey: ['doc-sections', id, selectedKind] });
    if (selectedSectionId) qc.invalidateQueries({ queryKey: ['doc-section', selectedSectionId] });
  };

  const acceptMutation = useMutation({
    mutationFn: (sectionId: string) => api.acceptDocSection(sectionId),
    onSuccess: invalidateDocs,
  });

  const rejectMutation = useMutation({
    mutationFn: (sectionId: string) => api.rejectDocSection(sectionId),
    onSuccess: invalidateDocs,
  });

  const generateMutation = useMutation({
    mutationFn: (stages?: string[]) => api.generateDocs(id, { force: true, stages }),
    onSuccess: (result) => {
      setLogLines([]);
      setRunError(null);
      setLogExpanded(true);
      setActiveRunId(result.generationRunId);
    },
  });

  const runStatus = useQuery({
    queryKey: ['doc-run', activeRunId],
    queryFn: () => api.getDocRun(activeRunId!),
    enabled: !!activeRunId,
    refetchInterval: (query) => {
      const s = query.state.data?.state;
      return s === 'RUNNING' || s === 'PENDING' ? 4000 : false;
    },
  });

  // Clear run and collapse log when generation finishes
  useEffect(() => {
    const s = runStatus.data?.state;
    if (s === 'SUCCEEDED' || s === 'FAILED') {
      if (s === 'FAILED') setRunError(runStatus.data?.errorSummary ?? 'Generation failed.');
      qc.invalidateQueries({ queryKey: ['docs-summary', id] });
      qc.invalidateQueries({ queryKey: ['doc-sections', id] });
      setActiveRunId(null);
      setLogExpanded(false); // Collapse log panel once the run ends
    }
  }, [runStatus.data?.state, runStatus.data?.errorSummary, id, qc]);

  // SSE log stream
  useEffect(() => {
    if (!activeRunId) return;
    const es = new EventSource(`${API_BASE}/api/v1/docs/runs/${activeRunId}/logs`);
    es.onmessage = (e: MessageEvent) => {
      try {
        const { message } = JSON.parse(e.data) as { message: string };
        setLogLines(prev => [...prev, message]);
      } catch { /* ignore malformed events */ }
    };
    es.addEventListener('done', () => {
      es.close();
      qc.invalidateQueries({ queryKey: ['doc-run', activeRunId] });
    });
    es.onerror = () => { es.close(); };
    return () => { es.close(); };
  }, [activeRunId, qc]);

  // Auto-scroll log panel to bottom while expanded
  useEffect(() => {
    if (logExpanded) logEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [logLines, logExpanded]);

  // Aggregate summary counts by kind
  const kindCounts = (summary.data?.breakdown ?? []).reduce<
    Record<string, { total: number; signed: number; draft: number }>
  >((acc, row) => {
    if (!acc[row.kind]) acc[row.kind] = { total: 0, signed: 0, draft: 0 };
    acc[row.kind].total += row.count;
    if (row.state === 'SIGNED') acc[row.kind].signed += row.count;
    else if (row.state === 'DRAFT' || row.state === 'IN_REVIEW') acc[row.kind].draft += row.count;
    return acc;
  }, {});

  const kindsWithData = KIND_ORDER.filter(k => kindCounts[k]);

  // Global counts across all kinds (for header badges)
  const globalCounts = (summary.data?.breakdown ?? []).reduce<Record<string, number>>((acc, r) => {
    acc[r.state] = (acc[r.state] ?? 0) + r.count;
    return acc;
  }, {});

  if (corpus.isPending || summary.isPending) {
    return (
      <div className="mx-auto max-w-[1400px] space-y-4 p-6">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-[600px] w-full" />
      </div>
    );
  }
  if (corpus.isError) {
    return (
      <div className="mx-auto max-w-[1400px] p-6">
        <ErrorBlock title="Could not load project" message={corpus.error.message} />
      </div>
    );
  }

  const c = corpus.data;
  const detail = sectionDetail.data;
  const isDiagram =
    detail?.kind === 'diagram' || detail?.kind === 'sequence-diagram' || detail?.kind === 'dependency-diagram';
  const payload = detail?.payload as Record<string, string> | undefined;

  const isMutating = acceptMutation.isPending || rejectMutation.isPending;
  const mutationError = acceptMutation.error ?? rejectMutation.error;

  const isGenerating = generateMutation.isPending || !!activeRunId;
  const hasLog = logLines.length > 0;
  const logComplete = hasLog && !activeRunId;
  const logFailed = logComplete && !!runError;

  return (
    <div className="flex h-[calc(100vh-110px)] flex-col fadeup">

      {/* ── Header ─────────────────────────────────────────────── */}
      <header className="border-b border-border-subtle bg-raised px-6 py-3">
        <div className="flex items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <Link
              to={`/corpora/${id}`}
              className="rounded p-1 text-ink-tertiary transition-colors hover:text-ink-primary"
            >
              <ArrowLeft className="h-4 w-4" />
            </Link>
            <div>
              <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                Documentation
              </p>
              <h1 className="font-mono text-h-md font-semibold text-ink-primary">{c.name}</h1>
            </div>
          </div>

          <div className="flex items-center gap-2">
            {/* Global state summary badges */}
            <div className="flex items-center gap-1.5 mr-2">
              {Object.entries(globalCounts).map(([state, count]) => (
                <Badge key={state} tone={stateTone(state)}>
                  {count} {stateLabel(state)}
                </Badge>
              ))}
            </div>

            {persona === 'admin' && (
              <>
                <Button
                  variant="secondary"
                  onClick={() => generateMutation.mutate(undefined)}
                  disabled={isGenerating}
                  title={kindsWithData.length === 0 ? 'Generate documentation' : 'Regenerate documentation'}
                >
                  {isGenerating
                    ? <Loader2 className="h-4 w-4 animate-spin" />
                    : <Play className="h-4 w-4" />}
                  {kindsWithData.length === 0 ? 'Generate Docs' : 'Regenerate'}
                </Button>

                <Button
                  variant="secondary"
                  onClick={() => generateMutation.mutate(REQUIREMENTS_STAGES)}
                  disabled={isGenerating}
                  data-testid="generate-requirements"
                  title="Generates the as-is requirements pack: capability map, process flows, numbered functional requirements with traceability, and non-functional characteristics. Synthesised from the docs already generated."
                >
                  <FileText className="h-4 w-4" />
                  Requirements pack
                </Button>

                {/* Export dropdown */}
                <div className="relative">
                  <select
                    value=""
                    onChange={e => { if (e.target.value) handleExport(e.target.value); }}
                    disabled={!!exportLoading || kindsWithData.length === 0}
                    aria-label="Export documentation"
                    className="appearance-none cursor-pointer rounded border border-border-subtle bg-raised py-1.5 pl-8 pr-7 text-sm text-ink-secondary transition-colors hover:border-brand hover:text-ink-primary disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    <option value="">{exportLoading ? `Exporting ${exportLoading}…` : 'Export…'}</option>
                    <option value="mkdocs">MkDocs site (.zip)</option>
                    <option value="docx">Word document (.docx)</option>
                    <option value="pdf">PDF (.pdf)</option>
                    <option value="confluence">Confluence JSON (.json)</option>
                    <option value="requirements-docx">Requirements pack (.docx)</option>
                    <option value="requirements-pdf">Requirements pack (.pdf)</option>
                  </select>
                  <Download className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-ink-tertiary" />
                  <ChevronDown className="pointer-events-none absolute right-2 top-1/2 h-3 w-3 -translate-y-1/2 text-ink-tertiary" />
                </div>
              </>
            )}
          </div>
        </div>

        {/* Inline errors */}
        {exportError && (
          <p className="mt-1 text-xs text-rose-600">{exportError}</p>
        )}
        {generateMutation.error && (
          <p className="mt-1 text-xs text-rose-600">
            {(generateMutation.error as Error).message}
          </p>
        )}
      </header>

      {/* ── Run error banner ────────────────────────────────────── */}
      {runError && (
        <div className="flex items-start gap-3 border-b border-rose-500/30 bg-rose-500/10 px-6 py-2">
          <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-rose-500" />
          <span className="flex-1 text-sm text-rose-700 dark:text-rose-400">{runError}</span>
          <button
            onClick={() => setRunError(null)}
            className="shrink-0 text-xs text-rose-500 hover:text-rose-700"
          >
            Dismiss
          </button>
        </div>
      )}

      {/* ── SSE log strip ───────────────────────────────────────── */}
      {(isGenerating || hasLog) && (
        <div className="border-b border-border-subtle bg-slate-900">

          {/* Header row — toggle button + separate Clear button, never nested */}
          <div className="flex items-center gap-2 px-4 py-2">
            <button
              onClick={() => { if (hasLog) setLogExpanded(v => !v); }}
              disabled={!hasLog}
              className="flex min-w-0 flex-1 items-center gap-2 text-left disabled:cursor-default"
            >
              {isGenerating
                ? <Loader2 className="h-3 w-3 shrink-0 animate-spin text-emerald-400" />
                : logFailed
                  ? <XCircle className="h-3 w-3 shrink-0 text-rose-400" />
                  : <CheckCircle2 className="h-3 w-3 shrink-0 text-emerald-500" />}

              <span className="min-w-0 flex-1 truncate font-mono text-[11px] text-white/60">
                {isGenerating
                  ? (runStatus.data?.summary ?? 'Generating documentation…')
                  : logFailed
                    ? `Generation failed — ${logLines.length} events`
                    : `Generation complete — ${logLines.length} events`}
              </span>

              {hasLog && (
                <ChevronRight
                  className={`h-3 w-3 shrink-0 text-white/30 transition-transform duration-150 ${logExpanded ? 'rotate-90' : ''}`}
                />
              )}
            </button>

            {hasLog && (
              <button
                onClick={() => { setLogLines([]); setLogExpanded(false); }}
                className="rounded px-1.5 py-0.5 font-mono text-[10px] text-white/30 transition-colors hover:bg-white/10 hover:text-white/60"
              >
                Clear
              </button>
            )}
          </div>

          {/* Expanded log body */}
          {logExpanded && hasLog && (
            <div className="max-h-48 overflow-y-auto border-t border-white/5 px-4 pb-3 pt-2">
              {logLines.map((line, i) => (
                <div
                  key={i}
                  className={`whitespace-pre-wrap font-mono text-[11px] leading-relaxed ${
                    line.startsWith('✗') ? 'text-rose-400'    :
                    line.startsWith('✓') ? 'text-emerald-400' :
                    line.startsWith('▶') ? 'text-sky-400'     :
                    'text-white/60'
                  }`}
                >
                  {line}
                </div>
              ))}
              <div ref={logEndRef} />
            </div>
          )}
        </div>
      )}

      {/* ── Three-pane body ─────────────────────────────────────── */}
      <div className="grid min-h-0 flex-1 grid-cols-[220px_280px_minmax(0,1fr)] divide-x divide-border-subtle">

        {/* Pane 1 — Kind selector */}
        <nav className="overflow-y-auto bg-raised py-2">
          {kindsWithData.length === 0 && !isGenerating && (
            <div className="px-4 py-8 text-center">
              <FileText className="mx-auto mb-3 h-9 w-9 text-ink-tertiary opacity-25" />
              <p className="mb-4 text-sm text-ink-tertiary">No documentation yet.</p>
              {persona === 'admin' && (
                <Button
                  variant="primary"
                  onClick={() => generateMutation.mutate(undefined)}
                  disabled={generateMutation.isPending}
                >
                  {generateMutation.isPending
                    ? <Loader2 className="h-4 w-4 animate-spin" />
                    : <Play className="h-4 w-4" />}
                  Generate Docs
                </Button>
              )}
            </div>
          )}

          {kindsWithData.length === 0 && isGenerating && (
            <div className="px-4 py-8 text-center">
              <Loader2 className="mx-auto mb-3 h-8 w-8 animate-spin text-brand opacity-50" />
              <p className="text-sm text-ink-tertiary">Generating…</p>
            </div>
          )}

          {kindsWithData.map(kind => {
            const kc = kindCounts[kind];
            const isActive = selectedKind === kind;
            const signedPct = kc.total > 0 ? Math.round((kc.signed / kc.total) * 100) : 0;

            return (
              <button
                key={kind}
                onClick={() => {
                  setSelectedKind(kind);
                  setSectionPage(1);
                  setSelectedSectionId(null);
                }}
                className={`group w-full px-4 py-3 text-left transition-colors hover:bg-sunken ${
                  isActive ? 'bg-sunken' : ''
                }`}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className={`truncate text-sm ${
                    isActive ? 'font-semibold text-ink-primary' : 'text-ink-secondary'
                  }`}>
                    {KIND_LABELS[kind] ?? kind}
                  </span>
                  <span className="shrink-0 font-mono text-xs text-ink-tertiary">{kc.total}</span>
                </div>

                {/* Thin signed progress bar */}
                <div className="mt-1.5 h-1 w-full overflow-hidden rounded-full bg-border-subtle">
                  <div
                    className="h-full rounded-full bg-emerald-500/70 transition-all duration-300"
                    style={{ width: `${signedPct}%` }}
                  />
                </div>
                <p className="mt-0.5 font-mono text-[10px] text-ink-tertiary">
                  {kc.signed}/{kc.total} signed
                </p>
              </button>
            );
          })}
        </nav>

        {/* Pane 2 — Section list */}
        <div className="flex min-h-0 flex-col">
          <div className="sticky top-0 z-10 border-b border-border-subtle bg-raised px-4 py-2.5">
            <h2 className="font-mono text-sm font-semibold text-ink-primary">
              {KIND_LABELS[selectedKind] ?? selectedKind}
            </h2>
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto">
            {sections.isPending && (
              <div className="space-y-2 p-4">
                {Array.from({ length: 6 }).map((_, i) => (
                  <Skeleton key={i} className="h-14" />
                ))}
              </div>
            )}

            {sections.isError && (
              <div className="p-4">
                <ErrorBlock title="Could not load sections" message={sections.error.message} />
              </div>
            )}

            {sections.data?.data.map(s => {
              const isActive = selectedSectionId === s.id;
              return (
                <button
                  key={s.id}
                  onClick={() => setSelectedSectionId(s.id)}
                  className={`w-full border-b border-border-subtle px-4 py-3 text-left transition-colors hover:bg-sunken ${
                    isActive ? 'bg-sunken' : ''
                  }`}
                >
                  <div className="flex items-start justify-between gap-2">
                    <span className={`font-mono text-sm line-clamp-2 ${
                      isActive ? 'font-semibold text-ink-primary' : 'font-medium text-ink-primary'
                    }`}>
                      {sectionLabel(s)}
                    </span>
                    <Badge tone={stateTone(s.state)} className="shrink-0 text-[10px]">
                      {stateLabel(s.state)}
                    </Badge>
                  </div>
                  <p className="mt-0.5 font-mono text-xs text-ink-tertiary">
                    {new Date(s.createdAt).toLocaleDateString(undefined, {
                      month: 'short', day: 'numeric',
                    })}
                  </p>
                </button>
              );
            })}

            {sections.data?.data.length === 0 && !sections.isPending && (
              <p className="px-4 py-6 text-sm text-ink-tertiary">No sections for this kind.</p>
            )}
          </div>

          {/* Pagination */}
          {sections.data && sections.data.total > 50 && (
            <div className="flex items-center justify-between border-t border-border-subtle px-4 py-2">
              <Button
                variant="secondary"
                onClick={() => setSectionPage(p => Math.max(1, p - 1))}
                disabled={sectionPage === 1}
              >
                Prev
              </Button>
              <span className="text-xs text-ink-tertiary">
                {sectionPage} / {Math.ceil(sections.data.total / 50)}
              </span>
              <Button
                variant="secondary"
                onClick={() => setSectionPage(p => p + 1)}
                disabled={sectionPage * 50 >= sections.data.total}
              >
                Next
              </Button>
            </div>
          )}
        </div>

        {/* Pane 3 — Detail + review */}
        <div className="flex min-h-0 flex-col bg-canvas">
          {!selectedSectionId && (
            <div className="flex flex-1 items-center justify-center p-10 text-ink-tertiary">
              <div className="text-center">
                <FileText className="mx-auto mb-3 h-10 w-10 opacity-20" />
                <p className="text-sm">Select a section to preview</p>
              </div>
            </div>
          )}

          {selectedSectionId && sectionDetail.isPending && (
            <div className="space-y-4 p-6">
              <Skeleton className="h-8 w-64" />
              <Skeleton className="h-48 w-full" />
              <Skeleton className="h-48 w-full" />
            </div>
          )}

          {detail && (
            <>
              {/* Sticky action bar */}
              <div className="sticky top-0 z-10 border-b border-border-subtle bg-raised px-6 py-3">
                <div className="flex items-center justify-between gap-3">
                  <div className="flex min-w-0 items-center gap-2">
                    <Badge tone={stateTone(detail.state)}>{stateLabel(detail.state)}</Badge>
                    <span className="truncate font-mono text-sm text-ink-secondary">
                      {sectionLabel(detail)}
                    </span>
                  </div>

                  {persona === 'admin' && (
                    <div className="flex shrink-0 gap-2">
                      <Button
                        variant="secondary"
                        onClick={() => rejectMutation.mutate(detail.id)}
                        disabled={
                          detail.state === 'SIGNED' ||
                          detail.state === 'REJECTED' ||
                          isMutating
                        }
                      >
                        <XCircle className="h-4 w-4" />
                        Reject
                      </Button>
                      <Button
                        variant="primary"
                        onClick={() => acceptMutation.mutate(detail.id)}
                        disabled={
                          detail.state === 'SIGNED' ||
                          detail.state === 'REJECTED' ||
                          isMutating
                        }
                      >
                        <ShieldCheck className="h-4 w-4" />
                        Sign
                      </Button>
                    </div>
                  )}
                </div>

                {mutationError && (
                  <p className="mt-1 text-xs text-rose-600">
                    {(mutationError as Error).message}
                  </p>
                )}
              </div>

              {/* Markdown / Mermaid content */}
              <div className="overflow-y-auto p-6">
                {isDiagram ? (
                  <div>
                    {payload?.title && (
                      <h2 className="mb-3 font-mono text-base font-semibold text-ink-primary">
                        {payload.title}
                      </h2>
                    )}
                    {payload?.narrative && (
                      <div className="prose prose-sm mb-4 max-w-none text-ink-primary">
                        <ReactMarkdown remarkPlugins={[remarkGfm]}>
                          {payload.narrative}
                        </ReactMarkdown>
                      </div>
                    )}
                    {payload?.mermaidSource ? (
                      <MermaidBlock source={payload.mermaidSource} />
                    ) : (
                      <pre className="overflow-x-auto rounded bg-sunken p-4 font-mono text-xs text-ink-secondary">
                        {detail.renderedMarkdown ?? '(no diagram source)'}
                      </pre>
                    )}
                  </div>
                ) : (
                  <div className="prose prose-sm max-w-none text-ink-primary
                    prose-headings:font-mono prose-headings:text-ink-primary
                    prose-code:rounded prose-code:bg-sunken prose-code:px-1 prose-code:font-mono
                    prose-pre:rounded prose-pre:bg-sunken prose-pre:p-4
                    prose-table:border-collapse prose-th:border prose-th:border-border-subtle prose-th:px-3 prose-th:py-1
                    prose-td:border prose-td:border-border-subtle prose-td:px-3 prose-td:py-1">
                    <ReactMarkdown remarkPlugins={[remarkGfm]}>
                      {detail.renderedMarkdown ?? '*(no content)*'}
                    </ReactMarkdown>
                  </div>
                )}
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
