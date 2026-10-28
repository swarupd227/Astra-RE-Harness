import { useEffect, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowLeft, Layers, Search, X } from 'lucide-react';
import { clsx } from 'clsx';
import { api } from '@/lib/api';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Card } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { Skeleton } from '@/components/Skeleton';
import { CitationChip } from '@/evidence/CitationChip';
import { useEvidence } from '@/evidence/EvidenceProvider';

const PAGE = 50;

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

/**
 * Everything a project can cite: the routines the parser found and the pieces of the artefacts
 * attached to it. Browse by kind, search, open one, or pick several and read them together.
 */
export function EvidencePage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const { open } = useEvidence();

  const kind = params.get('kind') ?? '';
  const path = params.get('path') ?? '';
  const [text, setText] = useState(params.get('q') ?? '');
  const q = useDebounced(text.trim(), 250);
  const [limit, setLimit] = useState(PAGE);
  const [picked, setPicked] = useState<string[]>([]);

  // A new filter starts from the top of the list.
  useEffect(() => setLimit(PAGE), [kind, path, q]);

  // A link can open the panel on arrival: ?open=SQL-f7f575c5,SRC-788da6f2 shows those together.
  const openParam = params.get('open');
  useEffect(() => {
    const ids = (openParam ?? '').split(',').map((s) => s.trim()).filter(Boolean);
    if (id && ids.length > 0) open({ corpusId: id, ids });
  }, [openParam, id, open]);

  const corpus = useQuery({ queryKey: ['corpus', id], queryFn: () => api.getCorpus(id), enabled: !!id });
  const evidence = useQuery({
    queryKey: ['evidence-list', id, kind, q, path, limit],
    queryFn: () => api.listEvidence(id, { kind: kind || undefined, q: q || undefined, path: path || undefined, limit }),
    enabled: !!id,
    placeholderData: keepPreviousData,
  });

  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  const toggle = (shortId: string) =>
    setPicked((p) => (p.includes(shortId) ? p.filter((x) => x !== shortId) : [...p, shortId]));

  const data = evidence.data;
  const filtered = !!(kind || q || path);
  const totalAll = data?.kinds.reduce((n, k) => n + k.count, 0) ?? 0;

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup" data-testid="evidence-page">
      <PageHero
        eyebrow="Project"
        title="Evidence"
        lead={
          <span className="text-caption text-ink-secondary">
            {corpus.data ? `${corpus.data.name} · ` : ''}
            Everything a statement can cite. Open one, or pick several to read them together.
          </span>
        }
        actions={
          <>
            <Button variant="secondary" onClick={() => navigate(`/corpora/${id}`)} data-testid="evidence-back">
              <ArrowLeft className="h-4 w-4" aria-hidden="true" />
              Project
            </Button>
            <Button
              variant={picked.length > 1 ? 'primary' : 'secondary'}
              disabled={picked.length < 2}
              onClick={() => open({ corpusId: id, ids: picked })}
              data-testid="evidence-show-together"
            >
              <Layers className="h-4 w-4" aria-hidden="true" />
              Show together{picked.length > 1 ? ` (${picked.length})` : ''}
            </Button>
          </>
        }
      />

      <div className="flex flex-wrap items-center gap-3">
        <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Filter by kind">
          <KindPill active={!kind} onClick={() => setParam('kind', '')} testid="evidence-kind-all">
            All <span className="tabular-nums text-ink-tertiary">{totalAll.toLocaleString()}</span>
          </KindPill>
          {data?.kinds.map((k) => (
            <KindPill key={k.kind} active={kind === k.kind} onClick={() => setParam('kind', kind === k.kind ? '' : k.kind)} testid={`evidence-kind-${k.kind}`}>
              {k.label} <span className="tabular-nums text-ink-tertiary">{k.count.toLocaleString()}</span>
            </KindPill>
          ))}
        </div>
        <label className="relative ml-auto w-full sm:w-72">
          <span className="sr-only">Search evidence</span>
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-ink-tertiary" aria-hidden="true" />
          <input
            type="search"
            value={text}
            onChange={(e) => setText(e.target.value)}
            onBlur={() => setParam('q', q)}
            placeholder="Search label, file, text…"
            className="h-9 w-full rounded-lg border border-line bg-raised pl-8 pr-3 text-body text-ink-primary placeholder:text-ink-tertiary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            data-testid="evidence-search"
          />
        </label>
      </div>

      {path && (
        <p className="flex items-center gap-2 text-caption text-ink-secondary" data-testid="evidence-path-filter">
          Only <span className="font-mono text-ink-primary">{path}</span>
          <button
            type="button"
            onClick={() => setParam('path', '')}
            className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          >
            <X className="h-3 w-3" aria-hidden="true" />
            Show all files
          </button>
        </p>
      )}

      {evidence.isPending ? (
        <div className="space-y-2" data-testid="evidence-loading">
          {Array.from({ length: 6 }).map((_, i) => <Skeleton key={i} className="h-14 w-full" />)}
        </div>
      ) : evidence.isError ? (
        <ErrorBlock title="Could not load the evidence" message={evidence.error.message} onRetry={() => void evidence.refetch()} />
      ) : data!.total === 0 ? (
        filtered ? (
          <Card className="p-8 text-center" data-testid="evidence-no-match">
            <p className="text-body font-medium text-ink-primary">Nothing matches.</p>
            <p className="mt-1 text-body text-ink-secondary">Try a different search, or clear the filters.</p>
            <Button
              variant="secondary"
              className="mt-4"
              onClick={() => {
                setText('');
                setParams(new URLSearchParams(), { replace: true });
              }}
            >
              Clear filters
            </Button>
          </Card>
        ) : (
          <Card className="p-8 text-center" data-testid="evidence-empty">
            <p className="text-body font-medium text-ink-primary">This project has no evidence yet.</p>
            <p className="mt-1 text-body text-ink-secondary">
              Evidence is built from the routines of the source code and from the artefacts you add on the project page.
            </p>
            <Link
              to={`/corpora/${id}`}
              className="mt-4 inline-block text-body text-ink-primary underline underline-offset-2 focus-visible:outline-2 focus-visible:outline-volt"
            >
              Go to the project
            </Link>
          </Card>
        )
      ) : (
        <Card className={clsx('overflow-hidden transition-opacity', evidence.isPlaceholderData && 'opacity-60')}>
          <ul className="divide-y divide-line-subtle" data-testid="evidence-list">
            {data!.data.map((e) => (
              <li key={e.shortId} className="flex items-center gap-3 px-4 py-2.5" data-testid="evidence-row" data-short-id={e.shortId}>
                <input
                  type="checkbox"
                  checked={picked.includes(e.shortId)}
                  onChange={() => toggle(e.shortId)}
                  aria-label={`Select ${e.shortId} to read together`}
                  className="h-4 w-4 shrink-0 rounded border-line accent-volt focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                  data-testid="evidence-pick"
                />
                <CitationChip corpusId={id} id={e.shortId} siblings={picked.length > 1 && picked.includes(e.shortId) ? picked : undefined} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-body text-ink-primary" title={e.label}>{e.label}</span>
                  <span className="block truncate font-mono text-micro text-ink-tertiary" title={`${e.path} ${e.locator}`}>
                    {e.path} · {e.locator}
                  </span>
                </span>
                <Badge tone="neutral" className="hidden sm:inline-flex">{e.kindLabel}</Badge>
                <Badge tone={e.trustTier === 'structured' ? 'info' : 'neutral'} className="hidden md:inline-flex">
                  {e.trustTier === 'structured' ? 'Structured' : 'Narrative'}
                </Badge>
              </li>
            ))}
          </ul>
          <div className="flex items-center justify-between gap-3 border-t border-line-subtle px-4 py-3">
            <p className="text-caption text-ink-secondary" data-testid="evidence-count">
              Showing {data!.data.length.toLocaleString()} of {data!.total.toLocaleString()}
            </p>
            {data!.data.length < data!.total && (
              <Button variant="secondary" size="sm" loading={evidence.isFetching} onClick={() => setLimit((l) => l + PAGE)} data-testid="evidence-more">
                Show more
              </Button>
            )}
          </div>
        </Card>
      )}
    </div>
  );
}

function KindPill({
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
        'inline-flex h-8 items-center gap-1.5 rounded-full border px-3 text-caption transition-colors duration-fast focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
        active ? 'border-volt bg-volt/10 text-ink-primary' : 'border-line text-ink-secondary hover:border-line-strong hover:text-ink-primary',
      )}
    >
      {children}
    </button>
  );
}
