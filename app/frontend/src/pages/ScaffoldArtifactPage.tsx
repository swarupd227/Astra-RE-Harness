import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeft, ClipboardCheck, Cog, GitCommit, GitBranch, RefreshCcw, ShieldCheck } from 'lucide-react';
import { api, type ScaffoldFile } from '@/lib/api';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ProviderStrip } from '@/components/ProviderStrip';
import { ProviderSettingsCard } from '@/components/ProviderSettingsCard';
import { MonacoSource, type TodoMarker } from '@/components/MonacoSource';
import { ScaffoldFileTree } from '@/components/ScaffoldFileTree';
import { TraceabilityPanel } from '@/components/TraceabilityPanel';

export function ScaffoldArtifactPage() {
  const { id = '' } = useParams<{ id: string }>();    // scaffold id
  const queryClient = useQueryClient();
  const scaffold = useQuery({
    queryKey: ['scaffold', id],
    queryFn: () => api.getScaffold(id),
    enabled: !!id,
  });
  const spec = useQuery({
    queryKey: ['spec-by-id', scaffold.data?.specId],
    queryFn: async () => {
      // We have a specId but the API exposes spec-by-subroutine.
      // Walk via my-reviews to find the matching subroutine id, then fetch.
      const reviews = await fetch(
        (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080') + '/api/v1/my-reviews',
        { headers: { 'X-Dev-Persona': 'engineer' } },
      ).then((r) => r.json());
      const all = [...(reviews.awaiting ?? []), ...(reviews.inProgress ?? []), ...(reviews.signed ?? [])];
      const match = all.find((r: any) => r.specId === scaffold.data?.specId);
      if (!match) return null;
      return api.getSpecForSubroutine(match.subroutineId);
    },
    enabled: !!scaffold.data?.specId,
  });

  const [activePath, setActivePath] = useState<string | null>(null);
  const [committing, setCommitting] = useState(false);
  const [commitError, setCommitError] = useState<string | null>(null);

  const files = scaffold.data?.files ?? [];
  const treeFiles = files.map((f) => ({ path: f.path, language: f.language, todoCount: f.todoCount }));
  const active = useMemo<ScaffoldFile | null>(() => {
    if (!files.length) return null;
    const path = activePath ?? files[0].path;
    return files.find((f) => f.path === path) ?? files[0];
  }, [files, activePath]);

  const todoMarkers = useMemo<TodoMarker[]>(() => {
    if (!active) return [];
    const marks: TodoMarker[] = [];
    const lines = active.content.split('\n');
    lines.forEach((line, i) => {
      if (line.includes('TODO') || line.includes('NotImplementedException')) {
        const tooltip = active.derivedFromClaimIds.length > 0
          ? `Engineer-implementation TODO · derived from ${active.derivedFromClaimIds.join(', ')}`
          : 'Engineer-implementation TODO';
        marks.push({ lineStart: i + 1, lineEnd: i + 1, tooltip });
      }
    });
    return marks;
  }, [active]);

  if (scaffold.isPending) {
    return <div className="mx-auto max-w-[1600px] space-y-4 p-6 lg:p-10"><Skeleton className="h-12 w-96" /><Skeleton className="h-[600px] w-full" /></div>;
  }
  if (scaffold.isError) {
    return <div className="mx-auto max-w-[1400px] p-6 lg:p-10"><ErrorBlock title="Could not load scaffold" message={scaffold.error.message} onRetry={() => scaffold.refetch()} /></div>;
  }

  const sc = scaffold.data;

  const onCommit = async () => {
    setCommitting(true);
    setCommitError(null);
    try {
      await api.commitScaffold(sc.id, { commitMessage: `scaffold(${sc.specId.slice(0, 8)}): generated package` });
      await queryClient.invalidateQueries({ queryKey: ['scaffold', id] });
    } catch (e) {
      setCommitError(e instanceof Error ? e.message : String(e));
    } finally {
      setCommitting(false);
    }
  };

  const isCommitted = sc.state === 'COMMITTED';

  return (
    <div className="flex h-[calc(100vh-110px)] flex-col">
      <header className="border-b border-border-subtle bg-raised px-6 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-3">
            <Link to={`/`} className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary" aria-label="Back">
              <ArrowLeft className="h-4 w-4" aria-hidden="true" />
            </Link>
            <span className="flex h-8 w-8 items-center justify-center rounded-md bg-[#F2E5C2] text-status-scaffolded">
              <Cog className="h-4 w-4" aria-hidden="true" />
            </span>
            <div>
              <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
                Stage 5 · Scaffolded package
              </p>
              <h1 className="font-mono text-h-md font-semibold text-ink-primary">
                {sc.fileCount} files · {sc.todoCount} TODOs · {sc.targetPlatform}
              </h1>
            </div>
            <Badge tone={isCommitted ? 'signed' : 'scaffolded'}>{sc.state}</Badge>
          </div>
          <div className="flex items-center gap-2">
            <Link
              to={`/scaffolds/${sc.id}/validation`}
              className="inline-flex items-center gap-1.5 rounded-md border border-border-subtle bg-canvas px-3 py-1.5 font-mono text-caption text-ink-secondary hover:bg-sunken hover:text-ink-primary"
              data-testid="open-validation-report"
              title="Validation: compile · test pack · cross-runtime equivalence"
            >
              <ClipboardCheck className="h-3.5 w-3.5" aria-hidden="true" />
              Validation report
            </Link>
            {!isCommitted && (
              <Button variant="primary" size="md" onClick={onCommit} loading={committing}>
                <GitCommit className="h-4 w-4" />
                Commit to Git
                <span className="ml-2 rounded-sm border border-white/30 bg-white/15 px-1.5 py-0.5 font-mono text-[10px] uppercase">
                  stub
                </span>
              </Button>
            )}
            {isCommitted && sc.git && (
              <a
                href={sc.git.commitUrl}
                target="_blank"
                rel="noreferrer"
                onClick={(e) => e.preventDefault()}
                className="inline-flex items-center gap-1.5 rounded-md border border-border-subtle bg-canvas px-3 py-1.5 font-mono text-caption text-ink-secondary hover:bg-sunken hover:text-ink-primary"
                title={`Faux commit (Phase B.4 stub)\n${sc.git.commitUrl}`}
              >
                <GitBranch className="h-3.5 w-3.5" aria-hidden="true" />
                {sc.git.branch}
                <span className="text-ink-tertiary">·</span>
                {sc.git.commitHash}
              </a>
            )}
          </div>
        </div>
      </header>

      {commitError && (
        <div className="px-6 py-3"><ErrorBlock title="Commit failed" message={commitError} /></div>
      )}

      <ProviderStrip
        info={sc.llmCall ? {
          name: sc.llmCall.provider,
          model: sc.llmCall.model,
          configVersion: sc.llmCall.providerConfigVersion,
          promptTemplateId: sc.llmCall.promptTemplateId,
          promptTemplateVersion: sc.llmCall.promptTemplateVersion,
        } as any : null}
        latencyMs={sc.llmCall?.latencyMs}
        tokens={sc.llmCall ? { in: sc.llmCall.inputTokens, out: sc.llmCall.outputTokens } : undefined}
      />

      <div className="border-b border-border-subtle bg-canvas/40 px-6 py-2">
        <ProviderSettingsCard compact />
      </div>

      {/* Signed-spec banner reminding the engineer this is a contract derivative */}
      {spec.data?.signature && (
        <div className="border-b border-status-signed/30 bg-[#DCE6F5]/40 px-6 py-2">
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 font-mono text-caption text-ink-secondary">
            <span className="inline-flex items-center gap-1.5 text-status-signed">
              <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" />
              <span className="font-semibold">Derived from a signed spec</span>
            </span>
            <span>signer <span className="text-ink-primary">{spec.data.signature.signerDisplay}</span></span>
            <span>algorithm <span className="text-ink-primary">{spec.data.signature.algorithm}</span></span>
            <span title={spec.data.signature.specCanonicalHash}>hash <span className="text-ink-primary">{spec.data.signature.specCanonicalHash.slice(0, 22)}…</span></span>
          </div>
        </div>
      )}

      <div className="grid flex-1 min-h-0 grid-cols-1 divide-y divide-border-subtle overflow-y-auto bg-canvas lg:grid-cols-[240px_minmax(0,1fr)_minmax(0,360px)] lg:divide-x lg:divide-y-0 lg:overflow-visible">
        {/* File tree */}
        <div className="h-52 min-h-0 overflow-y-auto bg-raised lg:h-auto">
          <div className="border-b border-border-subtle px-3 py-2 font-mono text-caption uppercase tracking-wider text-ink-tertiary">
            Package
          </div>
          <ScaffoldFileTree files={treeFiles} activePath={active?.path ?? null} onSelect={setActivePath} />
        </div>

        {/* Code editor */}
        <div className="h-[420px] min-h-0 flex flex-col bg-canvas lg:h-auto">
          <div className="shrink-0 flex items-center justify-between border-b border-border-subtle bg-raised px-4 py-2 font-mono text-caption text-ink-secondary">
            <span>{active?.path}</span>
            <span className="text-ink-tertiary">{active?.lineCount ?? '—'} lines · {active?.todoCount ?? 0} TODOs</span>
          </div>
          {active ? (
            <div className="flex-1 min-h-0">
              <MonacoSource
                value={active.content}
                language={active.language as any}
                height="100%"
                todoMarkers={todoMarkers}
              />
            </div>
          ) : (
            <div className="flex flex-1 items-center justify-center font-mono text-caption text-ink-tertiary">
              Empty package
            </div>
          )}
        </div>

        {/* Traceability panel */}
        <div className="max-h-64 min-h-0 overflow-y-auto bg-raised lg:max-h-none">
          <TraceabilityPanel spec={spec.data ?? null} claimIds={active?.derivedFromClaimIds ?? []} />
        </div>
      </div>
    </div>
  );
}

void RefreshCcw;
