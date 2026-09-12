/**
 * Workspace — the primary surface. You talk to a team of agents and they
 * talk back. Mounted at `/` (global thread = Mission Control) and
 * `/w/:conversationId` (one programme's thread).
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useParams } from 'react-router-dom';
import { PanelRightOpen } from 'lucide-react';
import { clsx } from 'clsx';
import { getPersona } from '@/lib/api';
import { conversationsApi, type Artifact, type Conversation, type Suggestion } from '@/lib/conversations';
import { takePendingIntent, usePendingIntent } from '@/copilot/paletteStore';
import { ArtifactPane } from './ArtifactPane';
import { Composer } from './Composer';
import { MissionControl } from './MissionControl';
import { ProgrammeThread } from './ProgrammeThread';
import { EmptyThread, LoadError } from './ThreadPanel';
import { ThreadActionsProvider, type ThreadActions } from './ThreadActions';
import type { ArtifactSelection } from './MessageBubble';
import { useConversation } from './useConversation';
import { useMediaQuery, useViewportFill } from './hooks';
import { formatInt, formatLanguage, personaLabel } from './format';

const GLOBAL_STARTERS = (firstProgramme: string | null): Suggestion[] => {
  const s: Suggestion[] = [
    { label: "What's the status of every programme?", intent: "What's the status of every programme?" },
    { label: 'Which programme has the most unsigned specs?', intent: 'Which programme has the most unsigned specs?' },
  ];
  if (firstProgramme) {
    s.push({ label: `Survey the patterns in ${firstProgramme}`, intent: `Survey the patterns in ${firstProgramme}` });
  }
  s.push({ label: 'Show me the riskiest routines', intent: 'Show me the riskiest routines' });
  return s;
};

const PROGRAMME_STARTERS: Suggestion[] = [
  { label: "What's the status of this programme?", intent: "What's the status of this programme?" },
  { label: 'Survey the patterns in this programme', intent: 'Survey the patterns in this programme' },
  { label: 'Which routines are still unsigned?', intent: 'Which routines are still unsigned?' },
  { label: 'Show me the riskiest routines', intent: 'Show me the riskiest routines' },
];

export function WorkspacePage() {
  const { conversationId: paramId } = useParams<{ conversationId: string }>();

  // At `/` the thread is the global one; resolve its id first.
  const globalQuery = useQuery({
    queryKey: ['conversation', 'global', 'id'],
    queryFn: () => conversationsApi.global(),
    enabled: !paramId,
    staleTime: 5 * 60_000,
  });
  const conversationId = paramId ?? globalQuery.data?.id;

  const conv = useConversation(conversationId);
  const conversation: Conversation | undefined = conv.conversation ?? (paramId ? undefined : globalQuery.data);
  const isGlobal = !paramId || conversation?.kind === 'global';

  const overviewQuery = useQuery({
    queryKey: ['copilot', 'overview'],
    queryFn: () => conversationsApi.overview(),
    enabled: isGlobal,
    refetchInterval: 30_000,
    staleTime: 15_000,
  });

  // ── Layout ────────────────────────────────────────────────────────
  const rootRef = useRef<HTMLDivElement>(null);
  const fill = useViewportFill(rootRef);
  const isXl = useMediaQuery('(min-width: 1280px)');
  const [paneOpen, setPaneOpen] = useState(true);
  const [overlayOpen, setOverlayOpen] = useState(false);

  // ── Artifact selection ────────────────────────────────────────────
  const [selection, setSelection] = useState<ArtifactSelection | null>(null);
  const messages = conv.messages;

  const defaultSelection = useMemo<ArtifactSelection | null>(() => {
    for (let i = messages.length - 1; i >= 0; i--) {
      const m = messages[i];
      if (m.role === 'agent' && m.artifacts?.length) return { messageId: m.id, index: 0 };
    }
    return null;
  }, [messages]);

  const effectiveSelection = selection ?? defaultSelection;
  const selectedArtifact: Artifact | null = useMemo(() => {
    if (!effectiveSelection) return null;
    const m = messages.find((x) => x.id === effectiveSelection.messageId);
    return m?.artifacts?.[effectiveSelection.index] ?? null;
  }, [effectiveSelection, messages]);

  useEffect(() => {
    setSelection(null);
    setOverlayOpen(false);
  }, [conversationId]);

  // A reply that just arrived opens its first card in the pane.
  useEffect(() => {
    if (!conv.lastArrivalId) return;
    const m = messages.find((x) => x.id === conv.lastArrivalId);
    if (!m?.artifacts?.length) return;
    setSelection({ messageId: m.id, index: 0 });
    if (isXl) setPaneOpen(true);
    else setOverlayOpen(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [conv.lastArrivalId]);

  const selectArtifact = useCallback(
    (sel: ArtifactSelection) => {
      setSelection(sel);
      if (isXl) setPaneOpen(true);
      else setOverlayOpen(true);
    },
    [isXl],
  );

  // ── Sending ───────────────────────────────────────────────────────
  const send = conv.sendMessage;
  const onSuggestion = useCallback((intent: string) => void send(intent), [send]);
  const { confirmAction, declineAction } = conv;
  const onConfirm = useCallback((id: string) => void confirmAction(id), [confirmAction]);
  const onDecline = useCallback((id: string) => void declineAction(id), [declineAction]);

  // Free text from the ⌘K palette lands here once the thread is ready.
  const pendingIntent = usePendingIntent();
  useEffect(() => {
    if (!pendingIntent || !conversationId || !conv.conversation || conv.streaming) return;
    const text = takePendingIntent();
    if (text) void send(text);
  }, [pendingIntent, conversationId, conv.conversation, conv.streaming, send]);

  // What cards may ask of this thread: send an intent, or open a card that
  // is already here (a citation chip opening the routine's source).
  const threadActions = useMemo<ThreadActions>(
    () => ({
      sendIntent: onSuggestion,
      openArtifact: (kind, refId) => {
        for (let i = messages.length - 1; i >= 0; i--) {
          const m = messages[i];
          const idx = (m.artifacts ?? []).findIndex((a) => a.kind === kind && a.refId === refId);
          if (idx >= 0) {
            selectArtifact({ messageId: m.id, index: idx });
            return true;
          }
        }
        return false;
      },
    }),
    [onSuggestion, messages, selectArtifact],
  );

  // ── Derived header bits ───────────────────────────────────────────
  const persona = getPersona();
  const programme = conversation?.programme ?? null;
  const title = isGlobal ? 'Mission Control' : conversation?.title || programme?.name || 'Programme';
  const firstProgramme = overviewQuery.data?.programmes?.[0]?.name ?? null;
  const starters = isGlobal ? GLOBAL_STARTERS(firstProgramme) : PROGRAMME_STARTERS;
  const loading = !conversationId ? globalQuery.isLoading : conv.isLoading;
  const loadError = (!conversationId ? (globalQuery.error as Error | null) : conv.error) ?? null;

  return (
    <ThreadActionsProvider value={threadActions}>
    <div
      ref={rootRef}
      style={fill}
      data-testid="workspace-root"
      className="flex min-h-[480px] flex-col bg-canvas text-ink-primary"
    >
      <header className="flex h-12 shrink-0 items-center gap-3 border-b border-line-subtle px-5 sm:px-6">
        <span
          className={clsx(
            'shrink-0 rounded-full border px-2 py-0.5 text-micro font-medium uppercase tracking-wide',
            isGlobal ? 'border-volt/40 text-volt' : 'border-line text-ink-secondary',
          )}
          data-testid="workspace-kind"
        >
          {isGlobal ? 'Global' : conversation?.kind ?? 'programme'}
        </span>
        <div className="min-w-0 flex-1">
          {loading && !conversation ? (
            <div className="h-4 w-48 animate-pulse rounded bg-sunken" aria-hidden="true" />
          ) : (
            <div className="flex min-w-0 items-baseline gap-2">
              <h1 className="truncate text-body font-semibold text-ink-primary" data-testid="workspace-title">
                {title}
              </h1>
              {programme && (
                <span className="hidden truncate text-caption text-ink-tertiary sm:inline">
                  {formatLanguage(programme.sourceLanguage)} · {formatInt(programme.routineCount)} routines ·{' '}
                  {formatInt(programme.fileCount)} files
                </span>
              )}
              {isGlobal && overviewQuery.data && (
                <span className="hidden truncate text-caption text-ink-tertiary sm:inline">
                  {formatInt(overviewQuery.data.programmes.length)} programme
                  {overviewQuery.data.programmes.length === 1 ? '' : 's'} ·{' '}
                  {formatInt(overviewQuery.data.totals?.total)} routines
                </span>
              )}
            </div>
          )}
        </div>
        {isXl && !paneOpen && (
          <button
            type="button"
            onClick={() => setPaneOpen(true)}
            data-testid="artifact-pane-toggle"
            title="Show artifact pane"
            className="inline-flex h-8 items-center gap-1.5 rounded-md border border-line-subtle px-2 text-caption text-ink-secondary transition-colors hover:border-line hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          >
            <PanelRightOpen size={15} aria-hidden="true" />
            Artifacts
          </button>
        )}
      </header>

      <div className="flex min-h-0 flex-1">
        <section data-testid="workspace" className="flex min-w-0 flex-1 flex-col" aria-label="Thread">
          {loadError && !conversation ? (
            <LoadError error={loadError} onRetry={() => (conversationId ? conv.refetch() : globalQuery.refetch())} />
          ) : (
            <ProgrammeThread
              key={conversationId ?? 'pending'}
              messages={messages}
              messageKey={conv.messageKey}
              working={conv.working}
              streaming={conv.streaming}
              loading={loading}
              confirmingId={conv.confirmingId}
              selected={effectiveSelection}
              onSelectArtifact={selectArtifact}
              onSuggestion={onSuggestion}
              onConfirm={onConfirm}
              onDecline={onDecline}
              before={
                isGlobal ? (
                  <MissionControl
                    overview={overviewQuery.data}
                    isLoading={overviewQuery.isLoading}
                    error={overviewQuery.error as Error | null}
                  />
                ) : null
              }
              empty={
                <EmptyThread
                  lead
                  title={isGlobal ? 'Ask about any programme.' : `Ask about ${programme?.name ?? 'this programme'}.`}
                  body="Astra routes what you ask to Discovery, Spec, Migration, Validation and the rest — and they answer here with cards you can open."
                  starters={starters}
                  onPick={onSuggestion}
                  disabled={conv.streaming || !conversationId}
                />
              }
            />
          )}
          <Composer
            onSend={(t) => void send(t)}
            onCancel={conv.cancel}
            streaming={conv.streaming}
            disabled={!conversationId || !conv.conversation}
            hint={`Acting as ${personaLabel(persona)}${programme ? ` · ${programme.name}` : isGlobal ? ' · all programmes' : ''}`}
            placeholder={
              isGlobal
                ? 'Ask Astra anything about your programmes… (⏎ to send, ⇧⏎ newline)'
                : 'Ask Astra anything about this programme… (⏎ to send, ⇧⏎ newline)'
            }
          />
        </section>

        {isXl && paneOpen && (
          <ArtifactPane
            mode="docked"
            artifact={selectedArtifact}
            onClose={() => setPaneOpen(false)}
            onIntent={conv.streaming ? undefined : onSuggestion}
          />
        )}
      </div>

      {!isXl && (
        <ArtifactPane
          mode="overlay"
          artifact={selectedArtifact}
          open={overlayOpen}
          onClose={() => setOverlayOpen(false)}
          onIntent={conv.streaming ? undefined : onSuggestion}
        />
      )}
    </div>
    </ThreadActionsProvider>
  );
}
