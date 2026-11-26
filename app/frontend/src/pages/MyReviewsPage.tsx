import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { ArrowRight, ChevronRight, MessageSquare, ShieldCheck, Timer } from 'lucide-react';
import { clsx } from 'clsx';
import { myReviewsApi, type MyReviewItem } from '@/lib/api';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { absoluteTime, formatInt, relativeTime } from '@/workspace/format';

const DAY = 24 * 60 * 60 * 1000;
type View = 'open' | 'awaiting' | 'progress' | 'signed';
type Sort = 'age' | 'effort' | 'project';

/** "DataFabricExtractorAttribute.DataFabricExtractorAttribute" → shorter, cut at a dot, full name on hover. */
function shortName(name: string, max = 48): string {
  if (name.length <= max) return name;
  const parts = name.split('.');
  return parts.length > 1 ? `…${parts.slice(-1)[0]}` : `${name.slice(0, max - 1)}…`;
}

function waitedDays(item: MyReviewItem): number {
  const t = Date.parse(item.routedAt || item.updatedAt);
  return Number.isFinite(t) ? Math.floor((Date.now() - t) / DAY) : 0;
}

/**
 * The review queue: specs waiting for an SME. The count pills filter, the oldest unstarted spec is one click away,
 * and each card says how long it has waited and how much is left. (Assignment to a named reviewer is a separate
 * feature — this page is honest that it lists every review, not "yours".)
 */
export function MyReviewsPage() {
  const reviews = useQuery({ queryKey: ['my-reviews'], queryFn: myReviewsApi.list });
  const [params, setParams] = useSearchParams();
  const view = (params.get('view') ?? 'open') as View;
  const sort = (params.get('sort') ?? 'age') as Sort;
  const navigate = useNavigate();
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

  const r = reviews.data;
  const open = useMemo(() => {
    if (!r) return [];
    const items =
      view === 'awaiting' ? r.awaiting : view === 'progress' ? r.inProgress : view === 'signed' ? [] : [...r.awaiting, ...r.inProgress];
    const cmp: Record<Sort, (a: MyReviewItem, b: MyReviewItem) => number> = {
      age: (a, b) => waitedDays(b) - waitedDays(a),
      effort: (a, b) =>
        a.estimatedReviewMinutes * (1 - a.processedClaims / Math.max(1, a.totalClaims)) -
        b.estimatedReviewMinutes * (1 - b.processedClaims / Math.max(1, b.totalClaims)),
      project: (a, b) => a.corpusName.localeCompare(b.corpusName) || waitedDays(b) - waitedDays(a),
    };
    return [...items].sort(cmp[sort]);
  }, [r, view, sort]);

  if (reviews.isPending) {
    return (
      <div className="mx-auto max-w-[1100px] space-y-4 p-4 lg:p-6">
        <Skeleton className="h-12 w-64" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    );
  }
  if (reviews.isError || !r) {
    return (
      <div className="mx-auto max-w-[1100px] p-4 lg:p-6">
        <ErrorBlock title="Could not load reviews" message={reviews.error?.message ?? ''} onRetry={() => reviews.refetch()} />
      </div>
    );
  }

  // The oldest spec nobody has started is the obvious next one.
  const next = [...r.awaiting].sort((a, b) => waitedDays(b) - waitedDays(a))[0] ?? [...r.inProgress].sort((a, b) => waitedDays(b) - waitedDays(a))[0];
  const signed = [...r.signed].sort((a, b) =>
    (b.signature?.signedAt ?? b.updatedAt).localeCompare(a.signature?.signedAt ?? a.updatedAt),
  );

  return (
    <div className="mx-auto max-w-[1100px] space-y-5 p-4 lg:p-6 fadeup">
      <header className="flex flex-wrap items-start justify-between gap-4 border-b border-line-subtle pb-4">
        <div>
          <p className="label">Work</p>
          <h1 className="mt-1 text-h-lg font-semibold tracking-tight text-ink-primary">My reviews</h1>
          <p className="mt-1 text-body text-ink-secondary">Specs waiting for an SME to review and sign — every reviewer sees the same queue.</p>
        </div>
        {next && (
          <button
            type="button"
            onClick={() => navigate(`/subroutines/${next.subroutineId}/review`)}
            className="inline-flex h-9 items-center gap-1.5 rounded-md bg-volt px-3 text-caption font-semibold text-on-volt hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt focus-visible:ring-offset-2 focus-visible:ring-offset-canvas"
            data-testid="start-next-review"
            title={`${next.subroutineName} · ${next.corpusName}`}
          >
            Start next review
            <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
          </button>
        )}
      </header>

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex flex-wrap gap-1.5" role="group" aria-label="Show">
          <Pill active={view === 'open'} onClick={() => set('view', null)} n={r.counts.awaiting + r.counts.inProgress} label="Open" testid="reviews-view-open" />
          <Pill active={view === 'awaiting'} onClick={() => set('view', 'awaiting')} n={r.counts.awaiting} label="Not started" testid="reviews-view-awaiting" />
          <Pill active={view === 'progress'} onClick={() => set('view', 'progress')} n={r.counts.inProgress} label="In progress" testid="reviews-view-progress" />
          <Pill active={view === 'signed'} onClick={() => set('view', 'signed')} n={r.counts.signed} label="Signed" testid="reviews-view-signed" />
        </div>
        {view !== 'signed' && (
          <select
            value={sort}
            onChange={(e) => set('sort', e.target.value === 'age' ? null : e.target.value)}
            aria-label="Sort"
            className="ml-auto h-8 rounded-md border border-line-subtle bg-raised px-2 text-caption text-ink-primary focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            data-testid="reviews-sort"
          >
            <option value="age">Waiting longest</option>
            <option value="effort">Least work left</option>
            <option value="project">Project</option>
          </select>
        )}
      </div>

      {view !== 'signed' &&
        (open.length === 0 ? (
          <p className="rounded-xl border border-dashed border-line px-4 py-8 text-center text-caption text-ink-secondary">
            Nothing waiting. When an engineer sends a draft spec for review it appears here with an effort estimate.
          </p>
        ) : (
          <ul className="space-y-2" data-testid="reviews-open">
            {open.map((it) => (
              <li key={it.specId}>
                <ReviewRow item={it} />
              </li>
            ))}
          </ul>
        ))}

      <SignedGroup items={signed} forceOpen={view === 'signed'} />
    </div>
  );
}

function Pill({ active, onClick, n, label, testid }: { active: boolean; onClick: () => void; n: number; label: string; testid: string }) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={clsx(
        'rounded-full border px-3 py-1 text-caption transition-colors',
        active ? 'border-volt/50 bg-volt/10 text-ink-primary' : 'border-line-subtle text-ink-secondary hover:border-line hover:text-ink-primary',
      )}
      data-testid={testid}
    >
      {label} <span className="font-mono tabular-nums">{formatInt(n)}</span>
    </button>
  );
}

function ReviewRow({ item }: { item: MyReviewItem }) {
  const days = waitedDays(item);
  const pct = item.totalClaims ? Math.round((item.processedClaims / item.totalClaims) * 100) : 0;
  const file = item.relativePath.split('/').pop() ?? item.relativePath;
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-2 rounded-xl border border-line-subtle bg-raised px-4 py-3 transition-colors hover:border-line-strong">
      <Link
        to={`/subroutines/${item.subroutineId}/review`}
        className="min-w-0 flex-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
        data-testid={`review-${item.specId}`}
      >
        <span className="block truncate font-mono text-body font-semibold text-ink-primary" title={item.subroutineName}>
          {shortName(item.subroutineName)}
        </span>
        <span className="block truncate text-caption text-ink-secondary" title={item.relativePath}>
          {item.corpusName} · {file}
        </span>
      </Link>
      <span className="text-caption text-ink-secondary">
        {item.invariantsCount} invariants · {item.edgeCaseCount} edge cases · {item.openQuestionCount} questions
      </span>
      <span className="w-40">
        <span className="flex items-baseline justify-between text-caption">
          <span className="text-ink-primary">{item.processedClaims === 0 ? 'Not started' : `${item.processedClaims} of ${item.totalClaims} done`}</span>
          <span className="inline-flex items-center gap-1 text-ink-tertiary">
            <Timer className="h-3 w-3" aria-hidden="true" />~{item.estimatedReviewMinutes} min
          </span>
        </span>
        <span className="mt-1 block h-1 overflow-hidden rounded-full bg-sunken">
          <span className="block h-full bg-status-ok" style={{ width: `${pct}%` }} />
        </span>
      </span>
      <span
        className={clsx('w-24 text-right text-caption', days >= 7 ? 'font-medium text-status-warn' : 'text-ink-secondary')}
        title={`Sent for review ${absoluteTime(item.routedAt || item.updatedAt)}`}
      >
        {days === 0 ? 'Today' : `Waiting ${days} d`}
      </span>
      <Link
        to={`/subroutines/${item.subroutineId}/review`}
        className="inline-flex items-center gap-1 rounded-md px-2 py-1 text-caption text-ink-secondary hover:bg-sunken hover:text-ink-primary"
        title="Open the spec with the Spec agent beside it"
      >
        <MessageSquare className="h-3.5 w-3.5" aria-hidden="true" />
        Ask the Spec agent
      </Link>
    </div>
  );
}

function SignedGroup({ items, forceOpen }: { items: MyReviewItem[]; forceOpen: boolean }) {
  const [open, setOpen] = useState(false);
  const shown = open || forceOpen;
  return (
    <section>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={shown}
        className="label flex w-full items-center gap-2 text-left hover:text-ink-secondary"
      >
        <ChevronRight className={clsx('h-3.5 w-3.5 transition-transform', shown && 'rotate-90')} aria-hidden="true" />
        Signed, most recent first
        <span className="rounded-sm bg-sunken px-1.5 py-0.5 text-[10px] text-ink-secondary">{items.length}</span>
      </button>
      {shown &&
        (items.length === 0 ? (
          <p className="mt-2 text-caption text-ink-secondary">Sign-offs land here, bound to the source version they were made against.</p>
        ) : (
          <ul className="mt-2 divide-y divide-line-subtle rounded-xl border border-line-subtle bg-raised">
            {items.map((it) => (
              <li key={it.specId}>
                <Link
                  to={`/subroutines/${it.subroutineId}/review`}
                  className="flex flex-wrap items-center gap-x-4 gap-y-1 px-4 py-2 text-caption hover:bg-sunken"
                >
                  <ShieldCheck className="h-3.5 w-3.5 shrink-0 text-status-signed" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate font-mono font-semibold text-ink-primary" title={it.subroutineName}>
                    {shortName(it.subroutineName)}
                  </span>
                  <span className="truncate text-ink-secondary">{it.corpusName}</span>
                  {it.signature && (
                    <span className="text-ink-tertiary" title={absoluteTime(it.signature.signedAt)}>
                      Signed by {it.signature.signerDisplay} · {relativeTime(it.signature.signedAt)}
                    </span>
                  )}
                </Link>
              </li>
            ))}
          </ul>
        ))}
    </section>
  );
}
