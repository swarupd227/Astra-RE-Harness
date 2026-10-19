/**
 * `flowBoard` — a programme's routines by pipeline stage, the card form of
 * the Routine flow board page. Card size: one row per stage (icon, label,
 * count, a bar for its share of the programme). Pane size — the card opened
 * in place or in the artifact pane — adds the routines sitting in each stage
 * as links underneath.
 */
import { Link } from 'react-router-dom';
import { clsx } from 'clsx';
import { columnStyle } from '@/board/columns';
import { arr, formatInt, num, obj, str } from '../format';
import type { ArtifactRenderProps } from './registry';

type Routine = { id: string; name: string; filePath: string; lineStart: number };
type Stage = { key: string; label: string; total: number; routines: Routine[] };

function stages(v: unknown): Stage[] {
  return arr(v)
    .map((raw) => {
      const c = obj(raw);
      const routines = arr(c.routines).map((r) => {
        const o = obj(r);
        return { id: str(o.id), name: str(o.name, '—'), filePath: str(o.filePath), lineStart: num(o.lineStart) };
      });
      return { key: str(c.key), label: str(c.label, str(c.key)), total: num(c.total), routines };
    })
    .filter((s) => s.key);
}

export function FlowBoardCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const pane = size === 'pane';
  const list = stages(p.columns);
  const total = num(p.totalRoutines, list.reduce((n, s) => n + s.total, 0));

  if (list.length === 0) {
    return <p className="text-caption text-ink-tertiary">No routines to show yet.</p>;
  }

  return (
    <div className="space-y-3" data-testid="flow-board-card">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-caption">
        <span className="font-medium text-ink-primary">{str(p.corpusName, 'Programme')}</span>
        <span className="ml-auto font-mono tabular-nums text-ink-secondary">{formatInt(total)} routines</span>
      </div>

      <ol className="space-y-2.5" aria-label="Pipeline stages">
        {list.map((s) => {
          const style = columnStyle(s.key);
          const Icon = style.icon;
          const pct = total > 0 ? (s.total / total) * 100 : 0;
          return (
            <li key={s.key} data-testid="flow-board-stage" data-stage={s.key} data-count={s.total} className={clsx(s.total === 0 && 'opacity-60')}>
              <div className="flex items-center gap-2">
                <Icon className={clsx('h-3.5 w-3.5 shrink-0', style.text, style.spin && s.total > 0 && 'animate-spin')} aria-hidden="true" />
                <span className="min-w-0 flex-1 truncate text-caption text-ink-primary">{s.label}</span>
                <span className="shrink-0 font-mono text-micro tabular-nums text-ink-secondary">{formatInt(s.total)}</span>
              </div>
              <div className="mt-1 h-1 w-full overflow-hidden rounded-full bg-sunken ring-1 ring-inset ring-line-subtle" aria-hidden="true">
                {s.total > 0 && (
                  <div className={clsx('h-full rounded-full', style.bar)} style={{ width: `${Math.max(2, Math.round(pct))}%` }} />
                )}
              </div>
              {pane && s.routines.length > 0 && (
                <ul className="mt-1.5 flex flex-wrap gap-1">
                  {s.routines.map((r, i) => (
                    <li key={r.id || `${r.name}-${i}`} className="min-w-0">
                      {r.id ? (
                        <Link
                          to={`/subroutines/${r.id}`}
                          onClick={(e) => e.stopPropagation()}
                          className="block max-w-[220px] truncate rounded border border-line-subtle bg-canvas px-1.5 py-0.5 font-mono text-micro text-ink-primary hover:border-line-strong"
                          title={r.filePath ? `${r.filePath}:${r.lineStart}` : undefined}
                        >
                          {r.name}
                        </Link>
                      ) : (
                        <span className="block max-w-[220px] truncate px-1.5 py-0.5 font-mono text-micro text-ink-primary">{r.name}</span>
                      )}
                    </li>
                  ))}
                  {s.total > s.routines.length && (
                    <li className="px-1 py-0.5 text-micro text-ink-tertiary">+{formatInt(s.total - s.routines.length)} more</li>
                  )}
                </ul>
              )}
            </li>
          );
        })}
      </ol>

      {!pane && <p className="text-micro text-ink-tertiary">Expand the card for the routines in each stage.</p>}
    </div>
  );
}
