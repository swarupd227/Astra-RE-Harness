import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { ArrowDown, ArrowUp, ChevronLeft, ChevronRight, Search } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type SubroutineSearchHit } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { PageHero } from '@/components/PageHero';
import { formatInt, formatLanguage } from '@/workspace/format';

/** The states a person can act on — the default view when nothing is searched. */
const NEEDS_ACTION = 'IN_REVIEW,DRAFT,EXTRACTING';
const PAGE_SIZE = 50;

/** One vocabulary across Home, Projects and here. Values are the API's state ids. */
const STATE_CHIPS: Array<{ id: string; label: string; states: string }> = [
  { id: 'action', label: 'Needs action', states: NEEDS_ACTION },
  { id: 'all', label: 'All', states: '' },
  { id: 'parsed', label: 'Not started', states: 'PARSED' },
  { id: 'draft', label: 'Draft', states: 'DRAFT,EXTRACTING' },
  { id: 'review', label: 'In review', states: 'IN_REVIEW' },
  { id: 'signed', label: 'Signed', states: 'SIGNED' },
  { id: 'code', label: 'With code', states: 'SCAFFOLDING,SCAFFOLDED,COMMITTED' },
];
const STATE_LABEL: Record<string, string> = {
  PARSED: 'Not started',
  EXTRACTING: 'Extracting',
  DRAFT: 'Draft',
  IN_REVIEW: 'In review',
  SIGNED: 'Signed',
  SCAFFOLDING: 'Generating',
  SCAFFOLDED: 'With code',
  COMMITTED: 'Committed',
  FAILED: 'Failed',
};
const LANGS = ['vb6', 'cobol', 'cpp', 'csharp', 'delphi', 'fortran-f77', 'java', 'php', 'unibasic', 'vbnet'];
type SortKey = 'project' | 'name' | 'lines' | 'state';

/**
 * Routines across every project. With nothing searched it shows what needs a person (in review, drafts); search
 * matches name, signature or file path; filters, sort and page all live in the URL. Older deep links with a single
 * `?state=` still work.
 */
export function SubroutinesPage() {
  const [params, setParams] = useSearchParams();
  const urlQ = params.get('q') ?? '';
  const corpus = params.get('corpus') ?? '';
  const legacyState = params.get('state') ?? '';
  const lang = params.get('lang') ?? '';
  const sort = (params.get('sort') ?? 'project') as SortKey;
  const dir = params.get('dir') === 'desc' ? 'desc' : 'asc';
  const page = Math.max(0, Number(params.get('page') ?? 0) || 0);
  // No chip in the URL: a search or an explicit ?state= means "all", otherwise "needs action".
  const chipId = params.get('view') ?? (urlQ || legacyState ? 'all' : 'action');
  const chip = STATE_CHIPS.find((c) => c.id === chipId) ?? STATE_CHIPS[0];

  const [rawQ, setRawQ] = useState(urlQ);
  const update = (patch: Record<string, string | null>, resetPage = true) =>
    setParams(
      (p) => {
        const n = new URLSearchParams(p);
        for (const [k, v] of Object.entries(patch)) {
          if (v) n.set(k, v);
          else n.delete(k);
        }
        if (resetPage) n.delete('page');
        return n;
      },
      { replace: true },
    );

  // Debounce typing into the URL.
  useEffect(() => {
    const t = setTimeout(() => {
      if (rawQ.trim() !== urlQ) update({ q: rawQ.trim() || null });
    }, 250);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rawQ]);
  useEffect(() => setRawQ(urlQ), [urlQ]);

  const corpora = useQuery({ queryKey: ['corpora'], queryFn: api.listCorpora });
  const search = useQuery({
    queryKey: ['subroutines', { urlQ, corpus, legacyState, states: chip.states, lang, sort, dir, page }],
    queryFn: () =>
      api.searchSubroutines({
        q: urlQ || undefined,
        corpus: corpus || undefined,
        state: legacyState || undefined,
        states: chip.states || undefined,
        lang: lang || undefined,
        sort,
        dir,
        excludeTest: !corpus,
        limit: PAGE_SIZE,
        offset: page * PAGE_SIZE,
      }),
    placeholderData: (prev) => prev,
  });

  const total = search.data?.total ?? 0;
  const rows = search.data?.data ?? [];
  const from = total ? page * PAGE_SIZE + 1 : 0;
  const to = page * PAGE_SIZE + rows.length;
  const filtered = !!(urlQ || corpus || legacyState || lang || chipId !== 'action');
  const projects = (corpora.data?.data ?? []).filter(
    (c) => !c.isTest && !/^zzz[-_ ]|delete[-_ ]?me|[-_ ]proof$|^qa[-_ ]test/i.test(c.name),
  );

  const onSort = (k: SortKey) =>
    update(sort === k ? { dir: dir === 'asc' ? 'desc' : null } : { sort: k === 'project' ? null : k, dir: k === 'lines' ? 'desc' : null });

  return (
    <div className="mx-auto max-w-[1280px] space-y-5 p-4 lg:p-6 fadeup">
      <PageHero
        eyebrow="Work"
        title="Routines"
        lead={chipId === 'action' && !urlQ ? 'Routines waiting on a person — drafts to send for review and specs to sign.' : 'Every routine across your projects.'}
      />

      <div className="space-y-3">
        <div className="flex flex-wrap items-center gap-2" role="search">
          <label className="relative min-w-[260px] flex-1">
            <span className="sr-only">Search routines by name, signature or file</span>
            <Search size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-ink-tertiary" aria-hidden="true" />
            <input
              type="search"
              value={rawQ}
              onChange={(e) => setRawQ(e.target.value)}
              placeholder="Search by name, signature or file — e.g. Premium, HYBRD, Module1.bas"
              className="h-9 w-full rounded-md border border-line-subtle bg-raised pl-8 pr-3 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-line-strong focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
              data-testid="subroutines-search"
              autoFocus
            />
          </label>
          <select
            value={corpus}
            onChange={(e) => update({ corpus: e.target.value || null })}
            aria-label="Project"
            className={selectClass}
            data-testid="filter-corpus"
          >
            <option value="">All projects</option>
            {projects.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
          <select
            value={lang}
            onChange={(e) => update({ lang: e.target.value || null })}
            aria-label="Language"
            className={selectClass}
            data-testid="filter-language"
          >
            <option value="">All languages</option>
            {LANGS.map((l) => (
              <option key={l} value={l}>
                {l === 'vb6' ? 'VB6 / VBA' : formatLanguage(l)}
              </option>
            ))}
          </select>
          {/* Kept for deep links and the e2e suite: a single exact state. */}
          <select
            value={legacyState}
            onChange={(e) => update({ state: e.target.value || null, view: e.target.value ? 'all' : null })}
            aria-label="Exact state"
            className={clsx(selectClass, !legacyState && 'sr-only')}
            data-testid="filter-state"
          >
            <option value="">Any state</option>
            {Object.entries(STATE_LABEL).map(([v, l]) => (
              <option key={v} value={v}>
                {l}
              </option>
            ))}
          </select>
          {filtered && (
            <button
              type="button"
              onClick={() => {
                setRawQ('');
                setParams({}, { replace: true });
              }}
              className="text-caption font-medium text-ink-link hover:underline"
              data-testid="clear-filters"
            >
              Clear filters
            </button>
          )}
        </div>
        <div className="flex flex-wrap gap-1.5" role="group" aria-label="Show">
          {STATE_CHIPS.map((c) => (
            <button
              key={c.id}
              type="button"
              aria-pressed={chip.id === c.id && !legacyState}
              onClick={() => update({ view: c.id === 'action' ? null : c.id, state: null })}
              className={clsx(
                'rounded-full border px-3 py-1 text-caption transition-colors',
                chip.id === c.id && !legacyState
                  ? 'border-volt/50 bg-volt/10 text-ink-primary'
                  : 'border-line-subtle text-ink-secondary hover:border-line hover:text-ink-primary',
              )}
              data-testid={`routines-view-${c.id}`}
            >
              {c.label}
            </button>
          ))}
        </div>
      </div>

      {search.isPending ? (
        <div className="space-y-2">
          {[0, 1, 2, 3, 4].map((i) => (
            <Skeleton key={i} className="h-10 w-full" />
          ))}
        </div>
      ) : search.isError ? (
        <ErrorBlock title="Could not search routines" message={search.error.message} onRetry={() => search.refetch()} />
      ) : rows.length === 0 ? (
        <div className="rounded-xl border border-dashed border-line px-4 py-10 text-center">
          <p className="text-body font-medium text-ink-primary">
            {chipId === 'action' && !urlQ ? 'Nothing is waiting on anyone' : 'No routines match'}
          </p>
          <p className="mt-1 text-caption text-ink-secondary">
            {chipId === 'action' && !urlQ ? (
              <>
                Every draft has been sent and every spec in review is signed.{' '}
                <button type="button" className="text-ink-link hover:underline" onClick={() => update({ view: 'all' })}>
                  Show all routines
                </button>
              </>
            ) : (
              'Try a shorter search, another language, or clear the filters.'
            )}
          </p>
        </div>
      ) : (
        <>
          <div className="overflow-x-auto rounded-xl border border-line-subtle bg-raised" data-testid="search-results">
            <table className="w-full min-w-[820px] border-collapse text-caption">
              <thead className="border-b border-line-subtle">
                <tr>
                  <Th k="name" sort={sort} dir={dir} onSort={onSort}>Routine</Th>
                  <Th k="project" sort={sort} dir={dir} onSort={onSort}>Project</Th>
                  <Th>Language</Th>
                  <Th k="state" sort={sort} dir={dir} onSort={onSort}>State</Th>
                  <Th k="lines" sort={sort} dir={dir} onSort={onSort} right>Lines</Th>
                </tr>
              </thead>
              <tbody className="divide-y divide-line-subtle">
                {rows.map((hit) => (
                  <HitRow key={hit.id} hit={hit} q={urlQ} />
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex items-center justify-between text-caption text-ink-secondary">
            <span data-testid="routines-range">
              {formatInt(from)}–{formatInt(to)} of {formatInt(total)}
            </span>
            <div className="flex gap-1">
              <PageButton disabled={page === 0} onClick={() => update({ page: page > 1 ? String(page - 1) : null }, false)} label="Previous page">
                <ChevronLeft size={14} aria-hidden="true" />
              </PageButton>
              <PageButton disabled={!search.data?.hasMore} onClick={() => update({ page: String(page + 1) }, false)} label="Next page">
                <ChevronRight size={14} aria-hidden="true" />
              </PageButton>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function HitRow({ hit, q }: { hit: SubroutineSearchHit & { sourceLanguage?: string | null }; q: string }) {
  const ql = q.toLowerCase();
  const matchedPath = !!ql && !hit.name.toLowerCase().includes(ql) && !hit.signature.toLowerCase().includes(ql);
  const file = hit.file.relativePath.split('/').pop() ?? hit.file.relativePath;
  const office = /\.(xlsm|xls|xlsb|xlam|docm|dotm|pptm|accdb|mdb)\//i.test(hit.file.relativePath);
  const lines = hit.lineEnd - hit.lineStart + 1;
  return (
    <tr className="hover:bg-sunken">
      <td className="max-w-[420px] px-3 py-2">
        {/* The link's accessible name starts with the routine name (the e2e suite anchors on it). */}
        <Link
          to={`/subroutines/${hit.id}`}
          className="block min-w-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          data-testid={`hit-${hit.id}`}
        >
          <span className="block truncate font-mono font-semibold text-ink-primary" title={hit.signature || hit.name}>
            {hit.name}
          </span>
          <span className="block truncate font-mono text-micro text-ink-tertiary" title={hit.file.relativePath}>
            {' '}
            {file} · L{hit.lineStart}–{hit.lineEnd}
            {matchedPath && <span className="ml-1 text-ink-secondary">· matched in file path</span>}
          </span>
        </Link>
      </td>
      <td className="max-w-[260px] truncate px-3 py-2 text-ink-secondary" title={hit.corpus.name}>
        {hit.corpus.name}
      </td>
      <td className="px-3 py-2 text-ink-secondary">
        {hit.sourceLanguage === 'vb6' && office ? 'VBA' : hit.sourceLanguage ? formatLanguage(hit.sourceLanguage) : '—'}
      </td>
      <td className="px-3 py-2">
        <Badge tone={badgeTone(hit.state)} className="text-[10px]">
          {STATE_LABEL[hit.state] ?? hit.state}
        </Badge>
      </td>
      <td className="px-3 py-2 text-right font-mono tabular-nums text-ink-secondary">{formatInt(lines)}</td>
    </tr>
  );
}

function Th({
  k,
  sort,
  dir,
  onSort,
  right,
  children,
}: {
  k?: SortKey;
  sort?: SortKey;
  dir?: 'asc' | 'desc';
  onSort?: (k: SortKey) => void;
  right?: boolean;
  children: string;
}) {
  const active = k && sort === k;
  return (
    <th
      scope="col"
      aria-sort={active ? (dir === 'asc' ? 'ascending' : 'descending') : undefined}
      className={clsx('px-3 py-2 text-micro font-semibold uppercase tracking-wide text-ink-secondary', right ? 'text-right' : 'text-left')}
    >
      {k && onSort ? (
        <button type="button" onClick={() => onSort(k)} className="inline-flex items-center gap-1 hover:text-ink-primary">
          {children}
          {active && (dir === 'asc' ? <ArrowUp size={11} aria-hidden="true" /> : <ArrowDown size={11} aria-hidden="true" />)}
        </button>
      ) : (
        children
      )}
    </th>
  );
}

function PageButton({
  disabled,
  onClick,
  label,
  children,
}: {
  disabled: boolean;
  onClick: () => void;
  label: string;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      className="grid h-8 w-8 place-items-center rounded-md border border-line-subtle text-ink-primary hover:border-line-strong disabled:opacity-40"
    >
      {children}
    </button>
  );
}

const selectClass =
  'h-9 rounded-md border border-line-subtle bg-raised px-2.5 text-caption text-ink-primary focus:border-line-strong focus:outline-none focus-visible:ring-2 focus-visible:ring-volt';

function badgeTone(state: string): 'draft' | 'review' | 'signed' | 'scaffolded' | 'failed' | 'neutral' {
  switch (state) {
    case 'DRAFT':
    case 'EXTRACTING':
      return 'draft';
    case 'IN_REVIEW':
      return 'review';
    case 'SIGNED':
      return 'signed';
    case 'SCAFFOLDING':
    case 'SCAFFOLDED':
    case 'COMMITTED':
      return 'scaffolded';
    case 'FAILED':
      return 'failed';
    default:
      return 'neutral';
  }
}
