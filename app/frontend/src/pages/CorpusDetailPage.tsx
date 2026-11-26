import { useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowRight, BookOpen, Boxes, ChevronDown, ChevronRight, Download, FileCode, Folder, GitBranch, GitMerge, Kanban, Layers, MessageSquare, RefreshCw, Search, Settings } from 'lucide-react';
import { clsx } from 'clsx';
import { conversationsApi } from '@/lib/conversations';
import { FunnelBar } from '@/workspace/FunnelBar';
import { nextStep } from '@/workspace/MissionControl';
import { absoluteTime, formatInt, formatLanguage, relativeTime } from '@/workspace/format';
import { api, getPersona } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { ReingestModal } from '@/components/ReingestModal';
import { ArtefactSources } from '@/components/ArtefactSources';
import { PageHero } from '@/components/PageHero';
import { formatState } from '@/lib/labels';

export function CorpusDetailPage() {
  const navigate = useNavigate();
  const { id = '' } = useParams();
  const qc = useQueryClient();
  const persona = getPersona();
  const [reingestOpen, setReingestOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);
  const corpus = useQuery({
    queryKey: ['corpus', id],
    queryFn: () => api.getCorpus(id),
    enabled: !!id,
  });
  // Stage counts, language and the project's thread come from the overview Home reads (cached across pages).
  const overview = useQuery({
    queryKey: ['copilot', 'overview'],
    queryFn: () => conversationsApi.overview(),
    staleTime: 15_000,
  });
  const programme = overview.data?.programmes.find((p) => p.corpusId === id) ?? null;

  const handleExport = async (includeSources: boolean) => {
    if (exporting) return;
    setExporting(true);
    setExportError(null);
    try {
      const { blob, filename } = await api.exportProjectBundle(id, includeSources);
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
      setExporting(false);
    }
  };

  if (corpus.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-4 lg:p-6">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }
  if (corpus.isError) {
    return (
      <div className="mx-auto max-w-[1200px] p-4 lg:p-6">
        <ErrorBlock
          title="Could not load project"
          message={corpus.error.message}
          onRetry={() => corpus.refetch()}
        />
      </div>
    );
  }
  const c = corpus.data;
  // Attached evidence (tickets, transcripts, …) is listed in Sources; this list is routines.
  const files = (c.latestVersion?.files ?? []).filter((f) => (f.kind ?? 'code') === 'code');
  const routineCount = files.reduce((n, f) => n + f.subroutines.length, 0);

  return (
    <div className="mx-auto max-w-[1500px] space-y-4 p-4 lg:p-6 fadeup">
      <PageHero
        eyebrow="Project"
        title={c.name}
        lead={
          <span className="flex flex-wrap items-center gap-x-2 gap-y-1 text-caption text-ink-secondary">
            {programme?.sourceLanguage && (
              <span className="rounded-sm bg-sand-300/15 px-1.5 py-0.5 text-[11px] font-semibold text-ink-secondary">
                {programme.sourceLanguage === 'vb6' && /\.(xlsm|xls|xlsb|xlam|docm|dotm|pptm|accdb|mdb)\//i.test(c.latestVersion?.files?.[0]?.relativePath ?? '')
                  ? 'VBA'
                  : formatLanguage(programme.sourceLanguage)}
              </span>
            )}
            <span className="capitalize">{c.sourceType}</span>
            <span aria-hidden="true">·</span>
            <span>
              {formatInt(c.fileCount)} file{c.fileCount === 1 ? '' : 's'} · {formatInt(c.totalLoc)} lines
            </span>
            <span aria-hidden="true">·</span>
            <span title={c.latestVersion ? absoluteTime(c.latestVersion.ingestedAt) : undefined}>
              ingested {c.latestVersion ? relativeTime(c.latestVersion.ingestedAt) : '—'}
            </span>
            <Badge tone={c.state === 'FAILED' ? 'failed' : 'signed'}>{formatState(c.state)}</Badge>
          </span>
        }
        actions={<>
          {/* Views: what this project can show you. Quiet, one row. */}
          <div className="flex flex-wrap items-center gap-1" role="group" aria-label="Views">
            <ViewLink onClick={() => navigate(`/corpora/${id}/docs`)} testid="open-docs" icon={BookOpen} label="Docs" />
            <ViewLink onClick={() => navigate(`/corpora/${id}/migration-plan`)} testid="open-migration-plan" icon={Layers} label="Migration plan" />
            <ViewLink onClick={() => navigate(`/corpora/${id}/board`)} testid="open-flow-board" icon={Kanban} label="Routine board" />
            <ViewLink onClick={() => navigate(`/corpora/${id}/pattern-analysis`)} testid="open-pattern-analysis" icon={Boxes} label="Pattern analysis" />
            <ViewLink onClick={() => navigate(`/corpora/${id}/dependency-graph`)} testid="open-dependency-graph" icon={GitBranch} label="Dependency graph" />
          </div>
          {/* Project actions: set apart from the views; Re-sync opens its own confirming dialog. */}
          <div className="flex items-center gap-1 border-l border-line-subtle pl-2">
            <button
              type="button"
              onClick={() => setReingestOpen(true)}
              data-testid="open-reingest"
              title="Re-sync: ingest a new version of the source"
              className="inline-flex h-8 items-center gap-1.5 rounded-md px-2 text-caption text-ink-secondary transition-colors hover:bg-raised hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            >
              <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
              Re-sync
            </button>
            <button
              type="button"
              onClick={() => navigate(`/corpora/${id}/settings`)}
              data-testid="open-project-settings"
              title="Project settings"
              className="inline-flex h-8 items-center gap-1.5 rounded-md px-2 text-caption text-ink-secondary transition-colors hover:bg-raised hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            >
              <Settings className="h-3.5 w-3.5" aria-hidden="true" />
              Settings
            </button>
          {persona === 'admin' && (
            <div className="relative">
              <select
                value=""
                onChange={(e) => { if (e.target.value) handleExport(e.target.value === 'with-sources'); }}
                disabled={exporting}
                aria-label="Export project artifacts"
                data-testid="export-project"
                className="h-8 cursor-pointer appearance-none rounded-md border border-line-subtle bg-raised py-1 pl-7 pr-6 text-caption text-ink-secondary transition-colors hover:border-line-strong hover:text-ink-primary disabled:cursor-not-allowed disabled:opacity-50"
              >
                <option value="">{exporting ? 'Exporting…' : 'Export…'}</option>
                <option value="artifacts">Artifacts (.zip)</option>
                <option value="with-sources">Artifacts + sources (.zip)</option>
              </select>
              <Download className="pointer-events-none absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-ink-tertiary" />
              <ChevronDown className="pointer-events-none absolute right-1.5 top-1/2 h-3 w-3 -translate-y-1/2 text-ink-tertiary" />
            </div>
          )}
          </div>
        </>}
      />

      {/* Where this project stands, and the one thing to do next */}
      <section
        aria-label="Progress"
        className="flex flex-wrap items-center gap-x-6 gap-y-3 rounded-xl border border-line-subtle bg-raised px-5 py-4"
        data-testid="project-summary"
      >
        <div className="min-w-[280px] flex-1">
          {programme ? (
            <>
              <div className="mb-2 flex items-baseline justify-between text-caption">
                <span className="text-ink-secondary">
                  {formatInt(programme.counts.scaffolded + programme.counts.committed)} of {formatInt(programme.counts.total)} routines have generated code
                </span>
              </div>
              <FunnelBar counts={programme.counts} dense />
            </>
          ) : (
            <div className="h-10 animate-pulse rounded bg-sunken" aria-hidden="true" />
          )}
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {programme && (
            <span className="text-caption font-medium text-ink-primary" data-testid="project-next-step">
              Next: {nextStep(programme.counts)}
            </span>
          )}
          {programme?.conversationId && (
            <Link
              to={`/w/${programme.conversationId}`}
              className="inline-flex h-9 items-center gap-1.5 rounded-md bg-volt px-3 text-caption font-semibold text-on-volt transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt focus-visible:ring-offset-2 focus-visible:ring-offset-canvas"
              data-testid="project-ask"
            >
              <MessageSquare className="h-4 w-4" aria-hidden="true" />
              Ask about this project
              <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
            </Link>
          )}
        </div>
      </section>

      {/* Jump between the page's two halves */}
      <nav aria-label="On this page" className="flex gap-1 text-caption">
        <a href="#routines" className="rounded-md px-2.5 py-1 text-ink-secondary hover:bg-raised hover:text-ink-primary">
          Routines ({formatInt(routineCount)})
        </a>
        <a href="#sources" className="rounded-md px-2.5 py-1 text-ink-secondary hover:bg-raised hover:text-ink-primary">
          Sources &amp; evidence
        </a>
      </nav>

      {c.state === 'FAILED' && (
        // The specific reason isn't persisted anywhere retrievable — only
        // logged server-side at the time of failure — so this can't say more
        // than "it failed" without either the corpus having a stored error
        // column (schema change) or the server log. Re-sync is the one
        // concrete thing this screen can actually offer.
        <ErrorBlock
          title="This project failed to ingest"
          message="Something went wrong while processing the uploaded files. Re-sync to try again, or check the server log if it keeps failing."
          onRetry={() => setReingestOpen(true)}
        />
      )}

      {exportError && (
        <p className="text-caption text-status-fail" data-testid="export-project-error">{exportError}</p>
      )}

      <ReingestModal
        open={reingestOpen}
        onClose={() => setReingestOpen(false)}
        onSuccess={() => {
          // Drop both the list and detail caches so the user sees the new version.
          qc.invalidateQueries({ queryKey: ['corpora'] });
          qc.invalidateQueries({ queryKey: ['corpus', id] });
        }}
        corpusId={c.id}
        corpusName={c.name}
        defaultSourceType={c.sourceType}
        defaultGitUrl={null}
        defaultBranch={null}
        defaultSourceRoot={null}
      />

      <section id="routines" className="scroll-mt-4">
        <RoutineBrowser files={files} />
      </section>

      {/* The routines are what people come here for, so they come first; evidence is the supporting material. */}
      <section id="sources" className="scroll-mt-4">
        <ArtefactSources corpusId={c.id} canWrite={persona !== 'observer'} />
      </section>
    </div>
  );
}

type Files = NonNullable<NonNullable<Awaited<ReturnType<typeof api.getCorpus>>>['latestVersion']>['files'];
type FileRow = Files[number];

const STATE_FILTERS: Array<{ id: string; label: string; match: (s: string) => boolean }> = [
  { id: '', label: 'All', match: () => true },
  { id: 'parsed', label: 'Not started', match: (s) => s === 'PARSED' },
  { id: 'draft', label: 'Draft', match: (s) => s === 'DRAFT' || s === 'EXTRACTING' },
  { id: 'review', label: 'In review', match: (s) => s === 'IN_REVIEW' },
  { id: 'signed', label: 'Signed', match: (s) => s === 'SIGNED' },
  { id: 'code', label: 'With code', match: (s) => s === 'SCAFFOLDED' || s === 'SCAFFOLDING' || s === 'COMMITTED' },
];
const PAGE = 150;

/** The longest folder prefix every path shares ("A/B/" for A/B/x.cs and A/B/C/y.cs). */
function commonPrefix(paths: string[]): string {
  if (paths.length < 2) return '';
  const parts = paths.map((p) => p.split('/').slice(0, -1));
  const first = parts[0];
  let n = 0;
  while (n < first.length && parts.every((x) => x[n] === first[n])) n++;
  return n ? `${first.slice(0, n).join('/')}/` : '';
}

/** "A/very/long/path/File.cs" → "A/very/…/path/File.cs" when it would not fit. */
function shortPath(path: string, max = 90): string {
  if (path.length <= max) return path;
  const parts = path.split('/');
  const file = parts.pop()!;
  let tail = file;
  while (parts.length && tail.length + parts[parts.length - 1].length + 4 < max) tail = `${parts.pop()}/${tail}`;
  return parts.length ? `${parts[0]}/…/${tail}` : tail;
}

/**
 * The project's routines: search, a state filter, files grouped under a short path (the folder every file shares is
 * said once), and a page at a time so a 2 000-routine estate stays a page, not 120 000 px.
 */
function RoutineBrowser({ files }: { files: Files }) {
  const [params, setParams] = useSearchParams();
  const q = (params.get('rq') ?? '').toLowerCase();
  const stateId = params.get('rstate') ?? '';
  const [limit, setLimit] = useState(PAGE);
  const set = (k: string, v: string) =>
    setParams(
      (p) => {
        const n = new URLSearchParams(p);
        if (v) n.set(k, v);
        else n.delete(k);
        return n;
      },
      { replace: true },
    );

  const prefix = useMemo(() => commonPrefix(files.map((f) => f.relativePath)), [files]);
  const filter = STATE_FILTERS.find((f) => f.id === stateId) ?? STATE_FILTERS[0];
  const counts = useMemo(() => {
    const all = files.flatMap((f) => f.subroutines.map((s) => s.state.toUpperCase()));
    return Object.fromEntries(STATE_FILTERS.map((f) => [f.id, all.filter(f.match).length]));
  }, [files]);

  const shown = useMemo(() => {
    const out: Array<{ file: FileRow; subs: FileRow['subroutines'] }> = [];
    for (const f of files) {
      const pathHit = !q || f.relativePath.toLowerCase().includes(q);
      const subs = f.subroutines.filter(
        (s) => filter.match(s.state.toUpperCase()) && (pathHit || s.name.toLowerCase().includes(q)),
      );
      // A file with no routines still shows when nothing filters it out (so the file list stays complete).
      if (subs.length || (!q && !stateId && f.subroutines.length === 0)) out.push({ file: f, subs });
    }
    return out;
  }, [files, q, stateId, filter]);

  let budget = limit;
  const page: typeof shown = [];
  for (const g of shown) {
    if (budget <= 0) break;
    const take = g.subs.slice(0, budget);
    page.push({ file: g.file, subs: take });
    budget -= Math.max(1, take.length);
  }
  const total = shown.reduce((n, g) => n + g.subs.length, 0);
  const onPage = page.reduce((n, g) => n + g.subs.length, 0);

  return (
    <div className="rounded-xl border border-line-subtle bg-raised" data-testid="project-routines">
      <div className="flex flex-wrap items-center gap-3 border-b border-line-subtle px-5 py-3">
        <div className="min-w-0 flex-1">
          <h2 className="text-body font-semibold text-ink-primary">Routines</h2>
          <p className="truncate text-caption text-ink-secondary" title={prefix || undefined}>
            {formatInt(counts[''])} routines in {formatInt(files.length)} file{files.length === 1 ? '' : 's'}
            {prefix && <> · under <span className="font-mono">{shortPath(prefix, 60)}</span></>}
          </p>
        </div>
        <label className="relative w-64 max-w-full">
          <span className="sr-only">Search routines and files</span>
          <Search size={13} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-ink-tertiary" aria-hidden="true" />
          <input
            type="search"
            value={params.get('rq') ?? ''}
            onChange={(e) => {
              setLimit(PAGE);
              set('rq', e.target.value);
            }}
            placeholder="Search routines or files"
            className="h-8 w-full rounded-md border border-line-subtle bg-canvas pl-7 pr-2 text-caption text-ink-primary placeholder:text-ink-tertiary focus:border-line-strong focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            data-testid="project-routine-search"
          />
        </label>
      </div>
      <div className="flex flex-wrap gap-1.5 border-b border-line-subtle px-5 py-2.5" role="group" aria-label="Filter by state">
        {STATE_FILTERS.map((f) => (
          <button
            key={f.id || 'all'}
            type="button"
            aria-pressed={stateId === f.id}
            onClick={() => {
              setLimit(PAGE);
              set('rstate', f.id);
            }}
            disabled={f.id !== '' && counts[f.id] === 0}
            className={clsx(
              'rounded-full border px-2.5 py-0.5 text-caption transition-colors disabled:opacity-40',
              stateId === f.id
                ? 'border-volt/50 bg-volt/10 text-ink-primary'
                : 'border-line-subtle text-ink-secondary hover:border-line hover:text-ink-primary',
            )}
            data-testid={`project-routine-filter-${f.id || 'all'}`}
          >
            {f.label} <span className="font-mono tabular-nums">{formatInt(counts[f.id])}</span>
          </button>
        ))}
      </div>

      {page.length === 0 ? (
        <p className="px-5 py-8 text-center text-caption text-ink-secondary">No routines match.</p>
      ) : (
        <ul className="divide-y divide-line-subtle">
          {page.map(({ file, subs }) => (
            <li key={file.id}>
              <FileBlock file={file} subs={subs} prefix={prefix} />
            </li>
          ))}
        </ul>
      )}
      {onPage < total && (
        <div className="flex items-center justify-between border-t border-line-subtle px-5 py-3 text-caption text-ink-secondary">
          <span>
            Showing {formatInt(onPage)} of {formatInt(total)}
          </span>
          <button
            type="button"
            onClick={() => setLimit((l) => l + PAGE * 2)}
            className="rounded-md border border-line-subtle px-3 py-1 text-ink-primary hover:border-line-strong"
            data-testid="project-routines-more"
          >
            Show more
          </button>
        </div>
      )}
    </div>
  );
}

function FileBlock({ file, subs, prefix }: { file: FileRow; subs: FileRow['subroutines']; prefix: string }) {
  const rel = prefix && file.relativePath.startsWith(prefix) ? file.relativePath.slice(prefix.length) : file.relativePath;
  return (
    <div className="px-5 py-3">
      <div className="flex min-w-0 items-center gap-2 text-caption">
        <Folder className="h-3.5 w-3.5 shrink-0 text-ink-tertiary" aria-hidden="true" />
        <FileCode className="h-3.5 w-3.5 shrink-0 text-ink-secondary" aria-hidden="true" />
        <span className="min-w-0 truncate font-mono text-ink-primary" title={file.relativePath}>
          {shortPath(rel)}
        </span>
        <span className="ml-auto shrink-0 font-mono text-micro text-ink-tertiary">{formatInt(file.lineCount)} lines</span>
      </div>
      {subs.length > 0 && (
        <ul className="mt-1.5 space-y-0.5 pl-6">
          {subs.map((sub) => (
            <li key={sub.id}>
              <Link
                to={`/subroutines/${sub.id}`}
                className="group flex min-w-0 items-center gap-3 rounded-md px-2.5 py-1.5 text-caption transition-colors duration-fast hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary"
              >
                <span className="min-w-0 truncate font-mono font-semibold text-ink-primary" title={sub.name}>
                  {sub.name}
                </span>
                <Badge tone={subBadgeTone(sub.state)} className="shrink-0 text-[10px]">
                  {stateLabel(sub.state)}
                </Badge>
                {sub.carriedForward && (
                  <span
                    className="inline-flex shrink-0 items-center gap-1 rounded-sm border border-status-signed/40 bg-status-info/10 px-1.5 py-0.5 font-mono text-micro uppercase tracking-wider text-status-signed"
                    title="Spec carried forward from a previous source version (file hash unchanged)"
                    data-testid={`carried-${sub.id}`}
                  >
                    <GitMerge className="h-3 w-3" aria-hidden="true" />
                    carried fwd
                  </span>
                )}
                <span className="ml-auto shrink-0 font-mono text-micro text-ink-tertiary">
                  L{sub.lineStart}–{sub.lineEnd}
                </span>
                <ChevronRight className="h-3.5 w-3.5 shrink-0 text-ink-tertiary opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function ViewLink({
  onClick,
  testid,
  icon: Icon,
  label,
}: {
  onClick: () => void;
  testid: string;
  icon: typeof BookOpen;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      data-testid={testid}
      className="inline-flex h-8 items-center gap-1.5 rounded-md border border-line-subtle px-2.5 text-caption text-ink-secondary transition-colors hover:border-line-strong hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
    >
      <Icon className="h-3.5 w-3.5" aria-hidden="true" />
      {label}
    </button>
  );
}

/** Same words as Home and Projects: "With code", not "Scaffolded". */
function stateLabel(state: string): string {
  const s = state.toUpperCase();
  if (s === 'SCAFFOLDED' || s === 'COMMITTED') return 'With code';
  if (s === 'PARSED') return 'Not started';
  return formatState(state);
}

function subBadgeTone(state: string): 'draft' | 'review' | 'signed' | 'scaffolded' | 'neutral' {
  switch (state) {
    case 'DRAFT': return 'draft';
    case 'IN_REVIEW': return 'review';
    case 'SIGNED': return 'signed';
    case 'SCAFFOLDED': return 'scaffolded';
    default: return 'neutral';
  }
}
