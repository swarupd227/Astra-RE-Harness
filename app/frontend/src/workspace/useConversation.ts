/**
 * One thread's state: the react-query cache for `['conversation', id]` is
 * the single source of truth for messages; streaming (send / confirm) and
 * the live post stream write into it via upsert-by-id.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { getPersona } from '@/lib/api';
import {
  confirmActionStream,
  conversationsApi,
  openConversationStream,
  sendMessageStream,
  type Conversation,
  type ConversationMessage,
  type CopilotStreamEvent,
} from '@/lib/conversations';

export type ToolResult = { name: string; ok: boolean; summary: string; durationMs: number };

export type WorkingState = {
  phase: 'thinking' | 'tool' | 'writing';
  label: string;
  tool?: string;
  results: ToolResult[];
};

export type ThreadData = { conversation: Conversation; messages: ConversationMessage[] };

export const conversationKey = (id: string | undefined) => ['conversation', id] as const;

const EMPTY: ConversationMessage[] = [];

/**
 * Append-or-replace by id. `replaceLocalId` swaps an optimistic placeholder
 * for the persisted copy. A replayed older copy never regresses a resolved
 * confirmation back to `pending`.
 */
export function upsertMessage(
  list: ConversationMessage[],
  msg: ConversationMessage,
  replaceLocalId?: string,
): ConversationMessage[] {
  const base = replaceLocalId ? list.filter((m) => m.id !== replaceLocalId) : list;
  const idx = base.findIndex((m) => m.id === msg.id);
  if (idx === -1) return [...base, msg];
  const existing = base[idx];
  if (
    existing.pendingAction &&
    existing.pendingAction.state !== 'pending' &&
    msg.pendingAction?.state === 'pending'
  ) {
    return base;
  }
  const next = base.slice();
  next[idx] = msg;
  return next;
}

function localMessage(
  conversationId: string,
  role: ConversationMessage['role'],
  markdown: string,
  prefix: string,
): ConversationMessage {
  return {
    id: `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
    conversationId,
    role,
    agent: null,
    persona: role === 'user' ? getPersona() : null,
    authorDisplay: null,
    markdown,
    artifacts: [],
    suggestions: [],
    toolCalls: [],
    pendingAction: null,
    runId: null,
    createdAt: new Date().toISOString(),
  };
}

export function useConversation(conversationId: string | undefined) {
  const qc = useQueryClient();

  const query = useQuery({
    queryKey: conversationKey(conversationId),
    queryFn: () => conversationsApi.get(conversationId as string),
    enabled: Boolean(conversationId),
    staleTime: 30_000,
    refetchOnWindowFocus: false,
  });

  const [working, setWorking] = useState<WorkingState | null>(null);
  const [streaming, setStreaming] = useState(false);
  const [confirmingId, setConfirmingId] = useState<string | null>(null);
  /** Id of the agent message that most recently arrived over a stream. */
  const [lastArrivalId, setLastArrivalId] = useState<string | null>(null);

  const abortRef = useRef<AbortController | null>(null);
  const lastSeqRef = useRef(0);
  /** persisted user-message id → optimistic placeholder id (stable React keys). */
  const keyAliases = useRef(new Map<string, string>());

  const patch = useCallback(
    (fn: (msgs: ConversationMessage[]) => ConversationMessage[]) => {
      if (!conversationId) return;
      qc.setQueryData<ThreadData>(conversationKey(conversationId), (old) =>
        old ? { ...old, messages: fn(old.messages ?? []) } : old,
      );
    },
    [qc, conversationId],
  );

  const upsert = useCallback(
    (msg: ConversationMessage, replaceLocalId?: string) =>
      patch((list) => upsertMessage(list, msg, replaceLocalId)),
    [patch],
  );

  const appendSystem = useCallback(
    (text: string) => {
      if (!conversationId) return;
      upsert(localMessage(conversationId, 'system', text, 'local-system'));
    },
    [conversationId, upsert],
  );

  const handleEvent = useCallback(
    (evt: CopilotStreamEvent, localUserId?: string) => {
      switch (evt.type) {
        case 'user':
          if (evt.data?.id) {
            if (localUserId) keyAliases.current.set(evt.data.id, localUserId);
            upsert(evt.data, localUserId);
          }
          break;
        case 'status':
          setWorking((w) => ({
            phase: evt.data?.phase ?? 'thinking',
            label: evt.data?.label || 'Working',
            tool: evt.data?.tool,
            results: w?.results ?? [],
          }));
          break;
        case 'tool_result':
          setWorking((w) => ({
            phase: w?.phase ?? 'tool',
            label: w?.label ?? 'Working',
            tool: w?.tool,
            results: [...(w?.results ?? []), evt.data],
          }));
          break;
        case 'message':
          if (evt.data?.id) {
            upsert(evt.data);
            if (evt.data.role === 'agent') setLastArrivalId(evt.data.id);
          }
          break;
        case 'error':
          appendSystem(evt.data?.message || 'Something went wrong.');
          break;
        case 'done':
        default:
          break;
      }
    },
    [upsert, appendSystem],
  );

  /** Runs one SSE-over-POST call with cancel + error handling. */
  const runStream = useCallback(
    async (run: (signal: AbortSignal) => Promise<void>) => {
      abortRef.current?.abort();
      const ac = new AbortController();
      abortRef.current = ac;
      setStreaming(true);
      setWorking({ phase: 'thinking', label: 'Thinking', results: [] });
      try {
        await run(ac.signal);
      } catch (e) {
        if (ac.signal.aborted) appendSystem('Cancelled.');
        else appendSystem(e instanceof Error ? e.message : 'The request failed.');
      } finally {
        if (abortRef.current === ac) abortRef.current = null;
        setStreaming(false);
        setWorking(null);
        void qc.invalidateQueries({ queryKey: ['conversations'] });
      }
    },
    [appendSystem, qc],
  );

  const sendMessage = useCallback(
    async (text: string) => {
      const trimmed = text.trim();
      if (!conversationId || !trimmed || abortRef.current) return;
      const placeholder = localMessage(conversationId, 'user', trimmed, 'local-user');
      upsert(placeholder);
      await runStream((signal) =>
        sendMessageStream(conversationId, trimmed, signal, (evt) => handleEvent(evt, placeholder.id)),
      );
    },
    [conversationId, upsert, runStream, handleEvent],
  );

  const confirmAction = useCallback(
    async (messageId: string) => {
      if (!conversationId || abortRef.current) return;
      setConfirmingId(messageId);
      patch((list) =>
        list.map((m) =>
          m.id === messageId && m.pendingAction
            ? { ...m, pendingAction: { ...m.pendingAction, state: 'confirmed' as const } }
            : m,
        ),
      );
      try {
        await runStream((signal) =>
          confirmActionStream(conversationId, messageId, signal, (evt) => handleEvent(evt)),
        );
      } finally {
        setConfirmingId(null);
      }
    },
    [conversationId, patch, runStream, handleEvent],
  );

  const declineAction = useCallback(
    async (messageId: string) => {
      if (!conversationId) return;
      setConfirmingId(messageId);
      try {
        const res = await conversationsApi.decline(conversationId, messageId);
        if (res?.message?.id) {
          upsert(res.message);
        } else {
          patch((list) =>
            list.map((m) =>
              m.id === messageId && m.pendingAction
                ? { ...m, pendingAction: { ...m.pendingAction, state: 'declined' as const } }
                : m,
            ),
          );
        }
      } catch (e) {
        appendSystem(e instanceof Error ? e.message : 'Could not decline the action.');
      } finally {
        setConfirmingId(null);
        void qc.invalidateQueries({ queryKey: ['conversations'] });
      }
    },
    [conversationId, upsert, patch, appendSystem, qc],
  );

  const cancel = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  // Live posts into this thread (narrator, other tabs). Dedupe by id via
  // upsert; keep the last seq in a ref; reconnect 3 s after an error.
  useEffect(() => {
    if (!conversationId) return;
    lastSeqRef.current = 0;
    let closed = false;
    let close: (() => void) | null = null;
    let timer: number | null = null;

    const open = () => {
      if (closed) return;
      close = openConversationStream(
        conversationId,
        lastSeqRef.current,
        (m, seq) => {
          if (seq > lastSeqRef.current) lastSeqRef.current = seq;
          if (!m || typeof m !== 'object' || !m.id) return;
          upsert(m);
          if (m.role === 'agent') setLastArrivalId(m.id);
          void qc.invalidateQueries({ queryKey: ['conversations'] });
        },
        () => {
          close?.();
          close = null;
          if (!closed) timer = window.setTimeout(open, 3000);
        },
      );
    };
    open();

    return () => {
      closed = true;
      close?.();
      if (timer) window.clearTimeout(timer);
    };
  }, [conversationId, upsert, qc]);

  // Switching threads (or leaving) cancels any in-flight turn.
  useEffect(
    () => () => {
      abortRef.current?.abort();
      abortRef.current = null;
    },
    [conversationId],
  );

  useEffect(() => {
    setLastArrivalId(null);
    setWorking(null);
  }, [conversationId]);

  const messageKey = useCallback(
    (m: ConversationMessage) => keyAliases.current.get(m.id) ?? m.id,
    [],
  );

  return {
    conversation: query.data?.conversation,
    messages: query.data?.messages ?? EMPTY,
    isLoading: query.isLoading,
    error: query.error as Error | null,
    refetch: query.refetch,
    working,
    streaming,
    confirmingId,
    lastArrivalId,
    sendMessage,
    cancel,
    confirmAction,
    declineAction,
    messageKey,
  };
}

export type ConversationController = ReturnType<typeof useConversation>;
