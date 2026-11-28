/**
 * `reviewQueue` — specs waiting for an SME, in the order worth signing them:
 * the ones holding agent work first. Refreshes from `GET /api/v1/review-queue`
 * so signed specs drop off while the reviewer works at their own pace.
 */
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/lib/api';
import { arr, num, obj, relativeTime, str } from '../format';
import type { ArtifactRenderProps } from './registry';

type Props = Record<string, unknown>;

export function ReviewQueueCard({ artifact, size, onIntent }: ArtifactRenderProps) {
  const pane = size === 'pane';
  const corpusId = str(artifact.props.corpusId);
  const [items, setItems] = useState<Props[]>(() => arr(artifact.props.items).map(obj));

  useEffect(() => {
    let closed = false;
    let timer: number | undefined;
    const tick = async () => {
      try {
        const res = await apiFetch<{ items: Props[] }>(`/api/v1/review-queue${corpusId ? `?corpusId=${corpusId}` : ''}`);
        if (closed) return;
        setItems(res.items);
        if (res.items.length === 0) return;
      } catch {
        if (closed) return; // keep the last list; try again
      }
      timer = window.setTimeout(tick, 10_000);
    };
    void tick();
    return () => {
      closed = true;
      window.clearTimeout(timer);
    };
  }, [corpusId]);

  const shown = pane ? items : items.slice(0, 6);

  return (
    <div className="space-y-2" data-testid="review-queue-card" data-count={items.length}>
      {items.length === 0 ? (
        <p className="text-caption text-ink-tertiary">Nothing is waiting for sign-off.</p>
      ) : (
        <ol className="divide-y divide-line-subtle" aria-label="Specs waiting for sign-off">
          {shown.map((i) => {
            const blocked = num(i.stepsBlocked);
            return (
              <li key={str(i.specId)} className="flex items-center gap-2.5 py-1.5" data-testid="review-queue-item" data-routine={str(i.name)}>
                <div className="min-w-0 flex-1">
                  <div className="truncate font-mono text-caption text-ink-primary">{str(i.name)}</div>
                  <div className="truncate text-micro text-ink-tertiary">
                    {str(i.corpusName)} · waiting {relativeTime(str(i.waitingSince))}
                    {blocked > 0 && ` · holds ${blocked} agent step${blocked === 1 ? '' : 's'}`}
                  </div>
                </div>
                <Link
                  to={`/subroutines/${str(i.subroutineId)}/review`}
                  className="shrink-0 rounded-md border border-line-subtle px-2 py-1 text-micro text-ink-primary hover:border-line-strong"
                >
                  Review
                </Link>
              </li>
            );
          })}
        </ol>
      )}
      {items.length > 1 && corpusId && onIntent && (
        <div className="flex flex-wrap items-center gap-2 pt-1">
          <button
            type="button"
            data-testid="review-queue-sign-all"
            onClick={() => onIntent(`Accept and sign all ${items.length} specs in the review queue of ${str(items[0]?.corpusName, "this programme")}`)}
            className="rounded-md border border-line px-2.5 py-1 text-caption text-ink-primary hover:border-line-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          >
            Accept and sign all ({items.length})
          </button>
          <span className="text-micro text-ink-tertiary">
            SME only · accepts every unreviewed claim as drafted · recorded as a bulk sign-off
          </span>
        </div>
      )}
      {!pane && items.length > shown.length && (
        <div className="text-micro text-ink-tertiary">+{items.length - shown.length} more — open in the pane to see all</div>
      )}
    </div>
  );
}
