import { memo } from 'react';
import { clsx } from 'clsx';
import { agentMeta } from '@/agents/agents';
import type { ConversationMessage, ToolCallSummary } from '@/lib/conversations';
import { AgentAvatar } from './AgentAvatar';
import { ConfirmCard, ResolvedActionLine } from './ConfirmCard';
import { Markdown } from './Markdown';
import { SuggestionChips } from './SuggestionChips';
import { ArtifactCard } from './artifacts/ArtifactCard';
import { absoluteTime, formatDuration, personaLabel, pretty, relativeTime, TONE_DOT } from './format';

export type ArtifactSelection = { messageId: string; index: number };

/** Kinds that need the full column width when rendered as a card. */
const WIDE_KINDS = new Set(['routineList', 'clusterGrid', 'routine', 'specSummary']);

export const MessageBubble = memo(function MessageBubble({
  message,
  now,
  selected,
  onSelectArtifact,
  onSuggestion,
  onConfirm,
  onDecline,
  confirming = false,
  disabled = false,
}: {
  message: ConversationMessage;
  now: number;
  selected: ArtifactSelection | null;
  onSelectArtifact: (sel: ArtifactSelection) => void;
  onSuggestion: (intent: string) => void;
  onConfirm: (messageId: string) => void;
  onDecline: (messageId: string) => void;
  confirming?: boolean;
  disabled?: boolean;
}) {
  const when = relativeTime(message.createdAt, now);
  const whenTitle = absoluteTime(message.createdAt);

  if (message.role === 'system') {
    return (
      <div data-testid="thread-message" data-role="system" className="flex justify-center">
        <div className="max-w-[85%] rounded-lg border border-line-subtle bg-sunken px-3 py-1.5 text-caption text-ink-secondary">
          {message.markdown}
        </div>
      </div>
    );
  }

  if (message.role === 'user') {
    const who = message.authorDisplay || personaLabel(message.persona) || 'You';
    return (
      <div data-testid="thread-message" data-role="user" className="flex justify-end">
        <div className="max-w-[78%] min-w-0">
          <div className="whitespace-pre-wrap rounded-2xl rounded-br-md border border-line-subtle bg-sunken px-4 py-3 text-body text-ink-primary [overflow-wrap:anywhere]">
            {message.markdown}
          </div>
          <div className="mt-1 flex justify-end gap-1.5 text-micro text-ink-tertiary">
            <span>{who}</span>
            {when && (
              <>
                <span aria-hidden="true">·</span>
                <span title={whenTitle}>{when}</span>
              </>
            )}
          </div>
        </div>
      </div>
    );
  }

  const meta = agentMeta(message.agent);
  const artifacts = message.artifacts ?? [];
  const toolCalls = message.toolCalls ?? [];
  const suggestions = message.suggestions ?? [];
  const pending = message.pendingAction;
  const twoUp = artifacts.length > 1 && !artifacts.some((a) => WIDE_KINDS.has(a.kind));

  return (
    <div
      data-testid="thread-message"
      data-role="agent"
      data-agent={message.agent ?? 'orchestrator'}
      className="flex gap-3"
    >
      <AgentAvatar agent={message.agent} size="md" className="mt-0.5" />
      <div className="min-w-0 flex-1 space-y-3">
        <div className="flex items-baseline gap-2 text-caption">
          <span className="font-medium text-ink-primary">{meta.name}</span>
          {when && (
            <span className="text-ink-tertiary" title={whenTitle}>
              {when}
            </span>
          )}
        </div>

        {message.markdown && <Markdown>{message.markdown}</Markdown>}

        {artifacts.length > 0 && (
          <div className={clsx('grid gap-3', twoUp && 'md:grid-cols-2')}>
            {artifacts.map((a, i) => (
              <ArtifactCard
                key={`${a.kind}-${a.refId ?? i}-${i}`}
                artifact={a}
                selected={selected?.messageId === message.id && selected.index === i}
                onSelect={() => onSelectArtifact({ messageId: message.id, index: i })}
              />
            ))}
          </div>
        )}

        {toolCalls.length > 0 && <SourcesRow toolCalls={toolCalls} />}

        {pending &&
          (pending.state === 'pending' ? (
            <ConfirmCard
              action={pending}
              busy={confirming}
              disabled={disabled}
              onConfirm={() => onConfirm(message.id)}
              onDecline={() => onDecline(message.id)}
            />
          ) : (
            <ResolvedActionLine action={pending} />
          ))}

        {suggestions.length > 0 && (
          <SuggestionChips suggestions={suggestions} onPick={onSuggestion} disabled={disabled} />
        )}
      </div>
    </div>
  );
});

/** "Sources" — the tool calls behind an answer, with the input on hover. */
function SourcesRow({ toolCalls }: { toolCalls: ToolCallSummary[] }) {
  return (
    <div className="flex flex-wrap items-center gap-1.5" data-testid="sources-row">
      <span className="mr-0.5 text-micro uppercase tracking-wide text-ink-tertiary">Sources</span>
      {toolCalls.map((t, i) => (
        <span key={`${t.name}-${i}`} className="group/tool relative inline-flex">
          <span
            className="inline-flex max-w-[320px] items-center gap-1.5 rounded-full border border-line-subtle bg-raised px-2.5 py-1 text-micro text-ink-secondary"
            tabIndex={0}
            data-testid="tool-call-chip"
            data-ok={t.ok ? 'true' : 'false'}
          >
            <span className={clsx('h-1.5 w-1.5 shrink-0 rounded-full', t.ok ? TONE_DOT.ok : TONE_DOT.fail)} aria-hidden="true" />
            <span className="font-mono text-ink-primary">{t.name}</span>
            {t.summary && <span className="truncate">{t.summary}</span>}
            {t.durationMs > 0 && <span className="text-ink-tertiary">{formatDuration(t.durationMs)}</span>}
          </span>
          <span
            role="tooltip"
            className="pointer-events-none absolute left-0 top-full z-20 mt-1 hidden w-[360px] max-w-[80vw] rounded-lg border border-line bg-raised p-2 shadow-e3 group-hover/tool:block group-focus-within/tool:block"
          >
            <span className="mb-1 block text-micro text-ink-tertiary">Input</span>
            <pre className="max-h-48 overflow-auto rounded-md bg-codebg p-2 font-mono text-[11.5px] leading-[1.5] text-sand-100">
              {pretty(t.input)}
            </pre>
          </span>
        </span>
      ))}
    </div>
  );
}
