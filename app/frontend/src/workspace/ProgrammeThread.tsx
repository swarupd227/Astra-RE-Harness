import { useCallback, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { AnimatePresence, motion, useReducedMotion } from 'framer-motion';
import { ArrowDown } from 'lucide-react';
import { clsx } from 'clsx';
import type { ConversationMessage } from '@/lib/conversations';
import { AgentWorkingRow } from './AgentWorkingRow';
import { MessageBubble, type ArtifactSelection } from './MessageBubble';
import { useTick } from './hooks';
import type { WorkingState } from './useConversation';

const STICK_THRESHOLD = 72;

/**
 * The scrolling message list. Sticks to the bottom while the user is there;
 * once they scroll up, new messages raise a "new messages" pill instead of
 * yanking the view.
 */
export function ProgrammeThread({
  messages,
  messageKey,
  working,
  streaming,
  loading = false,
  confirmingId,
  selected,
  onSelectArtifact,
  onSuggestion,
  onConfirm,
  onDecline,
  before,
  empty,
  expandInline = false,
  compact = false,
}: {
  messages: ConversationMessage[];
  messageKey?: (m: ConversationMessage) => string;
  working: WorkingState | null;
  streaming: boolean;
  loading?: boolean;
  confirmingId: string | null;
  selected: ArtifactSelection | null;
  onSelectArtifact: (sel: ArtifactSelection) => void;
  onSuggestion: (intent: string) => void;
  onConfirm: (messageId: string) => void;
  onDecline: (messageId: string) => void;
  /** Pinned block rendered above the messages (Mission Control). */
  before?: ReactNode;
  /** Friendly starter shown when the thread has no messages. */
  empty?: ReactNode;
  /** No artifact pane in this host: the selected card expands in place. */
  expandInline?: boolean;
  /** Narrow host (a side panel): tighter gutters, no max-width. */
  compact?: boolean;
}) {
  const reduced = useReducedMotion();
  const now = useTick(60_000);
  const scrollRef = useRef<HTMLDivElement>(null);
  const stickRef = useRef(true);
  const [unread, setUnread] = useState(false);

  const last = messages[messages.length - 1];
  const lastKey = last ? `${last.id}:${last.role}` : '';

  const scrollToBottom = useCallback(
    (smooth: boolean) => {
      const el = scrollRef.current;
      if (!el) return;
      el.scrollTo({ top: el.scrollHeight, behavior: smooth && !reduced ? 'smooth' : 'auto' });
      stickRef.current = true;
      setUnread(false);
    },
    [reduced],
  );

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < STICK_THRESHOLD;
    stickRef.current = atBottom;
    if (atBottom) setUnread(false);
  }, []);

  // First paint of a thread: jump straight to the newest message.
  useLayoutEffect(() => {
    if (loading) return;
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
    stickRef.current = true;
    setUnread(false);
  }, [loading]);

  // New message: follow if stuck (or if it's ours), else raise the pill.
  useEffect(() => {
    if (!lastKey) return;
    if (stickRef.current || last?.role === 'user') {
      // Let the entry animation start, then settle at the bottom.
      const id = window.requestAnimationFrame(() => scrollToBottom(true));
      return () => window.cancelAnimationFrame(id);
    }
    setUnread(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lastKey]);

  // The working row grows as tool results land — keep following it.
  useEffect(() => {
    if (!working || !stickRef.current) return;
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [working]);

  return (
    <div className="relative flex min-h-0 flex-1 flex-col">
      <div
        ref={scrollRef}
        onScroll={onScroll}
        className="min-h-0 flex-1 overflow-y-auto overscroll-contain [scrollbar-gutter:stable]"
      >
        <div className={clsx('w-full', compact ? 'px-4 pb-6 pt-4' : 'mx-auto max-w-[880px] px-5 pb-8 pt-6 sm:px-8')}>
          {before}

          {loading ? (
            <ThreadSkeleton />
          ) : (
            <div data-testid="thread" className={compact ? 'space-y-5' : 'space-y-7'} aria-live="polite">
              {messages.length === 0 && !working && empty}
              <AnimatePresence initial={false}>
                {messages.map((m) => (
                  <motion.div
                    key={messageKey ? messageKey(m) : m.id}
                    initial={reduced ? false : { opacity: 0, y: 10 }}
                    animate={{ opacity: 1, y: 0 }}
                    transition={{ duration: 0.22, ease: 'easeOut' }}
                  >
                    <MessageBubble
                      message={m}
                      now={now}
                      selected={selected}
                      onSelectArtifact={onSelectArtifact}
                      onSuggestion={onSuggestion}
                      onConfirm={onConfirm}
                      onDecline={onDecline}
                      confirming={confirmingId === m.id}
                      disabled={streaming}
                      expandInline={expandInline}
                    />
                  </motion.div>
                ))}
              </AnimatePresence>
              {working && (
                <motion.div
                  initial={reduced ? false : { opacity: 0, y: 6 }}
                  animate={{ opacity: 1, y: 0 }}
                  transition={{ duration: 0.18 }}
                >
                  <AgentWorkingRow working={working} />
                </motion.div>
              )}
            </div>
          )}
        </div>
      </div>

      <AnimatePresence>
        {unread && (
          <motion.button
            type="button"
            key="unread"
            initial={reduced ? false : { opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: 8 }}
            transition={{ duration: 0.16 }}
            onClick={() => scrollToBottom(true)}
            data-testid="new-messages-pill"
            className={clsx(
              'absolute bottom-3 left-1/2 z-10 -translate-x-1/2 inline-flex items-center gap-1.5 rounded-full border border-line bg-raised px-3 py-1.5 text-caption font-medium text-ink-primary shadow-e2',
              'hover:border-line-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
            )}
          >
            <ArrowDown size={13} aria-hidden="true" />
            New messages
          </motion.button>
        )}
      </AnimatePresence>
    </div>
  );
}

function ThreadSkeleton() {
  return (
    <div className="space-y-7" aria-hidden="true" data-testid="thread-skeleton">
      {[0, 1, 2].map((i) => (
        <div key={i} className={clsx('flex gap-3', i === 1 && 'justify-end')}>
          {i !== 1 && <div className="h-8 w-8 shrink-0 animate-pulse rounded-full bg-sunken" />}
          <div className={clsx('space-y-2', i === 1 ? 'w-1/2' : 'flex-1')}>
            <div className="h-3 w-24 animate-pulse rounded bg-sunken" />
            <div className="h-4 w-full animate-pulse rounded bg-sunken" />
            <div className="h-4 w-4/5 animate-pulse rounded bg-sunken" />
            {i === 2 && <div className="h-28 w-full animate-pulse rounded-xl bg-sunken" />}
          </div>
        </div>
      ))}
    </div>
  );
}
