import { clsx } from 'clsx';
import { Sparkles } from 'lucide-react';
import type { Suggestion } from '@/lib/conversations';

/**
 * Suggested next intents, phrased as natural language. Clicking one sends
 * `intent` as a new user turn — chips are the clickable form of typing.
 */
export function SuggestionChips({
  suggestions,
  onPick,
  disabled = false,
  className,
  lead = false,
}: {
  suggestions: Suggestion[];
  onPick: (intent: string) => void;
  disabled?: boolean;
  className?: string;
  /** Larger "starter" styling for empty threads. */
  lead?: boolean;
}) {
  const list = (suggestions ?? []).filter((s) => s && (s.intent || s.label));
  if (list.length === 0) return null;
  return (
    <div className={clsx('flex flex-wrap gap-2', className)} role="group" aria-label="Suggestions">
      {list.map((s, i) => {
        const intent = s.intent || s.label;
        return (
          <button
            key={`${intent}-${i}`}
            type="button"
            data-testid="suggestion-chip"
            data-intent={intent}
            disabled={disabled}
            onClick={() => onPick(intent)}
            className={clsx(
              'inline-flex max-w-full items-center gap-1.5 rounded-full border text-left transition-colors duration-fast',
              'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
              'disabled:cursor-not-allowed disabled:opacity-50',
              lead
                ? 'border-line bg-raised px-4 py-2 text-body text-ink-secondary hover:border-line-strong hover:text-ink-primary'
                : 'border-line-subtle bg-raised px-3 py-1.5 text-caption text-ink-secondary hover:border-line hover:text-ink-primary',
            )}
          >
            {lead && <Sparkles size={14} className="shrink-0 text-ink-tertiary" aria-hidden="true" />}
            <span className="truncate">{s.label || intent}</span>
          </button>
        );
      })}
    </div>
  );
}
