/**
 * WS2 — conversation spine client.
 *
 * Every programme is a thread; every action is an intent typed (or clicked
 * as a chip phrased as one); every agent reply arrives as a message with
 * artifact cards attached. This module is the single place the Workspace
 * talks to `/api/v1/conversations/*`, `/api/v1/copilot/*` and the generic
 * run-event stream.
 */
import { API_BASE, apiFetch, getPersona } from './api';

// ─── Types (canonical — mirrors Conversations/ConversationDtos.cs) ────

export type ConversationKind = 'global' | 'programme' | 'spec' | 'blueprint' | 'wave';

export type AgentId =
  | 'orchestrator'
  | 'discovery'
  | 'architecture'
  | 'planning'
  | 'spec'
  | 'migration'
  | 'data'
  | 'validation'
  | 'release'
  | 'programme';

export type Conversation = {
  id: string;
  corpusId: string | null;
  kind: ConversationKind;
  title: string;
  createdAt: string;
  updatedAt: string;
  lastMessageAt: string | null;
  lastMessagePreview: string | null;
  messageCount: number;
  programme: {
    name: string;
    sourceLanguage: string | null;
    routineCount: number;
    fileCount: number;
  } | null;
};

export type ArtifactKind =
  | 'funnel'
  | 'runProgress'
  | 'routineList'
  | 'clusterGrid'
  | 'specSummary'
  | 'programmeList'
  | 'routine'
  | 'gateResults'
  | 'scaffoldTree'
  | 'text';

export type Artifact = {
  kind: ArtifactKind;
  refId: string | null;
  props: Record<string, unknown>;
};

/** `intent` is the natural-language text sent when the chip is clicked. */
export type Suggestion = { label: string; intent: string };

export type ToolCallSummary = {
  name: string;
  input: Record<string, unknown>;
  summary: string;
  ok: boolean;
  durationMs: number;
};

export type PendingAction = {
  toolName: string;
  input: Record<string, unknown>;
  summary: string;
  requiredPersona: string | null;
  state: 'pending' | 'confirmed' | 'declined';
};

export type ConversationMessage = {
  id: string;
  conversationId: string;
  role: 'user' | 'agent' | 'system';
  agent: AgentId | null;
  persona: string | null;
  authorDisplay: string | null;
  markdown: string;
  artifacts: Artifact[];
  suggestions: Suggestion[];
  toolCalls: ToolCallSummary[];
  pendingAction: PendingAction | null;
  runId: string | null;
  createdAt: string;
};

export type FunnelCounts = {
  parsed: number;
  extracting: number;
  draft: number;
  inReview: number;
  signed: number;
  scaffolded: number;
  committed: number;
  total: number;
};

export type OverviewProgramme = {
  corpusId: string;
  conversationId: string;
  name: string;
  sourceLanguage: string | null;
  counts: FunnelCounts;
  latestRun: { id: string; kind: string; state: string; startedAt: string; summary: string } | null;
};

export type Overview = {
  programmes: OverviewProgramme[];
  totals: FunnelCounts;
  telemetry: {
    costTodayUsd: number;
    callsToday: number;
    p50LatencyMs: number | null;
    cacheHitRate: number | null;
  };
};

// ─── SSE event shapes ────────────────────────────────────────────────

export type CopilotStreamEvent =
  | { type: 'user'; data: ConversationMessage }
  | { type: 'status'; data: { phase: 'thinking' | 'tool' | 'writing'; tool?: string; label: string } }
  | { type: 'tool_result'; data: { name: string; ok: boolean; summary: string; durationMs: number } }
  | { type: 'message'; data: ConversationMessage }
  | { type: 'error'; data: { code: string; message: string } }
  | { type: 'done'; data: Record<string, never> };

/** Generic run event (same shape the pattern-analysis /logs route emits). */
export type RunStreamEvent = {
  type: 'log' | 'progress' | 'stage' | 'state' | 'item' | 'done';
  seq: number;
  ts: string;
  agent: string;
  stage: string;
  message: string | null;
  data: Record<string, unknown> | null;
};

// ─── REST ────────────────────────────────────────────────────────────

export const conversationsApi = {
  list: (corpusId?: string) =>
    apiFetch<{ data: Conversation[] }>(
      `/api/v1/conversations${corpusId ? `?corpusId=${encodeURIComponent(corpusId)}` : ''}`,
    ),
  global: () => apiFetch<Conversation>('/api/v1/conversations/global'),
  get: (id: string) =>
    apiFetch<{ conversation: Conversation; messages: ConversationMessage[] }>(`/api/v1/conversations/${id}`),
  decline: (conversationId: string, messageId: string) =>
    apiFetch<{ message: ConversationMessage }>(
      `/api/v1/conversations/${conversationId}/messages/${messageId}/decline`,
      { method: 'POST' },
    ),
  overview: () => apiFetch<Overview>('/api/v1/copilot/overview'),
};

// ─── SSE over POST (fetch + ReadableStream) ──────────────────────────

async function readSse(
  res: Response,
  onFrame: (type: string, data: string) => void,
): Promise<void> {
  if (!res.ok || !res.body) throw new Error(`Request failed (${res.status})`);
  const reader = res.body.getReader();
  const decoder = new TextDecoder('utf-8');
  let buffer = '';
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let idx;
    while ((idx = buffer.indexOf('\n\n')) >= 0) {
      const raw = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 2);
      let type = 'message';
      let data = '';
      for (const line of raw.split('\n')) {
        if (line.startsWith('event:')) type = line.slice(6).trim();
        else if (line.startsWith('data:')) data += line.slice(5).trim();
      }
      if (data) onFrame(type, data);
    }
  }
}

function sseHeaders(): HeadersInit {
  return {
    'X-Dev-Persona': getPersona(),
    Accept: 'text/event-stream',
    'Cache-Control': 'no-cache',
    'Content-Type': 'application/json',
  };
}

/** Send a user turn and stream the orchestrator's reply. */
export async function sendMessageStream(
  conversationId: string,
  text: string,
  signal: AbortSignal,
  onEvent: (evt: CopilotStreamEvent) => void,
): Promise<void> {
  const res = await fetch(`${API_BASE}/api/v1/conversations/${conversationId}/messages`, {
    method: 'POST',
    headers: sseHeaders(),
    body: JSON.stringify({ text }),
    signal,
  });
  await readSse(res, (type, data) => {
    try {
      onEvent({ type, data: JSON.parse(data) } as CopilotStreamEvent);
    } catch {
      /* ignore malformed frame */
    }
  });
}

/** Confirm a pending state-changing action and stream the continuation. */
export async function confirmActionStream(
  conversationId: string,
  messageId: string,
  signal: AbortSignal,
  onEvent: (evt: CopilotStreamEvent) => void,
): Promise<void> {
  const res = await fetch(
    `${API_BASE}/api/v1/conversations/${conversationId}/messages/${messageId}/confirm`,
    { method: 'POST', headers: sseHeaders(), signal },
  );
  await readSse(res, (type, data) => {
    try {
      onEvent({ type, data: JSON.parse(data) } as CopilotStreamEvent);
    } catch {
      /* ignore malformed frame */
    }
  });
}

// ─── Live streams (GET, EventSource) ─────────────────────────────────

/**
 * Live posts into a thread (narrator, other tabs). Returns a closer.
 * EventSource cannot set headers, so the persona rides on the query string
 * (the stream is read-only; the server ignores it for authorisation).
 */
export function openConversationStream(
  conversationId: string,
  afterSeq: number,
  onMessage: (m: ConversationMessage, seq: number) => void,
  onError?: () => void,
): () => void {
  const es = new EventSource(
    `${API_BASE}/api/v1/conversations/${conversationId}/stream?afterSeq=${afterSeq}`,
  );
  es.addEventListener('message', (e) => {
    try {
      const payload = JSON.parse((e as MessageEvent).data);
      const seq = Number((e as MessageEvent).lastEventId || payload.seq || 0);
      onMessage(payload.data ?? payload, seq);
    } catch {
      /* ignore */
    }
  });
  es.onerror = () => onError?.();
  return () => es.close();
}

/** Generic run event stream for a `runProgress` card. Returns a closer. */
export function openRunStream(
  runId: string,
  afterSeq: number,
  onEvent: (evt: RunStreamEvent) => void,
  onEnd?: () => void,
): () => void {
  const es = new EventSource(`${API_BASE}/api/v1/runs/${runId}/events?afterSeq=${afterSeq}`);
  const handle = (type: RunStreamEvent['type']) => (e: Event) => {
    try {
      const payload = JSON.parse((e as MessageEvent).data);
      onEvent({ ...payload, type: payload.type ?? type });
    } catch {
      /* ignore */
    }
  };
  es.onmessage = handle('log');
  for (const t of ['progress', 'stage', 'state', 'item'] as const) es.addEventListener(t, handle(t));
  es.addEventListener('done', () => {
    onEnd?.();
    es.close();
  });
  es.onerror = () => {
    /* EventSource auto-reconnects with Last-Event-ID */
  };
  return () => es.close();
}
