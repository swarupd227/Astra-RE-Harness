/**
 * AgentDock — the agent thread beside an artifact view.
 *
 * The Workspace is where you talk to the agents; the pages are what they
 * open for you. The dock closes the loop the other way: on a page, one click
 * opens that page's agent in the same programme thread the Workspace shows,
 * so a question asked here is answered with the same tools, cards and
 * confirm steps, and the conversation is still there when you go back.
 *
 * Mounted once in the shell; which pages get a dock, and with which agent
 * and starters, is the table in `dockMeta.ts`.
 */
import { useEffect } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ArrowUpRight, MessageSquare, X } from 'lucide-react';
import { clsx } from 'clsx';
import { api } from '@/lib/api';
import { conversationsApi } from '@/lib/conversations';
import { AgentAvatar } from '@/workspace/AgentAvatar';
import { ThreadPanel } from '@/workspace/ThreadPanel';
import { useMediaQuery } from '@/workspace/hooks';
import { matchDock } from './dockMeta';
import { setAgentDockOpen, useAgentDockOpen } from './dockStore';

const WIDE = '(min-width: 1280px)';

export function AgentDockToggle({ className }: { className?: string }) {
  const { pathname } = useLocation();
  const wide = useMediaQuery(WIDE);
  const open = useAgentDockOpen(wide);
  const match = matchDock(pathname);
  if (!match) return null;
  const label = `Ask the ${match.meta.agentName} agent`;
  return (
    <button
      type="button"
      onClick={() => setAgentDockOpen(!open, wide)}
      className={clsx(className, open && 'text-volt')}
      aria-label={label}
      aria-pressed={open}
      title={label}
      data-testid="agent-dock-toggle"
    >
      <MessageSquare className="h-4 w-4" aria-hidden="true" />
    </button>
  );
}

export function AgentDock() {
  const { pathname } = useLocation();
  const isXl = useMediaQuery(WIDE);
  const open = useAgentDockOpen(isXl);
  const match = matchDock(pathname);
  const show = !!match && open;

  // Where the thread lives: the corpus in the URL, the routine's corpus, or
  // Mission Control's thread.
  const scope = match?.meta.scope;
  const routine = useQuery({
    queryKey: ['subroutine', match?.id],
    queryFn: () => api.getSubroutine(match!.id as string),
    enabled: show && scope === 'routine' && !!match?.id,
  });
  const corpusId = scope === 'corpus' ? match?.id : scope === 'routine' ? routine.data?.corpus.id : undefined;

  const programmeThread = useQuery({
    queryKey: ['conversations', 'programme', corpusId],
    queryFn: () => conversationsApi.list(corpusId),
    enabled: show && !!corpusId,
    retry: false,
    staleTime: 5 * 60_000,
  });
  const globalThread = useQuery({
    queryKey: ['conversation', 'global', 'id'],
    queryFn: () => conversationsApi.global(),
    enabled: show && scope === 'global',
    staleTime: 5 * 60_000,
  });

  const conversationId =
    scope === 'global' ? globalThread.data?.id : programmeThread.data?.data?.[0]?.id;
  const resolver = scope === 'global' ? globalThread : scope === 'routine' && !corpusId ? routine : programmeThread;

  useEffect(() => {
    if (!show || isXl) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setAgentDockOpen(false, isXl);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [show, isXl]);

  if (!match || !show) return null;
  const { meta } = match;

  const panel = (
    <div
      data-testid="agent-dock"
      className={clsx(
        'flex min-h-0 flex-col bg-canvas text-ink-primary',
        isXl ? 'w-[400px] shrink-0 border-l border-line-subtle' : 'h-full w-full max-w-[440px] border-l border-line shadow-xl',
      )}
    >
      <header className="flex h-12 shrink-0 items-center gap-2 border-b border-line-subtle bg-raised px-3">
        <AgentAvatar agent={meta.agent} size="sm" />
        <div className="min-w-0 flex-1">
          <p className="truncate text-caption font-medium text-ink-primary">{meta.agentName} agent</p>
          <p className="truncate text-micro text-ink-tertiary">Same thread as the Workspace</p>
        </div>
        {conversationId && (
          <Link
            to={scope === 'global' ? '/' : `/w/${conversationId}`}
            className="inline-flex h-8 items-center gap-1 rounded-md px-2 text-caption text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            data-testid="agent-dock-open-workspace"
          >
            Workspace
            <ArrowUpRight className="h-3.5 w-3.5" aria-hidden="true" />
          </Link>
        )}
        <button
          type="button"
          onClick={() => setAgentDockOpen(false, isXl)}
          aria-label="Close agent thread"
          className="inline-flex h-8 w-8 items-center justify-center rounded-md text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          data-testid="agent-dock-close"
        >
          <X className="h-4 w-4" aria-hidden="true" />
        </button>
      </header>
      <ThreadPanel
        testid="agent-dock-thread"
        className="min-h-0 flex-1"
        pinStarters
        conversationId={conversationId}
        resolving={resolver.isPending && resolver.fetchStatus !== 'idle'}
        resolveError={(resolver.error as Error | null) ?? null}
        onRetryResolve={() => void resolver.refetch()}
        starters={meta.starters({ routine: routine.data?.name })}
        agent={meta.agent}
        emptyTitle={`Ask the ${meta.agentName} agent about ${meta.about}.`}
        emptyBody="It reads the same data you see here and answers with cards you can act on."
        hint={`${meta.agentName} agent`}
        placeholder="Ask about this page… (⏎ to send, ⇧⏎ newline)"
      />
    </div>
  );

  if (isXl) return panel;
  return (
    <div className="fixed inset-0 z-50 flex justify-end" role="dialog" aria-label="Agent thread">
      <div className="absolute inset-0 bg-black/60" onClick={() => setAgentDockOpen(false, isXl)} aria-hidden="true" />
      <div className="relative h-full">{panel}</div>
    </div>
  );
}
