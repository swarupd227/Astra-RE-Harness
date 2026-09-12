/**
 * `docSection` — one generated documentation section (≤ 8 000 chars of
 * markdown). Card size is clamped with a fade; the pane shows it all.
 */
import { clsx } from 'clsx';
import { ArrowUpRight } from 'lucide-react';
import { Link } from 'react-router-dom';
import { Markdown } from '../Markdown';
import { formatState, str } from '../format';
import type { ArtifactRenderProps } from './registry';

export function DocSectionCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const pane = size === 'pane';
  const title = str(p.title, 'Documentation');
  const kind = str(p.kind);
  const markdown = str(p.markdown);
  const href = str(p.href) || (str(p.corpusId) ? `/projects/${str(p.corpusId)}/docs` : '');

  return (
    <div className="space-y-2" data-testid="doc-section-card" data-doc-kind={kind || undefined}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="min-w-0 truncate text-caption font-medium text-ink-primary">{title}</span>
        {kind && (
          <span className="shrink-0 rounded-full border border-line-subtle bg-sunken px-2 py-px text-micro text-ink-secondary">
            {formatState(kind)}
          </span>
        )}
        {href && (
          <Link
            to={href}
            onClick={(e) => e.stopPropagation()}
            className="ml-auto inline-flex shrink-0 items-center gap-1 rounded-md border border-line-subtle bg-sunken px-2 py-0.5 text-micro text-ink-secondary hover:border-line hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            data-testid="doc-section-open"
          >
            Open full view
            <ArrowUpRight size={11} aria-hidden="true" />
          </Link>
        )}
      </div>
      {markdown ? (
        <Markdown
          className={clsx(
            'text-caption',
            !pane && 'max-h-72 overflow-hidden [mask-image:linear-gradient(to_bottom,black_78%,transparent)]',
          )}
        >
          {markdown}
        </Markdown>
      ) : (
        <p className="text-caption text-ink-tertiary">This section has no content yet.</p>
      )}
    </div>
  );
}
