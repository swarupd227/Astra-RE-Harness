import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { FileText, X, ChevronRight } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type PromptSummary } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Prompt Catalog — value-add #2 in the Nous platform pitch.
 *
 * Surfaces the externalised prompt library that ships with the harness:
 * one calibrated prompt per (source schema × target stack × kind), each
 * versioned and authored. Buyers can browse the full asset and click
 * into the rendered system + user template to verify substance over
 * a vague "we have calibrated prompts" claim.
 *
 * Wraps the existing GET /api/v1/prompts and /prompts/{src}/{tgt}/{kind}
 * endpoints from Phase #3b.
 */
export function PromptCatalogPage() {
  const list = useQuery({
    queryKey: ['prompts'],
    queryFn: api.listPrompts,
    staleTime: 5 * 60_000,
  });

  const [source, setSource] = useState<string>('all');
  const [target, setTarget] = useState<string>('all');
  const [kind, setKind] = useState<string>('all');
  const [open, setOpen] = useState<PromptSummary | null>(null);

  const filtered = useMemo(() => {
    if (!list.data) return [];
    return list.data.data.filter((p) => {
      if (source !== 'all' && p.sourceSchema !== source) return false;
      if (target !== 'all' && p.targetStack !== target) return false;
      if (kind !== 'all' && p.kind !== kind) return false;
      return true;
    });
  }, [list.data, source, target, kind]);

  const sources = useMemo(() => uniq(list.data?.data.map((p) => p.sourceSchema) ?? []), [list.data]);
  const targets = useMemo(() => uniq(list.data?.data.map((p) => p.targetStack) ?? []), [list.data]);
  const kinds = useMemo(() => uniq(list.data?.data.map((p) => p.kind) ?? []), [list.data]);

  if (list.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
          <Skeleton className="h-40" /><Skeleton className="h-40" /><Skeleton className="h-40" />
        </div>
      </div>
    );
  }
  if (list.isError || !list.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load prompt library" message={String(list.error)} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10" data-testid="prompt-catalog-page">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase #3b · Prompt asset library
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">Prompt Catalog</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Calibrated prompts shipped per source language × target stack × kind.
          Every Claude call records the exact prompt id @ version it used, so
          spec provenance carries forward into every audit pull.
        </p>
      </header>

      <Card>
        <CardBody className="flex flex-wrap items-end gap-4">
          <Filter label="Source schema" value={source} onChange={setSource} options={['all', ...sources]} />
          <Filter label="Target stack" value={target} onChange={setTarget} options={['all', ...targets]} />
          <Filter label="Kind" value={kind} onChange={setKind} options={['all', ...kinds]} />
          <span className="ml-auto font-mono text-caption text-ink-tertiary">
            {filtered.length} of {list.data.data.length} prompts
          </span>
        </CardBody>
      </Card>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
        {filtered.map((p) => (
          <PromptCard key={`${p.sourceSchema}/${p.targetStack}/${p.kind}/${p.version}`} prompt={p} onOpen={() => setOpen(p)} />
        ))}
      </div>

      {open && <PromptDetailDrawer prompt={open} onClose={() => setOpen(null)} />}
    </div>
  );
}

function PromptCard({ prompt, onOpen }: { prompt: PromptSummary; onOpen: () => void }) {
  const status = prompt.status ?? 'production';
  const isProd = status.toLowerCase().includes('production');
  return (
    <Card
      interactive
      onClick={onOpen}
      data-testid={`prompt-card-${prompt.sourceSchema}-${prompt.targetStack}-${prompt.kind}-${prompt.version}`}
    >
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <FileText className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
            {prompt.promptId}
          </span>
        }
        description={
          <span className="font-mono text-[11px] text-ink-tertiary">
            {prompt.sourceSchema} → {prompt.targetStack} · {prompt.kind} @ {prompt.version}
          </span>
        }
      />
      <CardBody className="space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone={isProd ? 'success' : 'neutral'}>{status}</Badge>
          {prompt.modelPreference && (
            <Badge tone="neutral">model: {prompt.modelPreference}</Badge>
          )}
        </div>
        {prompt.owner && (
          <div className="font-mono text-caption text-ink-tertiary">owner: {prompt.owner}</div>
        )}
        <div className="inline-flex items-center gap-1 text-caption text-accent">
          View body <ChevronRight className="h-3.5 w-3.5" aria-hidden="true" />
        </div>
      </CardBody>
    </Card>
  );
}

function PromptDetailDrawer({ prompt, onClose }: { prompt: PromptSummary; onClose: () => void }) {
  const q = useQuery({
    queryKey: ['prompt-detail', prompt.sourceSchema, prompt.targetStack, prompt.kind, prompt.version],
    queryFn: () => api.getPrompt(prompt.sourceSchema, prompt.targetStack, prompt.kind, prompt.version),
  });

  return (
    <div
      className="fixed inset-0 z-50 flex items-stretch justify-end"
      role="dialog"
      aria-modal="true"
      aria-labelledby="prompt-drawer-title"
      onClick={onClose}
      data-testid="prompt-detail-drawer"
    >
      <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
      <div
        className="relative ml-auto flex h-full w-full max-w-[800px] flex-col bg-raised shadow-e3"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-start justify-between gap-4 border-b border-border-subtle px-6 py-4">
          <div>
            <h3 id="prompt-drawer-title" className="text-h-md font-semibold text-ink-primary">
              {prompt.promptId} <span className="text-ink-tertiary">@ {prompt.version}</span>
            </h3>
            <p className="mt-1 font-mono text-caption text-ink-tertiary">
              {prompt.sourceSchema} → {prompt.targetStack} · {prompt.kind} · {prompt.path}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary"
            aria-label="Close"
          >
            <X className="h-4 w-4" aria-hidden="true" />
          </button>
        </header>
        <div className="flex-1 overflow-y-auto p-6">
          {q.isPending && <Skeleton className="h-[400px] w-full" />}
          {q.isError && <ErrorBlock title="Could not load prompt body" message={String(q.error)} />}
          {q.data && (
            <div className="space-y-6">
              <FrontmatterTable rows={q.data.frontmatter} />
              <TemplateBlock label="System template" body={q.data.systemTemplate} />
              <TemplateBlock label="User template" body={q.data.userTemplate} />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function FrontmatterTable({ rows }: { rows: Record<string, string> }) {
  const entries = Object.entries(rows ?? {});
  if (entries.length === 0) return null;
  return (
    <div>
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">Frontmatter</div>
      <div className="mt-2 overflow-hidden rounded-md border border-border-subtle">
        <table className="w-full text-body">
          <tbody>
            {entries.map(([k, v]) => (
              <tr key={k} className="border-t border-border-subtle first:border-t-0">
                <td className="bg-sunken/40 px-3 py-1.5 font-mono text-caption text-ink-secondary">{k}</td>
                <td className="px-3 py-1.5 text-ink-primary">{v}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function TemplateBlock({ label, body }: { label: string; body: string }) {
  return (
    <div>
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</div>
      <pre className="mt-2 max-h-[40vh] overflow-auto rounded-md border border-border-subtle bg-sunken/40 p-3 font-mono text-[12px] leading-relaxed text-ink-primary">
        {body}
      </pre>
    </div>
  );
}

function Filter({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: string[];
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className={clsx(
          'rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary',
          'focus:border-accent focus:outline-none',
        )}
      >
        {options.map((o) => (
          <option key={o} value={o}>{o}</option>
        ))}
      </select>
    </label>
  );
}

function uniq<T>(xs: T[]): T[] {
  return [...new Set(xs)];
}
