import { clsx } from 'clsx';
import { ArrowUpRight, Maximize2 } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { Artifact } from '@/lib/conversations';
import { artifactIcon, artifactKindLabel, artifactLink, artifactTitle } from './meta';
import { renderArtifact } from './registry';

/**
 * Card wrapper rendered inside a message. Clicking anywhere selects the
 * artifact in the right pane; inner links stop propagation.
 */
export function ArtifactCard({
  artifact,
  selected = false,
  onSelect,
  className,
}: {
  artifact: Artifact;
  selected?: boolean;
  onSelect?: (artifact: Artifact) => void;
  className?: string;
}) {
  const Icon = artifactIcon(artifact.kind);
  const link = artifactLink(artifact);
  const select = () => onSelect?.(artifact);

  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={selected}
      data-testid="artifact-card"
      data-kind={artifact.kind}
      data-ref-id={artifact.refId ?? undefined}
      onClick={select}
      onKeyDown={(e) => {
        if (e.target !== e.currentTarget) return;
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          select();
        }
      }}
      className={clsx(
        'group/card min-w-0 cursor-pointer rounded-xl border bg-raised text-left outline-none transition-colors duration-fast',
        'focus-visible:ring-2 focus-visible:ring-volt',
        selected ? 'border-line-strong shadow-glow' : 'border-line-subtle hover:border-line',
        className,
      )}
    >
      <div className="flex items-center gap-2 border-b border-line-subtle px-3.5 py-2">
        <Icon size={14} className="shrink-0 text-ink-tertiary" aria-hidden="true" />
        <span className="min-w-0 truncate text-caption font-medium text-ink-primary">
          {artifactTitle(artifact)}
        </span>
        <span className="hidden shrink-0 text-micro uppercase tracking-wide text-ink-tertiary sm:inline">
          {artifactKindLabel(artifact.kind)}
        </span>
        <span className="ml-auto flex shrink-0 items-center gap-1">
          {link && (
            <Link
              to={link.href}
              onClick={(e) => e.stopPropagation()}
              className="inline-flex items-center gap-0.5 rounded-md px-1.5 py-0.5 text-micro text-ink-tertiary hover:bg-sunken hover:text-ink-primary"
              title={link.label}
            >
              Open
              <ArrowUpRight size={12} aria-hidden="true" />
            </Link>
          )}
          <span
            className="inline-flex items-center rounded-md p-1 text-ink-tertiary opacity-0 transition-opacity group-hover/card:opacity-100"
            aria-hidden="true"
            title="Show in pane"
          >
            <Maximize2 size={12} />
          </span>
        </span>
      </div>
      <div className="px-3.5 py-3">{renderArtifact(artifact, { size: 'card' })}</div>
    </div>
  );
}
