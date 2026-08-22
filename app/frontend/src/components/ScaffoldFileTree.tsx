import { clsx } from 'clsx';
import { ChevronRight, FileCode, Folder, FolderOpen } from 'lucide-react';
import { useMemo } from 'react';

export type FileTreeEntry = {
  path: string;
  language: string;
  todoCount: number;
  /** True while the file is still streaming. */
  loading?: boolean;
};

type Node = {
  name: string;
  path?: string;       // present on file leaves
  todoCount?: number;
  loading?: boolean;
  children?: Node[];
};

function buildTree(files: FileTreeEntry[]): Node {
  const root: Node = { name: '', children: [] };
  for (const f of files) {
    const segs = f.path.split('/');
    let cursor = root;
    for (let i = 0; i < segs.length; i++) {
      const seg = segs[i];
      const isLeaf = i === segs.length - 1;
      let next = cursor.children?.find((c) => c.name === seg);
      if (!next) {
        next = isLeaf
          ? { name: seg, path: f.path, todoCount: f.todoCount, loading: f.loading }
          : { name: seg, children: [] };
        cursor.children = cursor.children ?? [];
        cursor.children.push(next);
      } else if (isLeaf) {
        next.path = f.path;
        next.todoCount = f.todoCount;
        next.loading = f.loading;
      }
      cursor = next;
    }
  }
  return root;
}

export function ScaffoldFileTree({
  files,
  activePath,
  onSelect,
}: {
  files: FileTreeEntry[];
  activePath: string | null;
  onSelect: (path: string) => void;
}) {
  const tree = useMemo(() => buildTree(files), [files]);
  return (
    <nav aria-label="Scaffold file tree" className="space-y-0.5 p-3">
      {tree.children?.map((c) => (
        <NodeView key={c.name} node={c} depth={0} activePath={activePath} onSelect={onSelect} />
      ))}
    </nav>
  );
}

function NodeView({
  node,
  depth,
  activePath,
  onSelect,
}: {
  node: Node;
  depth: number;
  activePath: string | null;
  onSelect: (p: string) => void;
}) {
  if (node.path) {
    const active = activePath === node.path;
    return (
      <button
        type="button"
        onClick={() => onSelect(node.path!)}
        className={clsx(
          'flex w-full items-center gap-2 rounded-sm px-2 py-1 text-left text-body transition-colors duration-fast hover:bg-sunken',
          active && 'bg-sunken font-semibold text-ink-primary',
          !active && 'text-ink-secondary',
        )}
        style={{ paddingLeft: 8 + depth * 14 }}
      >
        <FileCode className="h-3.5 w-3.5 shrink-0 text-ink-tertiary" aria-hidden="true" />
        <span className="truncate font-mono">{node.name}</span>
        {node.loading && (
          <span className="ml-auto h-1.5 w-1.5 shrink-0 rounded-full bg-accent motion-safe:animate-pulse" aria-hidden="true" />
        )}
        {!node.loading && (node.todoCount ?? 0) > 0 && (
          <span className="ml-auto rounded-sm bg-[#F2E5C2] px-1.5 py-0.5 font-mono text-micro uppercase text-status-scaffolded">
            TODO {node.todoCount}
          </span>
        )}
      </button>
    );
  }

  return (
    <details open className="group">
      <summary
        className="flex cursor-pointer items-center gap-2 rounded-sm px-2 py-1 text-body text-ink-secondary marker:hidden hover:bg-sunken"
        style={{ paddingLeft: 8 + depth * 14 }}
      >
        <ChevronRight className="h-3.5 w-3.5 shrink-0 text-ink-tertiary transition-transform duration-fast group-open:rotate-90" aria-hidden="true" />
        <FolderOpen className="hidden h-3.5 w-3.5 shrink-0 text-ink-tertiary group-open:inline" aria-hidden="true" />
        <Folder className="h-3.5 w-3.5 shrink-0 text-ink-tertiary group-open:hidden" aria-hidden="true" />
        <span className="font-mono">{node.name}</span>
      </summary>
      <div>
        {node.children?.map((c) => (
          <NodeView key={c.name} node={c} depth={depth + 1} activePath={activePath} onSelect={onSelect} />
        ))}
      </div>
    </details>
  );
}
