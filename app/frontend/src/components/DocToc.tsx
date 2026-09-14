import { useMemo } from 'react';
import GithubSlugger from 'github-slugger';
import { clsx } from 'clsx';

export type TocEntry = { id: string; text: string; level: 1 | 2 | 3 };

/**
 * Headings of a markdown document with the ids rehype-slug gives them, so a
 * table of contents links straight to the rendered sections. Fenced code is
 * skipped; inline markdown in a heading is reduced to its text the same way
 * the renderer does.
 */
export function tocOf(markdown: string): TocEntry[] {
  const slugger = new GithubSlugger();
  const out: TocEntry[] = [];
  let inFence = false;
  for (const raw of markdown.split('\n')) {
    const line = raw.trimEnd();
    if (/^\s*(```|~~~)/.test(line)) { inFence = !inFence; continue; }
    if (inFence) continue;
    const m = /^(#{1,3})\s+(.+?)\s*#*\s*$/.exec(line);
    if (!m) continue;
    const text = m[2]
      .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
      .replace(/[`*_~]/g, '')
      .trim();
    if (!text) continue;
    out.push({ id: slugger.slug(text), text, level: m[1].length as 1 | 2 | 3 });
  }
  return out;
}

/** Sticky contents list for a long document; renders nothing under three headings. */
export function DocToc({ markdown, className }: { markdown: string; className?: string }) {
  const entries = useMemo(() => tocOf(markdown), [markdown]);
  if (entries.length < 3) return null;
  return (
    <nav aria-label="Contents" className={clsx('text-caption', className)} data-testid="doc-toc">
      <p className="mb-2 text-micro font-medium uppercase tracking-wide text-ink-tertiary">Contents</p>
      <ol className="space-y-1 border-l border-line-subtle">
        {entries.map((e) => (
          <li key={e.id} className={clsx(e.level === 1 && 'pl-3', e.level === 2 && 'pl-5', e.level === 3 && 'pl-7')}>
            <a
              href={`#${e.id}`}
              className="block truncate text-ink-secondary hover:text-ink-primary hover:underline underline-offset-2"
            >
              {e.text}
            </a>
          </li>
        ))}
      </ol>
    </nav>
  );
}
