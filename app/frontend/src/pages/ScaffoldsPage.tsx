import { useMemo, useState } from 'react';
import { useQueries } from '@tanstack/react-query';
import { useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { AlertTriangle, CheckCircle2, ChevronDown, CircleDashed, Cog, GitBranch, Search, XCircle } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type ScaffoldSummary } from '@/lib/api';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { Skeleton } from '@/components/Skeleton';
import { EmptyState } from '@/components/EmptyState';
import { prettySchema, prettyStack } from '@/lib/targetStacks';
import { absoluteTime, formatInt, relativeTime } from '@/workspace/format';

type Gate = 'passed' | 'failed' | 'running' | 'none';
type GateFilter = '' | 'failing' | 'unvalidated' | 'passing';

const OFFICE = /\.(xlsm|xls|xlsb|xlam|docm|dotm|pptm|accdb|mdb)\//i;

function gate(status: string | null | undefined): Gate {
  const s = (status ?? '').toUpperCase();
  if (s === 'PASSED') return 'passed';
  if (s === 'FAILED' || s === 'ERRORED') return 'failed';
  if (s === 'RUNNING') return 'running';
  return 'none';
}
function overall(s: ScaffoldSummary): GateFilter {
  const c = gate(s.gates?.compile);
  const t = gate(s.gates?.tests);
  if (c === 'failed' || t === 'failed') return 'failing';
  if (c === 'passed' && (t === 'passed' || t === 'none')) return 'passing';
  return 'unvalidated';
}
function language(s: ScaffoldSummary): string {
  if (!s.sourceLanguage) return '—';
  return s.sourceLanguage === 'vb6' && OFFICE.test(s.sourcePath) ? 'VBA' : prettySchema(s.sourceLanguage);
}

/**
 * Generated code, grouped by project: whether each package builds and passes its tests is the first thing shown, and
 * the projects with a failing package come first. Stack, gate status, TODOs and search live in the URL.
 */
export function ScaffoldsPage() {
  const list = useQuery({ queryKey: ['scaffolds'], queryFn: () => api.listScaffolds() });
  const [params, setParams] = useSearchParams();
  const stack = params.get('stack') ?? 'all';
  const gateFilter = (params.get('gate') ?? '') as GateFilter;
  const todos = params.get('todos') === '1';
  const q = (params.get('q') ?? '').toLowerCase();
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

  const all = list.data?.data ?? [];
  const stackCounts = useMemo(() => {
    const m = new Map<string, number>();
    for (const s of all) m.set(s.targetPlatform, (m.get(s.targetPlatform) ?? 0) + 1);
    return [...m.entries()].sort((a, b) => b[1] - a[1]);
  }, [all]);

  const filtered = useMemo(
    () =>
      all.filter(
        (s) =>
          (stack === 'all' || s.targetPlatform === stack) &&
          (!gateFilter || overall(s) === gateFilter) &&
          (!todos || s.todoCount > 0) &&
          (!q ||
            s.routineName.toLowerCase().includes(q) ||
            s.corpusName.toLowerCase().includes(q) ||
            s.sourcePath.toLowerCase().includes(q)),
      ),
    [all, stack, gateFilter, todos, q],
  );

  const groups = useMemo(() => {
    const m = new Map<string, { id: string; name: string; rows: ScaffoldSummary[] }>();
    for (const s of filtered) {
      if (!m.has(s.corpusId)) m.set(s.corpusId, { id: s.corpusId, name: s.corpusName, rows: [] });
      m.get(s.corpusId)!.rows.push(s);
    }
    return [...m.values()].sort((a, b) => {
      const fa = a.rows.some((r) => overall(r) === 'failing') ? 0 : 1;
      const fb = b.rows.some((r) => overall(r) === 'failing') ? 0 : 1;
      if (fa !== fb) return fa - fb;
      return (b.rows[0]?.generatedAt ?? '').localeCompare(a.rows[0]?.generatedAt ?? '');
    });
  }, [filtered]);

  const passing = all.filter((s) => overall(s) === 'passing').length;
  const failing = all.filter((s) => overall(s) === 'failing').length;
  const projectCount = new Set(all.map((s) => s.corpusId)).size;

  return (
    <div className="mx-auto max-w-[1280px] space-y-5 p-4 lg:p-6 fadeup">
      <PageHero
        tone="amber"
        eyebrow="Work"
        title="Generated code"
        lead={
          list.data
            ? `${formatInt(all.length)} packages across ${formatInt(projectCount)} project${projectCount === 1 ? '' : 's'} · ${formatInt(passing)} passing${failing ? ` · ${formatInt(failing)} failing` : ''}`
            : 'Code generated from signed specs, with its build and test results.'
        }
      />

      {list.isPending ? (
        <div className="space-y-3">
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : list.isError ? (
        <ErrorBlock title="Could not load generated code" message={list.error.message} onRetry={() => list.refetch()} />
      ) : all.length === 0 ? (
        <div className="rounded-xl border border-line-subtle bg-raised p-6">
          <EmptyState
            illustration={<Cog className="h-12 w-12 text-ink-tertiary" aria-hidden="true" />}
            title="Nothing generated yet"
            description="Sign off a spec, pick a target stack on the routine page, and generate — the package will show up here."
          />
        </div>
      ) : (
        <>
          <div className="space-y-2.5">
            <div className="flex flex-wrap items-center gap-2">
              <label className="relative min-w-[240px] flex-1">
                <span className="sr-only">Filter by routine, project or path</span>
                <Search size={14} className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-ink-tertiary" aria-hidden="true" />
                <input
                  type="search"
                  value={params.get('q') ?? ''}
                  onChange={(e) => set('q', e.target.value || null)}
                  placeholder="Filter by routine, project, or path…"
                  className="h-9 w-full rounded-md border border-line-subtle bg-raised pl-8 pr-3 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-line-strong focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                  data-testid="scaffolds-search"
                />
              </label>
              <select
                value={gateFilter}
                onChange={(e) => set('gate', e.target.value || null)}
                aria-label="Build and test status"
                className="h-9 rounded-md border border-line-subtle bg-raised px-2.5 text-caption text-ink-primary focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                data-testid="scaffolds-filter-gate"
              >
                <option value="">Any result</option>
                <option value="failing">Failing</option>
                <option value="unvalidated">Not validated</option>
                <option value="passing">Passing</option>
              </select>
              <label className="inline-flex cursor-pointer items-center gap-2 text-caption text-ink-secondary">
                <input
                  type="checkbox"
                  checked={todos}
                  onChange={(e) => set('todos', e.target.checked ? '1' : null)}
                  className="accent-volt"
                  data-testid="scaffolds-filter-todos"
                />
                Has TODOs
              </label>
            </div>
            <div className="flex flex-wrap gap-1.5" role="group" aria-label="Target stack">
              <StackChip active={stack === 'all'} onClick={() => set('stack', null)} label="All stacks" count={all.length} testid="scaffold-stack-all" />
              {stackCounts.map(([s, n]) => (
                <StackChip
                  key={s}
                  active={stack === s}
                  onClick={() => set('stack', s)}
                  label={prettyStack(s)}
                  count={n}
                  testid={`scaffold-stack-${s}`}
                />
              ))}
            </div>
          </div>

          <LivePreviews scaffolds={all.filter((x) => x.targetPlatform === 'react-python').slice(0, 30)} />

          {groups.length === 0 ? (
            <p className="rounded-xl border border-dashed border-line px-4 py-8 text-center text-caption text-ink-secondary">
              No packages match.{' '}
              <button type="button" className="text-ink-link hover:underline" onClick={() => setParams({}, { replace: true })}>
                Clear filters
              </button>
            </p>
          ) : (
            <div className="space-y-3" data-testid="scaffolds-groups">
              {groups.map((g) => (
                <ProjectGroup key={g.id} name={g.name} id={g.id} rows={g.rows} />
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

function StackChip({ active, onClick, label, count, testid }: { active: boolean; onClick: () => void; label: string; count: number; testid: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={clsx(
        'rounded-full border px-3 py-1 text-caption transition-colors',
        active ? 'border-volt/50 bg-volt/10 text-ink-primary' : 'border-line-subtle text-ink-secondary hover:border-line hover:text-ink-primary',
      )}
      data-testid={testid}
    >
      {label} <span className="font-mono tabular-nums">{formatInt(count)}</span>
    </button>
  );
}

function ProjectGroup({ id, name, rows }: { id: string; name: string; rows: ScaffoldSummary[] }) {
  const failing = rows.filter((r) => overall(r) === 'failing').length;
  const builds = rows.filter((r) => gate(r.gates?.compile) === 'passed').length;
  const tests = rows.filter((r) => gate(r.gates?.tests) === 'passed').length;
  const [open, setOpen] = useState(rows.length <= 6 || failing > 0);
  const stacks = [...new Set(rows.map((r) => prettyStack(r.targetPlatform)))].join(', ');
  return (
    <section className={clsx('rounded-xl border bg-raised', failing ? 'border-status-failed/40' : 'border-line-subtle')} data-testid={`scaffold-group-${id}`}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 px-4 py-3 text-left"
      >
        <ChevronDown size={14} className={clsx('shrink-0 text-ink-tertiary transition-transform', !open && '-rotate-90')} aria-hidden="true" />
        <span className="min-w-0 truncate text-body font-semibold text-ink-primary">{name}</span>
        <span className="text-caption text-ink-secondary">
          {formatInt(rows.length)} package{rows.length === 1 ? '' : 's'} · {stacks} · {language(rows[0])}
        </span>
        <span className="ml-auto flex items-center gap-3 text-caption">
          <span className="text-ink-secondary">
            Build <strong className="font-mono text-ink-primary">{builds}/{rows.length}</strong>
          </span>
          <span className="text-ink-secondary">
            Tests <strong className="font-mono text-ink-primary">{tests}/{rows.length}</strong>
          </span>
          {failing > 0 && <span className="font-medium text-status-failed">{failing} failing</span>}
        </span>
      </button>
      {open && (
        <div className="overflow-x-auto border-t border-line-subtle">
          <table className="w-full min-w-[760px] border-collapse text-caption">
            <thead>
              <tr className="text-micro uppercase tracking-wide text-ink-secondary">
                <th scope="col" className="px-4 py-2 text-left font-semibold">Routine</th>
                <th scope="col" className="px-3 py-2 text-left font-semibold">Stack</th>
                <th scope="col" className="px-3 py-2 text-left font-semibold">Build</th>
                <th scope="col" className="px-3 py-2 text-left font-semibold">Tests</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Files</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">TODOs</th>
                <th scope="col" className="px-3 py-2 text-left font-semibold">Generated</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line-subtle">
              {rows.map((s) => (
                <tr key={s.id} className="hover:bg-sunken" data-testid={`scaffold-row-${s.id}`}>
                  <td className="max-w-[340px] px-4 py-2">
                    <Link to={`/scaffolds/${s.id}`} className="block truncate font-mono font-semibold text-ink-primary hover:underline" title={s.sourcePath}>
                      {s.routineName}
                    </Link>
                    {s.gitBranch && (
                      <span className="mt-0.5 inline-flex items-center gap-1 font-mono text-micro text-ink-tertiary">
                        <GitBranch size={11} aria-hidden="true" /> {s.gitBranch}
                        {s.gitCommitHash ? ` @ ${s.gitCommitHash.slice(0, 7)}` : ''}
                      </span>
                    )}
                  </td>
                  <td className="px-3 py-2 text-ink-secondary">{prettyStack(s.targetPlatform)}</td>
                  <td className="px-3 py-2"><GateCell g={gate(s.gates?.compile)} /></td>
                  <td className="px-3 py-2"><GateCell g={gate(s.gates?.tests)} /></td>
                  <td className="px-3 py-2 text-right font-mono tabular-nums text-ink-secondary">{formatInt(s.fileCount)}</td>
                  <td className={clsx('px-3 py-2 text-right font-mono tabular-nums', s.todoCount > 0 ? 'text-status-warn' : 'text-ink-tertiary')}>
                    {s.todoCount > 0 ? (
                      <span className="inline-flex items-center gap-1">
                        <AlertTriangle size={11} aria-hidden="true" />
                        {formatInt(s.todoCount)}
                      </span>
                    ) : (
                      '0'
                    )}
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-ink-secondary" title={absoluteTime(s.generatedAt)}>
                    {relativeTime(s.generatedAt)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function GateCell({ g }: { g: Gate }) {
  if (g === 'passed')
    return (
      <span className="inline-flex items-center gap-1 text-status-ok">
        <CheckCircle2 size={13} aria-hidden="true" /> Passed
      </span>
    );
  if (g === 'failed')
    return (
      <span className="inline-flex items-center gap-1 text-status-failed">
        <XCircle size={13} aria-hidden="true" /> Failed
      </span>
    );
  if (g === 'running') return <span className="text-ink-secondary">Running…</span>;
  return (
    <span className="inline-flex items-center gap-1 text-ink-tertiary">
      <CircleDashed size={13} aria-hidden="true" /> Not run
    </span>
  );
}

/**
 * Running previews of generated web apps, one click away — the most convincing artefact on the platform was only
 * reachable by opening each package. Asks each React + Python package (at most 30) whether a preview is up.
 */
function LivePreviews({ scaffolds }: { scaffolds: ScaffoldSummary[] }) {
  const results = useQueries({
    queries: scaffolds.map((s) => ({
      queryKey: ['preview', s.id],
      queryFn: () => api.getPreview(s.id),
      staleTime: 60_000,
      retry: 0,
    })),
  });
  const live = scaffolds
    .map((s, i) => ({ s, p: results[i]?.data }))
    .filter((x) => x.p && x.p.state === 'READY' && x.p.url);
  if (live.length === 0) return null;
  return (
    <section className="rounded-xl border border-status-ok/40 bg-status-ok/5 px-4 py-3" aria-label="Live previews" data-testid="scaffolds-live-previews">
      <p className="text-caption font-semibold text-ink-primary">
        Live previews <span className="font-normal text-ink-secondary">— generated apps running now</span>
      </p>
      <ul className="mt-2 flex flex-wrap gap-2">
        {live.map(({ s, p }) => (
          <li key={s.id}>
            <a
              href={p!.url!}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1.5 rounded-md border border-line-subtle bg-raised px-2.5 py-1 text-caption text-ink-primary hover:border-line-strong"
              title={`${s.corpusName} · ${s.routineName}${p!.expiresAt ? ` · closes ${relativeTime(p!.expiresAt)}` : ''}`}
            >
              <span className="h-1.5 w-1.5 rounded-full bg-status-ok" aria-hidden="true" />
              <span className="font-mono">{s.routineName}</span>
              <span className="text-ink-secondary">· {s.corpusName}</span>
            </a>
          </li>
        ))}
      </ul>
    </section>
  );
}
