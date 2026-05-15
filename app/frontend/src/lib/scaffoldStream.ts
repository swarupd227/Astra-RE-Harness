/**
 * SSE-over-POST client for /api/v1/specs/:id/scaffold.
 * Mirrors extractionStream.ts but for the Stage-5 file-emission protocol.
 */
import { getPersona } from './api';

const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');

export type ProviderInfo = {
  name: string;
  model: string;
  configVersion: string;
  promptTemplateId: string;
  promptTemplateVersion: string;
  targetPlatform: string;
};

export type ScaffoldDoneEvent = {
  scaffoldId: string;
  specId: string;
  fileCount: number;
  totalLines: number;
  todoCount: number;
  inputTokens: number;
  outputTokens: number;
  latencyMs: number;
  costUsd: number;
  packageBlobUri: string;
};

export type ScaffoldEvent =
  | { type: 'provider_info'; data: ProviderInfo }
  | { type: 'stage'; data: { stage: string; step: number; of: number; label?: string } }
  | { type: 'file_started'; data: { path: string; language: string; derivedFrom: string[] } }
  | { type: 'file_chunk'; data: { path: string; content: string } }
  | { type: 'file_done'; data: { path: string; lineCount: number; todoCount: number; derivedFrom: string[] } }
  | { type: 'done'; data: ScaffoldDoneEvent }
  | { type: 'error'; data: { code: string; message: string; retryable?: boolean } };

export async function streamScaffold(
  specId: string,
  options: { targetStack?: string } | AbortSignal,
  signalOrCallback: AbortSignal | ((evt: ScaffoldEvent) => void),
  maybeOnEvent?: (evt: ScaffoldEvent) => void,
): Promise<void> {
  // Backwards-compatible overload: streamScaffold(specId, signal, onEvent)
  // still works for callers that haven't been updated yet.
  let targetStack: string | undefined;
  let signal: AbortSignal;
  let onEvent: (evt: ScaffoldEvent) => void;
  if (options instanceof AbortSignal) {
    signal = options;
    onEvent = signalOrCallback as (evt: ScaffoldEvent) => void;
  } else {
    targetStack = options.targetStack;
    signal = signalOrCallback as AbortSignal;
    onEvent = maybeOnEvent!;
  }

  const qs = targetStack && targetStack !== 'dotnet8'
    ? `?targetStack=${encodeURIComponent(targetStack)}`
    : '';
  const res = await fetch(`${API_BASE}/api/v1/specs/${specId}/scaffold${qs}`, {
    method: 'POST',
    headers: {
      'X-Dev-Persona': getPersona(),
      Accept: 'text/event-stream',
      'Cache-Control': 'no-cache',
    },
    signal,
  });

  if (!res.ok || !res.body) {
    let msg = `Scaffold request failed (${res.status})`;
    try {
      const j = await res.json();
      msg = j?.error?.message ?? msg;
    } catch { /* ignore */ }
    throw new Error(msg);
  }

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
      const evt = parseSseEvent(raw);
      if (evt) onEvent(evt);
    }
  }
}

function parseSseEvent(raw: string): ScaffoldEvent | null {
  let type = '';
  let data = '';
  for (const line of raw.split('\n')) {
    if (line.startsWith('event:')) type = line.slice(6).trim();
    else if (line.startsWith('data:')) data += line.slice(5).trim();
  }
  if (!type || !data) return null;
  try {
    return { type, data: JSON.parse(data) } as ScaffoldEvent;
  } catch {
    return null;
  }
}
