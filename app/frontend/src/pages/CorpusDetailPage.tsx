import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { BookOpen, Boxes, ChevronDown, ChevronRight, Download, FileCode, Folder, GitBranch, GitMerge, Layers, RefreshCw } from 'lucide-react';
import { api, getPersona } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { ReingestModal } from '@/components/ReingestModal';

export function CorpusDetailPage() {
  const navigate = useNavigate();
  const { id = '' } = useParams();
  const qc = useQueryClient();
  const persona = getPersona();
  const [reingestOpen, setReingestOpen] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);
  const corpus = useQuery({
    queryKey: ['corpus', id],
    queryFn: () => api.getCorpus(id),
    enabled: !!id,
  });

  const handleExport = async (includeSources: boolean) => {
    if (exporting) return;
    setExporting(true);
    setExportError(null);
    try {
      const { blob, filename } = await api.exportProjectBundle(id, includeSources);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    } catch (err) {
      setExportError((err as Error).message ?? 'Export failed');
    } finally {
      setExporting(false);
    }
  };

  if (corpus.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }
  if (corpus.isError) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock
          title="Could not load project"
          message={corpus.error.message}
          onRetry={() => corpus.refetch()}
        />
      </div>
    );
  }
  const c = corpus.data;
  const files = c.latestVersion?.files ?? [];

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup">
      <header className="flex items-start justify-between gap-4">
        <div>
          <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
            Project
          </p>
          <h1 className="mt-2 text-display font-semibold text-ink-primary">{c.name}</h1>
          <p className="mt-2 font-mono text-caption text-ink-tertiary">
            {c.sourceType.toUpperCase()} · {c.fileCount} file{c.fileCount === 1 ? '' : 's'} ·{' '}
            {c.totalLoc.toLocaleString()} LOC ·{' '}
            ingested {c.latestVersion ? new Date(c.latestVersion.ingestedAt).toLocaleString() : '—'}
          </p>
        </div>
        <div className="flex items-center gap-3">
          <Badge tone={c.state === 'FAILED' ? 'failed' : 'signed'}>{c.state}</Badge>
          <Button variant="secondary" onClick={() => navigate(`/corpora/${id}/docs`)} data-testid="open-docs">
            <BookOpen className="h-4 w-4" aria-hidden="true" />
            Docs
          </Button>
          <Button variant="secondary" onClick={() => navigate(`/corpora/${id}/migration-plan`)} data-testid="open-migration-plan">
            <Layers className="h-4 w-4" aria-hidden="true" />
            Migration plan
          </Button>
          <Button variant="secondary" onClick={() => navigate(`/corpora/${id}/pattern-analysis`)} data-testid="open-pattern-analysis">
            <Boxes className="h-4 w-4" aria-hidden="true" />
            Pattern analysis
          </Button>
          <Button variant="secondary" onClick={() => navigate(`/corpora/${id}/dependency-graph`)} data-testid="open-dependency-graph">
            <GitBranch className="h-4 w-4" aria-hidden="true" />
            Dependency graph
          </Button>
          <Button
            variant="secondary"
            onClick={() => setReingestOpen(true)}
            data-testid="open-reingest"
          >
            <RefreshCw className="h-4 w-4" aria-hidden="true" />
            Re-sync
          </Button>
          {persona === 'admin' && (
            <div className="relative">
              <select
                value=""
                onChange={(e) => { if (e.target.value) handleExport(e.target.value === 'with-sources'); }}
                disabled={exporting}
                aria-label="Export project artifacts"
                data-testid="export-project"
                className="appearance-none cursor-pointer rounded border border-border-subtle bg-raised py-1.5 pl-8 pr-7 text-sm text-ink-secondary transition-colors hover:border-brand hover:text-ink-primary disabled:cursor-not-allowed disabled:opacity-50"
              >
                <option value="">{exporting ? 'Exporting…' : 'Export…'}</option>
                <option value="artifacts">Artifacts (.zip)</option>
                <option value="with-sources">Artifacts + sources (.zip)</option>
              </select>
              <Download className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-ink-tertiary" />
              <ChevronDown className="pointer-events-none absolute right-2 top-1/2 h-3 w-3 -translate-y-1/2 text-ink-tertiary" />
            </div>
          )}
        </div>
      </header>

      {exportError && (
        <p className="text-xs text-rose-600" data-testid="export-project-error">{exportError}</p>
      )}

      <ReingestModal
        open={reingestOpen}
        onClose={() => setReingestOpen(false)}
        onSuccess={() => {
          // Drop both the list and detail caches so the user sees the new version.
          qc.invalidateQueries({ queryKey: ['corpora'] });
          qc.invalidateQueries({ queryKey: ['corpus', id] });
        }}
        corpusId={c.id}
        corpusName={c.name}
        defaultSourceType={c.sourceType}
        defaultGitUrl={null}
        defaultBranch={null}
        defaultSourceRoot={null}
      />

      <Card>
        <CardHeader
          title="Files in this version"
          description="Select a routine to open its details."
        />
        <CardBody className="p-0">
          <ul className="divide-y divide-border-subtle">
            {files.map((file) => (
              <li key={file.id}>
                <FileBlock file={file} />
              </li>
            ))}
          </ul>
        </CardBody>
      </Card>
    </div>
  );
}

function FileBlock({ file }: { file: NonNullable<ReturnType<typeof useFiles>>[number] }) {
  return (
    <div className="px-6 py-4">
      <div className="flex items-center gap-3 text-body">
        <Folder className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
        <FileCode className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
        <span className="font-mono text-ink-primary">{file.relativePath}</span>
        <span className="ml-auto font-mono text-caption text-ink-tertiary">
          {file.lineCount} lines
        </span>
      </div>
      {file.subroutines.length > 0 && (
        <ul className="mt-3 space-y-1 pl-7">
          {file.subroutines.map((sub) => (
            <li key={sub.id}>
              <Link
                to={`/subroutines/${sub.id}`}
                className="group flex items-center justify-between rounded-md px-3 py-2 text-body transition-colors duration-fast hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary"
              >
                <span className="flex items-center gap-3">
                  <span className="font-mono text-caption text-ink-tertiary">
                    L{sub.lineStart}–{sub.lineEnd}
                  </span>
                  <span className="font-mono font-semibold text-ink-primary">{sub.name}</span>
                  <Badge tone={subBadgeTone(sub.state)} className="text-[10px]">
                    {sub.state}
                  </Badge>
                  {sub.carriedForward && (
                    <span
                      className="inline-flex items-center gap-1 rounded-sm border border-status-signed/40 bg-[#DCE6F5]/40 px-1.5 py-0.5 font-mono text-micro uppercase tracking-wider text-status-signed"
                      title="Spec carried forward from a previous source version (file hash unchanged)"
                      data-testid={`carried-${sub.id}`}
                    >
                      <GitMerge className="h-3 w-3" aria-hidden="true" />
                      carried fwd
                    </span>
                  )}
                </span>
                <ChevronRight className="h-4 w-4 text-ink-tertiary opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function subBadgeTone(state: string): 'draft' | 'review' | 'signed' | 'scaffolded' | 'neutral' {
  switch (state) {
    case 'DRAFT': return 'draft';
    case 'IN_REVIEW': return 'review';
    case 'SIGNED': return 'signed';
    case 'SCAFFOLDED': return 'scaffolded';
    default: return 'neutral';
  }
}

// Type helper to keep the FileBlock prop strongly typed without exporting internals
type Files = NonNullable<NonNullable<Awaited<ReturnType<typeof api.getCorpus>>>['latestVersion']>['files'];
function useFiles(): Files | null { return null; }
