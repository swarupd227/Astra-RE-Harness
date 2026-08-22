import { useQuery } from '@tanstack/react-query';
import { CheckCircle2, CircleSlash, Server } from 'lucide-react';
import { api, type ReadinessDep } from '@/lib/api';
import { buildInfo } from '@/lib/version';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { DependencyDot } from '@/components/DependencyDot';

export function SystemPage() {
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const readiness = useQuery({
    queryKey: ['readiness'],
    queryFn: api.readiness,
    refetchInterval: 5_000,
    retry: 0,
  });

  return (
    <div className="mx-auto max-w-[1100px] space-y-6 p-6 lg:p-10">
      <header>
        <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
          System
        </p>
        <h1 className="mt-2 text-display font-semibold text-ink-primary">Status</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Hello-world of every layer. The page below is alive only if the React app, the .NET 8
          API, Postgres, MinIO, and the parser sidecar are all reachable from inside the Docker
          network.
        </p>
      </header>

      <div className="grid gap-6 lg:grid-cols-3">
        <Card>
          <CardHeader title="Identity" />
          <CardBody>
            {whoami.isPending ? (
              <Skeleton className="h-6 w-48" />
            ) : whoami.isError ? (
              <ErrorBlock
                title="Could not reach the API"
                message={whoami.error.message}
                onRetry={() => whoami.refetch()}
              />
            ) : (
              <dl className="grid grid-cols-[110px_1fr] gap-y-2 text-body">
                <dt className="text-ink-tertiary">Persona</dt>
                <dd className="font-mono capitalize">{whoami.data.persona}</dd>
                <dt className="text-ink-tertiary">Display name</dt>
                <dd className="font-mono">{whoami.data.displayName}</dd>
                <dt className="text-ink-tertiary">Bypass</dt>
                <dd className="font-mono">{String(whoami.data.bypassEnabled)}</dd>
              </dl>
            )}
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Build" />
          <CardBody>
            <dl className="grid grid-cols-[110px_1fr] gap-y-2 text-body">
              <dt className="text-ink-tertiary">Version</dt>
              <dd className="font-mono">{buildInfo.version}</dd>
              <dt className="text-ink-tertiary">Phase</dt>
              <dd className="font-mono">
                {buildInfo.phase} · {buildInfo.phaseLabel}
              </dd>
              <dt className="text-ink-tertiary">Commit</dt>
              <dd className="font-mono truncate">{buildInfo.commit}</dd>
              <dt className="text-ink-tertiary">Built</dt>
              <dd className="font-mono">{buildInfo.builtAt}</dd>
            </dl>
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Auth" />
          <CardBody>
            <p className="text-body text-ink-secondary">
              <span className="font-mono text-ink-primary">X-Dev-Persona</span> header sets the
              active persona. OIDC via Microsoft Entra ID replaces this in Phase C; the surface
              keeps the same shape.
            </p>
          </CardBody>
        </Card>
      </div>

      <Card>
        <CardHeader
          title="Service readiness"
          description="Polled every 5 seconds. Each row is one upstream dependency."
          action={
            readiness.data ? (
              readiness.data.status === 'ready' ? (
                <Badge tone="success">All ready</Badge>
              ) : (
                <Badge tone="failed">Degraded</Badge>
              )
            ) : null
          }
        />
        <CardBody className="p-0">
          {readiness.isPending ? (
            <div className="space-y-2 p-6">
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-12 w-full" />
            </div>
          ) : readiness.isError ? (
            <div className="p-6">
              <ErrorBlock
                title="Readiness probe failed"
                message="The /health/ready endpoint is not responding. Check that the API container is up and Postgres + MinIO have started."
                onRetry={() => readiness.refetch()}
              />
            </div>
          ) : (
            <ul className="divide-y divide-border-subtle">
              {readiness.data.dependencies.map((d) => (
                <DependencyRow key={d.name} dep={d} pulsing={readiness.isFetching} />
              ))}
            </ul>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

function DependencyRow({ dep, pulsing }: { dep: ReadinessDep; pulsing: boolean }) {
  const ok = dep.status === 'ok';
  return (
    <li className="flex items-center justify-between px-6 py-4">
      <div className="flex items-center gap-3">
        {ok ? (
          <CheckCircle2 className="h-5 w-5 text-status-review" aria-hidden="true" />
        ) : (
          <CircleSlash className="h-5 w-5 text-status-failed" aria-hidden="true" />
        )}
        <Server className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
        <span className="font-mono text-body capitalize">{dep.name}</span>
      </div>
      <div className="flex items-center gap-3">
        {dep.error && (
          <span className="font-mono text-caption text-ink-tertiary" title={dep.error}>
            {dep.error.slice(0, 80)}
          </span>
        )}
        <DependencyDot status={ok ? 'ok' : 'down'} pulse={pulsing} />
        <span className="font-mono text-caption uppercase text-ink-tertiary">{dep.status}</span>
      </div>
    </li>
  );
}
