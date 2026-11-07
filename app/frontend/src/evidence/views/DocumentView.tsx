import { ChevronRight } from 'lucide-react';
import { DefaultEvidenceBody, type EvidenceBodyProps } from '@/evidence/views/DefaultBody';

/**
 * A section of a document: where it sits under its headings, then its text — as lines of the file for
 * Markdown and XML, as the section's own text for a Word file (which has no lines).
 */
export function DocumentView({ detail }: EvidenceBodyProps) {
  const path = Array.isArray(detail.structured?.path)
    ? (detail.structured.path as unknown[]).filter((p): p is string => typeof p === 'string')
    : [];

  return (
    <div className="space-y-3" data-testid="document-view">
      {path.length > 1 && (
        <nav aria-label="Where this section is" className="flex flex-wrap items-center gap-1 text-caption text-ink-secondary" data-testid="document-path">
          {path.map((p, i) => (
            <span key={`${i}-${p}`} className="inline-flex items-center gap-1">
              {i > 0 && <ChevronRight className="h-3 w-3 text-ink-tertiary" aria-hidden="true" />}
              <span className={i === path.length - 1 ? 'text-ink-primary' : undefined}>{p}</span>
            </span>
          ))}
        </nav>
      )}
      <DefaultEvidenceBody detail={detail} />
    </div>
  );
}
