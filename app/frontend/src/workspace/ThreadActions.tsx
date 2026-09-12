/**
 * What a card may ask its surrounding thread to do. Cards render in three
 * places — inside a message, in the artifact pane, and inline in a compact
 * ThreadPanel — and none of them should have to thread a bundle of callbacks
 * through every layer. The Workspace and ThreadPanel provide this; cards
 * read it with `useThreadActions()` and every member is optional.
 */
import { createContext, useContext } from 'react';
import type { ArtifactKind } from '@/lib/conversations';

export type ThreadActions = {
  /** Send natural language as the next user turn (chips, "Why?", "Resume"). */
  sendIntent?: (intent: string) => void;
  /**
   * Select an artifact already in the thread (by kind + refId) and open it in
   * the pane. Returns `true` when one was found; the caller falls back to a
   * route otherwise.
   */
  openArtifact?: (kind: ArtifactKind, refId: string) => boolean;
  /**
   * A citation chip was clicked. Returns `true` when the host handled it
   * (e.g. the spec review page highlighting the lines in its source view).
   */
  onCitation?: (subroutineId: string, lines: string) => boolean;
};

const ThreadActionsContext = createContext<ThreadActions>({});

export const ThreadActionsProvider = ThreadActionsContext.Provider;

export function useThreadActions(): ThreadActions {
  return useContext(ThreadActionsContext);
}
