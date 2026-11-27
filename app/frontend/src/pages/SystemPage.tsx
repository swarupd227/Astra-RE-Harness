import { useQuery } from '@tanstack/react-query';
import { CheckCircle2, CircleSlash } from 'lucide-react';
import { Link } from 'react-router-dom';
import { clsx } from 'clsx';
import { conversationsApi } from '@/lib/conversations';
import { systemApi } from '@/lib/api';
import { formatInt, formatMoney, formatPercent, relativeTime } from '@/workspace/format';
import { api, type ReadinessDep } from '@/lib/api';
import { buildInfo } from '@/lib/version';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Skeleton } from '@/components/Skeleton';
import { DependencyDot } from '@/components/DependencyDot';

const REPO = 'https://github.com/swarupd227/Astra-RE-Harness/commit/';

/** What each readiness dependency is, in words a user knows. */
const DEP_LABEL: Record<string, { label: string; what: string }> = {
  postgres: { label: 'Database', what: 'Projects, specs, reviews and the audit log' },
  minio: { label: 'File storage', what: 'Uploaded source, generated packages and logs' },
  blob: { label: 'File storage', what: 'Uploaded source, generated packages and logs' },
  parser: { label: 'Code parser', what: 'Reads source into code units and call graphs' },
};

/**
 * System health: what is deployed (frontend and API commits), whether every service the app needs answers, which
 * model is answering and whether its key works, and today's model usage.
 */
export function SystemPage() {
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const readiness = useQuery({
    queryKey: ['readiness'],
    queryFn: api.readiness,
    refetchInterval: 10_000,
    retry: 0,
  });
  const llm = useQuery({ queryKey: ['llm-settings'], queryFn: api.getLlmSettings, refetchInterval: 60_000 });
  const provider = useQuery({ queryKey: ['provider-settings'], queryFn: () => api.getProviderSettings() });
  const overview = useQuery({ queryKey: ['copilot', 'overview'], queryFn: () => conversationsApi.overview(), staleTime: 15_000 });

  const apiBuild = readiness.data?.build;
  const feBuild = buildInfo.commit;
  const keyRejected = !!llm.data?.keyRejected;
  const providerName = provider.data?.provider?.name;
  const model = provider.data?.provider?.model ?? llm.data?.model;
  const t = overview.data?.telemetry;
  const isAdmin = whoami.data?.persona === 'admin';

  return (
    <div className="mx-auto max-w-[1100px] space-y-5 p-4 lg:p-6 fadeup" role="region" aria-label="System status">
      <header className="border-b border-line-subtle pb-4">
        <p className="label">Platform</p>
        <h1 className="mt-1 text-h-lg font-semibold tracking-tight text-ink-primary">System health</h1>
        <p className="mt-1 text-body text-ink-secondary">
          {readiness.data
            ? readiness.data.status === 'ready' && !keyRejected
              ? 'Everything Astra depends on is answering.'
              : 'Something needs attention — see below.'
            : 'Checking every service Astra depends on…'}
        </p>
      </header>

      <div className="grid gap-4 lg:grid-cols-3">
        <Card>
          <CardHeader title="What is deployed" />
          <CardBody>
            <dl className="grid grid-cols-[90px_1fr] gap-y-2 text-body">
              <dt className="text-ink-secondary">Web app</dt>
              <dd className="font-mono" data-testid="system-build-frontend">
                <ShaLink sha={feBuild} />
              </dd>
              <dt className="text-ink-secondary">API</dt>
              <dd className="font-mono" data-testid="system-build-api">
                {apiBuild ? <ShaLink sha={apiBuild} /> : readiness.isPending ? '…' : 'unknown'}
              </dd>
            </dl>
            {apiBuild && feBuild !== 'dev' && apiBuild !== feBuild && (
              <p className="mt-2 text-caption text-ink-tertiary">The two can differ: each is deployed when it changes.</p>
            )}
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Model" />
          <CardBody>
            <dl className="grid grid-cols-[90px_1fr] gap-y-2 text-body">
              <dt className="text-ink-secondary">Provider</dt>
              <dd className="truncate font-mono">{providerName === 'anthropic' ? 'Claude (Anthropic)' : providerName ?? '…'}</dd>
              <dt className="text-ink-secondary">Model</dt>
              <dd className="truncate font-mono">{model ?? '…'}</dd>
              <dt className="text-ink-secondary">API key</dt>
              <dd className={clsx('font-mono', keyRejected ? 'text-status-failed' : 'text-ink-primary')} data-testid="system-key-state">
                {keyRejected ? 'rejected by the provider' : llm.data?.keyConfigured ? 'working' : llm.data ? 'not set' : '…'}
              </dd>
            </dl>
            {(keyRejected || (llm.data && !llm.data.keyConfigured)) && (
              <Link to="/platform/llm" className="mt-2 inline-block text-caption text-ink-link hover:underline">
                Open model provider
              </Link>
            )}
          </CardBody>
        </Card>

        <Card>
          <CardHeader title="Model usage today" />
          <CardBody>
            <dl className="grid grid-cols-[110px_1fr] gap-y-2 text-body">
              <dt className="text-ink-secondary">Calls</dt>
              <dd className="font-mono">{t ? formatInt(t.callsToday) : '…'}</dd>
              <dt className="text-ink-secondary">Spent</dt>
              <dd className="font-mono">{t ? formatMoney(t.costTodayUsd) : '…'}</dd>
              <dt className="text-ink-secondary">Median latency</dt>
              <dd className="font-mono">{t?.p50LatencyMs != null ? formatLatency(t.p50LatencyMs) : '—'}</dd>
              <dt className="text-ink-secondary">Cache hits</dt>
              <dd className="font-mono">{t ? formatPercent(t.cacheHitRate) : '…'}</dd>
            </dl>
          </CardBody>
        </Card>
      </div>

      <Card>
        <CardHeader
          title="Services"
          description="Checked every 10 seconds."
          action={
            readiness.data ? (
              readiness.data.status === 'ready' ? (
                <Badge tone="success">All answering</Badge>
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
                title="The API is not answering"
                message="The readiness check did not respond. The API may be restarting or unavailable."
                onRetry={() => readiness.refetch()}
              />
            </div>
          ) : (
            <ul className="divide-y divide-line-subtle">
              {readiness.data.dependencies.map((d) => (
                <DependencyRow key={d.name} dep={d} pulsing={readiness.isFetching} />
              ))}
            </ul>
          )}
        </CardBody>
      </Card>

      <ProblemsCard />

      <Card>
        <CardHeader title="You" />
        <CardBody>
          {whoami.isPending ? (
            <Skeleton className="h-6 w-48" />
          ) : whoami.isError ? (
            <ErrorBlock title="Could not reach the API" message={whoami.error.message} onRetry={() => whoami.refetch()} />
          ) : (
            <>
              <p className="text-body text-ink-primary">
                Signed in as <strong>{whoami.data.displayName}</strong> · <span className="capitalize">{whoami.data.persona}</span>
              </p>
              {isAdmin && (
                <p className="mt-2 text-caption text-ink-secondary" data-testid="system-dev-auth">
                  Authentication is in development mode: the <span className="font-mono">X-Dev-Persona</span> header sets
                  the role (bypass {String(whoami.data.bypassEnabled)}). Single sign-on replaces this in production.
                </p>
              )}
            </>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

/** 840 ms · 4.2 s · 2.8 min — whichever reads naturally. */
function formatLatency(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${(ms / 60_000).toFixed(1)} min`;
}

function ShaLink({ sha }: { sha: string }) {
  if (!sha || sha === 'dev') return <span className="text-ink-secondary">local build</span>;
  return (
    <a href={`${REPO}${sha}`} target="_blank" rel="noreferrer" className="text-ink-link hover:underline">
      {sha}
    </a>
  );
}

function DependencyRow({ dep, pulsing }: { dep: ReadinessDep; pulsing: boolean }) {
  const ok = dep.status === 'ok';
  const meta = DEP_LABEL[dep.name.toLowerCase()] ?? { label: dep.name, what: '' };
  return (
    <li className="flex items-center justify-between gap-4 px-6 py-3.5" data-testid={`system-dep-${dep.name}`}>
      <div className="flex min-w-0 items-center gap-3">
        {ok ? (
          <CheckCircle2 className="h-5 w-5 shrink-0 text-status-review" aria-hidden="true" />
        ) : (
          <CircleSlash className="h-5 w-5 shrink-0 text-status-failed" aria-hidden="true" />
        )}
        <div className="min-w-0">
          <p className="text-body font-medium text-ink-primary">
            {meta.label}
            {dep.version && <span className="ml-2 font-mono text-caption text-ink-secondary">{dep.version}</span>}
          </p>
          {meta.what && <p className="text-caption text-ink-secondary">{meta.what}</p>}
        </div>
      </div>
      <div className="flex shrink-0 items-center gap-3">
        {dep.error && (
          <span className="max-w-[320px] truncate font-mono text-caption text-status-failed" title={dep.error}>
            {dep.error}
          </span>
        )}
        <DependencyDot status={ok ? 'ok' : 'down'} pulse={pulsing} />
        <span className={clsx('text-caption', ok ? 'text-ink-secondary' : 'font-medium text-status-failed')}>{ok ? 'Answering' : 'Down'}</span>
      </div>
    </li>
  );
}

const STAGE_WORD: Record<string, string> = { COMPILE: 'Build', TEST_PACK: 'Tests', QUALITY: 'Quality', EQUIVALENCE: 'Equivalence' };

/** Validation runs stuck in "running" (a restart leaves them so) and the ones that failed in the last day. */
function ProblemsCard() {
  const q = useQuery({ queryKey: ['system-problems'], queryFn: systemApi.problems, refetchInterval: 60_000, retry: 0 });
  if (q.isError) return null; // an older API without the endpoint: say nothing rather than show an error
  const stuck = q.data?.stuck ?? [];
  const failed = q.data?.failed ?? [];
  return (
    <Card>
      <CardHeader
        title="Recent problems"
        description="Validation runs stuck for over 30 minutes, and runs that failed in the last day."
        action={q.data ? <Badge tone={stuck.length + failed.length ? 'failed' : 'success'}>{stuck.length + failed.length ? `${stuck.length + failed.length} to look at` : 'None'}</Badge> : null}
      />
      <CardBody className="p-0">
        {!q.data ? (
          <div className="p-6"><Skeleton className="h-10 w-full" /></div>
        ) : stuck.length + failed.length === 0 ? (
          <p className="px-6 py-4 text-caption text-ink-secondary">Nothing stuck or failing.</p>
        ) : (
          <ul className="divide-y divide-line-subtle" data-testid="system-problems">
            {[...stuck, ...failed].map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-6 py-2.5 text-caption">
                <span className={clsx('font-medium', r.status === 'RUNNING' ? 'text-status-warn' : 'text-status-failed')}>
                  {r.status === 'RUNNING' ? 'Stuck' : 'Failed'}
                </span>
                <span className="text-ink-primary">{STAGE_WORD[r.stage] ?? r.stage}</span>
                <Link to={`/scaffolds/${r.scaffoldId}/validation`} className="font-mono text-ink-link hover:underline">
                  {r.routineName}
                </Link>
                <span className="min-w-0 flex-1 truncate text-ink-secondary" title={r.summary}>{r.summary}</span>
                <span className="text-ink-tertiary">{relativeTime(r.startedAt)}</span>
              </li>
            ))}
          </ul>
        )}
      </CardBody>
    </Card>
  );
}
