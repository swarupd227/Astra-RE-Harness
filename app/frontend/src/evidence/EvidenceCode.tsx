import { useEffect, useMemo, useRef, useState } from 'react';
import { clsx } from 'clsx';
import { highlightLines } from '@/evidence/highlight';

/**
 * Numbered, read-only source with the cited lines marked. It renders from the bundle alone
 * (highlight.js colours, no editor and no CDN), so it works on an offline laptop. The cited range
 * is scrolled into view inside the box; the page itself never scrolls sideways.
 */
export function EvidenceCode({
  text,
  path,
  startLine = 1,
  highlightStart,
  highlightEnd,
  className,
}: {
  text: string;
  path: string;
  /** The file line number of the first line of `text`. */
  startLine?: number;
  highlightStart?: number;
  highlightEnd?: number;
  className?: string;
}) {
  const lines = useMemo(() => text.split('\n'), [text]);
  const [coloured, setColoured] = useState<string[] | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const firstMarked = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let live = true;
    setColoured(null);
    void highlightLines(text, path).then((html) => {
      if (live && html && html.length === lines.length) setColoured(html);
    });
    return () => {
      live = false;
    };
  }, [text, path, lines.length]);

  useEffect(() => {
    const el = firstMarked.current;
    const container = box.current;
    if (!el || !container) return;
    // Centre the cited lines in the box without scrolling the page behind it.
    container.scrollTop = Math.max(0, el.offsetTop - container.clientHeight / 3);
  }, [text, highlightStart]);

  const width = String(startLine + lines.length - 1).length;
  const marked = (n: number) => highlightStart != null && highlightEnd != null && n >= highlightStart && n <= highlightEnd;
  const firstMarkedLine = highlightStart != null ? Math.max(highlightStart, startLine) : null;

  return (
    <div
      ref={box}
      role="region"
      aria-label={`Source of ${path}`}
      tabIndex={0}
      className={clsx(
        // `relative` makes this box the offset parent of its lines, so the scroll position below is measured from it.
        'relative max-h-[26rem] overflow-auto rounded-md border border-line-subtle bg-sunken font-mono text-caption leading-5 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
        className,
      )}
      data-testid="evidence-code"
    >
      <div className="min-w-max py-2">
        {lines.map((line, i) => {
          const n = startLine + i;
          const isMarked = marked(n);
          return (
            <div
              key={n}
              ref={n === firstMarkedLine ? firstMarked : undefined}
              data-line={n}
              data-marked={isMarked ? 'true' : undefined}
              className={clsx('flex', isMarked && 'bg-volt/10')}
            >
              <span
                className={clsx(
                  'shrink-0 select-none px-3 text-right tabular-nums',
                  isMarked ? 'text-ink-primary' : 'text-ink-tertiary',
                )}
                style={{ minWidth: `${width + 2}ch` }}
                aria-hidden="true"
              >
                {n}
              </span>
              {coloured ? (
                <span className="hljs whitespace-pre pr-4" dangerouslySetInnerHTML={{ __html: coloured[i] || ' ' }} />
              ) : (
                <span className="whitespace-pre pr-4 text-ink-primary">{line || ' '}</span>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
