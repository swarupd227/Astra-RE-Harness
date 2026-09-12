import { clsx } from 'clsx';
import { AgentAvatar } from './AgentAvatar';
import { formatDuration, TONE_DOT } from './format';
import type { WorkingState } from './useConversation';

/**
 * Shown while a turn is streaming: pulsing volt ring on the orchestrator's
 * avatar, the current status label, and each completed tool result as it
 * lands.
 */
export function AgentWorkingRow({ working }: { working: WorkingState }) {
  return (
    <div className="flex gap-3" data-testid="agent-working-row" aria-live="polite" aria-busy="true">
      <AgentAvatar agent="orchestrator" size="md" working className="mt-0.5" />
      <div className="min-w-0 flex-1 space-y-2 pt-1">
        <div className="flex items-center gap-2 text-caption">
          <span className="font-medium text-ink-primary">Astra</span>
          <span className="text-ink-secondary">
            {working.label}
            <span className="inline-block w-4 text-left text-ink-tertiary" aria-hidden="true">
              <Dots />
            </span>
          </span>
          {working.tool && (
            <span className="rounded bg-sunken px-1.5 py-px font-mono text-micro text-ink-tertiary">{working.tool}</span>
          )}
        </div>
        {working.results.length > 0 && (
          <ul className="flex flex-wrap gap-1.5" aria-label="Completed steps">
            {working.results.map((r, i) => (
              <li
                key={`${r.name}-${i}`}
                className="inline-flex max-w-full items-center gap-1.5 rounded-full border border-line-subtle bg-raised px-2.5 py-1 text-micro text-ink-secondary"
                title={r.summary}
              >
                <span className={clsx('h-1.5 w-1.5 shrink-0 rounded-full', r.ok ? TONE_DOT.ok : TONE_DOT.fail)} aria-hidden="true" />
                <span className="font-mono text-ink-primary">{r.name}</span>
                {r.summary && <span className="truncate">{r.summary}</span>}
                {r.durationMs > 0 && <span className="text-ink-tertiary">{formatDuration(r.durationMs)}</span>}
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function Dots() {
  return (
    <span className="motion-reduce:hidden">
      <span className="animate-pulse [animation-delay:0ms]">.</span>
      <span className="animate-pulse [animation-delay:200ms]">.</span>
      <span className="animate-pulse [animation-delay:400ms]">.</span>
    </span>
  );
}
