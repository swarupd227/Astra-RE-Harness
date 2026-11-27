import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { AlertTriangle, ArrowDown, ArrowUp, FlaskConical, GitBranch, LayoutGrid, Plus, Rows3, Search, Upload } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type CorpusListItem } from '@/lib/api';
import { conversationsApi, type FunnelCounts } from '@/lib/conversations';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { Skeleton } from '@/components/Skeleton';
import { EmptyState } from '@/components/EmptyState';
import { NoCorporaIllustration } from '@/illustrations/NoCorpora';
import { FunnelBar } from '@/workspace/FunnelBar';
import { nextStep } from '@/workspace/MissionControl';
import { absoluteTime, formatInt, formatLanguage, relativeTime } from '@/workspace/format';

/** A parse that has not finished in a day is stuck, not "in progress". */
const STUCK_MS = 24 * 60 * 60 * 1000;
/** Names that mark a QA / scratch project even before anyone flags it in settings. */
const TEST_NAME = /^zzz[-_ ]|delete[-_ ]?me|[-_ ]proof$|^qa[-_ ]test/i;

type Health = 'attention' | 'progress' | 'healthy';
type SortKey = 'activity' | 'name' | 'progress' | 'size';
type Row = CorpusListItem & {
  counts: FunnelCounts | null;
  conversationId: string | null;
  health: Health;
  problem: 'failed' | 'stuck' | null;
  isTestish: boolean;
  langLabel: string;
  progress: number;
};

function languageId(c: CorpusListItem): string | null {
  if (!c.sourceLanguage) return null;
  return c.sourceLanguage === 'vb6' && c.officeVba ? 'vba' : c.sourceLanguage;
}

function toRow(c: CorpusListItem, counts: FunnelCounts | null, conversationId: string | null, now: number): Row {
  const state = c.state.toUpperCase();
  const busy = state === 'PARSING' || state === 'INGESTING';
  const stuck = busy && now - Date.parse(c.updatedAt) > STUCK_MS;
  const problem = state === 'FAILED' ? 'failed' : stuck ? 'stuck' : null;
  const lang = languageId(c);
  const done = counts ? counts.scaffolded + counts.committed : 0;
  return {
    ...c,
    counts,
    conversationId,
    problem,
    health: problem ? 'attention' : busy || (counts && counts.total > 0 && done < counts.total) ? 'progress' : 'healthy',
    isTestish: !!c.isTest || TEST_NAME.test(c.name),
    langLabel: lang === 'vba' ? 'VBA' : lang ? formatLanguage(lang) : 'Unknown',
    progress: counts && counts.total ? done / counts.total : 0,
  };
}

/**
 * Projects: every corpus, findable. Search, language / status filters, sort and a grid-or-table toggle all live in
 * the URL so a filtered view can be shared. Problems (a failed ingest, a parse stuck for a day) are pulled to the top;
 * QA scratch projects stay out of the way unless asked for.
 */
export function CorporaPage() {
  const corpora = useQuery({ queryKey: ['corpora'], queryFn: api.listCorpora });
  // Per-programme stage counts come from the same overview Home reads.
  const overview = useQuery({
    queryKey: ['copilot', 'overview'],
    queryFn: () => conversationsApi.overview(),
    staleTime: 15_000,
  });
  const [params, setParams] = useSearchParams();
  const q = params.get('q') ?? '';
  const lang = params.get('lang') ?? '';
  const status = (params.get('status') ?? '') as Health | '';
  const sort = (params.get('sort') ?? 'activity') as SortKey;
  const dir = params.get('dir') === 'asc' ? 'asc' : 'desc';
  const showTest = params.get('test') === '1';
  const set = (k: string, v: string | null) =>
    setParams(
      (p) => {
        const n = new URLSearchParams(p);
        if (v) n.set(k, v);
        else n.delete(k);
        return n;
      },
      { replace: true },
    );

  const all = useMemo(() => {
    const byCorpus = new Map((overview.data?.programmes ?? []).map((p) => [p.corpusId, p]));
    const now = Date.now();
    return (corpora.data?.data ?? []).map((c) => {
      const o = byCorpus.get(c.id);
      return toRow(c, o?.counts ?? null, o?.conversationId ?? null, now);
    });
  }, [corpora.data, overview.data]);

  const visible = useMemo(() => {
    const needle = q.trim().toLowerCase();
    const rows = all.filter(
      (r) =>
        (showTest || !r.isTestish) &&
        (!needle || r.name.toLowerCase().includes(needle)) &&
        (!lang || r.langLabel === lang) &&
        (!status || r.health === status),
    );
    const cmp: Record<SortKey, (a: Row, b: Row) => number> = {
      activity: (a, b) => a.updatedAt.localeCompare(b.updatedAt),
      name: (a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
      progress: (a, b) => a.progress - b.progress,
      size: (a, b) => a.totalLoc - b.totalLoc,
    };
    rows.sort((a, b) => (dir === 'asc' ? 1 : -1) * cmp[sort](a, b));
    return rows;
  }, [all, q, lang, status, sort, dir, showTest]);

  const languages = useMemo(
    () => [...new Set(all.filter((r) => showTest || !r.isTestish).map((r) => r.langLabel))].sort(),
    [all, showTest],
  );
  const problems = all.filter((r) => r.problem && (showTest || !r.isTestish));
  const testCount = all.filter((r) => r.isTestish).length;
  const shownCount = all.length - (showTest ? 0 : testCount);
  // A long estate reads better as a table; a handful of projects as cards. The user's choice wins.
  const view = (params.get('view') as 'grid' | 'table' | null) ?? (shownCount >= 12 ? 'table' : 'grid');
  const totalRoutines = overview.data?.totals?.total;

  const onSort = (k: SortKey) => {
    if (sort === k) set('dir', dir === 'asc' ? 'desc' : 'asc');
    else {
      setParams(
        (p) => {
          const n = new URLSearchParams(p);
          n.set('sort', k);
          if (k === 'name') n.set('dir', 'asc');
          else n.delete('dir');
          return n;
        },
        { replace: true },
      );
    }
  };

  return (
    <div className="mx-auto max-w-[1280px] space-y-5 p-4 lg:p-6 fadeup">
      <PageHero
        tone="emerald"
        title="Projects"
        lead={
          corpora.data
            ? `${formatInt(shownCount)} project${shownCount === 1 ? '' : 's'}${totalRoutines != null ? ` · ${formatInt(totalRoutines)} code units` : ''}`
            : 'Your source, versioned and parsed.'
        }
        actions={
          <Link to="/projects/new" data-testid="add-corpus">
            <Button variant="primary">
              <Plus className="h-4 w-4" aria-hidden="true" />
              Add project
            </Button>
          </Link>
        }
      />

      {corpora.isPending ? (
        <div className="space-y-2">
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-12 w-full" />
          ))}
        </div>
      ) : corpora.isError ? (
        <ErrorBlock title="Could not load projects" message={corpora.error.message} onRetry={() => corpora.refetch()} />
      ) : all.length === 0 ? (
        <div className="rounded-xl border border-line-subtle bg-raised p-6">
          <EmptyState
            illustration={<NoCorporaIllustration size={140} />}
            title="No projects yet"
            description="A project is a versioned snapshot of your source. Upload files or a .zip, or clone a Git repository — the language is detected automatically."
            action={
              <Link to="/projects/new">
                <Button variant="primary">
                  <Plus className="h-4 w-4" aria-hidden="true" />
                  Add project
                </Button>
              </Link>
            }
          />
        </div>
      ) : (
        <>
          {problems.length > 0 && (
            <section
              aria-label="Needs attention"
              className="rounded-xl border border-status-warn/40 bg-status-warn/5 px-4 py-3"
              data-testid="projects-attention"
            >
              <p className="flex items-center gap-2 text-caption font-semibold text-ink-primary">
                <AlertTriangle size={14} className="text-status-warn" aria-hidden="true" />
                Needs attention
                <span className="font-normal text-ink-secondary">
                  {[
                    problems.filter((p) => p.problem === 'failed').length &&
                      `${problems.filter((p) => p.problem === 'failed').length} failed ingest`,
                    problems.filter((p) => p.problem === 'stuck').length &&
                      `${problems.filter((p) => p.problem === 'stuck').length} stuck parsing`,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </span>
              </p>
              <ul className="mt-2 space-y-1">
                {problems.map((p) => (
                  <li key={p.id} className="flex flex-wrap items-center gap-x-3 text-caption">
                    <Link to={`/corpora/${p.id}`} className="font-medium text-ink-link hover:underline">
                      {p.name}
                    </Link>
                    <span className={p.problem === 'failed' ? 'text-status-failed' : 'text-status-warn'}>
                      {p.problem === 'failed'
                        ? 'Ingest failed'
                        : `Parsing since ${relativeTime(p.updatedAt)} — open it to re-sync`}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {/* Toolbar */}
          <div className="flex flex-wrap items-center gap-2" role="search">
            <label className="relative min-w-[220px] flex-1">
              <span className="sr-only">Search projects</span>
              <Search
                size={14}
                className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-ink-tertiary"
                aria-hidden="true"
              />
              <input
                type="search"
                value={q}
                onChange={(e) => set('q', e.target.value || null)}
                placeholder="Search projects"
                className="h-9 w-full rounded-md border border-line-subtle bg-raised pl-8 pr-3 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-line-strong focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                data-testid="projects-search"
              />
            </label>
            <Select
              label="Language"
              value={lang}
              onChange={(v) => set('lang', v || null)}
              options={[['', 'All languages'], ...languages.map((l) => [l, l] as [string, string])]}
              testid="projects-filter-language"
            />
            <Select
              label="Status"
              value={status}
              onChange={(v) => set('status', v || null)}
              options={[
                ['', 'Any status'],
                ['attention', 'Needs attention'],
                ['progress', 'In progress'],
                ['healthy', 'Done'],
              ]}
              testid="projects-filter-status"
            />
            <Select
              label="Sort"
              value={sort}
              onChange={(v) => onSort(v as SortKey)}
              options={[
                ['activity', 'Last activity'],
                ['name', 'Name'],
                ['progress', 'Progress'],
                ['size', 'Size'],
              ]}
              testid="projects-sort"
            />
            <div className="flex rounded-md border border-line-subtle" role="group" aria-label="View">
              <ViewButton active={view === 'table'} onClick={() => set('view', 'table')} label="Table" icon={Rows3} />
              <ViewButton active={view === 'grid'} onClick={() => set('view', 'grid')} label="Cards" icon={LayoutGrid} />
            </div>
            {testCount > 0 && (
              <label className="ml-auto inline-flex cursor-pointer items-center gap-2 text-caption text-ink-secondary">
                <input
                  type="checkbox"
                  checked={showTest}
                  onChange={(e) => set('test', e.target.checked ? '1' : null)}
                  className="accent-volt"
                  data-testid="projects-show-test"
                />
                Show test projects ({testCount})
              </label>
            )}
          </div>

          {visible.length === 0 ? (
            <p className="rounded-xl border border-dashed border-line px-4 py-8 text-center text-caption text-ink-secondary">
              No projects match.{' '}
              <button type="button" className="text-ink-link hover:underline" onClick={() => setParams({}, { replace: true })}>
                Clear filters
              </button>
            </p>
          ) : view === 'table' ? (
            <ProjectsTable rows={visible} sort={sort} dir={dir} onSort={onSort} />
          ) : (
            <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
              {visible.map((r) => (
                <li key={r.id}>
                  <ProjectCard row={r} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}

function Select({
  label,
  value,
  onChange,
  options,
  testid,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: Array<[string, string]>;
  testid: string;
}) {
  return (
    <label className="inline-flex items-center">
      <span className="sr-only">{label}</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="h-9 rounded-md border border-line-subtle bg-raised px-2.5 text-caption text-ink-primary focus:border-line-strong focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
        data-testid={testid}
      >
        {options.map(([v, l]) => (
          <option key={v} value={v}>
            {l}
          </option>
        ))}
      </select>
    </label>
  );
}

function ViewButton({
  active,
  onClick,
  label,
  icon: Icon,
}: {
  active: boolean;
  onClick: () => void;
  label: string;
  icon: typeof Rows3;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      title={label}
      className={clsx(
        'inline-flex h-9 items-center gap-1.5 px-2.5 text-caption transition-colors first:rounded-l-md last:rounded-r-md',
        active ? 'bg-raised text-ink-primary' : 'text-ink-secondary hover:text-ink-primary',
      )}
      data-testid={`projects-view-${label.toLowerCase()}`}
    >
      <Icon size={14} aria-hidden="true" />
      <span className="hidden sm:inline">{label}</span>
    </button>
  );
}

/** Language pill. Keeps the `corpus-language-<id>` ids the demo specs look for. */
function LangPill({ row }: { row: Row }) {
  const id = languageId(row);
  return (
    <span
      className={clsx(
        'inline-flex shrink-0 items-center rounded-sm px-1.5 py-0.5 text-[11px] font-semibold',
        id ? 'bg-sand-300/15 text-ink-secondary' : 'border border-dashed border-line text-ink-tertiary',
      )}
      data-testid={id ? `corpus-language-${id}` : 'corpus-language-unknown'}
      title={id ? `Source language: ${row.langLabel}` : 'No code units parsed yet, or several languages tie — open the project to see its files'}
    >
      {row.langLabel}
    </span>
  );
}

function SourceIcon({ type }: { type: string }) {
  const t = type.toLowerCase();
  const Icon = t === 'git' ? GitBranch : t === 'synthetic' ? FlaskConical : Upload;
  const label = t === 'git' ? 'Cloned from Git' : t === 'synthetic' ? 'Synthetic sample' : 'Uploaded';
  return (
    <span title={label} className="inline-flex shrink-0 text-ink-tertiary">
      <Icon size={13} aria-hidden="true" />
      <span className="sr-only">{label}</span>
    </span>
  );
}

function StatusText({ row }: { row: Row }) {
  if (row.problem === 'failed') return <span className="text-status-failed">Ingest failed</span>;
  if (row.problem === 'stuck') return <span className="text-status-warn">Stuck parsing</span>;
  const s = row.state.toUpperCase();
  if (s === 'PARSING' || s === 'INGESTING') return <span className="text-ink-secondary">Parsing…</span>;
  return <span className="text-ink-primary">{nextStep(row.counts ?? undefined)}</span>;
}

function edgeFor(row: Row): string {
  if (row.problem === 'failed') return 'border-l-status-failed';
  if (row.problem === 'stuck') return 'border-l-status-warn';
  if (row.counts && row.counts.total > 0 && row.progress >= 1) return 'border-l-status-ok';
  return 'border-l-line';
}

function ProgressCell({ row }: { row: Row }) {
  if (!row.counts || !row.counts.total) return <span className="text-ink-tertiary">—</span>;
  const done = row.counts.scaffolded + row.counts.committed;
  return (
    <div className="min-w-[140px]">
      <FunnelBar counts={row.counts} dense className="[&>div:last-child]:hidden" />
      <p className="mt-1 font-mono text-micro tabular-nums text-ink-secondary">
        {formatInt(done)}/{formatInt(row.counts.total)} with code
      </p>
    </div>
  );
}

function ProjectsTable({
  rows,
  sort,
  dir,
  onSort,
}: {
  rows: Row[];
  sort: SortKey;
  dir: 'asc' | 'desc';
  onSort: (k: SortKey) => void;
}) {
  const Th = ({ k, children, className }: { k?: SortKey; children: string; className?: string }) => (
    <th
      scope="col"
      aria-sort={k && sort === k ? (dir === 'asc' ? 'ascending' : 'descending') : undefined}
      className={clsx('px-3 py-2 text-left text-micro font-semibold uppercase tracking-wide text-ink-secondary', className)}
    >
      {k ? (
        <button type="button" onClick={() => onSort(k)} className="inline-flex items-center gap-1 hover:text-ink-primary">
          {children}
          {sort === k && (dir === 'asc' ? <ArrowUp size={11} aria-hidden="true" /> : <ArrowDown size={11} aria-hidden="true" />)}
        </button>
      ) : (
        children
      )}
    </th>
  );
  return (
    <div className="overflow-x-auto rounded-xl border border-line-subtle bg-raised">
      <table className="w-full min-w-[860px] border-collapse text-caption" data-testid="projects-table">
        <thead className="border-b border-line-subtle">
          <tr>
            <Th k="name">Project</Th>
            <Th>Language</Th>
            <Th k="size" className="text-right">Code units</Th>
            <Th k="progress">Progress</Th>
            <Th>Next step</Th>
            <Th k="activity">Last activity</Th>
          </tr>
        </thead>
        <tbody className="divide-y divide-line-subtle">
          {rows.map((r) => (
            <tr key={r.id} className={clsx('border-l-2 hover:bg-sunken', edgeFor(r))} data-testid={`project-row-${r.id}`}>
              <td className="max-w-[320px] px-3 py-2.5">
                <div className="flex items-center gap-2">
                  <SourceIcon type={r.sourceType} />
                  <Link
                    to={`/corpora/${r.id}`}
                    className="truncate font-medium text-ink-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                    title={r.name}
                  >
                    {r.name}
                  </Link>
                  {r.isTestish && <span className="shrink-0 text-micro text-ink-tertiary">test</span>}
                </div>
                <p className="mt-0.5 pl-[21px] font-mono text-micro text-ink-tertiary">
                  {formatInt(r.fileCount)} files · {formatInt(r.totalLoc)} lines
                </p>
              </td>
              <td className="px-3 py-2.5">
                <LangPill row={r} />
              </td>
              <td className="px-3 py-2.5 text-right font-mono tabular-nums text-ink-primary">
                {r.counts ? formatInt(r.counts.total) : '—'}
              </td>
              <td className="px-3 py-2.5">
                <ProgressCell row={r} />
              </td>
              <td className="max-w-[260px] truncate px-3 py-2.5">
                <StatusText row={r} />
              </td>
              <td className="whitespace-nowrap px-3 py-2.5 text-ink-secondary" title={absoluteTime(r.updatedAt)}>
                {relativeTime(r.updatedAt)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ProjectCard({ row: r }: { row: Row }) {
  return (
    <Link
      to={`/corpora/${r.id}`}
      className={clsx(
        'flex h-full flex-col gap-2.5 rounded-xl border border-l-4 border-line-subtle bg-raised px-4 py-3 transition-colors hover:border-line-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
        edgeFor(r),
      )}
      data-testid={`project-card-${r.id}`}
    >
      <div className="flex items-start gap-2">
        <h2 className="min-w-0 flex-1 truncate text-body font-semibold text-ink-primary" title={r.name}>
          {r.name}
        </h2>
        <LangPill row={r} />
      </div>
      <p className="flex items-center gap-2 text-caption text-ink-secondary">
        <SourceIcon type={r.sourceType} />
        <span>
          {r.counts ? `${formatInt(r.counts.total)} code units` : `${formatInt(r.fileCount)} files`} ·{' '}
          <span title={absoluteTime(r.updatedAt)}>{relativeTime(r.updatedAt)}</span>
        </span>
        {r.isTestish && <span className="text-micro text-ink-tertiary">· test</span>}
      </p>
      <ProgressCell row={r} />
      <p className="mt-auto truncate text-caption font-medium">
        <StatusText row={r} />
      </p>
    </Link>
  );
}
