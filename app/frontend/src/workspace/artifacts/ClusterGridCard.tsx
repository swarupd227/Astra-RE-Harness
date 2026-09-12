import { clsx } from 'clsx';
import { arr, formatInt, num, obj, str } from '../format';
import type { ArtifactRenderProps } from './registry';

type Cluster = {
  id: string;
  label: string;
  suggestedArchetypeName: string;
  memberCount: number;
  rationale: string;
  members: { subroutineId: string; subroutineName: string }[];
};

function clusters(v: unknown): Cluster[] {
  return arr(v).map((raw, i) => {
    const c = obj(raw);
    return {
      id: str(c.id, `cluster-${i}`),
      label: str(c.label, 'Untitled pattern'),
      suggestedArchetypeName: str(c.suggestedArchetypeName),
      memberCount: num(c.memberCount, arr(c.members).length),
      rationale: str(c.rationale),
      members: arr(c.members).map((m) => {
        const mm = obj(m);
        return { subroutineId: str(mm.subroutineId), subroutineName: str(mm.subroutineName) };
      }),
    };
  });
}

/** `clusterGrid` — pattern clusters as tiles; the pane adds the rationale. */
export function ClusterGridCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const all = clusters(p.clusters);
  const pane = size === 'pane';
  const shown = pane ? all : all.slice(0, 6);
  const clusterCount = num(p.clusterCount, all.length);
  const routineCount = num(p.routineCount);

  if (all.length === 0) {
    return <p className="text-caption text-ink-tertiary">No clusters yet — run a pattern analysis first.</p>;
  }

  return (
    <div className="space-y-3">
      <div className="text-caption text-ink-secondary">
        <span className="font-mono tabular-nums text-ink-primary">{formatInt(clusterCount)}</span> pattern
        {clusterCount === 1 ? '' : 's'}
        {routineCount > 0 && (
          <>
            {' across '}
            <span className="font-mono tabular-nums text-ink-primary">{formatInt(routineCount)}</span> routines
          </>
        )}
      </div>
      <div className={clsx('grid gap-2', pane ? 'grid-cols-1' : 'grid-cols-1 sm:grid-cols-2')}>
        {shown.map((c) => {
          const previewMembers = c.members.slice(0, pane ? 8 : 3);
          const rest = c.memberCount - previewMembers.length;
          return (
            <div key={c.id} className="min-w-0 rounded-lg border border-line-subtle bg-sunken px-3 py-2.5">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="truncate text-caption font-medium text-ink-primary">{c.label}</div>
                  {c.suggestedArchetypeName && (
                    <div className="truncate font-mono text-micro text-ink-secondary" title={c.suggestedArchetypeName}>
                      {c.suggestedArchetypeName}
                    </div>
                  )}
                </div>
                <span className="shrink-0 rounded-full border border-line-subtle bg-raised px-1.5 py-px font-mono text-micro tabular-nums text-ink-secondary">
                  {formatInt(c.memberCount)}
                </span>
              </div>
              {previewMembers.length > 0 && (
                <div className="mt-1.5 flex flex-wrap gap-1">
                  {previewMembers.map((m, i) => (
                    <span
                      key={m.subroutineId || `${m.subroutineName}-${i}`}
                      className="rounded bg-raised px-1 py-px font-mono text-micro text-ink-tertiary"
                    >
                      {m.subroutineName || m.subroutineId}
                    </span>
                  ))}
                  {rest > 0 && <span className="px-1 py-px text-micro text-ink-tertiary">+{rest}</span>}
                </div>
              )}
              {pane && c.rationale && (
                <p className="mt-2 border-t border-line-subtle pt-2 text-caption leading-[1.5] text-ink-secondary">
                  {c.rationale}
                </p>
              )}
            </div>
          );
        })}
      </div>
      {!pane && all.length > shown.length && (
        <div className="text-micro text-ink-tertiary">+{all.length - shown.length} more — open in the pane to see all</div>
      )}
    </div>
  );
}
