import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Sparkles, RefreshCcw, ArrowLeft, ClipboardCheck, ShieldCheck } from 'lucide-react';
import { api } from '@/lib/api';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ProviderStrip } from '@/components/ProviderStrip';
import { ProviderSettingsCard } from '@/components/ProviderSettingsCard';
import { MonacoSource, type Citation } from '@/components/MonacoSource';
import { StreamingSpecPanel, type DraftSpec } from '@/components/StreamingSpecPanel';

export function DraftSpecPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const sub = useQuery({ queryKey: ['subroutine', id], queryFn: () => api.getSubroutine(id), enabled: !!id });
  const source = useQuery({ queryKey: ['subroutine-source', id], queryFn: () => api.getSubroutineSource(id), enabled: !!id });
  const spec = useQuery({ queryKey: ['spec', id], queryFn: () => api.getSpecForSubroutine(id), enabled: !!id });
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });

  const [activeCitation, setActiveCitation] = useState<string | null>(null);
  const [activeLine, setActiveLine] = useState<number | undefined>(undefined);
  const [routing, setRouting] = useState(false);

  const citations: Citation[] = useMemo(() => {
    if (!spec.data) return [];
    const out: Citation[] = [];
    const collect = (lines: string) => {
      const r = parseRange(lines);
      if (r) out.push({ lineStart: r[0], lineEnd: r[1], tone: activeCitation === lines ? 'accent' : undefined });
    };
    spec.data.spec.invariants?.forEach((i) => i.citations?.forEach((c) => collect(c.lines)));
    spec.data.spec.side_effects?.forEach((s) => s.citations?.forEach((c) => collect(c.lines)));
    spec.data.spec.edge_cases?.forEach((e) => e.citations?.forEach((c) => collect(c.lines)));
    return out;
  }, [spec.data, activeCitation]);

  if (sub.isPending || source.isPending || spec.isPending) {
    return (
      <div className="mx-auto max-w-[1600px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-12 w-96" />
        <Skeleton className="h-[600px] w-full" />
      </div>
    );
  }

  if (sub.isError || source.isError) {
    return (
      <div className="mx-auto max-w-[1400px] p-6 lg:p-10">
        <ErrorBlock title="Could not load subroutine" message={String(sub.error ?? source.error)} />
      </div>
    );
  }
  if (spec.isError) {
    return (
      <div className="mx-auto max-w-[1400px] p-6 lg:p-10">
        <ErrorBlock
          title="No spec yet"
          message="This subroutine has not been extracted. Click 'Extract spec' on the subroutine surface."
        />
      </div>
    );
  }

  const s = sub.data;
  const sp = spec.data;
  const draft: DraftSpec = sp.spec as DraftSpec;
  const isEngineer = whoami.data?.persona === 'engineer';
  const isSme = whoami.data?.persona === 'sme';
  const isDraft = sp.state === 'DRAFT';
  const isInReview = sp.state === 'IN_REVIEW';
  const isSigned = sp.state === 'SIGNED';

  const onRoute = async () => {
    setRouting(true);
    try {
      await api.routeSpec(sp.id, { reviewerIds: [], routingNote: '' });
      await queryClient.invalidateQueries({ queryKey: ['spec', id] });
      await queryClient.invalidateQueries({ queryKey: ['subroutine', id] });
      navigate(`/subroutines/${s.id}/review`);
    } finally {
      setRouting(false);
    }
  };

  const onCite = (lines: string) => {
    setActiveCitation(lines);
    const r = parseRange(lines);
    if (r) setActiveLine(r[0]);
  };

  return (
    <div className="flex h-[calc(100vh-110px)] flex-col">
      <header className="border-b border-border-subtle bg-raised px-6 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <Link
              to={`/subroutines/${s.id}`}
              className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary"
              aria-label="Back to subroutine"
            >
              <ArrowLeft className="h-4 w-4" aria-hidden="true" />
            </Link>
            <div>
              <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
                Draft spec
              </p>
              <h1 className="font-mono text-h-md font-semibold text-ink-primary">{s.name}</h1>
            </div>
            <Badge tone={isSigned ? 'signed' : isInReview ? 'review' : 'draft'}>{sp.state}</Badge>
          </div>
          <div className="flex items-center gap-2">
            {isDraft && isEngineer && (
              <Button variant="primary" size="sm" onClick={onRoute} loading={routing}>
                <ClipboardCheck className="h-4 w-4" />
                Route to SME for review
              </Button>
            )}
            {(isInReview || isSigned) && (
              <Link to={`/subroutines/${s.id}/review`}>
                <Button variant={isSme && isInReview ? 'primary' : 'secondary'} size="sm">
                  {isSigned ? (
                    <>
                      <ShieldCheck className="h-4 w-4" /> View signed
                    </>
                  ) : isSme ? (
                    <>
                      <ClipboardCheck className="h-4 w-4" /> Begin review
                    </>
                  ) : (
                    <>
                      <ClipboardCheck className="h-4 w-4" /> View review
                    </>
                  )}
                </Button>
              </Link>
            )}
            <Link to={`/subroutines/${s.id}/extract`}>
              <Button variant="secondary" size="sm">
                <RefreshCcw className="h-4 w-4" />
                Re-extract
              </Button>
            </Link>
          </div>
        </div>
      </header>

      <ProviderStrip
        info={
          sp.llmCall
            ? {
                name: sp.llmCall.provider,
                model: sp.llmCall.model,
                configVersion: sp.llmCall.providerConfigVersion,
                promptTemplateId: sp.llmCall.promptTemplateId,
                promptTemplateVersion: sp.llmCall.promptTemplateVersion,
              }
            : null
        }
        latencyMs={sp.llmCall?.latencyMs}
        tokens={sp.llmCall ? { in: sp.llmCall.inputTokens, out: sp.llmCall.outputTokens } : undefined}
      />

      <div className="border-b border-border-subtle bg-canvas/40 px-6 py-2">
        <ProviderSettingsCard compact />
      </div>

      <div className="grid flex-1 min-h-0 grid-cols-[minmax(0,1fr)_minmax(0,560px)] divide-x divide-border-subtle">
        <div className="min-h-0 overflow-y-auto bg-raised">
          <SpecHeader spec={draft} />
          <StreamingSpecPanel
            draft={draft}
            onCitationClick={onCite}
            activeCitation={activeCitation}
          />
        </div>
        <div className="min-h-0 flex flex-col bg-canvas">
          <div className="shrink-0 flex items-center justify-between border-b border-border-subtle bg-raised px-4 py-2 font-mono text-caption text-ink-secondary">
            <span>{s.file.relativePath}</span>
            <span className="text-ink-tertiary">{source.data!.lineCount} lines</span>
          </div>
          <div className="flex-1 min-h-0">
            <MonacoSource
              value={source.data!.content}
              height="100%"
              citations={citations}
              highlightLine={activeLine}
            />
          </div>
        </div>
      </div>
    </div>
  );
}

function SpecHeader({ spec }: { spec: DraftSpec }) {
  return (
    <div className="border-b border-border-subtle bg-canvas/40 px-6 py-4">
      <div className="flex flex-wrap items-center gap-x-6 gap-y-1 font-mono text-caption text-ink-tertiary">
        <span><Sparkles className="mr-1 inline h-3 w-3" aria-hidden="true" />routine <span className="text-ink-primary">{spec.routine}</span></span>
        <span>source <span className="text-ink-secondary">{spec.source_path}</span></span>
        <span>lines <span className="text-ink-secondary">{spec.source_lines}</span></span>
      </div>
    </div>
  );
}

function parseRange(s: string): [number, number] | null {
  const m = /^(\d+)\s*[-–]\s*(\d+)$/.exec(s.trim());
  if (m) return [Number(m[1]), Number(m[2])];
  const single = /^(\d+)/.exec(s.trim());
  if (single) return [Number(single[1]), Number(single[1])];
  return null;
}
