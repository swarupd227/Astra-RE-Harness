/**
 * `specSummary` — a spec as a conversation object. Every claim in plain
 * language with its section and a citation chip; when the spec is in review
 * and you are the SME, the review actions are right here (Accept / Reject /
 * Edit / Question), plus "Why?" which asks the Spec agent to explain the
 * claim. The footer routes (engineer, DRAFT) or signs (SME, all reviewed)
 * through the same REST the review page uses, so both surfaces agree.
 */
import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { clsx } from 'clsx';
import { Check, Edit3, HelpCircle, MessageCircleQuestion, Route, ShieldCheck, X } from 'lucide-react';
import { api, ApiError, claimPathFor, getPersona } from '@/lib/api';
import { SignOffModal } from '@/components/SignOffModal';
import { StatePill } from '../StatePill';
import { useThreadActions } from '../ThreadActions';
import {
  absoluteTime,
  arr,
  bool,
  formatInt,
  num,
  obj,
  relativeTime,
  str,
  TONE_DOT,
  TONE_TEXT,
  type Tone,
} from '../format';
import type { ArtifactRenderProps } from './registry';

type ReviewAction = 'accept' | 'reject' | 'edit' | 'question';

type Claim = {
  section: string;
  id: string;
  text: string;
  review: ReviewAction | null;
  citation: string | null;
  /** "120-134" — line range without the `L` prefix. */
  citationLines: string | null;
};

type LocalReview = { action: ReviewAction; reason?: string; editedText?: string; at: number };

type Editor = { id: string; mode: 'reject' | 'edit' | 'question'; draft: string };

const SECTION_LABELS: Record<string, string> = {
  inputs: 'Inputs',
  outputs: 'Outputs',
  invariants: 'Invariants',
  side_effects: 'Side effects',
  sideEffects: 'Side effects',
  edge_cases: 'Edge cases',
  edgeCases: 'Edge cases',
  open_questions: 'Open questions',
  openQuestions: 'Open questions',
  section_contracts: 'Section contracts',
};

const SECTION_ORDER = ['inputs', 'outputs', 'invariants', 'side_effects', 'sideEffects', 'edge_cases', 'edgeCases', 'open_questions', 'openQuestions'];

const CARD_BUDGET = 6;
const REJECT_MIN = 20;

function reviewTone(r: ReviewAction | null): Tone {
  switch (r) {
    case 'accept':
      return 'ok';
    case 'reject':
      return 'fail';
    case 'edit':
      return 'warn';
    case 'question':
      return 'info';
    default:
      return 'neutral';
  }
}

function reviewLabel(r: ReviewAction | null): string {
  switch (r) {
    case 'accept':
      return 'Accepted';
    case 'reject':
      return 'Rejected';
    case 'edit':
      return 'Edited';
    case 'question':
      return 'Questioned';
    default:
      return 'Unreviewed';
  }
}

function asAction(v: unknown): ReviewAction | null {
  const s = str(v);
  return s === 'accept' || s === 'reject' || s === 'edit' || s === 'question' ? s : null;
}

function claims(v: unknown): Claim[] {
  return arr(v).map((raw, i) => {
    const c = obj(raw);
    const citation = str(c.citation) || null;
    const citationLines = str(c.citationLines) || (citation ? citation.replace(/^L/i, '') : null);
    return {
      section: str(c.section, 'claims'),
      id: str(c.id, `claim-${i}`),
      text: str(c.text),
      review: asAction(c.review),
      citation,
      citationLines,
    };
  });
}

/** "120-134" → "L120–134"; "88" → "L88". */
function citationChip(lines: string): string {
  return `L${lines.replace(/\s*-\s*/, '–')}`;
}

/** A claim counts as decided unless it is an open question still marked as a question. */
function decided(section: string, review: ReviewAction | null): boolean {
  if (!review) return false;
  return !(section === 'open_questions' && review === 'question');
}

/** Bubble-stopper: clicks on controls inside the card must not select the card. */
function stopInteractive(e: MouseEvent<HTMLElement>) {
  const t = e.target as HTMLElement | null;
  if (t && t.closest('button, a, textarea, input, label, details, summary')) e.stopPropagation();
}

export function SpecSummaryCard({ artifact, size, onIntent }: ArtifactRenderProps) {
  const p = artifact.props;
  const qc = useQueryClient();
  const actions = useThreadActions();
  const sendIntent = onIntent ?? actions.sendIntent;

  const specId = artifact.refId;
  const subroutineId = str(p.subroutineId) || null;
  const routineName = str(p.routineName, 'Routine');
  const pane = size === 'pane';
  const persona = getPersona();

  const wireClaims = useMemo(() => claims(p.claims), [p.claims]);
  const sectionLabels = obj(p.sectionLabels);
  const counts = obj(p.counts);

  // ── Local, optimistic state (re-seeded when the backend re-emits the card) ──
  const seed = `${specId}|${str(p.state)}|${wireClaims.map((c) => `${c.id}:${c.review ?? ''}`).join(',')}|${str(p.signedAt)}`;
  const [state, setState] = useState(() => str(p.state));
  const [reviews, setReviews] = useState<Record<string, LocalReview>>({});
  const [signedAt, setSignedAt] = useState<string | null>(() => str(p.signedAt) || null);
  const [signer, setSigner] = useState<string | null>(() => str(p.signerDisplay) || null);
  const [routed, setRouted] = useState<number | null>(null);
  const seedRef = useRef(seed);
  useEffect(() => {
    if (seedRef.current === seed) return;
    seedRef.current = seed;
    setState(str(p.state));
    setReviews({});
    setSignedAt(str(p.signedAt) || null);
    setSigner(str(p.signerDisplay) || null);
  }, [seed, p.state, p.signedAt, p.signerDisplay]);

  const [editor, setEditor] = useState<Editor | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [footerError, setFooterError] = useState<string | null>(null);
  const [routing, setRouting] = useState(false);
  const [signOpen, setSignOpen] = useState(false);
  const [showAll, setShowAll] = useState(false);
  const editorRef = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    if (editor) editorRef.current?.focus();
  }, [editor]);

  const all: Claim[] = useMemo(
    () => wireClaims.map((c) => (reviews[c.id] ? { ...c, review: reviews[c.id].action } : c)),
    [wireClaims, reviews],
  );

  const total = all.length;
  const reviewed = all.filter((c) => decided(c.section, c.review)).length;
  const undecided = all.filter((c) => !decided(c.section, c.review));
  const allReviewed = total > 0 && undecided.length === 0;

  const inReview = state === 'IN_REVIEW';
  const canReview = bool(p.canReview, inReview) && inReview && persona === 'sme';
  const canRoute = bool(p.canRoute, state === 'DRAFT') && state === 'DRAFT' && persona === 'engineer';
  const canSign = inReview && allReviewed && persona === 'sme';

  const grouped = useMemo(() => {
    const g = new Map<string, Claim[]>();
    for (const c of all) g.set(c.section, [...(g.get(c.section) ?? []), c]);
    return g;
  }, [all]);
  const sections = [...grouped.keys()].sort((a, b) => {
    const ia = SECTION_ORDER.indexOf(a);
    const ib = SECTION_ORDER.indexOf(b);
    return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib);
  });
  const sectionLabel = (key: string) =>
    str(sectionLabels[key]) || SECTION_LABELS[key] || key.charAt(0).toUpperCase() + key.slice(1).replace(/[_-]/g, ' ');

  // ── REST ─────────────────────────────────────────────────────────────
  const invalidate = useCallback(() => {
    void qc.invalidateQueries({ queryKey: ['spec'] });
    if (subroutineId) void qc.invalidateQueries({ queryKey: ['subroutine', subroutineId] });
  }, [qc, subroutineId]);

  const review = useCallback(
    async (claim: Claim, action: ReviewAction, payload?: { reason?: string; editedText?: string }) => {
      if (!specId || busy) return;
      setBusy(claim.id);
      setErrors((e) => ({ ...e, [claim.id]: '' }));
      const previous = reviews[claim.id];
      setReviews((r) => ({ ...r, [claim.id]: { action, ...payload, at: Date.now() } }));
      try {
        await api.reviewClaim(specId, { claimPath: claimPathFor(claim.section, claim.id), action, ...payload });
        setEditor(null);
        invalidate();
      } catch (e) {
        setReviews((r) => {
          const next = { ...r };
          if (previous) next[claim.id] = previous;
          else delete next[claim.id];
          return next;
        });
        setErrors((er) => ({ ...er, [claim.id]: e instanceof Error ? e.message : 'The review could not be saved.' }));
      } finally {
        setBusy(null);
      }
    },
    [specId, busy, reviews, invalidate],
  );

  const route = useCallback(async () => {
    if (!specId || routing) return;
    setRouting(true);
    setFooterError(null);
    try {
      await api.routeSpec(specId, {});
      setState('IN_REVIEW');
      setRouted(Date.now());
      invalidate();
    } catch (e) {
      setFooterError(e instanceof Error ? e.message : 'Could not route the spec.');
    } finally {
      setRouting(false);
    }
  }, [specId, routing, invalidate]);

  // The sign-off modal needs the spec's source version for its canonical
  // sentence; fetch it lazily (same key the review page uses).
  const specQuery = useQuery({
    queryKey: ['spec', subroutineId],
    queryFn: () => api.getSpecForSubroutine(subroutineId as string),
    enabled: signOpen && !!subroutineId,
  });

  const sign = useCallback(
    async (sentence: string) => {
      if (!specId) return;
      try {
        await api.signSpec(specId, sentence);
      } catch (e) {
        const already =
          e instanceof ApiError &&
          (e.code === 'spec.already_signed' || (e.code === 'spec.invalid_state' && e.message.includes('SIGNED')));
        if (!already) throw e;
      }
      setState('SIGNED');
      setSignedAt(new Date().toISOString());
      setSigner(null);
      setSignOpen(false);
      invalidate();
    },
    [specId, invalidate],
  );

  const preconditionFailures = undecided.map(
    (c) => `${sectionLabel(c.section)} · ${c.id} — ${c.review === 'question' ? 'unresolved question' : 'untouched'}`,
  );

  // ── Editor helpers ───────────────────────────────────────────────────
  const openEditor = (c: Claim, mode: Editor['mode']) => {
    const local = reviews[c.id];
    const draft = mode === 'edit' ? local?.editedText ?? c.text : mode === 'reject' ? local?.reason ?? '' : '';
    setEditor({ id: c.id, mode, draft });
  };
  const editorValid = (ed: Editor) => {
    const t = ed.draft.trim();
    if (!t) return false;
    if (ed.mode === 'reject') return t.length >= REJECT_MIN;
    return true;
  };
  const submitEditor = (c: Claim) => {
    if (!editor || editor.id !== c.id || !editorValid(editor)) return;
    const text = editor.draft.trim();
    if (editor.mode === 'edit') void review(c, 'edit', { editedText: text });
    else if (editor.mode === 'reject') void review(c, 'reject', { reason: text });
    else void review(c, 'question', { reason: text });
  };

  const why = (c: Claim) => sendIntent?.(`Explain claim ${c.id} in the spec for ${routineName}`);

  const cite = (e: MouseEvent<HTMLAnchorElement>, lines: string) => {
    if (!subroutineId) return;
    if (actions.onCitation?.(subroutineId, lines)) {
      e.preventDefault();
      return;
    }
    if (actions.openArtifact?.('routine', subroutineId)) e.preventDefault();
  };

  // ── Render ───────────────────────────────────────────────────────────
  let budget = pane || showAll ? Number.POSITIVE_INFINITY : CARD_BUDGET;
  const hidden = !pane && !showAll && total > CARD_BUDGET;
  const summary = str(p.summary);
  const sourcePath = str(p.sourceFilePath);
  const lineStart = num(p.lineStart);
  const lineEnd = num(p.lineEnd);

  const stats: Array<{ label: string; value: string; tone?: Tone }> = [
    {
      label: 'Reviewed',
      value: `${formatInt(reviewed)}/${formatInt(total || num(counts.total))}`,
      tone: total > 0 && reviewed === total ? 'ok' : reviewed > 0 ? 'warn' : undefined,
    },
    { label: 'Invariants', value: formatInt(num(counts.invariants, grouped.get('invariants')?.length ?? 0)) },
    { label: 'Side effects', value: formatInt(num(counts.sideEffects, grouped.get('side_effects')?.length ?? 0)) },
    { label: 'Edge cases', value: formatInt(num(counts.edgeCases, grouped.get('edge_cases')?.length ?? 0)) },
    { label: 'Open questions', value: formatInt(num(counts.openQuestions, grouped.get('open_questions')?.length ?? 0)) },
  ];

  return (
    <div className="space-y-3" onClick={stopInteractive} data-testid="spec-summary-card" data-spec-state={state}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-body text-ink-primary">{routineName}</span>
        <StatePill state={state} size="xs" />
        {persona === 'observer' && <span className="text-micro text-ink-tertiary">read-only</span>}
        {(sourcePath || lineStart > 0) && (
          <span className="ml-auto truncate font-mono text-micro text-ink-tertiary" title={sourcePath}>
            {sourcePath}
            {lineStart > 0 && (
              <>
                :{lineStart}
                {lineEnd > lineStart ? `–${lineEnd}` : ''}
              </>
            )}
          </span>
        )}
      </div>

      {summary && <p className={clsx('text-caption leading-[1.5] text-ink-secondary', !pane && 'line-clamp-2')}>{summary}</p>}

      <div className="grid grid-cols-5 gap-1.5" data-testid="spec-counts">
        {stats.map((s) => (
          <div key={s.label} className="min-w-0 rounded-lg border border-line-subtle bg-sunken px-2 py-1.5">
            <div className={clsx('font-mono text-caption tabular-nums', s.tone ? TONE_TEXT[s.tone] : 'text-ink-primary')}>
              {s.value}
            </div>
            <div className="truncate text-micro text-ink-tertiary">{s.label}</div>
          </div>
        ))}
      </div>

      {total === 0 ? (
        <p className="text-caption text-ink-tertiary">No claims extracted yet.</p>
      ) : (
        <div className="space-y-3">
          {sections.map((sec) => {
            if (budget <= 0) return null;
            const list = grouped.get(sec) ?? [];
            const shown = list.slice(0, budget);
            budget -= shown.length;
            return (
              <div key={sec}>
                <div className="mb-1 text-micro font-medium uppercase tracking-wide text-ink-tertiary">
                  {sectionLabel(sec)}
                  <span className="ml-1.5 font-mono normal-case tracking-normal">{list.length}</span>
                </div>
                <ul className="space-y-1.5">
                  {shown.map((c) => {
                    const local = reviews[c.id];
                    const tone = reviewTone(c.review);
                    const isBusy = busy === c.id;
                    const open = editor?.id === c.id ? editor : null;
                    const error = errors[c.id];
                    const isQuestion = c.section === 'open_questions';
                    return (
                      <li
                        key={c.id}
                        className={clsx(
                          'rounded-lg border px-2.5 py-2 text-caption transition-colors',
                          open ? 'border-line bg-sunken/70' : 'border-transparent hover:border-line-subtle',
                        )}
                        data-testid="spec-claim"
                        data-claim-id={c.id}
                        data-review={c.review ?? 'none'}
                      >
                        <div className="flex items-start gap-2">
                          <span
                            className={clsx('mt-[7px] h-1.5 w-1.5 shrink-0 rounded-full', TONE_DOT[tone])}
                            title={reviewLabel(c.review)}
                            aria-label={reviewLabel(c.review)}
                          />
                          <div className="min-w-0 flex-1">
                            <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
                              <span className="font-mono text-micro text-ink-tertiary">{c.id}</span>
                              {c.citationLines && subroutineId && (
                                <Link
                                  to={`/subroutines/${subroutineId}/review`}
                                  onClick={(e) => cite(e, c.citationLines as string)}
                                  data-testid="claim-citation"
                                  data-lines={c.citationLines}
                                  className="rounded border border-line-subtle bg-sunken px-1 py-px font-mono text-micro text-ink-secondary transition-colors hover:border-line hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                                  title={`Source lines ${c.citationLines} — open the routine`}
                                >
                                  {citationChip(c.citationLines)}
                                </Link>
                              )}
                              {c.citationLines && !subroutineId && (
                                <span className="rounded border border-line-subtle bg-sunken px-1 py-px font-mono text-micro text-ink-tertiary">
                                  {citationChip(c.citationLines)}
                                </span>
                              )}
                            </div>
                            <p
                              className={clsx(
                                'leading-[1.5]',
                                c.review === 'reject' ? 'text-ink-tertiary line-through' : 'text-ink-primary/90',
                                !pane && !open && 'line-clamp-3',
                              )}
                            >
                              {(local?.editedText ?? c.text) || <span className="italic text-ink-tertiary">(empty claim)</span>}
                            </p>
                            {local && (
                              <p className="mt-1 flex flex-wrap items-center gap-1.5 text-micro text-ink-tertiary" data-testid="claim-resolved">
                                <span className={clsx('h-1.5 w-1.5 rounded-full', TONE_DOT[reviewTone(local.action)])} aria-hidden="true" />
                                <span className={TONE_TEXT[reviewTone(local.action)]}>{reviewLabel(local.action)}</span>
                                <span title={absoluteTime(new Date(local.at).toISOString())}>{relativeTime(new Date(local.at).toISOString())}</span>
                                {local.reason && <span className="text-ink-secondary">— {local.reason}</span>}
                              </p>
                            )}
                            {error && <p className="mt-1 text-micro text-status-fail">{error}</p>}

                            {open ? (
                              <div className="mt-2 space-y-1.5">
                                <textarea
                                  ref={editorRef}
                                  value={open.draft}
                                  onChange={(e) => setEditor({ ...open, draft: e.target.value })}
                                  onKeyDown={(e) => {
                                    if (e.key === 'Escape') setEditor(null);
                                    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) submitEditor(c);
                                  }}
                                  rows={open.mode === 'edit' ? 3 : 2}
                                  placeholder={
                                    open.mode === 'edit'
                                      ? isQuestion
                                        ? 'Rewrite the question as a resolved claim…'
                                        : 'Refined claim text…'
                                      : open.mode === 'reject'
                                        ? `Reason for rejection (≥ ${REJECT_MIN} characters)…`
                                        : 'What needs clarifying?'
                                  }
                                  aria-label={open.mode === 'edit' ? 'Edited claim text' : open.mode === 'reject' ? 'Rejection reason' : 'Question'}
                                  data-testid={`claim-inline-${open.mode}-input`}
                                  className="w-full resize-y rounded-md border border-line bg-raised px-2 py-1.5 text-caption text-ink-primary outline-none placeholder:text-ink-tertiary focus:border-line-strong focus:ring-1 focus:ring-volt/60"
                                />
                                <div className="flex flex-wrap items-center gap-1.5">
                                  <button
                                    type="button"
                                    onClick={() => submitEditor(c)}
                                    disabled={!editorValid(open) || isBusy}
                                    data-testid={`claim-inline-${open.mode}-submit`}
                                    className="inline-flex items-center gap-1 rounded-md bg-volt px-2.5 py-1 text-micro font-semibold text-on-volt transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt disabled:cursor-not-allowed disabled:opacity-50"
                                  >
                                    {isBusy ? 'Saving…' : open.mode === 'reject' ? 'Reject' : open.mode === 'edit' ? 'Save edit' : 'Ask'}
                                  </button>
                                  <button
                                    type="button"
                                    onClick={() => setEditor(null)}
                                    className="rounded-md px-2 py-1 text-micro text-ink-secondary hover:bg-sunken hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
                                  >
                                    Cancel
                                  </button>
                                  {open.mode === 'reject' && (
                                    <span
                                      className={clsx(
                                        'ml-auto font-mono text-micro tabular-nums',
                                        open.draft.trim().length >= REJECT_MIN ? 'text-ink-tertiary' : 'text-status-warn',
                                      )}
                                    >
                                      {open.draft.trim().length}/{REJECT_MIN}
                                    </span>
                                  )}
                                </div>
                              </div>
                            ) : (
                              (canReview || sendIntent) && (
                                <div className="mt-1.5 flex flex-wrap items-center gap-1" data-testid="claim-inline-actions">
                                  {canReview && (
                                    <>
                                      <InlineButton
                                        testid="claim-inline-accept"
                                        icon={Check}
                                        label="Accept"
                                        active={c.review === 'accept'}
                                        tone="ok"
                                        disabled={isBusy}
                                        onClick={() => void review(c, 'accept')}
                                      />
                                      <InlineButton
                                        testid="claim-inline-reject"
                                        icon={X}
                                        label="Reject"
                                        active={c.review === 'reject'}
                                        tone="fail"
                                        disabled={isBusy}
                                        onClick={() => openEditor(c, 'reject')}
                                      />
                                      <InlineButton
                                        testid="claim-inline-edit"
                                        icon={Edit3}
                                        label={isQuestion ? 'Resolve' : 'Edit'}
                                        active={c.review === 'edit'}
                                        tone="warn"
                                        disabled={isBusy}
                                        onClick={() => openEditor(c, 'edit')}
                                      />
                                      <InlineButton
                                        testid="claim-inline-question"
                                        icon={HelpCircle}
                                        label="Question"
                                        active={c.review === 'question'}
                                        tone="info"
                                        disabled={isBusy}
                                        onClick={() => openEditor(c, 'question')}
                                      />
                                    </>
                                  )}
                                  {sendIntent && (
                                    <InlineButton
                                      testid="claim-inline-why"
                                      icon={MessageCircleQuestion}
                                      label="Why?"
                                      disabled={isBusy}
                                      onClick={() => why(c)}
                                      className={canReview ? 'ml-auto' : undefined}
                                    />
                                  )}
                                </div>
                              )
                            )}
                          </div>
                        </div>
                      </li>
                    );
                  })}
                </ul>
              </div>
            );
          })}
          {hidden && (
            <button
              type="button"
              onClick={() => setShowAll(true)}
              className="text-micro text-ink-secondary underline-offset-2 hover:text-ink-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
              data-testid="spec-show-all"
            >
              Show all {formatInt(total)} claims
            </button>
          )}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2 border-t border-line-subtle pt-2 text-caption">
        {signedAt ? (
          <>
            <span className={clsx('h-1.5 w-1.5 rounded-full', TONE_DOT.ok)} aria-hidden="true" />
            <span className="text-ink-secondary">
              Signed {signer ? <>by <span className="text-ink-primary">{signer}</span> </> : null}
              <span title={absoluteTime(signedAt)}>{relativeTime(signedAt)}</span>
            </span>
          </>
        ) : routed ? (
          <>
            <span className={clsx('h-1.5 w-1.5 rounded-full', TONE_DOT.ok)} aria-hidden="true" />
            <span className="text-ink-secondary">Routed to SME {relativeTime(new Date(routed).toISOString())}</span>
          </>
        ) : (
          <>
            <span className={clsx('h-1.5 w-1.5 rounded-full', inReview ? TONE_DOT.warn : TONE_DOT.neutral)} aria-hidden="true" />
            <span className="text-ink-tertiary">
              {inReview
                ? allReviewed
                  ? 'Every claim reviewed — ready to sign'
                  : `${formatInt(undecided.length)} claim${undecided.length === 1 ? '' : 's'} still need a decision`
                : state === 'DRAFT'
                  ? 'Draft — not yet routed for review'
                  : 'Not yet signed'}
            </span>
          </>
        )}
        <span className="ml-auto flex flex-wrap items-center gap-1.5">
          {canRoute && (
            <button
              type="button"
              onClick={() => void route()}
              disabled={routing}
              data-testid="card-route-to-sme"
              className="inline-flex items-center gap-1.5 rounded-lg bg-volt px-3 py-1.5 text-caption font-semibold text-on-volt transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt disabled:cursor-not-allowed disabled:opacity-50"
            >
              <Route size={13} aria-hidden="true" />
              {routing ? 'Routing…' : 'Route to SME'}
            </button>
          )}
          {inReview && persona === 'sme' && (
            <button
              type="button"
              onClick={() => setSignOpen(true)}
              disabled={!canSign}
              data-testid="card-sign-spec"
              title={canSign ? undefined : `${undecided.length} claim${undecided.length === 1 ? '' : 's'} still need a decision`}
              className="inline-flex items-center gap-1.5 rounded-lg bg-volt px-3 py-1.5 text-caption font-semibold text-on-volt transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt disabled:cursor-not-allowed disabled:opacity-50"
            >
              <ShieldCheck size={13} aria-hidden="true" />
              Sign spec
            </button>
          )}
        </span>
        {footerError && <p className="basis-full text-micro text-status-fail">{footerError}</p>}
      </div>

      {signOpen && (
        <SignOffModal
          spec={specQuery.data ?? null}
          open={signOpen && !!specQuery.data}
          onClose={() => setSignOpen(false)}
          onConfirm={sign}
          preconditionFailures={preconditionFailures}
        />
      )}
      {signOpen && specQuery.isError && (
        <p className="text-micro text-status-fail">Could not load the spec for signing: {String((specQuery.error as Error)?.message)}</p>
      )}
    </div>
  );
}

function InlineButton({
  testid,
  icon: Icon,
  label,
  active = false,
  tone,
  disabled,
  onClick,
  className,
}: {
  testid: string;
  icon: typeof Check;
  label: string;
  active?: boolean;
  tone?: Tone;
  disabled?: boolean;
  onClick: () => void;
  className?: string;
}) {
  return (
    <button
      type="button"
      data-testid={testid}
      disabled={disabled}
      onClick={onClick}
      aria-pressed={active || undefined}
      className={clsx(
        'inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-micro font-medium transition-colors duration-fast',
        'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt disabled:cursor-not-allowed disabled:opacity-50',
        active && tone
          ? clsx('border-transparent', TONE_TEXT[tone], tone === 'ok' ? 'bg-status-ok/15' : tone === 'fail' ? 'bg-status-fail/15' : tone === 'warn' ? 'bg-status-warn/15' : 'bg-status-info/15')
          : 'border-line-subtle bg-raised text-ink-secondary hover:border-line hover:text-ink-primary',
        className,
      )}
    >
      <Icon size={11} aria-hidden="true" />
      {label}
    </button>
  );
}
