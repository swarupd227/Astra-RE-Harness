import { clsx } from 'clsx';
import { ArrowUpRight, Maximize2, Minimize2 } from 'lucide-react';
import { Link } from 'react-router-dom';
import type { Artifact } from '@/lib/conversations';
import { artifactIcon, artifactKindLabel, artifactLink, artifactTitle } from './meta';
import { renderArtifact } from './registry';

/**
 * Card wrapper rendered inside a message. Clicking anywhere selects the
 * artifact in the right pane; inner links stop propagation. Where there is
 * no pane (a compact ThreadPanel) `expanded` renders the pane-size body in
 * place instead.
 */
export function ArtifactCard({
  artifact,
  selected = false,
  expanded = false,
  onSelect,
  onIntent,
  className,
}: {
  artifact: Artifact;
  selected?: boolean;
  /** Render the pane-size body inline (hosts without an artifact pane). */
  expanded?: boolean;
  onSelect?: (artifact: Artifact) => void;
  onIntent?: (intent: string) => void;
  className?: string;
}) {
  const Icon = artifactIcon(artifact.kind);
  const link = artifactLink(artifact);
  const select = () => onSelect?.(artifact);

  return (
    // A plain container, not role="button": a card holds real links and buttons, and
    // interactive content may not nest inside a button. Clicking anywhere still selects it
    // (mouse/touch); the keyboard path is the explicit toggle in the header.
    <div
      data-testid="artifact-card"
      data-kind={artifact.kind}
      data-ref-id={artifact.refId ?? undefined}
      data-expanded={expanded ? 'true' : undefined}
      data-selected={selected ? 'true' : undefined}
      onClick={select}
      className={clsx(
        'group/card min-w-0 cursor-pointer rounded-xl border bg-raised text-left transition-colors duration-fast',
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
          <button
            type="button"
            aria-pressed={selected}
            aria-label={`${expanded ? 'Collapse' : 'Show in pane'}: ${artifactTitle(artifact)}`}
            title={expanded ? 'Collapse' : 'Show in pane'}
            onClick={(e) => {
              e.stopPropagation();
              select();
            }}
            className="inline-flex items-center rounded-md p-1 text-ink-tertiary opacity-0 transition-opacity hover:bg-sunken hover:text-ink-primary focus-visible:opacity-100 group-hover/card:opacity-100"
          >
            {expanded ? <Minimize2 size={12} aria-hidden="true" /> : <Maximize2 size={12} aria-hidden="true" />}
          </button>
        </span>
      </div>
      <div className="px-3.5 py-3">{renderArtifact(artifact, { size: expanded ? 'pane' : 'card', onIntent })}</div>
    </div>
  );
}
