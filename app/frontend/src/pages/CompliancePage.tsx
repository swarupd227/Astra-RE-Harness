import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Download, FileSpreadsheet, FileText, ShieldCheck } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type ComplianceFormat } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
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
export function CompliancePage() {
  const formats = useQuery({
    queryKey: ['compliance-formats'],
    queryFn: api.listComplianceFormats,
    staleTime: 5 * 60_000,
  });

  const [selectedId, setSelectedId] = useState<string>('sox');
  const [since, setSince] = useState<string>('');
  const [until, setUntil] = useState<string>('');
  const [severity, setSeverity] = useState<string>('');
  const [limit, setLimit] = useState<string>('100');

  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);
  const [lastDownload, setLastDownload] = useState<{ fileName: string; rowCount: number } | null>(null);

  const selected: ComplianceFormat | undefined = useMemo(() => {
    return formats.data?.data.find((f) => f.id === selectedId) ?? formats.data?.data[0];
  }, [formats.data, selectedId]);

  const onDownload = async () => {
    if (!selected) return;
    setDownloading(true);
    setDownloadError(null);
    try {
      const { blob, fileName, rowCount } = await api.downloadComplianceFeed({
        format: selected.id,
        since: toIsoMaybe(since),
        until: toIsoMaybe(until),
        severity: severity || undefined,
        limit: limit ? Number(limit) : undefined,
      });
      // Synthesise a download click so the browser saves the file.
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = fileName;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
      setLastDownload({ fileName, rowCount });
    } catch (e) {
      setDownloadError(e instanceof Error ? e.message : String(e));
    } finally {
      setDownloading(false);
    }
  };

  if (formats.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (formats.isError || !formats.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load compliance formats" message={String(formats.error)} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup" data-testid="compliance-page">
      <PageHero
        tone="amber"
        eyebrow="Governance"
        title="Audit & compliance feed"
        lead="Export the audit log as a SOX, HIPAA, or PCI evidence bundle. Every export is itself recorded in the log."
      />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        {formats.data.data.map((f) => (
          <FormatCard
            key={f.id}
            format={f}
            selected={f.id === selectedId}
            onSelect={() => setSelectedId(f.id)}
          />
        ))}
      </div>

      {selected && (
        <Card>
          <CardHeader
            title={
              <span className="inline-flex items-center gap-2">
                <FileSpreadsheet className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
                Export {selected.displayName}
              </span>
            }
            description={selected.description}
          />
          <CardBody className="space-y-6">
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
              <FilterField label="Since (UTC)" type="datetime-local" value={since} onChange={setSince} />
              <FilterField label="Until (UTC)" type="datetime-local" value={until} onChange={setUntil} />
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
              <FilterField label="Row limit" type="number" value={limit} onChange={setLimit} />
            </div>

            <ColumnTable columns={selected.columns} />

            <div className="flex flex-wrap items-center gap-3">
              <Button
                variant="primary"
                onClick={onDownload}
                loading={downloading}
                data-testid="compliance-download"
              >
                <Download className="h-4 w-4" />
                Download {selected.id.toUpperCase()} CSV
              </Button>
              {lastDownload && (
                <span className="inline-flex items-center gap-2 text-caption text-ink-secondary">
                  <ShieldCheck className="h-3.5 w-3.5 text-status-review" aria-hidden="true" />
                  Last export: <span className="font-mono text-ink-primary">{lastDownload.fileName}</span> · {lastDownload.rowCount} rows
                </span>
              )}
            </div>

            {downloadError && (
              <ErrorBlock title="Download failed" message={downloadError} />
            )}
          </CardBody>
        </Card>
      )}
    </div>
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
          ? 'border-accent bg-accent-muted shadow-e1'
          : 'border-border-subtle bg-raised hover:border-border hover:shadow-e1',
      )}
    >
      <div className="flex w-full items-center justify-between gap-2">
        <span className="inline-flex items-center gap-1.5 text-h-sm font-semibold text-ink-primary">
          <FileText className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
          {format.displayName}
        </span>
        <Badge tone={isProduction ? 'success' : 'neutral'}>
          {isProduction ? 'Production' : 'Adapter'}
        </Badge>
      </div>
      <span className="text-body text-ink-secondary">{format.description}</span>
      <span className="font-mono text-caption text-ink-tertiary">
        {format.columnCount} columns · {format.extension}
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
          className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20"
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
          className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 font-mono text-body text-ink-primary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20"
        />
      )}
    </label>
  );
}

function ColumnTable({ columns }: { columns: { id: string; header: string; description: string }[] }) {
  return (
    <div>
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">Columns in this bundle</div>
      <div className="mt-2 overflow-x-auto rounded-md border border-border-subtle">
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
              <tr key={c.id} className="border-t border-border-subtle">
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

function toIsoMaybe(local: string): string | undefined {
  if (!local) return undefined;
  // <input type=datetime-local> gives "YYYY-MM-DDTHH:mm" without timezone.
  // Treat as UTC for the export filter; auditors live in UTC anyway.
  return `${local}:00Z`;
}
