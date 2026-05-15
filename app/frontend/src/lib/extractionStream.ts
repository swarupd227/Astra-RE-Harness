/**
 * SSE-over-POST client for /api/v1/subroutines/:id/extract.
 *
 * Browser EventSource only supports GET, so we use fetch() + ReadableStream
 * to consume the same `event: ...\ndata: ...\n\n` framing.
 */
import { getPersona } from './api';

const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');

export type ExtractionEvent =
  | { type: 'provider_info'; data: ProviderInfo }
  | { type: 'stage'; data: { stage: string; step: number; of: number; label?: string } }
  | { type: 'patch'; data: { op: string; path: string; value: unknown } }
  | { type: 'token'; data: { path: string; text: string } }
  | { type: 'citation_pulse'; data: { claimPath: string; lines: string } }
  | { type: 'warning'; data: { code: string; message: string } }
  | { type: 'done'; data: DoneEvent }
  | { type: 'error'; data: { code: string; message: string; retryable?: boolean } };

export type ProviderInfo = {
  name: string;
  model: string;
  configVersion: string;
  promptTemplateId: string;
  promptTemplateVersion: string;
};

export type DoneEvent = {
  specId: string;
  callId: string;
  inputTokens: number;
  outputTokens: number;
  latencyMs: number;
  costUsd: number;
};

export async function streamExtraction(
  subroutineId: string,
  signal: AbortSignal,
  onEvent: (evt: ExtractionEvent) => void,
): Promise<void> {
  const res = await fetch(`${API_BASE}/api/v1/subroutines/${subroutineId}/extract`, {
    method: 'POST',
    headers: {
      'X-Dev-Persona': getPersona(),
      Accept: 'text/event-stream',
      'Cache-Control': 'no-cache',
    },
    signal,
  });

  if (!res.ok || !res.body) {
    throw new Error(`Extraction request failed (${res.status})`);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder('utf-8');
  let buffer = '';

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    // Split on the SSE event terminator
    let idx;
    while ((idx = buffer.indexOf('\n\n')) >= 0) {
      const raw = buffer.slice(0, idx);
      buffer = buffer.slice(idx + 2);
      const evt = parseSseEvent(raw);
      if (evt) onEvent(evt);
    }
  }
}

function parseSseEvent(raw: string): ExtractionEvent | null {
  let type = '';
  let data = '';
  for (const line of raw.split('\n')) {
    if (line.startsWith('event:')) type = line.slice(6).trim();
    else if (line.startsWith('data:')) data += line.slice(5).trim();
  }
  if (!type || !data) return null;
  try {
    return { type, data: JSON.parse(data) } as ExtractionEvent;
  } catch {
    return null;
  }
}

/**
 * Apply a JSON-patch-style { op, path, value } to a draft object using a
 * minimal pointer parser. Supports `add` ops only — sufficient for B.2.
 *
 * Path syntax: '/foo/bar/0' walks .foo.bar[0]. '/-' on an array means append.
 */
export function applyPatch(
  draft: Record<string, unknown>,
  op: { op: string; path: string; value: unknown },
): void {
  if (op.op !== 'add') return;
  const segments = op.path.split('/').slice(1);
  let cursor: any = draft;
  for (let i = 0; i < segments.length - 1; i++) {
    const seg = segments[i];
    const next = segments[i + 1];
    const isNextNumeric = next === '-' || /^\d+$/.test(next);
    if (!(seg in cursor)) {
      cursor[seg] = isNextNumeric ? [] : {};
    }
    cursor = cursor[seg];
  }
  const last = segments[segments.length - 1];
  if (Array.isArray(cursor)) {
    if (last === '-') cursor.push(op.value);
    else cursor[Number(last)] = op.value;
  } else {
    cursor[last] = op.value;
  }
}
