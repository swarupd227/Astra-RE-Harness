import { clsx } from 'clsx';
import { Markdown } from '../Markdown';
import { str } from '../format';
import type { ArtifactRenderProps } from './registry';

/** `text` — a titled markdown note. Also the fallback for unknown kinds. */
export function TextCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const title = str(p.title);
  const markdown = str(p.markdown);
  const pane = size === 'pane';
  return (
    <div className="space-y-2">
      {title && <div className="text-caption font-medium text-ink-primary">{title}</div>}
      {markdown ? (
        <Markdown className={clsx('text-caption', !pane && 'max-h-64 overflow-hidden [mask-image:linear-gradient(to_bottom,black_80%,transparent)]')}>
          {markdown}
        </Markdown>
      ) : (
        <p className="text-caption text-ink-tertiary">Nothing to show.</p>
      )}
    </div>
  );
}
