import { clsx } from 'clsx';
import type { ClaimReview, SpecClaim } from '@/lib/api';
import { claimPathFor } from '@/lib/api';

export type OutlineItem = {
  section: string;
  label: string;
  claims: SpecClaim[];
};

export function OutlinePane({
  items,
  reviews,
  activeId,
  onJump,
}: {
  items: OutlineItem[];
  reviews: ClaimReview[];
  activeId?: string | null;
  onJump?: (id: string) => void;
}) {
  const reviewBy = new Map<string, ClaimReview>();
  for (const r of reviews) reviewBy.set(r.claimPath, r);

  const counts = items.reduce(
    (acc, item) => {
      for (const c of item.claims) {
        const r = reviewBy.get(claimPathFor(item.section, c.id));
        if (!r) acc.untouched++;
        else if (r.action === 'accept') acc.accepted++;
        else if (r.action === 'edit') acc.edited++;
        else if (r.action === 'reject') acc.rejected++;
        else if (r.action === 'question') acc.questioned++;
      }
      return acc;
    },
    { untouched: 0, accepted: 0, edited: 0, rejected: 0, questioned: 0 },
  );
  const total = items.reduce((n, x) => n + x.claims.length, 0);
  const processed = total - counts.untouched;

  return (
    <aside className="flex h-full flex-col gap-4 overflow-y-auto border-r border-border-subtle bg-raised p-4">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">Outline</p>
        <h2 className="mt-1 text-h-md font-semibold text-ink-primary">{processed} / {total} claims processed</h2>
        <ProgressBar processed={processed} total={total} />
        <CountStrip counts={counts} />
      </header>

      <nav aria-label="Spec outline" className="space-y-3">
        {items.map((item) => (
          <section key={item.section}>
            <h3 className="mb-1 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
              {item.label}
              <span className="ml-1 text-ink-tertiary/70">({item.claims.length})</span>
            </h3>
            <ul className="space-y-0.5">
              {item.claims.map((c) => {
                const path = claimPathFor(item.section, c.id);
                const review = reviewBy.get(path);
                const state = review?.action ?? 'untouched';
                const active = activeId === c.id;
                return (
                  <li key={c.id}>
                    <button
                      type="button"
                      onClick={() => onJump?.(c.id)}
                      className={clsx(
                        'flex w-full items-center gap-2 rounded-sm px-2 py-1 text-left font-mono text-caption transition-colors duration-fast hover:bg-sunken',
                        active && 'bg-sunken',
                      )}
                    >
                      <StateDot state={state} />
                      <span className="text-ink-primary">{c.id}</span>
                      <span className="ml-auto truncate text-ink-tertiary">
                        {(c.claim ?? c.description ?? c.question ?? '').slice(0, 24)}
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>
        ))}
      </nav>
    </aside>
  );
}

function ProgressBar({ processed, total }: { processed: number; total: number }) {
  const pct = total === 0 ? 0 : Math.round((processed / total) * 100);
  return (
    <div className="mt-2 h-1 w-full overflow-hidden rounded-full bg-sunken">
      <div
        className="h-full bg-status-review transition-all duration-medium"
        style={{ width: `${pct}%` }}
        aria-hidden="true"
      />
    </div>
  );
}

function CountStrip({
  counts,
}: {
  counts: { untouched: number; accepted: number; edited: number; rejected: number; questioned: number };
}) {
  const items: { key: keyof typeof counts; label: string; tone: string }[] = [
    { key: 'accepted', label: 'accept', tone: 'text-status-review' },
    { key: 'edited', label: 'edit', tone: 'text-status-draft' },
    { key: 'rejected', label: 'reject', tone: 'text-status-failed' },
    { key: 'questioned', label: 'q', tone: 'text-status-scaffolded' },
    { key: 'untouched', label: 'untouched', tone: 'text-ink-tertiary' },
  ];
  return (
    <div className="mt-2 flex flex-wrap gap-2 font-mono text-[11px]">
      {items.map((it) => (
        <span key={it.key} className={clsx('inline-flex items-center gap-1', it.tone)}>
          <span>{counts[it.key]}</span>
          <span className="text-ink-tertiary/80">{it.label}</span>
        </span>
      ))}
    </div>
  );
}

function StateDot({ state }: { state: string }) {
  const cls =
    state === 'accept'
      ? 'bg-status-review'
      : state === 'edit'
        ? 'bg-status-draft'
        : state === 'reject'
          ? 'bg-status-failed'
          : state === 'question'
            ? 'bg-status-scaffolded'
            : 'bg-status-untouched';
  return <span className={clsx('h-2 w-2 shrink-0 rounded-full', cls)} aria-hidden="true" />;
}
