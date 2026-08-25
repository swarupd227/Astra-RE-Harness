import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { Cog, FileCode, GitBranch } from 'lucide-react';
import { api, type ScaffoldSummary } from '@/lib/api';
import { Card, CardBody } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { Skeleton } from '@/components/Skeleton';
import { EmptyState } from '@/components/EmptyState';
import { prettySchema, prettyStack } from '@/lib/targetStacks';
import { formatState } from '@/lib/labels';

/**
 * Generated-code index — every scaffold package the platform has produced,
 * across every routine and every target stack.
 *
 * This surface did not exist before: the only way back to a piece of
 * generated code was the spec-review CTA or the live-generation success
 * screen, both of which require remembering which routine produced it.
 * Close the tab and the artifact was effectively unreachable. This page is
 * the answer to "where do I see the code I already generated" without
 * knowing anything except that it was generated at some point.
 */
export function ScaffoldsPage() {
  const list = useQuery({ queryKey: ['scaffolds'], queryFn: () => api.listScaffolds() });

  const [stackFilter, setStackFilter] = useState<string>('all');
  const [queryText, setQueryText] = useState('');

  const stacks = useMemo(
    () => [...new Set((list.data?.data ?? []).map((s) => s.targetPlatform))].sort(),
    [list.data],
  );
  const filtered = useMemo(() => {
    const rows = list.data?.data ?? [];
    const q = queryText.trim().toLowerCase();
    return rows.filter((s) => {
      if (stackFilter !== 'all' && s.targetPlatform !== stackFilter) return false;
      if (!q) return true;
      return (
        s.routineName.toLowerCase().includes(q) ||
        s.corpusName.toLowerCase().includes(q) ||
        s.sourcePath.toLowerCase().includes(q)
      );
    });
  }, [list.data, stackFilter, queryText]);

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup">
      <PageHero
        tone="amber"
        eyebrow="Platform"
        title="Generated code"
        lead="Every package Claude has scaffolded from a signed spec, across every target stack. Open one to read the files, or jump to its validation report."
      />

      {list.isPending ? (
        <div className="space-y-3">
          <Skeleton className="h-12 w-full" />
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-24 w-full" />
        </div>
      ) : list.isError ? (
        <ErrorBlock
          title="Could not load generated code"
          message={list.error.message}
          onRetry={() => list.refetch()}
        />
      ) : list.data.data.length === 0 ? (
        <Card>
          <CardBody>
            <EmptyState
              illustration={<Cog className="h-12 w-12 text-ink-tertiary" aria-hidden="true" />}
              title="Nothing generated yet"
              description="Sign off a spec, pick a target stack on the routine page, and generate — the package will show up here."
            />
          </CardBody>
        </Card>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-3">
            <input
              type="search"
              value={queryText}
              onChange={(e) => setQueryText(e.target.value)}
              placeholder="Filter by routine, project, or path…"
              className="h-9 w-72 rounded-md border border-border-subtle bg-raised px-3 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-accent focus:outline-none"
              data-testid="scaffolds-search"
            />
            <div className="flex items-center gap-1.5" role="tablist" aria-label="Filter by target stack">
              <FilterPill active={stackFilter === 'all'} onClick={() => setStackFilter('all')}>
                All stacks
              </FilterPill>
              {stacks.map((s) => (
                <FilterPill key={s} active={stackFilter === s} onClick={() => setStackFilter(s)}>
                  {prettyStack(s)}
                </FilterPill>
              ))}
            </div>
            <span className="ml-auto font-mono text-caption text-ink-tertiary">
              {filtered.length} of {list.data.data.length}
            </span>
          </div>

          {filtered.length === 0 ? (
            <Card>
              <CardBody>
                <p className="text-body text-ink-secondary">No generated packages match this filter.</p>
              </CardBody>
            </Card>
          ) : (
            <ul className="space-y-3">
              {filtered.map((s) => (
                <li key={s.id}>
                  <ScaffoldRow scaffold={s} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}

function FilterPill({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={
        active
          ? 'rounded-full bg-accent-muted px-3 py-1 font-mono text-caption font-semibold text-accent'
          : 'rounded-full px-3 py-1 font-mono text-caption text-ink-tertiary hover:bg-sunken hover:text-ink-secondary'
      }
    >
      {children}
    </button>
  );
}

function ScaffoldRow({ scaffold: s }: { scaffold: ScaffoldSummary }) {
  return (
    <Link to={`/scaffolds/${s.id}`} className="block focus-visible:outline-2 focus-visible:outline-ink-primary">
      <Card interactive>
        <CardBody>
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="flex items-start gap-3">
              <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-md bg-[#F2E5C2] text-status-scaffolded">
                <Cog className="h-5 w-5" aria-hidden="true" />
              </span>
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <h2 className="font-mono text-h-md font-semibold text-ink-primary">{s.routineName}</h2>
                  <span
                    className="rounded-sm bg-accent-muted px-1.5 py-0.5 font-mono text-[10px] font-semibold uppercase text-accent"
                    data-testid={`scaffold-stack-${s.targetPlatform}`}
                  >
                    {prettyStack(s.targetPlatform)}
                  </span>
                </div>
                <p className="mt-0.5 truncate font-mono text-caption text-ink-tertiary">
                  {s.corpusName} · {s.sourcePath}
                  {s.sourceLanguage && ` · ${prettySchema(s.sourceLanguage)}`}
                </p>
              </div>
            </div>
            <Badge tone={s.state === 'COMMITTED' ? 'signed' : 'scaffolded'}>{formatState(s.state)}</Badge>
          </div>
          <dl className="mt-4 grid grid-cols-2 gap-4 text-body sm:grid-cols-4">
            <div>
              <dt className="text-caption text-ink-tertiary">Files</dt>
              <dd className="mt-0.5 font-mono text-body font-semibold text-ink-primary">
                <FileCode className="mr-1 inline h-3.5 w-3.5 -translate-y-0.5 text-ink-tertiary" aria-hidden="true" />
                {s.fileCount}
              </dd>
            </div>
            <div>
              <dt className="text-caption text-ink-tertiary">TODOs</dt>
              <dd className="mt-0.5 font-mono text-body font-semibold text-ink-primary">{s.todoCount}</dd>
            </div>
            <div>
              <dt className="text-caption text-ink-tertiary">Generated</dt>
              <dd className="mt-0.5 font-mono text-caption text-ink-secondary">
                {new Date(s.generatedAt).toLocaleString()}
              </dd>
            </div>
            <div>
              <dt className="text-caption text-ink-tertiary">Git</dt>
              <dd className="mt-0.5 font-mono text-caption text-ink-secondary">
                {s.gitCommitHash ? (
                  <span className="inline-flex items-center gap-1">
                    <GitBranch className="h-3 w-3" aria-hidden="true" /> {s.gitBranch}
                  </span>
                ) : (
                  '—'
                )}
              </dd>
            </div>
          </dl>
        </CardBody>
      </Card>
    </Link>
  );
}
