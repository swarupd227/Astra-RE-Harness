import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, Cog, History, MessageSquare, ShieldCheck } from 'lucide-react';
import { api, ApiError, claimPathFor, commentsApi, type ClaimReview, type SpecClaim } from '@/lib/api';
import { CommentsThread } from '@/components/CommentsThread';
import { EvidenceTrail } from '@/components/EvidenceTrail';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ProviderStrip } from '@/components/ProviderStrip';
import { ProviderSettingsCard } from '@/components/ProviderSettingsCard';
import { TargetSelector } from '@/components/TargetSelector';
import { SignatureHealthBadge } from '@/components/SignatureHealthBadge';
import { MonacoSource, type Citation } from '@/components/MonacoSource';
import { OutlinePane, type OutlineItem } from '@/components/OutlinePane';
import { ReviewableClaimCard } from '@/components/ReviewableClaimCard';
import { SignOffModal } from '@/components/SignOffModal';

type Section = { key: string; label: string; claims: SpecClaim[] };

export function SpecReviewPage() {
  const { id = '' } = useParams();
  const queryClient = useQueryClient();

  const sub = useQuery({ queryKey: ['subroutine', id], queryFn: () => api.getSubroutine(id), enabled: !!id });
  const source = useQuery({ queryKey: ['subroutine-source', id], queryFn: () => api.getSubroutineSource(id), enabled: !!id });
  const spec = useQuery({ queryKey: ['spec', id], queryFn: () => api.getSpecForSubroutine(id), enabled: !!id });
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });

  const [activeCitation, setActiveCitation] = useState<string | null>(null);
  const [activeLine, setActiveLine] = useState<number | undefined>(undefined);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [signOpen, setSignOpen] = useState(false);

  // Phase #4 / value-add #3 — engineer-chosen target stack. Persists
  // across reloads so the demo can switch once and keep the choice; the
  // server-side guard still rejects gated stacks (e.g. java-spring) so
  // self-service .NET 8 is the only thing that actually generates today.
  //
  // Phase 10.1.a.3 — `dotnet8` is the wrong fallback for specs whose
  // schema (vb6, future cobol-only stacks…) has no dotnet8 archetypes.
  // We still init from localStorage so the user's last explicit choice
  // wins, but track whether the value was stored so we can auto-correct
  // when the corpus's inferred schema has no archetypes on the default.
  const initialHadStored = useMemo(() => {
    try { return !!localStorage.getItem('astra.targetStack'); }
    catch { return false; }
  }, []);
  const [targetStack, setTargetStack] = useState<string>(() => {
    try {
      return localStorage.getItem('astra.targetStack') ?? 'dotnet8';
    } catch {
      return 'dotnet8';
    }
  });
  const onTargetChange = (next: string) => {
    setTargetStack(next);
    try {
      localStorage.setItem('astra.targetStack', next);
    } catch {
      /* ignore — private mode etc. */
    }
  };

  // Phase 10.1.a.3 — auto-correct the default when the current stack
  // has no archetype compatible with the spec's source schema. Only
  // fires when the user has NOT previously made an explicit choice
  // (initialHadStored guard) — otherwise we'd override their pick.
  // Schema is inferred from the corpus name because SpecResponse
  // doesn't yet carry schemaId — same heuristic as CorporaPage's
  // language pill so the two stay in sync without a backend change.
  const archetypes = useQuery({
    queryKey: ['archetypes'],
    queryFn: api.listArchetypes,
    staleTime: 5 * 60_000,
  });
  useEffect(() => {
    if (initialHadStored) return;
    const arches = archetypes.data?.data ?? [];
    if (arches.length === 0) return;
    // Phase 10.1.b.1 — read schema directly from the subroutine row.
    // SubroutineEndpoints already projects `sourceLanguage`; we no
    // longer guess from the corpus name. Null means "subroutine
    // parsed before sourceLanguage was populated" — fall through
    // to the unfiltered candidate search.
    const schema = sub.data?.sourceLanguage ?? null;
    const currentHasMatch = arches.some(
      (a) =>
        a.targetStack === targetStack &&
        a.status.toLowerCase().startsWith('production') &&
        (!schema || a.compatibleSchemas.includes(schema)),
    );
    if (currentHasMatch) return;
    const candidate = arches.find(
      (a) =>
        a.status.toLowerCase().startsWith('production') &&
        (schema ? a.compatibleSchemas.includes(schema) : true),
    );
    if (candidate && candidate.targetStack !== targetStack) {
      setTargetStack(candidate.targetStack);
    }
  }, [archetypes.data, sub.data, initialHadStored, targetStack]);

  const sections: Section[] = useMemo(() => {
    const s = spec.data?.spec;
    if (!s) return [];
    return [
      { key: 'invariants',     label: 'Invariants',     claims: s.invariants ?? [] },
      { key: 'side_effects',   label: 'Side effects',   claims: (s.side_effects ?? []).map((c, i) => ({ ...c, id: c.id ?? `SE-${i + 1}` })) },
      { key: 'edge_cases',     label: 'Edge cases',     claims: (s.edge_cases ?? []).map((c, i) => ({ ...c, id: c.id ?? `EC-${i + 1}` })) },
      { key: 'open_questions', label: 'Open questions', claims: s.open_questions ?? [] },
    ].filter((sec) => sec.claims.length > 0);
  }, [spec.data]);

  const outlineItems: OutlineItem[] = sections.map((s) => ({
    section: s.key,
    label: s.label,
    claims: s.claims,
  }));

  const reviewByPath = useMemo(() => {
    const map = new Map<string, ClaimReview>();
    spec.data?.claimReviews?.forEach((r) => map.set(r.claimPath, r));
    return map;
  }, [spec.data]);

  const preconditionFailures = useMemo(() => {
    const failures: string[] = [];
    for (const sec of sections) {
      for (const c of sec.claims) {
        const path = claimPathFor(sec.key, c.id);
        const r = reviewByPath.get(path);
        if (!r) failures.push(`${sec.label} · ${c.id} — untouched`);
        else if (sec.key === 'open_questions' && r.action === 'question')
          failures.push(`${sec.label} · ${c.id} — unresolved question`);
      }
    }
    return failures;
  }, [sections, reviewByPath]);

  const total = sections.reduce((n, s) => n + s.claims.length, 0);
  const processed = total - sections.reduce(
    (n, s) => n + s.claims.filter((c) => !reviewByPath.has(claimPathFor(s.key, c.id))).length,
    0,
  );

  // Build all citations for Monaco decoration
  const citations: Citation[] = useMemo(() => {
    const out: Citation[] = [];
    for (const sec of sections) {
      for (const c of sec.claims) {
        c.citations?.forEach((cit) => {
          const r = parseRange(cit.lines);
          if (r) out.push({ lineStart: r[0], lineEnd: r[1], tone: activeCitation === cit.lines ? 'accent' : undefined });
        });
      }
    }
    return out;
  }, [sections, activeCitation]);

  const onCite = (lines: string) => {
    setActiveCitation(lines);
    const r = parseRange(lines);
    if (r) setActiveLine(r[0]);
    // Hold the pulse for 1.8s to match the CSS animation in index.css.
    window.setTimeout(() => setActiveCitation((cur) => (cur === lines ? null : cur)), 1800);
  };

  const onJump = (claimId: string) => {
    setActiveId(claimId);
    document.getElementById(`claim-${claimId}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  // Keyboard nav: J/K next/prev claim, A/E/R/?/S
  useEffect(() => {
    const flatIds = sections.flatMap((s) => s.claims.map((c) => c.id));
    const handler = (e: KeyboardEvent) => {
      const t = e.target as HTMLElement | null;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      if (flatIds.length === 0) return;
      if (e.key === 'j' || e.key === 'k') {
        e.preventDefault();
        const idx = activeId ? flatIds.indexOf(activeId) : -1;
        const next = e.key === 'j' ? Math.min(flatIds.length - 1, idx + 1) : Math.max(0, idx - 1);
        onJump(flatIds[next]);
      } else if (e.key === 's' && spec.data?.state === 'IN_REVIEW' && whoami.data?.persona === 'sme') {
        e.preventDefault();
        setSignOpen(true);
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [sections, activeId, spec.data?.state, whoami.data?.persona]);

  if (sub.isPending || source.isPending || spec.isPending) {
    return <div className="mx-auto max-w-[1600px] space-y-4 p-6 lg:p-10"><Skeleton className="h-12 w-96" /><Skeleton className="h-[600px] w-full" /></div>;
  }
  if (sub.isError || source.isError) {
    return <div className="mx-auto max-w-[1400px] p-6 lg:p-10"><ErrorBlock title="Could not load subroutine" message={String(sub.error ?? source.error)} /></div>;
  }
  if (spec.isError) {
    return <div className="mx-auto max-w-[1400px] p-6 lg:p-10"><ErrorBlock title="No spec yet" message="Extract a spec first." /></div>;
  }

  const s = sub.data;
  const sp = spec.data;
  const isSme = whoami.data?.persona === 'sme';
  const inReview = sp.state === 'IN_REVIEW';
  const signed = sp.state === 'SIGNED';
  const readOnly = !inReview || !isSme;

  const onAct = async (claimPath: string, action: string, payload?: { reason?: string; editedText?: string }) => {
    await api.reviewClaim(sp.id, { claimPath, action, ...payload });
    await queryClient.invalidateQueries({ queryKey: ['spec', id] });
  };

  const onSign = async (sentence: string) => {
    try {
      await api.signSpec(sp.id, sentence);
    } catch (e) {
      // Recoverable: another tab / a previous request already signed this
      // spec. Treat as success and let the page refresh into SIGNED mode
      // so the user is never trapped in a stuck-modal state.
      const isAlreadySigned =
        e instanceof ApiError &&
        (e.code === 'spec.already_signed' ||
          (e.code === 'spec.invalid_state' && e.message.includes('SIGNED')));
      if (!isAlreadySigned) throw e;
    }
    await queryClient.invalidateQueries({ queryKey: ['spec', id] });
    await queryClient.invalidateQueries({ queryKey: ['subroutine', id] });
    setSignOpen(false);
  };

  return (
    <div className="flex h-[calc(100vh-110px)] flex-col">
      {/* Header */}
      <header className="border-b border-border-subtle bg-raised px-6 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <Link to={`/subroutines/${s.id}`} className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary" aria-label="Back to subroutine">
              <ArrowLeft className="h-4 w-4" aria-hidden="true" />
            </Link>
            <div>
              <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">Spec review</p>
              <h1 className="font-mono text-h-md font-semibold text-ink-primary">{s.name}</h1>
            </div>
            <Badge tone={signed ? 'signed' : inReview ? 'review' : 'draft'}>{sp.state}</Badge>
            {signed && <SignatureHealthBadge specId={sp.id} compact />}
          </div>
          <div className="flex items-center gap-3 font-mono text-caption text-ink-secondary">
            <span><span className="text-ink-primary">{processed}</span> / {total} processed</span>
            <Link to={`/specs/${sp.id}/audit`} className="inline-flex items-center gap-1.5 rounded-md border border-border-subtle bg-canvas px-2.5 py-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary">
              <History className="h-3.5 w-3.5" aria-hidden="true" />
              Audit trail
            </Link>
            {inReview && isSme && (
              <Button variant="primary" size="md" onClick={() => setSignOpen(true)} disabled={preconditionFailures.length > 0}>
                <ShieldCheck className="h-4 w-4" /> Sign spec
              </Button>
            )}
            {signed && whoami.data?.persona === 'engineer' && (
              <ScaffoldCta specId={sp.id} targetStack={targetStack} />
            )}
          </div>
        </div>
      </header>

      <ProviderStrip
        info={sp.llmCall ? { name: sp.llmCall.provider, model: sp.llmCall.model, configVersion: sp.llmCall.providerConfigVersion, promptTemplateId: sp.llmCall.promptTemplateId, promptTemplateVersion: sp.llmCall.promptTemplateVersion } : null}
        latencyMs={sp.llmCall?.latencyMs}
        tokens={sp.llmCall ? { in: sp.llmCall.inputTokens, out: sp.llmCall.outputTokens } : undefined}
      />

      <div className="border-b border-border-subtle bg-canvas/40 px-6 py-2">
        <ProviderSettingsCard compact />
      </div>

      {signed && whoami.data?.persona === 'engineer' && (
        <div className="border-b border-border-subtle bg-canvas/40 px-6 py-3">
          <TargetSelector value={targetStack} onChange={onTargetChange} />
        </div>
      )}

      {signed && sp.signature && (
        <div className="px-6 pt-4">
          <EvidenceTrail specId={sp.id} />
        </div>
      )}

      <div className="grid flex-1 min-h-0 grid-cols-[280px_minmax(0,1fr)_minmax(0,560px)]">
        <OutlinePane items={outlineItems} reviews={sp.claimReviews ?? []} activeId={activeId} onJump={onJump} />

        <div className="min-h-0 overflow-y-auto bg-canvas">
          <div className="space-y-6 p-6">
            {sections.map((sec) => (
              <section key={sec.key}>
                <h3 className="mb-2 text-caption font-medium uppercase tracking-wider text-ink-tertiary">
                  {sec.label} <span className="ml-1 rounded-sm bg-sunken px-1.5 py-0.5 text-[10px] text-ink-secondary">{sec.claims.length}</span>
                </h3>
                <ul className="space-y-3">
                  {sec.claims.map((c) => {
                    const path = claimPathFor(sec.key, c.id);
                    return (
                      <li key={c.id}>
                        <ReviewableClaimCard
                          id={c.id}
                          section={sec.key}
                          claim={c}
                          review={reviewByPath.get(path)}
                          readOnly={readOnly}
                          onAct={(action, payload) => onAct(path, action, payload)}
                          onCite={onCite}
                          activeCitation={activeCitation}
                        />
                        <ClaimCommentsToggle specId={sp.id} claimPath={path} />
                      </li>
                    );
                  })}
                </ul>
              </section>
            ))}
          </div>
        </div>

        <div className="min-h-0 flex flex-col bg-canvas">
          <div className="shrink-0 flex items-center justify-between border-b border-border-subtle bg-raised px-4 py-2 font-mono text-caption text-ink-secondary">
            <span>{s.file.relativePath}</span>
            <span className="text-ink-tertiary">{source.data!.lineCount} lines</span>
          </div>
          <div className="flex-1 min-h-0">
            <MonacoSource value={source.data!.content} height="100%" citations={citations} highlightLine={activeLine} />
          </div>
        </div>
      </div>

      <SignOffModal
        spec={sp}
        open={signOpen}
        onClose={() => setSignOpen(false)}
        onConfirm={onSign}
        preconditionFailures={preconditionFailures}
      />
    </div>
  );
}

/**
 * Phase C.7 — per-claim comments toggle. Renders a "💬 N comments" pill
 * under each claim card; expanding it inlines the thread. The unread/total
 * count is cheap (the parent query is already cached for the whole spec).
 */
function ClaimCommentsToggle({ specId, claimPath }: { specId: string; claimPath: string }) {
  const [open, setOpen] = useState(false);
  const list = useQuery({
    queryKey: ['comments', specId, claimPath],
    queryFn: () => commentsApi.list(specId, claimPath),
  });
  const count = list.data?.data.length ?? 0;
  return (
    <div className="mt-1.5" data-testid={`claim-comments-${claimPath}`}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-caption text-ink-tertiary hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
        data-testid="claim-comments-toggle"
      >
        <MessageSquare className="h-3.5 w-3.5" aria-hidden="true" />
        {count === 0 ? 'Comment' : `${count} comment${count === 1 ? '' : 's'}`}
      </button>
      {open && (
        <div className="mt-2 rounded-md border border-border-subtle bg-sunken/50 p-3">
          <CommentsThread specId={specId} claimPath={claimPath} />
        </div>
      )}
    </div>
  );
}

/**
 * SIGNED-state CTA: if a scaffold already exists for this spec, link to it;
 * otherwise offer "Generate scaffold". A single round-trip to the scaffold
 * endpoint disambiguates without needing a separate probe.
 */
function ScaffoldCta({ specId, targetStack }: { specId: string; targetStack: string }) {
  const probe = useQuery({
    queryKey: ['scaffold-by-spec', specId],
    queryFn: () => api.getScaffoldForSpec(specId).catch(() => null),
    retry: 0,
    staleTime: 0,
  });
  if (probe.data?.id) {
    return (
      <Link to={`/scaffolds/${probe.data.id}`}>
        <Button variant="secondary" size="md">
          <Cog className="h-4 w-4" /> Open scaffold
        </Button>
      </Link>
    );
  }
  // Forward the engineer-chosen target stack on the navigation URL so the
  // LiveScaffoldPage can pass it through to POST /scaffold.
  const qs = targetStack && targetStack !== 'dotnet8' ? `?target=${encodeURIComponent(targetStack)}` : '';
  return (
    <Link to={`/specs/${specId}/scaffold${qs}`}>
      <Button variant="primary" size="md">
        <Cog className="h-4 w-4" /> Generate scaffold
      </Button>
    </Link>
  );
}

function parseRange(s: string): [number, number] | null {
  const m = /^(\d+)\s*[-–]\s*(\d+)$/.exec(s.trim());
  if (m) return [Number(m[1]), Number(m[2])];
  const single = /^(\d+)/.exec(s.trim());
  if (single) return [Number(single[1]), Number(single[1])];
  return null;
}

