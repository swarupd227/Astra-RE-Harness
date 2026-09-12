/**
 * ThreadPanel — the compact form of the Workspace: one thread, its composer
 * and the working row, nothing else. No Mission Control, no artifact pane;
 * cards render inline and the selected one expands in place. Used beside a
 * page that already owns the main surface (the spec review page's "Spec
 * agent"), and shares its empty/error states with `WorkspacePage`.
 */
import { useCallback, useEffect, useMemo, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { clsx } from 'clsx';
import { getPersona } from '@/lib/api';
import type { AgentId, Suggestion } from '@/lib/conversations';
import { AgentAvatar } from './AgentAvatar';
import { Composer } from './Composer';
import { ProgrammeThread } from './ProgrammeThread';
import { SuggestionChips } from './SuggestionChips';
import { ThreadActionsProvider, type ThreadActions } from './ThreadActions';
import type { ArtifactSelection } from './MessageBubble';
import { useConversation } from './useConversation';
import { personaLabel } from './format';

/** A starter chip; `prefill` puts the text in the composer instead of sending it. */
export type Starter = Suggestion & { prefill?: boolean };

export function ThreadPanel({
  conversationId,
  resolving = false,
  resolveError = null,
  onRetryResolve,
  starters,
  agent = 'orchestrator',
  emptyTitle,
  emptyBody,
  hint,
  placeholder,
  actions,
  className,
  testid = 'thread-panel',
}: {
  /** Undefined while the host is still resolving which thread to open. */
  conversationId: string | undefined;
  /** The host is still resolving the thread id. */
  resolving?: boolean;
  /** The host failed to resolve the thread id. */
  resolveError?: Error | null;
  onRetryResolve?: () => void;
  starters: Starter[];
  /** Avatar shown in the empty state (the agent this thread is "about"). */
  agent?: AgentId;
  emptyTitle: string;
  emptyBody?: string;
  hint?: string;
  placeholder?: string;
  /** Extra thread actions (citation handling …); `sendIntent` is supplied here. */
  actions?: Omit<ThreadActions, 'sendIntent'>;
  className?: string;
  testid?: string;
}) {
  const conv = useConversation(conversationId);
  const messages = conv.messages;

  // Inline expansion: the selected card renders at pane size in place.
  const [selection, setSelection] = useState<ArtifactSelection | null>(null);
  useEffect(() => setSelection(null), [conversationId]);
  const toggleSelect = useCallback(
    (sel: ArtifactSelection) =>
      setSelection((cur) => (cur && cur.messageId === sel.messageId && cur.index === sel.index ? null : sel)),
    [],
  );

  const send = conv.sendMessage;
  const onSuggestion = useCallback((intent: string) => void send(intent), [send]);
  const [prefill, setPrefill] = useState<{ text: string; nonce: number } | null>(null);
  const onStarter = useCallback(
    (intent: string) => {
      const s = starters.find((x) => (x.intent || x.label) === intent);
      if (s?.prefill) setPrefill({ text: intent, nonce: Date.now() });
      else void send(intent);
    },
    [starters, send],
  );
  const { confirmAction, declineAction } = conv;
  const onConfirm = useCallback((id: string) => void confirmAction(id), [confirmAction]);
  const onDecline = useCallback((id: string) => void declineAction(id), [declineAction]);

  const threadActions = useMemo<ThreadActions>(
    () => ({
      ...actions,
      sendIntent: onSuggestion,
      openArtifact: (kind, refId) => {
        for (let i = messages.length - 1; i >= 0; i--) {
          const m = messages[i];
          const idx = (m.artifacts ?? []).findIndex((a) => a.kind === kind && a.refId === refId);
          if (idx >= 0) {
            setSelection({ messageId: m.id, index: idx });
            return true;
          }
        }
        return actions?.openArtifact?.(kind, refId) ?? false;
      },
    }),
    [actions, onSuggestion, messages],
  );

  const persona = getPersona();
  const loading = resolving || (!!conversationId && conv.isLoading);
  const loadError = resolveError ?? (conversationId ? conv.error : null);
  const ready = !!conversationId && !!conv.conversation;

  return (
    <ThreadActionsProvider value={threadActions}>
      <section
        data-testid={testid}
        className={clsx('flex min-h-0 min-w-0 flex-col bg-canvas text-ink-primary', className)}
        aria-label="Agent thread"
      >
        {loadError && !conv.conversation ? (
          <LoadError error={loadError} onRetry={() => (conversationId ? conv.refetch() : onRetryResolve?.())} />
        ) : (
          <ProgrammeThread
            key={conversationId ?? 'pending'}
            messages={messages}
            messageKey={conv.messageKey}
            working={conv.working}
            streaming={conv.streaming}
            loading={loading}
            confirmingId={conv.confirmingId}
            selected={selection}
            onSelectArtifact={toggleSelect}
            onSuggestion={onSuggestion}
            onConfirm={onConfirm}
            onDecline={onDecline}
            expandInline
            compact
            empty={
              <EmptyThread
                agent={agent}
                title={emptyTitle}
                body={emptyBody}
                starters={starters}
                onPick={onStarter}
                disabled={conv.streaming || !ready}
              />
            }
          />
        )}
        <Composer
          compact
          onSend={(t) => void send(t)}
          onCancel={conv.cancel}
          streaming={conv.streaming}
          disabled={!ready}
          hint={hint ?? `Acting as ${personaLabel(persona)}`}
          placeholder={placeholder}
          prefill={prefill}
        />
      </section>
    </ThreadActionsProvider>
  );
}

/** Starter block for a thread with no messages. Shared with the Workspace. */
export function EmptyThread({
  agent = 'orchestrator',
  title,
  body,
  starters,
  onPick,
  disabled,
  lead = false,
}: {
  agent?: AgentId;
  title: string;
  body?: string;
  starters: Suggestion[];
  onPick: (intent: string) => void;
  disabled: boolean;
  /** Larger chips (the Workspace's full-width empty state). */
  lead?: boolean;
}) {
  return (
    <div className="flex flex-col items-start gap-4 py-2" data-testid="thread-empty">
      <div className="flex items-center gap-3">
        <AgentAvatar agent={agent} size="lg" />
        <div>
          <p className="text-body font-medium text-ink-primary">{title}</p>
          {body && <p className="text-caption text-ink-secondary">{body}</p>}
        </div>
      </div>
      <SuggestionChips suggestions={starters} onPick={onPick} disabled={disabled} lead={lead} />
    </div>
  );
}

/** Shown when a thread cannot be opened. Shared with the Workspace. */
export function LoadError({ error, onRetry }: { error: Error; onRetry: () => void }) {
  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-3 px-6 text-center" data-testid="thread-error">
      <p className="text-body text-ink-primary">Couldn’t open this thread.</p>
      <p className="max-w-md text-caption text-ink-secondary">{error.message}</p>
      <button
        type="button"
        onClick={onRetry}
        className="inline-flex items-center gap-1.5 rounded-lg border border-line px-3 py-1.5 text-caption font-medium text-ink-secondary hover:border-line-strong hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
      >
        <RefreshCw size={13} aria-hidden="true" />
        Try again
      </button>
    </div>
  );
}
