import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Download, FileSpreadsheet, FileText, ShieldCheck } from 'lucide-react';
import { clsx } from 'clsx';
import { Link } from 'react-router-dom';
import { api, complianceApi, type ComplianceFormat } from '@/lib/api';
import { absoluteTime, formatInt, relativeTime } from '@/workspace/format';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';

/**
 * Compliance page — value-add #6 in the Nous platform pitch.
 *
 * Wraps the existing GET /api/v1/compliance/{formats,feed} API in a
 * filterable + downloadable surface. Three audit-bundles ship today:
 * SOX as the production reference shape, HIPAA + PCI as adapter
 * starting points for engagement-time customisation. Every export is
 * itself logged as a `compliance.feed_exported` audit row (meta-audit),
 * which is why this page is safe to leave self-service for the auditor
 * persona.
 */
type Range = 'all' | '7d' | 'quarter' | 'lastQuarter' | 'custom';

function rangeBounds(r: Range, since: string, until: string): { since?: string; until?: string } {
  const now = new Date();
  const qStart = (d: Date) => new Date(d.getFullYear(), Math.floor(d.getMonth() / 3) * 3, 1);
  switch (r) {
    case '7d':
      return { since: new Date(now.getTime() - 7 * 864e5).toISOString() };
    case 'quarter':
      return { since: qStart(now).toISOString() };
    case 'lastQuarter': {
      const thisQ = qStart(now);
      const lastQ = new Date(thisQ.getFullYear(), thisQ.getMonth() - 3, 1);
      return { since: lastQ.toISOString(), until: new Date(thisQ.getTime() - 1).toISOString() };
    }
    case 'custom':
      // <input type=datetime-local> is the viewer's local time; send the instant it means.
      return {
        since: since ? new Date(since).toISOString() : undefined,
        until: until ? new Date(until).toISOString() : undefined,
      };
    default:
      return {};
  }
}

function tzLabel(): string {
  const m = -new Date().getTimezoneOffset();
  const sign = m >= 0 ? '+' : '-';
  const a = Math.abs(m);
  return `UTC${sign}${String(Math.floor(a / 60)).padStart(2, '0')}:${String(a % 60).padStart(2, '0')}`;
}

/** "spec.signed" → "Spec signed"; "compliance.feed_exported" → "Compliance feed exported". */
/** The kind of thing an event was about, in words. */
const TARGET_KIND: Record<string, string> = {
  spec: 'Spec',
  scaffold: 'Generated code',
  corpus: 'Project',
  subroutine: 'Code unit',
  conversation: 'Conversation',
  settings: 'Settings',
};

function eventLabel(t: string): string {
  const s = t.replace(/[._]/g, ' ');
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/**
 * Compliance & audit: what happened in a range (sign-offs, commits, validation bypasses, exports) at a glance,
 * the events themselves, and the evidence export — by default every matching event, with the count on the button.
 * Every export is itself an audit event, and shows up in the list.
 */
export function CompliancePage() {
  const formats = useQuery({
    queryKey: ['compliance-formats'],
    queryFn: api.listComplianceFormats,
    staleTime: 5 * 60_000,
  });

  const [selectedId, setSelectedId] = useState<string>('sox');
  const [range, setRange] = useState<Range>('quarter');
  const [since, setSince] = useState<string>('');
  const [until, setUntil] = useState<string>('');
  const [severity, setSeverity] = useState<string>('');
  const [limit, setLimit] = useState<string>('');
  const [typeFilter, setTypeFilter] = useState<string>('');
  const [page, setPage] = useState(0);

  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [lastDownload, setLastDownload] = useState<{ fileName: string; rowCount: number } | null>(null);

  const bounds = useMemo(() => rangeBounds(range, since, until), [range, since, until]);
  const summary = useQuery({
    queryKey: ['compliance-summary', bounds],
    queryFn: () => complianceApi.summary(bounds),
  });
  const events = useQuery({
    queryKey: ['compliance-events', bounds, typeFilter, page],
    queryFn: () => complianceApi.events({ ...bounds, type: typeFilter || undefined, limit: 25, offset: page * 25 }),
    placeholderData: (prev) => prev,
  });

  const selected: ComplianceFormat | undefined = useMemo(() => {
    return formats.data?.data.find((f) => f.id === selectedId) ?? formats.data?.data[0];
  }, [formats.data, selectedId]);

  // Validate before the request fires: an inverted range or a non-positive cap would produce a confusing file.
  const validationError = useMemo(() => {
    if (range === 'custom' && since && until && new Date(since).getTime() > new Date(until).getTime()) {
      return '"From" is after "To" — no events can match this range.';
    }
    if (limit) {
      const n = Number(limit);
      if (!Number.isFinite(n) || n <= 0) return 'Row cap must be a positive number, or empty for every matching event.';
    }
    return null;
  }, [range, since, until, limit]);

  const onDownload = async () => {
    if (!selected || validationError) return;
    setDownloading(true);
    setDownloadError(null);
    try {
      const { blob, fileName, rowCount } = await api.downloadComplianceFeed({
        format: selected.id,
        since: bounds.since,
        until: bounds.until,
        severity: severity || undefined,
        limit: limit ? Number(limit) : undefined,
      });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
      setLastDownload({ fileName, rowCount });
      summary.refetch();
      events.refetch();
    } catch (e) {
      setDownloadError(e instanceof Error ? e.message : String(e));
    } finally {
      setDownloading(false);
    }
  };

  if (formats.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-4 lg:p-6">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (formats.isError || !formats.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-4 lg:p-6">
        <ErrorBlock title="Could not load compliance formats" message={String(formats.error)} />
      </div>
    );
  }

  const sm = summary.data;
  const matching = sm?.total;
  const cap = limit ? Number(limit) : null;
  const willExport = matching == null ? null : cap && cap > 0 ? Math.min(cap, matching) : matching;
  const pickType = (t: string) => {
    setTypeFilter(t);
    setPage(0);
  };

  return (
    <div className="mx-auto max-w-[1200px] space-y-5 p-4 lg:p-6 fadeup" data-testid="compliance-page">
      <PageHero
        tone="amber"
        eyebrow="Quality"
        title="Compliance & audit"
        lead="What happened, who did it and when — and the evidence bundle for your auditor. Every export is itself recorded."
      />

      {/* Range */}
      <div className="flex flex-wrap items-end gap-2">
        <div className="flex flex-wrap gap-1.5" role="group" aria-label="Date range">
          {(
            [
              ['7d', 'Last 7 days'],
              ['quarter', 'This quarter'],
              ['lastQuarter', 'Last quarter'],
              ['all', 'All time'],
              ['custom', 'Custom'],
            ] as Array<[Range, string]>
          ).map(([id, label]) => (
            <button
              key={id}
              type="button"
              aria-pressed={range === id}
              onClick={() => {
                setRange(id);
                setPage(0);
              }}
              className={clsx(
                'rounded-full border px-3 py-1 text-caption transition-colors',
                range === id ? 'border-volt/50 bg-volt/10 text-ink-primary' : 'border-line-subtle text-ink-secondary hover:border-line hover:text-ink-primary',
              )}
              data-testid={`compliance-range-${id}`}
            >
              {label}
            </button>
          ))}
        </div>
        {range === 'custom' && (
          <div className="flex flex-wrap items-end gap-2">
            <FilterField label={`From (your time, ${tzLabel()})`} type="datetime-local" value={since} onChange={setSince} />
            <FilterField label={`To (your time, ${tzLabel()})`} type="datetime-local" value={until} onChange={setUntil} />
          </div>
        )}
      </div>

      {/* Overview */}
      <ul className="grid grid-cols-2 gap-3 md:grid-cols-5" aria-label="In this range">
        <Stat label="Events" value={sm?.total} onClick={() => pickType('')} active={!typeFilter} testid="compliance-stat-events" />
        <Stat label="Specs signed" value={sm?.signed} onClick={() => pickType('spec.signed')} active={typeFilter === 'spec.signed'} testid="compliance-stat-signed" />
        <Stat label="Code committed" value={sm?.committed} onClick={() => pickType('scaffold.committed')} active={typeFilter === 'scaffold.committed'} testid="compliance-stat-committed" />
        <Stat
          label="Validation bypassed"
          value={sm?.validationBypassed}
          warn={(sm?.validationBypassed ?? 0) > 0}
          hint="Commits made although a validation gate had failed"
          onClick={() => pickType('scaffold.committed')}
          active={false}
          testid="compliance-stat-bypassed"
        />
        <Stat label="Exports" value={sm?.exports} onClick={() => pickType('compliance.feed_exported')} active={typeFilter === 'compliance.feed_exported'} testid="compliance-stat-exports" />
      </ul>

      {/* Events */}
      <section className="rounded-xl border border-line-subtle bg-raised" aria-label="Events">
        <div className="flex flex-wrap items-center gap-2 border-b border-line-subtle px-4 py-2.5">
          <h2 className="text-body font-semibold text-ink-primary">Events</h2>
          <select
            value={typeFilter}
            onChange={(e) => pickType(e.target.value)}
            aria-label="Event type"
            className="ml-auto h-8 rounded-md border border-line-subtle bg-canvas px-2 text-caption text-ink-primary focus:outline-none focus-visible:ring-2 focus-visible:ring-volt"
            data-testid="compliance-event-type"
          >
            <option value="">All event types</option>
            {(sm?.byType ?? []).map((t) => (
              <option key={t.type} value={t.type}>
                {eventLabel(t.type)} ({t.count})
              </option>
            ))}
          </select>
        </div>
        {events.isPending ? (
          <div className="space-y-2 p-4">
            <Skeleton className="h-8 w-full" />
            <Skeleton className="h-8 w-full" />
          </div>
        ) : events.isError ? (
          <div className="p-4">
            <ErrorBlock title="Could not load events" message={events.error.message} onRetry={() => events.refetch()} />
          </div>
        ) : (events.data?.data.length ?? 0) === 0 ? (
          <p className="px-4 py-6 text-center text-caption text-ink-secondary">No events in this range.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[720px] border-collapse text-caption" data-testid="compliance-events">
              <thead>
                <tr className="text-micro uppercase tracking-wide text-ink-secondary">
                  <th scope="col" className="px-4 py-2 text-left font-semibold">When</th>
                  <th scope="col" className="px-3 py-2 text-left font-semibold">Event</th>
                  <th scope="col" className="px-3 py-2 text-left font-semibold">Who</th>
                  <th scope="col" className="px-3 py-2 text-left font-semibold">On</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-line-subtle">
                {events.data!.data.map((e) => (
                  <tr key={e.id} className="hover:bg-sunken">
                    <td className="whitespace-nowrap px-4 py-1.5 text-ink-secondary" title={absoluteTime(e.occurredAt)}>
                      {relativeTime(e.occurredAt)}
                    </td>
                    <td className="px-3 py-1.5 text-ink-primary">
                      {eventLabel(e.eventType)}
                      {(e.payload as { validationBypassed?: boolean })?.validationBypassed === true && (
                        <Badge tone="failed" className="ml-2 text-[10px]">gate bypassed</Badge>
                      )}
                    </td>
                    <td className="px-3 py-1.5 text-ink-secondary">{e.actorDisplay}</td>
                    <td className="max-w-[320px] truncate px-3 py-1.5 text-ink-secondary" data-testid="audit-target">
                      {/* What it was about, in words, linked to the code unit when there is one. */}
                      <span className="text-ink-tertiary">{TARGET_KIND[e.targetType] ?? e.targetType}</span>
                      {(() => {
                        const name =
                          e.targetName ??
                          (typeof (e.payload as { subroutineName?: string })?.subroutineName === 'string'
                            ? (e.payload as { subroutineName: string }).subroutineName
                            : null);
                        if (!name) return null;
                        return (
                          <>
                            {' · '}
                            {e.subroutineId ? (
                              <Link to={`/subroutines/${e.subroutineId}`} className="font-mono text-ink-primary hover:underline" title={name}>
                                {name}
                              </Link>
                            ) : (
                              <span className="text-ink-primary" title={name}>{name}</span>
                            )}
                          </>
                        );
                      })()}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="flex items-center justify-between border-t border-line-subtle px-4 py-2 text-caption text-ink-secondary">
          <span>Page {page + 1}</span>
          <span className="flex gap-1">
            <button type="button" disabled={page === 0} onClick={() => setPage((p) => p - 1)} className="rounded-md border border-line-subtle px-2 py-0.5 disabled:opacity-40">
              Newer
            </button>
            <button
              type="button"
              disabled={(events.data?.data.length ?? 0) < 25}
              onClick={() => setPage((p) => p + 1)}
              className="rounded-md border border-line-subtle px-2 py-0.5 disabled:opacity-40"
            >
              Older
            </button>
          </span>
        </div>
      </section>

      {/* Export */}
      <section className="space-y-4 rounded-xl border border-line-subtle bg-raised p-4" aria-label="Export evidence">
        <h2 className="flex items-center gap-2 text-body font-semibold text-ink-primary">
          <FileSpreadsheet className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
          Export evidence for this range
        </h2>
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-3">
          {formats.data.data.map((f) => (
            <FormatCard key={f.id} format={f} selected={f.id === selectedId} onSelect={() => setSelectedId(f.id)} />
          ))}
        </div>
        {selected && (
          <>
            <div className="flex flex-wrap items-end gap-3">
              <FilterField
                label="Severity"
                type="select"
                value={severity}
                onChange={setSeverity}
                options={[
                  { label: 'Any', value: '' },
                  { label: 'High', value: 'high' },
                  { label: 'Medium', value: 'medium' },
                  { label: 'Low', value: 'low' },
                ]}
              />
              <FilterField label="Row cap (empty = all)" type="number" value={limit} onChange={setLimit} />
              <Button
                variant="primary"
                onClick={onDownload}
                loading={downloading}
                disabled={!!validationError}
                data-testid="compliance-download"
              >
                <Download className="h-4 w-4" />
                Download {selected.id.toUpperCase()} CSV
                {willExport != null && !severity && ` · ${formatInt(willExport)} event${willExport === 1 ? '' : 's'}`}
              </Button>
              {lastDownload && (
                <span className="inline-flex items-center gap-2 text-caption text-ink-secondary">
                  <ShieldCheck className="h-3.5 w-3.5 text-status-review" aria-hidden="true" />
                  Last export: <span className="font-mono text-ink-primary">{lastDownload.fileName}</span> · {lastDownload.rowCount} rows
                </span>
              )}
            </div>
            {validationError && (
              <p className="text-caption text-status-failed" data-testid="compliance-validation-error">
                {validationError}
              </p>
            )}
            {downloadError && <ErrorBlock title="Download failed" message={downloadError} />}
            <details className="text-caption">
              <summary className="cursor-pointer text-ink-secondary hover:text-ink-primary">
                Columns in this bundle ({selected.columns.length})
              </summary>
              <div className="mt-2">
                <ColumnTable columns={selected.columns} />
              </div>
            </details>
          </>
        )}
      </section>
    </div>
  );
}

function Stat({
  label,
  value,
  onClick,
  active,
  warn,
  hint,
  testid,
}: {
  label: string;
  value: number | undefined;
  onClick: () => void;
  active: boolean;
  warn?: boolean;
  hint?: string;
  testid: string;
}) {
  return (
    <li>
      <button
        type="button"
        onClick={onClick}
        title={hint}
        aria-pressed={active}
        className={clsx(
          'block w-full rounded-xl border px-4 py-3 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
          warn ? 'border-status-failed/50 bg-status-fail/10' : active ? 'border-volt/50 bg-volt/5' : 'border-line-subtle bg-raised hover:border-line-strong',
        )}
        data-testid={testid}
      >
        <span className="block text-caption text-ink-secondary">{label}</span>
        <span className={clsx('mt-0.5 block font-mono text-h-md font-semibold tabular-nums', warn ? 'text-status-failed' : 'text-ink-primary')}>
          {value == null ? '…' : formatInt(value)}
        </span>
      </button>
    </li>
  );
}

function FormatCard({
  format,
  selected,
  onSelect,
}: {
  format: ComplianceFormat;
  selected: boolean;
  onSelect: () => void;
}) {
  const isProduction = format.status.toLowerCase().includes('production');
  return (
    <button
      type="button"
      onClick={onSelect}
      aria-pressed={selected}
      data-testid={`compliance-format-${format.id}`}
      className={clsx(
        'flex flex-col items-start gap-2 rounded-md border p-4 text-left transition-all',
        selected
          ? 'border-volt bg-volt/10 shadow-e1'
          : 'border-line-subtle bg-raised hover:border-line hover:shadow-e1',
      )}
    >
      <div className="flex w-full items-center justify-between gap-2">
        <span className="inline-flex items-center gap-1.5 text-h-sm font-semibold text-ink-primary">
          <FileText className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
          {format.displayName}
        </span>
        <span title={isProduction ? 'Ready to hand to an auditor as is' : 'A starting point: map its fields to the engagement before handing it over'}>
          <Badge tone={isProduction ? 'success' : 'neutral'}>{isProduction ? 'Production' : 'Template'}</Badge>
        </span>
      </div>
      <span className="line-clamp-2 text-caption text-ink-secondary" title={format.description}>
        {format.description}
      </span>
      <span className="font-mono text-micro text-ink-tertiary">
        {format.columnCount} columns · {format.extension}
        {!isProduction && ' · template: adapt per engagement'}
      </span>
    </button>
  );
}

function FilterField({
  label,
  type,
  value,
  onChange,
  options,
}: {
  label: string;
  type: 'datetime-local' | 'number' | 'select';
  value: string;
  onChange: (v: string) => void;
  options?: { label: string; value: string }[];
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</span>
      {type === 'select' ? (
        <select
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className="rounded-md border border-line-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-volt focus:outline-none focus:ring-2 focus:ring-volt/20"
        >
          {options?.map((o) => (
            <option key={o.value} value={o.value}>{o.label}</option>
          ))}
        </select>
      ) : (
        <input
          type={type}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className="rounded-md border border-line-subtle bg-raised px-3 py-1.5 font-mono text-body text-ink-primary focus:border-volt focus:outline-none focus:ring-2 focus:ring-volt/20"
        />
      )}
    </label>
  );
}

function ColumnTable({ columns }: { columns: { id: string; header: string; description: string }[] }) {
  return (
    <div>
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">Columns in this bundle</div>
      <div
        className="mt-2 overflow-x-auto rounded-md border border-line-subtle"
        role="region"
        aria-label="Columns in this bundle"
        tabIndex={0}
      >
        <table className="w-full text-body">
          <thead className="bg-sunken/60 text-caption text-ink-tertiary">
            <tr>
              <th className="px-3 py-2 text-left font-medium">id</th>
              <th className="px-3 py-2 text-left font-medium">header</th>
              <th className="px-3 py-2 text-left font-medium">description</th>
            </tr>
          </thead>
          <tbody>
            {columns.map((c) => (
              <tr key={c.id} className="border-t border-line-subtle">
                <td className="px-3 py-1.5 font-mono text-caption text-ink-secondary">{c.id}</td>
                <td className="px-3 py-1.5 text-ink-primary">{c.header}</td>
                <td className="px-3 py-1.5 text-ink-secondary">{c.description}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
