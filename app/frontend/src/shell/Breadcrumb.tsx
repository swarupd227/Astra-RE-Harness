import { ChevronRight } from 'lucide-react';

export function Breadcrumb({ items }: { items: { label: string; href?: string }[] }) {
  return (
    <nav aria-label="Breadcrumb" className="border-b border-border-subtle bg-canvas px-6 py-2">
      <ol className="flex items-center gap-1 text-caption text-ink-secondary">
        {items.map((item, idx) => (
          <li key={idx} className="flex items-center gap-1">
            {idx > 0 && (
              <ChevronRight className="h-3 w-3 text-ink-tertiary" aria-hidden="true" />
            )}
            {item.href ? (
              <a href={item.href} className="hover:text-ink-primary">
                {item.label}
              </a>
            ) : (
              <span className={idx === items.length - 1 ? 'text-ink-primary' : ''}>
                {item.label}
              </span>
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
}
