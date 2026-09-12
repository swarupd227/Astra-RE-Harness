import { ChevronRight } from 'lucide-react';
import { Link } from 'react-router-dom';
import { StatePill } from '../StatePill';
import { arr, formatInt, formatLanguage, num, obj, relativeTime, str } from '../format';
import type { ArtifactRenderProps } from './registry';

type Programme = {
  id: string;
  conversationId: string;
  name: string;
  sourceLanguage: string;
  routineCount: number;
  fileCount: number;
  state: string;
  createdAt: string;
};

function programmes(v: unknown): Programme[] {
  return arr(v).map((raw, i) => {
    const o = obj(raw);
    return {
      id: str(o.id, `p-${i}`),
      conversationId: str(o.conversationId),
      name: str(o.name, 'Untitled programme'),
      sourceLanguage: str(o.sourceLanguage),
      routineCount: num(o.routineCount),
      fileCount: num(o.fileCount),
      state: str(o.state),
      createdAt: str(o.createdAt),
    };
  });
}

/** `programmeList` — every programme; a row opens its thread. */
export function ProgrammeListCard({ artifact, size }: ArtifactRenderProps) {
  const items = programmes(artifact.props.items);
  const pane = size === 'pane';
  const shown = pane ? items : items.slice(0, 8);

  if (items.length === 0) {
    return (
      <p className="text-caption text-ink-tertiary">
        No programmes yet.{' '}
        <Link to="/projects/new" onClick={(e) => e.stopPropagation()} className="text-ink-link hover:underline">
          Add one
        </Link>
        .
      </p>
    );
  }

  return (
    <ul className="divide-y divide-line-subtle">
      {shown.map((p) => {
        const inner = (
          <>
            <div className="min-w-0 flex-1">
              <div className="flex items-center gap-2">
                <span className="truncate text-caption font-medium text-ink-primary">{p.name}</span>
                {p.state && <StatePill state={p.state} size="xs" />}
              </div>
              <div className="truncate text-micro text-ink-tertiary">
                {formatLanguage(p.sourceLanguage || null)} · {formatInt(p.routineCount)} routines · {formatInt(p.fileCount)} files
                {p.createdAt && <> · added {relativeTime(p.createdAt)}</>}
              </div>
            </div>
            {p.conversationId && <ChevronRight size={14} className="shrink-0 text-ink-tertiary" aria-hidden="true" />}
          </>
        );
        return (
          <li key={p.id}>
            {p.conversationId ? (
              <Link
                to={`/w/${p.conversationId}`}
                onClick={(e) => e.stopPropagation()}
                className="-mx-1 flex items-center gap-2 rounded-md px-1 py-2 hover:bg-sunken"
              >
                {inner}
              </Link>
            ) : (
              <div className="flex items-center gap-2 py-2">{inner}</div>
            )}
          </li>
        );
      })}
      {!pane && items.length > shown.length && (
        <li className="pt-2 text-micro text-ink-tertiary">+{items.length - shown.length} more</li>
      )}
    </ul>
  );
}
