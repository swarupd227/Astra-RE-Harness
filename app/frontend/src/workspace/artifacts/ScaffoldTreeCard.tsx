import { FileCode2 } from 'lucide-react';
import { arr, formatInt, num, obj, str } from '../format';
import { prettyStack } from '@/lib/targetStacks';
import type { ArtifactRenderProps } from './registry';

type FileRow = { path: string; language: string; lines: number };

function files(v: unknown): FileRow[] {
  return arr(v).map((raw) => {
    const f = obj(raw);
    return { path: str(f.path), language: str(f.language), lines: num(f.lines) };
  });
}

/** `scaffoldTree` — the generated package's file list and totals. */
export function ScaffoldTreeCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const list = files(p.files);
  const pane = size === 'pane';
  const shown = pane ? list : list.slice(0, 8);
  const todo = num(p.todoCount);

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2 text-caption">
        <span className="font-mono text-body text-ink-primary">{str(p.routineName, 'Routine')}</span>
        {str(p.targetPlatform) && <span className="text-ink-tertiary">→ {prettyStack(str(p.targetPlatform))}</span>}
      </div>

      <div className="grid grid-cols-3 gap-2">
        <div className="rounded-lg border border-line-subtle bg-sunken px-2 py-1.5">
          <div className="font-mono text-body tabular-nums text-ink-primary">{formatInt(num(p.fileCount, list.length))}</div>
          <div className="text-micro text-ink-tertiary">Files</div>
        </div>
        <div className="rounded-lg border border-line-subtle bg-sunken px-2 py-1.5">
          <div className="font-mono text-body tabular-nums text-ink-primary">{formatInt(num(p.totalLines))}</div>
          <div className="text-micro text-ink-tertiary">Lines</div>
        </div>
        <div className="rounded-lg border border-line-subtle bg-sunken px-2 py-1.5">
          <div className={todo > 0 ? 'font-mono text-body tabular-nums text-status-warn' : 'font-mono text-body tabular-nums text-ink-primary'}>
            {formatInt(todo)}
          </div>
          <div className="text-micro text-ink-tertiary">TODOs</div>
        </div>
      </div>

      {list.length === 0 ? (
        <p className="text-caption text-ink-tertiary">No files listed.</p>
      ) : (
        <ul className="divide-y divide-line-subtle rounded-lg border border-line-subtle">
          {shown.map((f, i) => (
            <li key={`${f.path}-${i}`} className="flex items-center gap-2 px-2.5 py-1.5 text-caption">
              <FileCode2 size={13} className="shrink-0 text-ink-tertiary" aria-hidden="true" />
              <span className="min-w-0 flex-1 truncate font-mono text-ink-primary" title={f.path}>
                {f.path}
              </span>
              {f.language && <span className="shrink-0 text-micro text-ink-tertiary">{f.language}</span>}
              {f.lines > 0 && (
                <span className="shrink-0 font-mono text-micro tabular-nums text-ink-tertiary">{formatInt(f.lines)} ln</span>
              )}
            </li>
          ))}
        </ul>
      )}
      {!pane && list.length > shown.length && (
        <div className="text-micro text-ink-tertiary">+{list.length - shown.length} more files</div>
      )}
    </div>
  );
}
