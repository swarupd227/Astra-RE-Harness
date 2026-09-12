import { memo, type ComponentProps } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { Link } from 'react-router-dom';
import { clsx } from 'clsx';

/** Drop react-markdown's `node` prop before spreading onto a DOM element. */
function clean<T extends { node?: unknown }>(p: T): Omit<T, 'node'> {
  const { node, ...rest } = p;
  void node;
  return rest;
}

const components: Components = {
  p: (p) => <p {...clean(p)} className="my-2 first:mt-0 last:mb-0" />,
  h1: (p) => <h1 {...clean(p)} className="mb-2 mt-4 text-h-md font-semibold text-ink-primary first:mt-0" />,
  h2: (p) => <h2 {...clean(p)} className="mb-2 mt-4 text-h-sm font-semibold text-ink-primary first:mt-0" />,
  h3: (p) => <h3 {...clean(p)} className="mb-1.5 mt-3 text-body font-semibold text-ink-primary first:mt-0" />,
  h4: (p) => <h4 {...clean(p)} className="mb-1 mt-3 text-body font-medium text-ink-primary first:mt-0" />,
  ul: (p) => <ul {...clean(p)} className="my-2 list-disc space-y-1 pl-5 marker:text-ink-tertiary" />,
  ol: (p) => <ol {...clean(p)} className="my-2 list-decimal space-y-1 pl-5 marker:text-ink-tertiary" />,
  li: (p) => <li {...clean(p)} className="[&>p]:my-0" />,
  strong: (p) => <strong {...clean(p)} className="font-semibold text-ink-primary" />,
  em: (p) => <em {...clean(p)} className="italic" />,
  hr: (p) => <hr {...clean(p)} className="my-4 border-line-subtle" />,
  blockquote: (p) => (
    <blockquote
      {...clean(p)}
      className="my-2 border-l-2 border-line pl-3 text-ink-secondary [&>p]:my-1"
    />
  ),
  a: (p) => {
    const { href, children, ...rest } = clean(p);
    if (href && href.startsWith('/')) {
      return (
        <Link to={href} className="text-ink-link underline-offset-2 hover:underline" {...rest}>
          {children}
        </Link>
      );
    }
    return (
      <a
        href={href}
        target="_blank"
        rel="noreferrer"
        className="text-ink-link underline-offset-2 hover:underline"
        {...rest}
      >
        {children}
      </a>
    );
  },
  code: (p) => (
    <code
      {...clean(p)}
      className={clsx(
        'rounded bg-sunken px-1 py-px font-mono text-[13px] text-ink-primary',
        p.className,
      )}
    />
  ),
  pre: (p) => (
    <pre
      {...clean(p)}
      className="my-3 overflow-x-auto rounded-lg border border-line-subtle bg-codebg p-3 font-mono text-[13px] leading-[1.55] text-sand-100 [&>code]:rounded-none [&>code]:bg-transparent [&>code]:p-0 [&>code]:text-inherit"
    />
  ),
  table: (p) => (
    <div className="my-3 overflow-x-auto rounded-lg border border-line-subtle">
      <table {...clean(p)} className="w-full border-collapse text-caption" />
    </div>
  ),
  thead: (p) => <thead {...clean(p)} className="bg-sunken text-ink-secondary" />,
  th: (p) => (
    <th {...clean(p)} className="border-b border-line-subtle px-3 py-1.5 text-left font-medium" />
  ),
  td: (p) => <td {...clean(p)} className="border-b border-line-subtle px-3 py-1.5 align-top last:border-b-0" />,
  tr: (p) => <tr {...clean(p)} className="last:[&>td]:border-b-0" />,
  input: (p) => {
    const { type, ...rest } = clean(p);
    if (type === 'checkbox') {
      return <input type="checkbox" {...rest} className="mr-1.5 accent-volt" disabled />;
    }
    return <input type={type} {...rest} />;
  },
};

/**
 * Agent prose. GFM on (tables, task lists, strikethrough); routine names in
 * backticks get the mono inline-code treatment.
 */
export const Markdown = memo(function Markdown({
  children,
  className,
}: {
  children: string;
  className?: string;
}) {
  return (
    <div className={clsx('text-body text-ink-primary/90 [overflow-wrap:anywhere]', className)}>
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {children ?? ''}
      </ReactMarkdown>
    </div>
  );
});

export type MarkdownProps = ComponentProps<typeof Markdown>;
