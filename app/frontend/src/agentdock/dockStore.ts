/**
 * Open/closed state of the docked agent thread, shared by the TopBar toggle
 * and the dock itself.
 *
 * Two independent states: the docked panel (wide screens) remembers your
 * choice per viewer, best-effort; the overlay drawer (narrow screens) covers
 * the page, so it never reopens on its own and starts closed every load.
 */
import { useSyncExternalStore } from 'react';

const KEY = 'astra.agentDock';

function read(): boolean {
  try {
    return localStorage.getItem(KEY) === 'open';
  } catch {
    return false;
  }
}

const state = { docked: read(), overlay: false };
const listeners = new Set<() => void>();

function subscribe(cb: () => void) {
  listeners.add(cb);
  return () => {
    listeners.delete(cb);
  };
}

export function setAgentDockOpen(next: boolean, wide: boolean) {
  if (wide) {
    state.docked = next;
    try {
      localStorage.setItem(KEY, next ? 'open' : 'closed');
    } catch {
      /* private mode — the in-memory choice still applies */
    }
  } else {
    state.overlay = next;
  }
  for (const l of listeners) l();
}

export function useAgentDockOpen(wide: boolean): boolean {
  return useSyncExternalStore(
    subscribe,
    () => (wide ? state.docked : state.overlay),
    () => false,
  );
}
