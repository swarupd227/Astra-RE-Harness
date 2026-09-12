/**
 * Tiny cross-component store for the ⌘K palette so the TopBar trigger
 * (shell) and the palette itself (copilot) don't need a shared parent.
 *
 * Also carries `pendingIntent`: free text typed into the palette that the
 * Workspace should send as the next user turn once it has a thread open.
 */
import { useSyncExternalStore } from 'react';

type State = { open: boolean; initialQuery: string; pendingIntent: string | null };
let state: State = { open: false, initialQuery: '', pendingIntent: null };
const listeners = new Set<() => void>();

function emit() {
  for (const l of listeners) l();
}

function subscribe(cb: () => void) {
  listeners.add(cb);
  return () => {
    listeners.delete(cb);
  };
}

export function openCommandPalette(initialQuery = '') {
  state = { ...state, open: true, initialQuery };
  emit();
}

export function closeCommandPalette() {
  state = { ...state, open: false };
  emit();
}

export function useCommandPalette(): State {
  return useSyncExternalStore(subscribe, () => state, () => state);
}

/** Queue a natural-language intent for the Workspace to send. */
export function setPendingIntent(text: string) {
  state = { ...state, pendingIntent: text };
  emit();
}

/** Consume (and clear) the queued intent. Returns null when nothing is queued. */
export function takePendingIntent(): string | null {
  const text = state.pendingIntent;
  if (text !== null) {
    state = { ...state, pendingIntent: null };
    emit();
  }
  return text;
}

/** Reactive view of the queued intent so the Workspace can react to it even
 *  when the route does not change (e.g. already on the thread it targets). */
export function usePendingIntent(): string | null {
  return useSyncExternalStore(subscribe, () => state.pendingIntent, () => state.pendingIntent);
}
