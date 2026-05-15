import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { Database, FileText, Layers, Plus } from 'lucide-react';
import { api, type CorpusListItem } from '@/lib/api';
import { Card, CardBody } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { EmptyState } from '@/components/EmptyState';
import { NoCorporaIllustration } from '@/illustrations/NoCorpora';

export function CorporaPage() {
  const corpora = useQuery({ queryKey: ['corpora'], queryFn: api.listCorpora });

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10">
      <header className="flex items-start justify-between gap-4">
        <div>
          <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
            Phase C.1
          </p>
          <h1 className="mt-2 text-display font-semibold text-ink-primary">Projects</h1>
          <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
            Each project is a versioned snapshot of Fortran source. Upload <span className="font-mono">.f / .for / .f90</span>{' '}
            files or a <span className="font-mono">.zip</span>, or clone from a Git URL — the parser sidecar runs{' '}
            <span className="font-mono">fparser2</span> and persists every subroutine.
          </p>
        </div>
        <Link to="/projects/new" data-testid="add-corpus">
          <Button variant="primary">
            <Plus className="h-4 w-4" aria-hidden="true" />
            Add project
          </Button>
        </Link>
      </header>

      {corpora.isPending ? (
        <div className="grid gap-4 lg:grid-cols-2">
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : corpora.isError ? (
        <ErrorBlock
          title="Could not load projects"
          message={corpora.error.message}
          onRetry={() => corpora.refetch()}
        />
      ) : corpora.data.data.length === 0 ? (
        <Card>
          <CardBody>
            <EmptyState
              illustration={<NoCorporaIllustration size={140} />}
              title="No projects yet"
              description="Upload Fortran sources or connect a Git repo to start. The seed project is created on first boot if Database__SeedDemo is true."
              action={
                <Link to="/projects/new">
                  <Button variant="primary">
                    <Plus className="h-4 w-4" aria-hidden="true" />
                    Add project
                  </Button>
                </Link>
              }
            />
          </CardBody>
        </Card>
      ) : (
        <ul className="grid gap-4 lg:grid-cols-2">
          {corpora.data.data.map((c) => (
            <li key={c.id}>
              <CorpusCard corpus={c} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function CorpusCard({ corpus }: { corpus: CorpusListItem }) {
  // Each card gets a status-colored left accent + a tinted icon background
  // so a single glance at the corpora grid tells you what's healthy vs failed.
  const accent = (() => {
    switch (corpus.state) {
      case 'PARSED':    return { edge: 'border-l-status-review', iconBg: 'bg-[#DAEFE9]', iconFg: 'text-status-review' };
      case 'PARSING':
      case 'INGESTING': return { edge: 'border-l-status-draft',  iconBg: 'bg-accent-muted', iconFg: 'text-status-draft' };
      case 'FAILED':    return { edge: 'border-l-status-failed', iconBg: 'bg-[#F4D8D7]', iconFg: 'text-status-failed' };
      default:          return { edge: 'border-l-border',        iconBg: 'bg-sunken',    iconFg: 'text-ink-secondary' };
    }
  })();
  return (
    <Link
      to={`/corpora/${corpus.id}`}
      className="block focus-visible:outline-2 focus-visible:outline-ink-primary"
    >
      <Card interactive className={`border-l-4 ${accent.edge}`}>
        <CardBody>
          <header className="flex items-start justify-between gap-3">
            <div className="flex items-center gap-3">
              <span className={`flex h-10 w-10 items-center justify-center rounded-md ${accent.iconBg} ${accent.iconFg}`}>
                <Database className="h-5 w-5" aria-hidden="true" />
              </span>
              <div>
                <h2 className="text-h-md font-semibold text-ink-primary">{corpus.name}</h2>
                <p className="mt-0.5 font-mono text-caption text-ink-tertiary">
                  {corpus.sourceType.toUpperCase()}
                </p>
              </div>
            </div>
            <Badge tone={badgeToneForState(corpus.state)}>{corpus.state}</Badge>
          </header>
          <dl className="mt-5 grid grid-cols-3 gap-4 text-body">
            <div>
              <dt className="text-caption text-ink-tertiary">Files</dt>
              <dd className="mt-0.5 font-mono text-h-md font-semibold text-ink-primary">
                <FileText className="mr-1 inline h-4 w-4 -translate-y-0.5 text-ink-tertiary" aria-hidden="true" />
                {corpus.fileCount}
              </dd>
            </div>
            <div>
              <dt className="text-caption text-ink-tertiary">Lines of code</dt>
              <dd className="mt-0.5 font-mono text-h-md font-semibold text-ink-primary">
                <Layers className="mr-1 inline h-4 w-4 -translate-y-0.5 text-ink-tertiary" aria-hidden="true" />
                {corpus.totalLoc.toLocaleString()}
              </dd>
            </div>
            <div>
              <dt className="text-caption text-ink-tertiary">Last activity</dt>
              <dd className="mt-0.5 font-mono text-caption text-ink-secondary">
                {new Date(corpus.updatedAt).toLocaleString()}
              </dd>
            </div>
          </dl>
        </CardBody>
      </Card>
    </Link>
  );
}

function badgeToneForState(state: string): 'draft' | 'review' | 'signed' | 'failed' | 'neutral' {
  switch (state) {
    case 'PARSED':
    case 'INGESTED':
      return 'signed';
    case 'PARSING':
    case 'INGESTING':
      return 'draft';
    case 'FAILED':
      return 'failed';
    default:
      return 'neutral';
  }
}
