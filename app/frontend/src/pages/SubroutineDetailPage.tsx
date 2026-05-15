import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { ArrowRight, FileCode, GitCommit, Sparkles } from 'lucide-react';
import { api } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { Button } from '@/components/Button';
import { MonacoSource } from '@/components/MonacoSource';

export function SubroutineDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const sub = useQuery({
    queryKey: ['subroutine', id],
    queryFn: () => api.getSubroutine(id),
    enabled: !!id,
  });
  const source = useQuery({
    queryKey: ['subroutine-source', id],
    queryFn: () => api.getSubroutineSource(id),
    enabled: !!id,
  });

  if (sub.isPending || source.isPending) {
    return (
      <div className="mx-auto max-w-[1400px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-96" />
        <Skeleton className="h-[520px] w-full" />
      </div>
    );
  }
  if (sub.isError) {
    return (
      <div className="mx-auto max-w-[1400px] p-6 lg:p-10">
        <ErrorBlock
          title="Could not load subroutine"
          message={sub.error.message}
          onRetry={() => sub.refetch()}
        />
      </div>
    );
  }

  const s = sub.data;

  return (
    <div className="mx-auto max-w-[1400px] space-y-6 p-6 lg:p-10">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
            <Link to={`/projects/${s.corpus.id}`} className="hover:text-ink-primary">
              {s.corpus.name}
            </Link>
            <span className="mx-1 text-ink-tertiary">›</span>
            <span className="text-ink-secondary">{s.file.relativePath}</span>
          </p>
          <h1 className="mt-2 font-mono text-display font-semibold text-ink-primary">
            {s.name}
          </h1>
          <p className="mt-1 font-mono text-caption text-ink-tertiary">{s.signature}</p>
        </div>
        <div className="flex items-center gap-2">
          <Badge tone={badgeToneForState(s.state)}>{s.state}</Badge>
          {(s.state === 'DRAFT' || s.state === 'IN_REVIEW' || s.state === 'SIGNED') && (
            <Link to={s.state === 'DRAFT' ? `/subroutines/${s.id}/spec` : `/subroutines/${s.id}/review`}>
              <Button variant="secondary" size="md">
                {s.state === 'DRAFT' ? 'View draft spec' : s.state === 'SIGNED' ? 'View signed spec' : 'Open review'}
                <ArrowRight className="h-4 w-4" />
              </Button>
            </Link>
          )}
          <Button
            variant="primary"
            size="md"
            onClick={() => navigate(`/subroutines/${s.id}/extract`)}
          >
            <Sparkles className="h-4 w-4" />
            {s.state === 'DRAFT' ? 'Re-extract spec' : 'Extract spec'}
          </Button>
        </div>
      </header>

      <div className="grid gap-6 lg:grid-cols-[1fr_320px]">
        <Card className="overflow-hidden">
          <CardHeader
            title={
              <span className="flex items-center gap-2">
                <FileCode className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                <span className="font-mono text-body">{s.file.relativePath}</span>
              </span>
            }
            description={`Lines ${s.lineStart}–${s.lineEnd} · ${s.file.lineCount} total`}
            action={
              <span className="font-mono text-[11px] text-ink-tertiary">
                <GitCommit className="mr-1 inline h-3 w-3" aria-hidden="true" />
                {s.file.fileHash.slice(0, 19)}…
              </span>
            }
          />
          <CardBody className="p-0">
            <MonacoSource
              value={source.data!.content}
              height={560}
              highlightLine={s.lineStart}
            />
          </CardBody>
        </Card>

        <aside className="space-y-4">
          <StructurePanel sub={s} />
          <NextStepCard />
        </aside>
      </div>
    </div>
  );
}

function StructurePanel({
  sub,
}: {
  sub: NonNullable<Awaited<ReturnType<typeof api.getSubroutine>>>;
}) {
  return (
    <Card>
      <CardHeader title="Structure" description="From AST. Lights up further in Phase C." />
      <CardBody className="space-y-5">
        <Group title="COMMON blocks" items={sub.commonBlockRefs ?? []} />
        <Group title="Calls" items={sub.calledSubroutines ?? []} />
        <Group
          title="ISAM reads"
          items={sub.ioPatterns?.isam_reads ?? []}
        />
        <Group
          title="ISAM writes"
          items={sub.ioPatterns?.isam_writes ?? []}
        />
        <Group
          title="Notifications"
          items={sub.ioPatterns?.notifications ?? []}
        />
      </CardBody>
    </Card>
  );
}

function Group({ title, items }: { title: string; items: string[] }) {
  if (!items || items.length === 0) {
    return (
      <div>
        <h4 className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">{title}</h4>
        <p className="mt-1 text-caption text-ink-tertiary">—</p>
      </div>
    );
  }
  return (
    <div>
      <h4 className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">{title}</h4>
      <ul className="mt-1.5 flex flex-wrap gap-1.5">
        {items.map((item) => (
          <li key={item}>
            <span className="inline-flex items-center rounded-sm border border-border-subtle bg-canvas px-1.5 py-0.5 font-mono text-caption text-ink-primary">
              {item}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function NextStepCard() {
  return (
    <Card className="border-accent/40 bg-accent-muted/40">
      <CardBody>
        <p className="font-mono text-caption uppercase tracking-wider text-accent">Up next</p>
        <p className="mt-2 text-body font-semibold text-ink-primary">
          Stage 3 — Live extraction
        </p>
        <p className="mt-1 text-caption text-ink-secondary">
          Anthropic Claude streams a behavioural spec with line-cited claims onto this surface.
        </p>
        <p className="mt-3 inline-flex items-center gap-1.5 text-caption text-ink-tertiary">
          Lights up in Phase B.2
          <ArrowRight className="h-3 w-3" />
        </p>
      </CardBody>
    </Card>
  );
}

function badgeToneForState(state: string): 'draft' | 'review' | 'signed' | 'scaffolded' | 'neutral' {
  switch (state) {
    case 'DRAFT': return 'draft';
    case 'IN_REVIEW': return 'review';
    case 'SIGNED': return 'signed';
    case 'SCAFFOLDED': return 'scaffolded';
    default: return 'neutral';
  }
}
