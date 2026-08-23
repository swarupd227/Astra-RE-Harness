import { useQuery } from '@tanstack/react-query';
import { useParams, useSearchParams } from 'react-router-dom';
import { useMemo } from 'react';
import {
  ClipboardCheck,
  Database,
  Edit3,
  FileSearch,
  GitBranch as GitBranchIcon,
  GitMerge,
  HelpCircle,
  MessageSquare,
  Package as PackageIcon,
  ShieldCheck,
  Sparkles,
  X,
  Check,
  RotateCcw,
  History as HistoryIcon,
} from 'lucide-react';
import { auditApi, type AuditEvent } from '@/lib/api';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { CommentsThread } from '@/components/CommentsThread';
import { EvidenceTrail } from '@/components/EvidenceTrail';
import { clsx } from 'clsx';

type ToneKey = 'neutral' | 'draft' | 'review' | 'signed' | 'scaffolded' | 'failed' | 'superseded';

const EVENT_META: Record<string, { label: string; icon: any; tone: ToneKey; branch?: boolean }> = {
  'corpus.ingested':    { label: 'Source ingested',     icon: Database,        tone: 'neutral' },
  'corpus.reingested':  { label: 'Source re-synced',    icon: RotateCcw,       tone: 'draft', branch: true },
  'source.parsed':      { label: 'AST parsed',          icon: FileSearch,      tone: 'neutral' },
  'spec.extracted':     { label: 'Spec extracted',      icon: Sparkles,        tone: 'draft' },
  'spec.routed':        { label: 'Routed to review',    icon: ClipboardCheck,  tone: 'review' },
  'spec.signed':        { label: 'Spec signed',         icon: ShieldCheck,     tone: 'signed' },
  'spec.carried_forward': { label: 'Spec carried forward', icon: GitMerge,     tone: 'signed', branch: true },
  'spec.superseded':    { label: 'Spec superseded',     icon: GitBranchIcon,   tone: 'superseded', branch: true },
  'claim.accept':       { label: 'Claim accepted',      icon: Check,           tone: 'review' },
  'claim.edit':         { label: 'Claim edited',        icon: Edit3,           tone: 'draft' },
  'claim.reject':       { label: 'Claim rejected',      icon: X,               tone: 'failed' },
  'claim.question':     { label: 'Question raised',     icon: HelpCircle,      tone: 'scaffolded' },
  'scaffold.generated': { label: 'Scaffold generated',  icon: PackageIcon,     tone: 'scaffolded' },
  'scaffold.committed': { label: 'Scaffold committed',  icon: GitMerge,        tone: 'scaffolded' },
  'comment.posted':     { label: 'Comment posted',      icon: MessageSquare,   tone: 'neutral' },
  'comment.edited':     { label: 'Comment edited',      icon: MessageSquare,   tone: 'neutral' },
  'comment.resolved':   { label: 'Comment resolved',    icon: Check,           tone: 'review' },
  'comment.unresolved': { label: 'Comment reopened',    icon: RotateCcw,       tone: 'draft' },
  'comment.deleted':    { label: 'Comment deleted',     icon: X,               tone: 'failed' },
};

// Per-tone styling — kept as static class names so Tailwind sees them at build time.
const TONE_STYLES: Record<ToneKey, { dot: string; ring: string; icon: string; edge: string }> = {
  neutral:    { dot: 'bg-sunken',                ring: 'ring-border-subtle',           icon: 'text-ink-secondary',     edge: 'border-l-border' },
  draft:      { dot: 'bg-accent-muted',          ring: 'ring-status-draft/40',         icon: 'text-status-draft',      edge: 'border-l-status-draft' },
  review:     { dot: 'bg-[#DAEFE9]',             ring: 'ring-status-review/40',        icon: 'text-status-review',     edge: 'border-l-status-review' },
  signed:     { dot: 'bg-[#DCE6F5]',             ring: 'ring-status-signed/40',        icon: 'text-status-signed',     edge: 'border-l-status-signed' },
  scaffolded: { dot: 'bg-[#FBF1D9]',             ring: 'ring-status-scaffolded/40',    icon: 'text-status-scaffolded', edge: 'border-l-status-scaffolded' },
  failed:     { dot: 'bg-[#F4D8D7]',             ring: 'ring-status-failed/40',        icon: 'text-status-failed',     edge: 'border-l-status-failed' },
  superseded: { dot: 'bg-sunken',                ring: 'ring-status-superseded/40',    icon: 'text-status-superseded', edge: 'border-l-status-superseded' },
};

export function AuditTrailPage() {
  const { id = '' } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const filterType = searchParams.get('type') ?? '';
  const filterActor = searchParams.get('actor') ?? '';

  const audit = useQuery({
    queryKey: ['audit', 'spec', id],
    queryFn: () => auditApi.forSpec(id),
    enabled: !!id,
    refetchInterval: 5_000,
  });

  const filtered = useMemo(() => {
    if (!audit.data) return [];
    return audit.data.data.filter((e) => {
      if (filterType && e.eventType !== filterType) return false;
      if (filterActor && e.actorPersona !== filterActor) return false;
      return true;
    });
  }, [audit.data, filterType, filterActor]);

  const grouped = useMemo(() => groupByDate(filtered), [filtered]);

  if (audit.isPending) {
    return <div className="mx-auto max-w-[1100px] space-y-4 p-6 lg:p-10"><Skeleton className="h-12 w-64" /><Skeleton className="h-[600px] w-full" /></div>;
  }
  if (audit.isError) {
    return <div className="mx-auto max-w-[1100px] p-6 lg:p-10"><ErrorBlock title="Could not load audit trail" message={audit.error.message} onRetry={() => audit.refetch()} /></div>;
  }

  const setFilter = (key: 'type' | 'actor', value: string) => {
    const next = new URLSearchParams(searchParams);
    if (!value) next.delete(key); else next.set(key, value);
    setSearchParams(next, { replace: true });
  };

  return (
    <div className="mx-auto max-w-[1100px] space-y-6 p-6 lg:p-10">
      <header>
        <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">Provenance</p>
        <h1 className="mt-2 text-display font-semibold text-ink-primary">Audit trail</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Every event recorded for this specification, in order. Records are never edited or deleted.
        </p>
      </header>

      {/* Phase C UX polish #2: evidence-trail panel as the page hero. */}
      <EvidenceTrail specId={id} />

      <Card>
        <CardHeader title="Filters" description="Narrow by event type or who performed it." />
        <CardBody className="flex flex-wrap items-center gap-3">
          <FilterGroup
            label="Type"
            value={filterType}
            options={[
              { v: '', label: 'all' },
              { v: 'spec.extracted', label: 'extracted' },
              { v: 'spec.routed', label: 'routed' },
              { v: 'claim.accept', label: 'accept' },
              { v: 'claim.edit', label: 'edit' },
              { v: 'claim.reject', label: 'reject' },
              { v: 'spec.signed', label: 'signed' },
            ]}
            onChange={(v) => setFilter('type', v)}
          />
          <FilterGroup
            label="Actor"
            value={filterActor}
            options={[
              { v: '', label: 'all' },
              { v: 'engineer', label: 'engineer' },
              { v: 'sme', label: 'sme' },
              { v: 'system', label: 'system' },
            ]}
            onChange={(v) => setFilter('actor', v)}
          />
          <span className="ml-auto font-mono text-caption text-ink-tertiary">
            {filtered.length} of {audit.data.data.length} events
          </span>
        </CardBody>
      </Card>

      {grouped.length === 0 ? (
        <Card><CardBody>
          <p className="py-8 text-center text-ink-tertiary">No events match the current filters.</p>
        </CardBody></Card>
      ) : (
        <ol className="relative space-y-8 pl-6">
          {/* The vertical rail — slight gradient from neutral to a hint of accent. */}
          <span
            className="absolute left-2 top-1 bottom-1 w-[2px] rounded-full bg-gradient-to-b from-border to-border-subtle"
            aria-hidden="true"
          />
          {grouped.map(([date, events]) => (
            <li key={date}>
              <h3 className="mb-3 -ml-6 text-caption font-medium uppercase tracking-wider text-ink-tertiary">
                <HistoryIcon className="mr-1 inline h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
                {date}
              </h3>
              <ol className="space-y-3">
                {events.map((e) => <li key={e.id}><EventCard event={e} /></li>)}
              </ol>
            </li>
          ))}
        </ol>
      )}

      {/* Phase C.7: spec-level discussion thread alongside the audit trail. */}
      <Card>
        <CardHeader title="Discussion" description="@-mention a teammate to notify them." />
        <CardBody>
          <CommentsThread specId={id} emptyHint="No discussion yet. Open with a question or context for the next reviewer." />
        </CardBody>
      </Card>
    </div>
  );
}

function FilterGroup({ label, value, options, onChange }: {
  label: string;
  value: string;
  options: { v: string; label: string }[];
  onChange: (v: string) => void;
}) {
  return (
    <div className="flex items-center gap-1.5">
      <span className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">{label}</span>
      <div className="flex flex-wrap gap-1">
        {options.map((o) => (
          <button
            key={o.v}
            type="button"
            onClick={() => onChange(o.v)}
            className={clsx(
              'rounded-sm border px-2 py-0.5 font-mono text-caption transition-colors duration-fast',
              value === o.v
                ? 'border-ink-primary bg-ink-primary text-ink-inverse'
                : 'border-border-subtle bg-canvas text-ink-secondary hover:bg-sunken',
            )}
          >
            {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

function EventCard({ event }: { event: AuditEvent }) {
  const meta = EVENT_META[event.eventType] ?? {
    label: event.eventType,
    icon: HistoryIcon,
    tone: 'neutral' as ToneKey,
  };
  const Icon = meta.icon;
  const styles = TONE_STYLES[meta.tone];
  return (
    <div className={clsx('relative', meta.branch && 'pl-4')}>
      {/* Optional branch fork — only rendered for re-sync supersession + carry-forward + reingest. */}
      {meta.branch && (
        <span
          aria-hidden="true"
          className={clsx('absolute -left-[24px] top-3 h-px w-6 bg-current', styles.icon)}
        />
      )}
      {/* Colored dot offset over the timeline rail. */}
      <span
        className={clsx(
          'absolute -left-[26px] top-2 flex h-5 w-5 items-center justify-center rounded-full ring-2',
          styles.dot,
          styles.ring,
        )}
        aria-hidden="true"
      >
        <Icon className={clsx('h-3 w-3', styles.icon)} />
      </span>
      <Card className={clsx('border-l-2', styles.edge)}>
        <CardBody className="space-y-2">
          <header className="flex flex-wrap items-baseline justify-between gap-2">
            <h4 className="font-semibold text-ink-primary">{meta.label}</h4>
            <span className="font-mono text-caption text-ink-tertiary">{new Date(event.occurredAt).toLocaleTimeString()}</span>
          </header>
          <p className="font-mono text-caption text-ink-secondary">
            <span className="rounded-sm bg-sunken px-1.5 py-0.5 uppercase">{event.actorPersona}</span>
            <span className="ml-2">{event.actorDisplay}</span>
          </p>
          {event.payload && Object.keys(event.payload).length > 0 && (
            <details className="text-caption">
              <summary className="cursor-pointer font-mono text-ink-tertiary hover:text-ink-secondary">Payload</summary>
              <pre className="mt-2 overflow-x-auto rounded-md bg-codebg p-3 font-mono text-[11px] text-ink-inverse">
                {JSON.stringify(event.payload, null, 2)}
              </pre>
            </details>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

function groupByDate(events: AuditEvent[]): [string, AuditEvent[]][] {
  const map = new Map<string, AuditEvent[]>();
  for (const e of events) {
    const date = new Date(e.occurredAt).toLocaleDateString();
    if (!map.has(date)) map.set(date, []);
    map.get(date)!.push(e);
  }
  return Array.from(map.entries());
}
