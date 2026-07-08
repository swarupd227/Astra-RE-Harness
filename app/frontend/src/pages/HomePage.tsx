import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import {
  ArrowRight,
  CheckCircle2,
  ChevronRight,
  ClipboardCheck,
  Cpu,
  Database,
  FileCode,
  FileSearch,
  GitBranch,
  History as HistoryIcon,
  Inbox,
  MessageSquare,
  Plus,
  ShieldCheck,
  Sparkles,
} from 'lucide-react';
import {
  api,
  auditApi,
  myReviewsApi,
  notificationsApi,
  type AuditEvent,
  type MyReviewItem,
  type SystemStats,
} from '@/lib/api';
import type { Persona } from '@/tokens/tokens';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';

/**
 * Home page — replaces the Phase-A placeholder with persona-adaptive,
 * live-data surfaces. The whole page renders against four endpoints
 * already in production: /system/stats, /corpora, /audit, /my-reviews,
 * /notifications/unread-count. No new backend code was added for this
 * rewrite beyond the stats rollup.
 */
export function HomePage() {
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami, retry: 0 });
  const persona: Persona = whoami.data?.persona ?? 'engineer';

  const stats = useQuery({
    queryKey: ['system-stats'],
    queryFn: api.systemStats,
    refetchInterval: 30_000,
  });

  return (
    <div className="mx-auto max-w-[1280px] space-y-6 p-6 lg:p-10">
      <Hero persona={persona} loading={whoami.isPending} provider={whoami.data?.llmProvider} model={whoami.data?.llmModel} />

      <KPIStrip stats={stats.data} loading={stats.isPending} />

      {persona === 'engineer' && <EngineerHome stats={stats.data} />}
      {persona === 'sme' && <SmeHome />}
      {persona === 'observer' && <ObserverHome />}
      {persona === 'admin' && <AdminHome stats={stats.data} llmProvider={whoami.data?.llmProvider ?? null} llmModel={whoami.data?.llmModel ?? null} />}
    </div>
  );
}

// ─── Hero ────────────────────────────────────────────────────────────

function Hero({
  persona,
  loading,
  provider,
  model,
}: {
  persona: Persona;
  loading: boolean;
  provider?: string;
  model?: string;
}) {
  const personaCopy: Record<Persona, { title: string; subtitle: string }> = {
    engineer: {
      title: 'Engineer console',
      subtitle: 'Ingest legacy Fortran. Stream a citation-grounded spec. Hand it to an SME for review.',
    },
    sme: {
      title: 'SME review console',
      subtitle: 'Read each claim, accept or edit, resolve open questions, sign the spec with HSM-backed RS256.',
    },
    observer: {
      title: 'Observer console',
      subtitle: 'Audit every transition. Inspect signed specs, costs, and provider provenance.',
    },
    admin: {
      title: 'Admin console',
      subtitle: 'Operator view: provider status, cost rollup, system health, and reset levers.',
    },
  };
  const copy = personaCopy[persona];

  return (
    <section>
      <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">Astra RE Harness</p>
      <h1 className="mt-2 max-w-4xl text-display font-semibold leading-tight text-ink-primary">
        {copy.title}
      </h1>
      <p className="mt-3 max-w-3xl text-body-lg text-ink-secondary">{copy.subtitle}</p>

      <div className="mt-4 flex flex-wrap items-center gap-2 text-caption">
        {loading ? (
          <Skeleton className="h-7 w-72" />
        ) : (
          <>
            <span className="inline-flex items-center gap-1.5 rounded-md border border-border-subtle bg-raised px-2.5 py-1">
              <span className="text-ink-tertiary">Signed in as</span>
              <span className="font-mono font-semibold capitalize text-ink-primary">{persona}</span>
            </span>
            {provider && (
              <span
                className="inline-flex items-center gap-1.5 rounded-md border border-border-subtle bg-raised px-2.5 py-1"
                title={model}
              >
                <Cpu className="h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
                <span className="text-ink-tertiary">Provider</span>
                <span className="font-mono font-semibold text-ink-primary">{provider}</span>
                {model && <span className="font-mono text-ink-tertiary">· {shortModel(model)}</span>}
              </span>
            )}
          </>
        )}
      </div>
    </section>
  );
}

// ─── KPI strip ───────────────────────────────────────────────────────

function KPIStrip({ stats, loading }: { stats?: SystemStats; loading: boolean }) {
  if (loading) {
    return (
      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        {[0, 1, 2, 3, 4].map((i) => <Skeleton key={i} className="h-20 w-full" />)}
      </div>
    );
  }
  if (!stats) return null;

  const signedCount = stats.specs.byState['SIGNED'] ?? 0;
  const scaffoldedCount = stats.specs.byState['SCAFFOLDED'] ?? 0;

  const kpis = [
    {
      label: 'Projects',
      value: stats.corpora.total.toLocaleString(),
      sub: `${stats.corpora.files.toLocaleString()} files · ${stats.corpora.totalLoc.toLocaleString()} LOC`,
      icon: Database,
      tone: 'corpora' as const,
      href: '/projects',
    },
    {
      label: 'Subroutines',
      value: stats.subroutines.total.toLocaleString(),
      sub: subroutineStateSummary(stats.subroutines.byState),
      icon: FileSearch,
      tone: 'review' as const,
      href: '/subroutines',
    },
    {
      label: 'Signed specs',
      value: (signedCount + scaffoldedCount).toLocaleString(),
      sub: scaffoldedCount > 0 ? `${scaffoldedCount} scaffolded` : 'awaiting scaffold',
      icon: ShieldCheck,
      tone: 'signed' as const,
    },
    {
      label: 'Claude spend',
      value: formatUsd(stats.llm.totalCostUsd),
      sub: `${stats.llm.totalCalls} call${stats.llm.totalCalls === 1 ? '' : 's'}${stats.llm.avgLatencyMs ? ` · ${(stats.llm.avgLatencyMs / 1000).toFixed(1)}s avg` : ''}`,
      icon: Sparkles,
      tone: 'draft' as const,
    },
    {
      label: 'Last activity',
      value: stats.llm.lastCalledAt ? relativeTime(stats.llm.lastCalledAt) : '—',
      sub: stats.llm.lastCalledAt ? new Date(stats.llm.lastCalledAt).toLocaleString() : 'no LLM calls yet',
      icon: HistoryIcon,
      tone: 'scaffolded' as const,
    },
  ];

  // Per-tone classes, mapped to the existing status palette in tailwind.config.
  // Kept as a fully-written-out object so Tailwind picks the class names up at
  // build time (it can't see dynamically-constructed class names).
  const toneStyles: Record<string, { icon: string; bg: string; value: string }> = {
    corpora:    { icon: 'text-ink-link',          bg: 'bg-[#E4ECF8]', value: 'text-ink-primary' },
    draft:      { icon: 'text-status-draft',      bg: 'bg-accent-muted', value: 'text-status-draft' },
    review:     { icon: 'text-status-review',     bg: 'bg-[#DAEFE9]', value: 'text-ink-primary' },
    signed:     { icon: 'text-status-signed',     bg: 'bg-[#DCE6F5]', value: 'text-status-signed' },
    scaffolded: { icon: 'text-status-scaffolded', bg: 'bg-[#FBF1D9]', value: 'text-ink-primary' },
    neutral:    { icon: 'text-ink-secondary',     bg: 'bg-sunken',    value: 'text-ink-primary' },
  };

  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-5" data-testid="home-kpi-strip">
      {kpis.map((k) => {
        const Icon = k.icon;
        const ts = toneStyles[k.tone] ?? toneStyles.neutral;
        const tile = (
          <div
            className={`flex h-full flex-col rounded-md border border-border-subtle bg-raised p-3 shadow-e1 transition-all duration-medium ${k.href ? 'hover:-translate-y-0.5 hover:shadow-e2' : ''}`}
          >
            <div className="flex items-center gap-2">
              <span className={`flex h-6 w-6 items-center justify-center rounded-md ${ts.bg}`}>
                <Icon className={`h-3.5 w-3.5 ${ts.icon}`} aria-hidden="true" />
              </span>
              <span className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">{k.label}</span>
            </div>
            <span className={`mt-2 font-mono text-display font-semibold leading-none ${ts.value}`}>
              {k.value}
            </span>
            <span className="mt-1 truncate font-mono text-caption text-ink-tertiary" title={k.sub}>{k.sub}</span>
          </div>
        );
        // Only KPIs with a destination are links — give them a hover lift so
        // their interactivity is signalled; the static tiles stay flat, so the
        // two are visually distinguishable (they previously looked identical).
        return k.href ? (
          <Link
            key={k.label}
            to={k.href}
            className="block rounded-xl transition-all duration-fast hover:-translate-y-0.5 hover:shadow-card focus-visible:outline-2 focus-visible:outline-ink-primary"
          >
            {tile}
          </Link>
        ) : (
          <div key={k.label}>{tile}</div>
        );
      })}
    </div>
  );
}

// ─── Engineer home ───────────────────────────────────────────────────

function EngineerHome({ stats }: { stats?: SystemStats }) {
  const corpora = useQuery({ queryKey: ['corpora'], queryFn: api.listCorpora });
  const recentActivity = useQuery({
    queryKey: ['audit-global', 'recent'],
    queryFn: () => auditApi.global({ limit: 8 }),
    refetchInterval: 30_000,
  });

  // Next-step heuristic from the stats rollup.
  const nextSteps = buildNextSteps(stats);

  return (
    <>
      {nextSteps.length > 0 && (
        <Card data-testid="next-steps">
          <CardHeader
            titleAs="h2"
            title={<span className="inline-flex items-center gap-2"><Sparkles className="h-4 w-4 text-accent" aria-hidden="true" /> What's next</span>}
            description="Actionable handles based on the current pipeline state."
          />
          <CardBody>
            <ul className="space-y-2">
              {nextSteps.map((s) => (
                <li key={s.label}>
                  <Link
                    to={s.href}
                    className="group flex items-center gap-3 rounded-md border border-border-subtle bg-raised px-4 py-3 transition-colors duration-fast hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary"
                  >
                    <span className="flex h-7 w-7 items-center justify-center rounded-md bg-accent-muted text-accent">
                      <s.icon className="h-3.5 w-3.5" aria-hidden="true" />
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="text-body font-semibold text-ink-primary">{s.label}</p>
                      <p className="font-mono text-caption text-ink-tertiary">{s.sub}</p>
                    </div>
                    <ChevronRight className="h-4 w-4 text-ink-tertiary opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
                  </Link>
                </li>
              ))}
            </ul>
          </CardBody>
        </Card>
      )}

      <div className="grid gap-6 lg:grid-cols-2">
        <Card data-testid="home-corpora">
          <CardHeader
            titleAs="h2"
            title={<span className="inline-flex items-center gap-2"><Database className="h-4 w-4 text-ink-tertiary" aria-hidden="true" /> Projects</span>}
            description="Connect a Git URL or upload source files to start the pipeline."
            action={
              <Link to="/projects/new"><Button variant="primary" size="sm"><Plus className="h-4 w-4" /> Add project</Button></Link>
            }
          />
          <CardBody className="p-0">
            {corpora.isPending ? (
              <div className="space-y-2 p-6"><Skeleton className="h-12 w-full" /><Skeleton className="h-12 w-full" /></div>
            ) : (corpora.data?.data.length ?? 0) === 0 ? (
              <p className="p-6 text-body text-ink-secondary">
                No projects yet. Click <span className="font-medium text-ink-primary">Add project</span> to connect a Git
                URL (e.g. <span className="font-mono">https://github.com/certik/minpack.git</span>) or upload
                <span className="font-mono"> .f / .for / .f90</span> files.
              </p>
            ) : (
              <ul className="divide-y divide-border-subtle">
                {corpora.data!.data.slice(0, 5).map((c) => (
                  <li key={c.id}>
                    <Link
                      to={`/projects/${c.id}`}
                      className="group flex items-center gap-3 px-6 py-3 transition-colors duration-fast hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary"
                    >
                      <FileCode className="h-4 w-4 shrink-0 text-ink-tertiary" aria-hidden="true" />
                      <div className="min-w-0 flex-1">
                        <p className="truncate text-body font-medium text-ink-primary">{c.name}</p>
                        <p className="font-mono text-caption text-ink-tertiary">
                          {c.sourceType.toUpperCase()} · {c.fileCount} file{c.fileCount === 1 ? '' : 's'} · {c.totalLoc.toLocaleString()} LOC
                        </p>
                      </div>
                      <Badge tone={c.state === 'PARSED' ? 'signed' : c.state === 'FAILED' ? 'failed' : 'draft'}>
                        {c.state}
                      </Badge>
                      <ChevronRight className="h-4 w-4 shrink-0 text-ink-tertiary opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </CardBody>
        </Card>

        <RecentActivityCard activity={recentActivity.data?.data ?? []} loading={recentActivity.isPending} />
      </div>
    </>
  );
}

function buildNextSteps(stats?: SystemStats): { label: string; sub: string; href: string; icon: typeof Sparkles }[] {
  if (!stats) return [];
  const out: { label: string; sub: string; href: string; icon: typeof Sparkles }[] = [];

  const parsedSubs = stats.subroutines.byState['PARSED'] ?? 0;
  if (parsedSubs > 0) {
    out.push({
      label: `Extract a spec`,
      sub: `${parsedSubs} subroutine${parsedSubs === 1 ? '' : 's'} parsed and ready for LLM extraction`,
      href: '/subroutines?state=PARSED',
      icon: Sparkles,
    });
  }

  const drafts = stats.specs.byState['DRAFT'] ?? 0;
  if (drafts > 0) {
    out.push({
      label: `Route ${drafts} draft spec${drafts === 1 ? '' : 's'} to an SME`,
      sub: 'Specs ready to hand off for review',
      href: '/subroutines?state=DRAFT',
      icon: ClipboardCheck,
    });
  }

  const signed = stats.specs.byState['SIGNED'] ?? 0;
  if (signed > 0) {
    out.push({
      label: `Generate scaffold from ${signed} signed spec${signed === 1 ? '' : 's'}`,
      sub: 'SIGNED specs without a scaffold yet',
      href: '/subroutines?state=SIGNED',
      icon: GitBranch,
    });
  }

  if (out.length === 0 && stats.corpora.total === 0) {
    out.push({
      label: 'Connect your first project',
      sub: 'Upload Fortran or paste a Git URL — the parser sidecar runs fparser2',
      href: '/projects/new',
      icon: Plus,
    });
  }
  return out;
}

// ─── SME home ────────────────────────────────────────────────────────

function SmeHome() {
  const reviews = useQuery({ queryKey: ['my-reviews'], queryFn: () => myReviewsApi.list() });
  const inbox = useQuery({
    queryKey: ['notifications-inbox', 'home'],
    queryFn: () => notificationsApi.list({ limit: 5 }),
    refetchInterval: 30_000,
  });

  return (
    <div className="grid gap-6 lg:grid-cols-2">
      <Card data-testid="home-sme-reviews">
        <CardHeader
          title={<span className="inline-flex items-center gap-2"><ClipboardCheck className="h-4 w-4 text-ink-tertiary" aria-hidden="true" /> Awaiting your review</span>}
          description={reviews.data ? `${reviews.data.counts.awaiting} awaiting · ${reviews.data.counts.inProgress} in progress · ${reviews.data.counts.signed} signed` : '—'}
          action={<Link to="/my-reviews"><Button variant="secondary" size="sm">Open queue <ArrowRight className="h-4 w-4" /></Button></Link>}
        />
        <CardBody className="p-0">
          {reviews.isPending ? (
            <div className="space-y-2 p-6"><Skeleton className="h-14 w-full" /><Skeleton className="h-14 w-full" /></div>
          ) : (reviews.data?.awaiting.length ?? 0) === 0 ? (
            <p className="p-6 text-body text-ink-secondary">
              Nothing awaiting you. Engineers route draft specs here for sign-off — each row will show the routine name,
              estimated review time, and the routing note.
            </p>
          ) : (
            <ul className="divide-y divide-border-subtle">
              {reviews.data!.awaiting.slice(0, 5).map((r) => <ReviewRow key={r.specId} r={r} />)}
            </ul>
          )}
        </CardBody>
      </Card>

      <Card data-testid="home-sme-inbox">
        <CardHeader
          title={<span className="inline-flex items-center gap-2"><Inbox className="h-4 w-4 text-ink-tertiary" aria-hidden="true" /> Mentions inbox</span>}
          description={inbox.data ? `${inbox.data.unread} unread` : '—'}
          action={<Link to="/comments"><Button variant="ghost" size="sm">Open inbox <ArrowRight className="h-4 w-4" /></Button></Link>}
        />
        <CardBody className="p-0">
          {inbox.isPending ? (
            <div className="space-y-2 p-6"><Skeleton className="h-12 w-full" /></div>
          ) : (inbox.data?.data.length ?? 0) === 0 ? (
            <p className="p-6 text-body text-ink-secondary">No mentions. When an engineer @-mentions you in a spec or claim comment, it lands here.</p>
          ) : (
            <ul className="divide-y divide-border-subtle">
              {inbox.data!.data.slice(0, 5).map((n) => (
                <li key={n.id} className="px-6 py-3">
                  <div className="flex items-center gap-2 text-caption">
                    <MessageSquare className="h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
                    <span className="font-medium text-ink-primary">{n.payload.authorDisplay ?? '—'}</span>
                    <span className="text-ink-tertiary">mentioned you</span>
                    {n.readAt === null && <Badge tone="draft" className="text-[10px]">new</Badge>}
                    <span className="ml-auto font-mono text-ink-tertiary">{relativeTime(n.createdAt)}</span>
                  </div>
                  <p className="mt-1 truncate text-body text-ink-primary">{n.payload.excerpt ?? '—'}</p>
                </li>
              ))}
            </ul>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

function ReviewRow({ r }: { r: MyReviewItem }) {
  return (
    <li>
      <Link
        to={`/subroutines/${r.subroutineId}/review`}
        className="group flex items-center gap-3 px-6 py-3 transition-colors duration-fast hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary"
      >
        <FileCode className="h-4 w-4 shrink-0 text-ink-tertiary" aria-hidden="true" />
        <div className="min-w-0 flex-1">
          <p className="truncate font-mono font-semibold text-ink-primary">{r.subroutineName}</p>
          <p className="font-mono text-caption text-ink-tertiary">
            {r.corpusName} · {r.relativePath} · ~{r.estimatedReviewMinutes} min
          </p>
        </div>
        <Badge tone={r.state === 'SIGNED' ? 'signed' : r.state === 'IN_REVIEW' ? 'review' : 'draft'}>{r.state}</Badge>
        <ChevronRight className="h-4 w-4 shrink-0 text-ink-tertiary opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
      </Link>
    </li>
  );
}

// ─── Observer + Admin homes (lean, leverage shared cards) ───────────

function ObserverHome() {
  const recent = useQuery({
    queryKey: ['audit-global', 'recent'],
    queryFn: () => auditApi.global({ limit: 12 }),
    refetchInterval: 30_000,
  });
  return (
    <div className="grid gap-6 lg:grid-cols-2">
      <RecentActivityCard activity={recent.data?.data ?? []} loading={recent.isPending} dense limit={12} />
      <SignedSpecsCard />
    </div>
  );
}

function AdminHome({
  stats,
  llmProvider,
  llmModel,
}: {
  stats?: SystemStats;
  llmProvider: string | null;
  llmModel: string | null;
}) {
  return (
    <div className="grid gap-6 lg:grid-cols-2">
      <Card data-testid="home-admin-provider">
        <CardHeader
          title={<span className="inline-flex items-center gap-2"><Cpu className="h-4 w-4 text-ink-tertiary" aria-hidden="true" /> Provider status</span>}
          description="Active LLM configuration. Every call records provider + model + residency stamp on its LlmCall row."
        />
        <CardBody className="space-y-3">
          <div className="rounded-md border border-border-subtle bg-sunken p-3 font-mono text-body">
            <div className="flex items-center justify-between">
              <span className="text-ink-tertiary">Provider</span>
              <span className="font-semibold text-ink-primary">{llmProvider ?? '—'}</span>
            </div>
            <div className="mt-1 flex items-center justify-between">
              <span className="text-ink-tertiary">Model</span>
              <span className="text-ink-primary">{llmModel ?? '—'}</span>
            </div>
            <div className="mt-1 flex items-center justify-between">
              <span className="text-ink-tertiary">Calls</span>
              <span className="text-ink-primary">{stats?.llm.totalCalls ?? 0}</span>
            </div>
            <div className="mt-1 flex items-center justify-between">
              <span className="text-ink-tertiary">Total spend</span>
              <span className="font-semibold text-status-draft">{formatUsd(stats?.llm.totalCostUsd ?? 0)}</span>
            </div>
            <div className="mt-1 flex items-center justify-between">
              <span className="text-ink-tertiary">Avg latency</span>
              <span className="text-ink-primary">{stats?.llm.avgLatencyMs ? `${(stats.llm.avgLatencyMs / 1000).toFixed(1)}s` : '—'}</span>
            </div>
          </div>
          <Link to="/system" className="inline-flex items-center gap-1 text-caption font-medium text-accent hover:underline focus-visible:outline-2 focus-visible:outline-ink-primary">
            Open system page <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
          </Link>
        </CardBody>
      </Card>

      <SignedSpecsCard />
    </div>
  );
}

// ─── Shared cards ────────────────────────────────────────────────────

function RecentActivityCard({
  activity,
  loading,
  dense,
  limit = 8,
}: {
  activity: AuditEvent[];
  loading: boolean;
  dense?: boolean;
  limit?: number;
}) {
  return (
    <Card data-testid="home-activity">
      <CardHeader
        titleAs="h2"
        title={<span className="inline-flex items-center gap-2"><HistoryIcon className="h-4 w-4 text-ink-tertiary" aria-hidden="true" /> Pipeline activity</span>}
        description="Most recent events across every project."
      />
      <CardBody className="p-0">
        {loading ? (
          <div className="space-y-2 p-6"><Skeleton className="h-10 w-full" /><Skeleton className="h-10 w-full" /></div>
        ) : activity.length === 0 ? (
          <p className="p-6 text-body text-ink-secondary">No events yet. Activity appears the moment the first ingest or extraction runs.</p>
        ) : (
          <ul className="divide-y divide-border-subtle">
            {activity.slice(0, limit).map((e) => (
              <li key={e.id} className={dense ? 'px-6 py-2' : 'px-6 py-3'}>
                <div className="flex items-center gap-2 text-caption">
                  <Badge tone={eventBadgeTone(e.eventType)} className="text-[10px]">{shortEvent(e.eventType)}</Badge>
                  <span className="font-medium text-ink-primary">{e.actorDisplay}</span>
                  <span className="font-mono text-ink-tertiary">{eventSubject(e)}</span>
                  <span className="ml-auto font-mono text-ink-tertiary">{relativeTime(e.occurredAt)}</span>
                </div>
              </li>
            ))}
          </ul>
        )}
      </CardBody>
    </Card>
  );
}

function SignedSpecsCard() {
  const signed = useQuery({
    queryKey: ['audit-global', 'signed'],
    queryFn: () => auditApi.global({ type: 'spec.signed', limit: 8 }),
    refetchInterval: 60_000,
  });
  return (
    <Card data-testid="home-signed">
      <CardHeader
        title={<span className="inline-flex items-center gap-2"><ShieldCheck className="h-4 w-4 text-status-signed" aria-hidden="true" /> Signed specs</span>}
        description="Cryptographically signed; verifiable independently via specCanonicalHash."
      />
      <CardBody className="p-0">
        {signed.isPending ? (
          <div className="space-y-2 p-6"><Skeleton className="h-12 w-full" /></div>
        ) : (signed.data?.data.length ?? 0) === 0 ? (
          <p className="p-6 text-body text-ink-secondary">
            No signed specs yet. Once an SME signs a draft, it appears here with the signer + algorithm + canonical hash.
          </p>
        ) : (
          <ul className="divide-y divide-border-subtle">
            {signed.data!.data.slice(0, 6).map((e) => {
              const payload = (e.payload ?? {}) as Record<string, unknown>;
              const hash = typeof payload.specCanonicalHash === 'string' ? payload.specCanonicalHash : '';
              const algo = typeof payload.algorithm === 'string' ? payload.algorithm : 'RS256';
              return (
                <li key={e.id}>
                  <Link
                    to={`/specs/${e.targetId}/audit`}
                    className="group flex items-center gap-3 px-6 py-3 transition-colors duration-fast hover:bg-sunken focus-visible:outline-2 focus-visible:outline-ink-primary"
                  >
                    <CheckCircle2 className="h-4 w-4 shrink-0 text-status-signed" aria-hidden="true" />
                    <div className="min-w-0 flex-1">
                      <p className="truncate text-body font-medium text-ink-primary">
                        Signed by {e.actorDisplay}
                      </p>
                      <p className="truncate font-mono text-caption text-ink-tertiary" title={hash}>
                        {algo} · {hash ? hash.slice(0, 32) + '…' : ''}
                      </p>
                    </div>
                    <span className="font-mono text-caption text-ink-tertiary">{relativeTime(e.occurredAt)}</span>
                    <ChevronRight className="h-4 w-4 shrink-0 text-ink-tertiary opacity-0 transition-opacity duration-fast group-hover:opacity-100" />
                  </Link>
                </li>
              );
            })}
          </ul>
        )}
      </CardBody>
    </Card>
  );
}

// ─── Helpers ─────────────────────────────────────────────────────────

function subroutineStateSummary(byState: Record<string, number>): string {
  const order = ['PARSED', 'DRAFT', 'IN_REVIEW', 'SIGNED', 'SCAFFOLDED'];
  const parts = order
    .map((s) => (byState[s] ? `${byState[s]} ${s.toLowerCase()}` : null))
    .filter(Boolean) as string[];
  return parts.length === 0 ? 'no subroutines yet' : parts.slice(0, 3).join(' · ');
}

function formatUsd(value: number): string {
  if (!value) return '$0.00';
  if (value < 1) return `$${value.toFixed(3)}`;
  return `$${value.toFixed(2)}`;
}

function relativeTime(iso: string): string {
  const t = new Date(iso).getTime();
  const diffSec = Math.max(0, (Date.now() - t) / 1000);
  if (diffSec < 60) return `${Math.floor(diffSec)}s ago`;
  if (diffSec < 3600) return `${Math.floor(diffSec / 60)}m ago`;
  if (diffSec < 86400) return `${Math.floor(diffSec / 3600)}h ago`;
  return `${Math.floor(diffSec / 86400)}d ago`;
}

function shortEvent(t: string): string {
  return t
    .replace(/^corpus\./, '')
    .replace(/^spec\./, '')
    .replace(/^claim\./, '')
    .replace(/^scaffold\./, '')
    .replace(/^comment\./, 'comment.');
}

function eventBadgeTone(t: string): 'draft' | 'review' | 'signed' | 'scaffolded' | 'failed' | 'neutral' {
  if (t.startsWith('spec.signed') || t.startsWith('scaffold.committed')) return 'signed';
  if (t.startsWith('spec.extracted') || t.startsWith('spec.routed')) return 'draft';
  if (t.startsWith('scaffold.')) return 'scaffolded';
  if (t.endsWith('.failed') || t === 'spec.superseded') return 'failed';
  if (t.startsWith('claim.')) return 'review';
  return 'neutral';
}

function eventSubject(e: AuditEvent): string {
  const p = (e.payload ?? {}) as Record<string, unknown>;
  if (typeof p.subroutine === 'string') return p.subroutine;
  if (typeof p.name === 'string') return p.name;
  if (typeof p.routine === 'string') return p.routine;
  return e.targetType;
}

function shortModel(model: string): string {
  // Trim long anthropic ids like "claude-sonnet-4-5-20250929" to "sonnet 4.5"
  const m = /claude-(sonnet|opus|haiku)-(\d)-(\d)/i.exec(model);
  if (m) return `${m[1].toLowerCase()} ${m[2]}.${m[3]}`;
  return model.length > 28 ? model.slice(0, 28) + '…' : model;
}
