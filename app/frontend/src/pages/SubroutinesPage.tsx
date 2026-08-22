import { useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { ChevronRight, Database, FileCode, Search } from 'lucide-react';
import { api, type SubroutineSearchHit } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { EmptyState } from '@/components/EmptyState';
import { NoResultsIllustration } from '@/illustrations/NoResults';

const STATES = ['PARSED', 'EXTRACTING', 'DRAFT', 'IN_REVIEW', 'SIGNED', 'SCAFFOLDING', 'SCAFFOLDED'];

/**
 * Phase C.12 — Cross-corpus subroutine search. Lists every subroutine
 * across every corpus's current version, with fuzzy name match and
 * corpus/state filters. Drilling in lands on the existing subroutine
 * detail surface, so the extract → sign → scaffold flow continues from
 * here unchanged.
 */
export function SubroutinesPage() {
  // URL is the source of truth — deep-links from the home page's "Next steps"
  // cards (e.g. /subroutines?state=PARSED) need to land with the filter
  // already applied, and copy-paste sharing should work.
  const [searchParams, setSearchParams] = useSearchParams();
  const urlQ = searchParams.get('q') ?? '';
  const urlCorpus = searchParams.get('corpus') ?? '';
  const urlState = searchParams.get('state') ?? '';

  const [rawQ, setRawQ] = useState(urlQ);
  const [q, setQ] = useState(urlQ);
  const [corpus, setCorpus] = useState<string>(urlCorpus);
  const [state, setState] = useState<string>(urlState);

  // Debounce keystrokes to 250 ms so we don't fire a query per character.
  useEffect(() => {
    const t = setTimeout(() => setQ(rawQ.trim()), 250);
    return () => clearTimeout(t);
  }, [rawQ]);

  // Sync state → URL so the address bar reflects what the user is seeing.
  useEffect(() => {
    const next = new URLSearchParams();
    if (q) next.set('q', q);
    if (corpus) next.set('corpus', corpus);
    if (state) next.set('state', state);
    setSearchParams(next, { replace: true });
  }, [q, corpus, state, setSearchParams]);

  const corpora = useQuery({ queryKey: ['corpora'], queryFn: api.listCorpora });

  const search = useQuery({
    queryKey: ['subroutines', { q, corpus, state }],
    queryFn: () =>
      api.searchSubroutines({
        q: q || undefined,
        corpus: corpus || undefined,
        state: state || undefined,
        limit: 100,
      }),
    placeholderData: (prev) => prev,
  });

  const grouped = useMemo(() => {
    const data = search.data?.data ?? [];
    const map = new Map<string, { corpusName: string; hits: SubroutineSearchHit[] }>();
    for (const hit of data) {
      const key = hit.corpus.id;
      if (!map.has(key)) map.set(key, { corpusName: hit.corpus.name, hits: [] });
      map.get(key)!.hits.push(hit);
    }
    return [...map.values()];
  }, [search.data]);

  const total = search.data?.total ?? 0;
  const shown = search.data?.data.length ?? 0;
  const hasMore = search.data?.hasMore ?? false;

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10">
      <header>
        <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
          Phase C.12 · Cross-project search
        </p>
        <h1 className="mt-2 text-display font-semibold text-ink-primary">Subroutines</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Search every subroutine across every project's current version. Click a result to drop
          straight into Stage 2 (subroutine detail) and continue the extract → sign → scaffold flow.
        </p>
      </header>

      <Card>
        <CardBody className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-[1fr_220px_180px]">
            <label className="block">
              <span className="text-caption text-ink-tertiary">Name or signature contains</span>
              <div className="mt-1 flex items-center gap-2 rounded-md border border-border bg-raised px-3 py-2 focus-within:border-accent focus-within:ring-2 focus-within:ring-accent/20">
                <Search className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                <input
                  type="search"
                  value={rawQ}
                  onChange={(e) => setRawQ(e.target.value)}
                  placeholder="e.g. HYBRD, INV_READ, levenberg"
                  className="w-full bg-transparent font-mono text-body text-ink-primary placeholder:text-ink-tertiary focus:outline-none"
                  data-testid="subroutines-search"
                  autoFocus
                />
              </div>
            </label>
            <label className="block">
              <span className="text-caption text-ink-tertiary">Project</span>
              <select
                value={corpus}
                onChange={(e) => setCorpus(e.target.value)}
                className={selectClass}
                data-testid="filter-corpus"
              >
                <option value="">All projects</option>
                {(corpora.data?.data ?? []).map((c) => (
                  <option key={c.id} value={c.id}>{c.name}</option>
                ))}
              </select>
            </label>
            <label className="block">
              <span className="text-caption text-ink-tertiary">State</span>
              <select
                value={state}
                onChange={(e) => setState(e.target.value)}
                className={selectClass}
                data-testid="filter-state"
              >
                <option value="">Any state</option>
                {STATES.map((s) => (
                  <option key={s} value={s}>{s}</option>
                ))}
              </select>
            </label>
          </div>

          <div className="flex items-center justify-between text-caption text-ink-tertiary">
            <span>
              {search.isPending
                ? 'Searching…'
                : total === 0
                  ? 'No matches'
                  : `${total.toLocaleString()} match${total === 1 ? '' : 'es'} · showing ${shown}${hasMore ? ' (refine to see more)' : ''}`}
            </span>
            {(q || corpus || state) && (
              <button
                type="button"
                onClick={() => { setRawQ(''); setQ(''); setCorpus(''); setState(''); }}
                className="font-medium text-accent hover:underline focus-visible:outline-2 focus-visible:outline-ink-primary"
                data-testid="clear-filters"
              >
                Clear filters
              </button>
            )}
          </div>
        </CardBody>
      </Card>

      {search.isPending ? (
        <div className="space-y-3">
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-24 w-full" />
        </div>
      ) : search.isError ? (
        <ErrorBlock
          title="Could not search subroutines"
          message={search.error.message}
          onRetry={() => search.refetch()}
        />
      ) : grouped.length === 0 ? (
        <Card>
          <CardBody>
            <EmptyState
              illustration={<NoResultsIllustration size={140} />}
              title={q || corpus || state ? 'No matches' : 'Type to search'}
              description={
                q || corpus || state
                  ? 'Try a shorter query, or clear the project/state filter.'
                  : 'Start typing a subroutine name (e.g. HYBRD), or pick a project to browse all of its routines.'
              }
            />
          </CardBody>
        </Card>
      ) : (
        <div className="space-y-4" data-testid="search-results">
          {grouped.map((group) => (
            <Card key={group.corpusName}>
              <CardHeader
                title={
                  <span className="inline-flex items-center gap-2">
                    <Database className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                    {group.corpusName}
                  </span>
                }
                description={`${group.hits.length} match${group.hits.length === 1 ? '' : 'es'}`}
              />
              <CardBody className="p-0">
                <ul className="divide-y divide-border-subtle">
                  {group.hits.map((hit) => (
                    <li key={hit.id}>
                      <Link
                        to={`/subroutines/${hit.id}`}
                        className={`group flex items-center gap-3 border-l-2 px-6 py-3 transition-colors duration-fast hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary ${stateEdgeClass(hit.state)}`}
                        data-testid={`hit-${hit.id}`}
                      >
                        <FileCode className="h-4 w-4 shrink-0 text-ink-tertiary" aria-hidden="true" />
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="font-mono font-semibold text-ink-primary">{hit.name}</span>
                            <Badge tone={badgeTone(hit.state)} className="text-[10px]">
                              {hit.state}
                            </Badge>
                            <span className="font-mono text-caption text-ink-tertiary">
                              L{hit.lineStart}–L{hit.lineEnd}
                            </span>
                          </div>
                          <div className="mt-0.5 truncate font-mono text-caption text-ink-tertiary">
                            {hit.file.relativePath}
                          </div>
                        </div>
                        <ChevronRight className="h-4 w-4 shrink-0 text-ink-tertiary opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
                      </Link>
                    </li>
                  ))}
                </ul>
              </CardBody>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}

const selectClass =
  'mt-1 w-full rounded-md border border-border bg-raised px-3 py-2 text-body text-ink-primary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20';

// A 2px colored left-border keyed to subroutine state. Tailwind needs the
// class names to appear verbatim somewhere in the source so the JIT picks
// them up — listing them in this switch keeps that contract.
function stateEdgeClass(state: string): string {
  switch (state) {
    case 'DRAFT':
    case 'EXTRACTING':  return 'border-l-status-draft';
    case 'IN_REVIEW':   return 'border-l-status-review';
    case 'SIGNED':      return 'border-l-status-signed';
    case 'SCAFFOLDING':
    case 'SCAFFOLDED':  return 'border-l-status-scaffolded';
    case 'FAILED':      return 'border-l-status-failed';
    default:            return 'border-l-transparent';
  }
}

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
      return 'scaffolded';
    case 'FAILED':
      return 'failed';
    default:
      return 'neutral';
  }
}
