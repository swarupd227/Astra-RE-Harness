import { Link } from 'react-router-dom';
import { StatePill } from '../StatePill';
import { arr, formatInt, formatLanguage, num, obj, str } from '../format';
import type { ArtifactRenderProps } from './registry';

type Row = {
  id: string;
  name: string;
  signature: string;
  state: string;
  sourceLanguage: string;
  path: string;
  lineStart: number;
  lineEnd: number;
};

function rows(v: unknown): Row[] {
  return arr(v).map((raw) => {
    const r = obj(raw);
    return {
      id: str(r.id),
      name: str(r.name, '—'),
      signature: str(r.signature),
      state: str(r.state),
      sourceLanguage: str(r.sourceLanguage),
      path: str(r.path),
      lineStart: num(r.lineStart),
      lineEnd: num(r.lineEnd),
    };
  });
}

/** `routineList` — compact table; each row links to `/subroutines/{id}`. */
export function RoutineListCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const items = rows(p.items);
  const total = num(p.total, items.length);
  const pane = size === 'pane';
  const limit = pane ? items.length : 8;
  const shown = items.slice(0, limit);
  const more = items.length - shown.length + Math.max(0, total - items.length);

  if (items.length === 0) {
    return <p className="text-caption text-ink-tertiary">No routines matched{str(p.query) ? ` “${str(p.query)}”` : ''}.</p>;
  }

  return (
    <div className="space-y-2">
      <div className="text-caption text-ink-secondary">
        <span className="font-mono tabular-nums text-ink-primary">{formatInt(total)}</span> routine{total === 1 ? '' : 's'}
        {str(p.query) && (
          <>
            {' '}matching <span className="font-mono text-ink-primary">“{str(p.query)}”</span>
          </>
        )}
      </div>
      <div className="-mx-1 overflow-x-auto">
        <table className="w-full min-w-[420px] border-collapse text-caption">
          <thead className="text-micro uppercase tracking-wide text-ink-tertiary">
            <tr>
              <th className="px-1 pb-1.5 text-left font-medium">Routine</th>
              <th className="px-1 pb-1.5 text-left font-medium">State</th>
              <th className="px-1 pb-1.5 text-left font-medium">Language</th>
              <th className="px-1 pb-1.5 text-left font-medium">Location</th>
            </tr>
          </thead>
          <tbody>
            {shown.map((r, i) => (
              <tr key={r.id || `${r.name}-${i}`} className="border-t border-line-subtle">
                <td className="px-1 py-1.5 align-top">
                  {r.id ? (
                    <Link
                      to={`/subroutines/${r.id}`}
                      onClick={(e) => e.stopPropagation()}
                      className="font-mono text-ink-primary underline-offset-2 hover:underline"
                      title={r.signature || undefined}
                    >
                      {r.name}
                    </Link>
                  ) : (
                    <span className="font-mono text-ink-primary">{r.name}</span>
                  )}
                </td>
                <td className="px-1 py-1.5 align-top">
                  <StatePill state={r.state} size="xs" />
                </td>
                <td className="px-1 py-1.5 align-top text-ink-secondary">{formatLanguage(r.sourceLanguage || null)}</td>
                <td className="max-w-[220px] px-1 py-1.5 align-top">
                  <span className="block truncate font-mono text-micro text-ink-tertiary" title={r.path}>
                    {r.path}
                    {r.lineStart > 0 && (
                      <span className="text-ink-secondary">
                        :{r.lineStart}
                        {r.lineEnd > r.lineStart ? `–${r.lineEnd}` : ''}
                      </span>
                    )}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {more > 0 && <div className="text-micro text-ink-tertiary">+{formatInt(more)} more</div>}
    </div>
  );
}
