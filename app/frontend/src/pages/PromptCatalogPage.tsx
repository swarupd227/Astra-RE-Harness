import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ChevronRight, FileText, Pencil, Plus, Save, Trash2, X } from 'lucide-react';
import { clsx } from 'clsx';
import { api, ApiError, type PromptSummary } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

const PROMPT_TEMPLATE = `---
id: example-extract
kind: extract
version: 1.0
status: preview
owner: Nous
modelPreference: claude-sonnet-4-5
notes: |
  Replace this body with the production prompt before promoting to status: production.
---

# System

You are an expert assistant. Produce a structured behavioural spec for the supplied source.

# User

Read the source below and emit a JSON object with invariants, side_effects, edge_cases, open_questions.

\`\`\`
{{sourceCode}}
\`\`\`
`;

/**
 * Prompt Catalog — value-add #2 in the Nous platform pitch.
 *
 * Phase #4.3 adds admin CRUD on top of the existing browse surface:
 *   - "New version" button → modal with form + markdown editor
 *   - Drawer "Edit" mode → in-place markdown editor + save
 *   - Drawer "Delete" button with confirm
 *
 * Wraps GET /api/v1/prompts (Phase #3b) and the POST / PUT / DELETE
 * mutations added in Phase #4.3.
 */
export function PromptCatalogPage() {
  const queryClient = useQueryClient();
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  const list = useQuery({
    queryKey: ['prompts'],
    queryFn: api.listPrompts,
    staleTime: 0,
  });

  const [source, setSource] = useState<string>('all');
  const [target, setTarget] = useState<string>('all');
  const [kind, setKind] = useState<string>('all');
  const [open, setOpen] = useState<PromptSummary | null>(null);
  const [creating, setCreating] = useState(false);

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
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
            Phase #3b · Prompt asset library
          </p>
          <h1 className="mt-1 text-display font-semibold text-ink-primary">Prompt Catalog</h1>
          <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
            Calibrated prompts shipped per source language × target stack × kind.
            Every Claude call records the exact prompt id @ version it used, so
            spec provenance carries forward into every audit pull.
          </p>
        </div>
        {isAdmin && (
          <Button variant="primary" size="sm" onClick={() => setCreating(true)} data-testid="prompt-new">
            <Plus className="h-4 w-4" />
            New version
          </Button>
        )}
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

      {open && (
        <PromptDetailDrawer
          prompt={open}
          isAdmin={isAdmin}
          onClose={() => setOpen(null)}
          onMutated={() => {
            queryClient.invalidateQueries({ queryKey: ['prompts'] });
          }}
        />
      )}

      {creating && (
        <NewPromptModal
          existing={list.data.data}
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            queryClient.invalidateQueries({ queryKey: ['prompts'] });
          }}
        />
      )}
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

function PromptDetailDrawer({
  prompt,
  isAdmin,
  onClose,
  onMutated,
}: {
  prompt: PromptSummary;
  isAdmin: boolean;
  onClose: () => void;
  onMutated: () => void;
}) {
  const q = useQuery({
    queryKey: ['prompt-detail', prompt.sourceSchema, prompt.targetStack, prompt.kind, prompt.version],
    queryFn: () => api.getPrompt(prompt.sourceSchema, prompt.targetStack, prompt.kind, prompt.version),
  });

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (editing && q.data?.body != null) setDraft(q.data.body);
  }, [editing, q.data]);

  const save = useMutation({
    mutationFn: () =>
      api.updatePrompt(prompt.sourceSchema, prompt.targetStack, prompt.kind, prompt.version, draft),
    onSuccess: () => {
      setEditing(false);
      setError(null);
      onMutated();
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : String(e)),
  });

  const remove = useMutation({
    mutationFn: () =>
      api.deletePrompt(prompt.sourceSchema, prompt.targetStack, prompt.kind, prompt.version),
    onSuccess: () => {
      onMutated();
      onClose();
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : String(e)),
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
          <div className="flex items-center gap-2">
            {isAdmin && !editing && (
              <>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => setEditing(true)}
                  data-testid="prompt-edit"
                >
                  <Pencil className="h-4 w-4" />
                  Edit
                </Button>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => {
                    if (window.confirm(`Delete ${prompt.promptId}@${prompt.version}? This removes the file from disk.`)) {
                      remove.mutate();
                    }
                  }}
                  loading={remove.isPending}
                  data-testid="prompt-delete"
                >
                  <Trash2 className="h-4 w-4" />
                  Delete
                </Button>
              </>
            )}
            {isAdmin && editing && (
              <>
                <Button
                  variant="primary"
                  size="sm"
                  onClick={() => save.mutate()}
                  loading={save.isPending}
                  data-testid="prompt-save"
                >
                  <Save className="h-4 w-4" />
                  Save
                </Button>
                <Button variant="secondary" size="sm" onClick={() => { setEditing(false); setError(null); }}>
                  Cancel
                </Button>
              </>
            )}
            <button
              type="button"
              onClick={onClose}
              className="rounded-md p-1.5 text-ink-secondary hover:bg-sunken hover:text-ink-primary"
              aria-label="Close"
            >
              <X className="h-4 w-4" aria-hidden="true" />
            </button>
          </div>
        </header>
        <div className="flex-1 overflow-y-auto p-6">
          {q.isPending && <Skeleton className="h-[400px] w-full" />}
          {q.isError && <ErrorBlock title="Could not load prompt body" message={String(q.error)} />}
          {error && <ErrorBlock title="Save failed" message={error} />}
          {q.data && !editing && (
            <div className="space-y-6">
              <FrontmatterTable rows={q.data.frontmatter} />
              <TemplateBlock label="System template" body={q.data.systemTemplate} />
              <TemplateBlock label="User template" body={q.data.userTemplate} />
            </div>
          )}
          {q.data && editing && (
            <div>
              <div className="text-caption uppercase tracking-wide text-ink-tertiary">
                Markdown body (frontmatter + # System + # User)
              </div>
              <textarea
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                rows={28}
                spellCheck={false}
                className="mt-2 w-full rounded-md border border-border-subtle bg-sunken/40 p-3 font-mono text-[12px] leading-relaxed text-ink-primary focus:border-accent focus:outline-none"
                data-testid="prompt-edit-textarea"
              />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function NewPromptModal({
  existing,
  onClose,
  onCreated,
}: {
  existing: PromptSummary[];
  onClose: () => void;
  onCreated: () => void;
}) {
  const [sourceSchema, setSourceSchema] = useState('');
  const [targetStack, setTargetStack] = useState('');
  const [kind, setKind] = useState('extract');
  const [version, setVersion] = useState('1.0');
  const [markdown, setMarkdown] = useState(PROMPT_TEMPLATE);
  const [error, setError] = useState<string | null>(null);

  const knownSources = useMemo(() => uniq(existing.map((p) => p.sourceSchema)), [existing]);
  const knownTargets = useMemo(() => uniq(existing.map((p) => p.targetStack)), [existing]);

  const create = useMutation({
    mutationFn: () => api.createPrompt({ sourceSchema, targetStack, kind, version, markdown }),
    onSuccess: onCreated,
    onError: (e) => setError(e instanceof ApiError ? e.message : String(e)),
  });

  const ready = sourceSchema.trim() && targetStack.trim() && kind.trim() && version.trim() && markdown.trim();

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      onClick={onClose}
      data-testid="prompt-new-modal"
    >
      <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
      <div
        className="relative flex h-[90vh] w-full max-w-[960px] flex-col rounded-lg border border-border-subtle bg-raised shadow-e3"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-start justify-between gap-4 border-b border-border-subtle px-6 py-4">
          <h3 className="text-h-md font-semibold text-ink-primary">Add a prompt version</h3>
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
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-4">
            <LabelledInput
              label="Source schema"
              value={sourceSchema}
              onChange={setSourceSchema}
              placeholder={knownSources[0] ?? 'fortran-f77'}
            />
            <LabelledInput
              label="Target stack"
              value={targetStack}
              onChange={setTargetStack}
              placeholder={knownTargets[0] ?? 'dotnet8'}
            />
            <LabelledInput label="Kind" value={kind} onChange={setKind} placeholder="extract" />
            <LabelledInput label="Version" value={version} onChange={setVersion} placeholder="1.0" />
          </div>
          <div className="mt-4">
            <div className="text-caption uppercase tracking-wide text-ink-tertiary">
              Markdown body (frontmatter + # System + # User)
            </div>
            <textarea
              value={markdown}
              onChange={(e) => setMarkdown(e.target.value)}
              rows={22}
              spellCheck={false}
              className="mt-2 w-full rounded-md border border-border-subtle bg-sunken/40 p-3 font-mono text-[12px] leading-relaxed text-ink-primary focus:border-accent focus:outline-none"
              data-testid="prompt-new-textarea"
            />
          </div>
          {error && <ErrorBlock title="Could not create prompt" message={error} />}
        </div>
        <footer className="flex items-center justify-end gap-2 border-t border-border-subtle px-6 py-3">
          <Button variant="secondary" size="md" onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            size="md"
            disabled={!ready}
            loading={create.isPending}
            onClick={() => { setError(null); create.mutate(); }}
            data-testid="prompt-new-submit"
          >
            <Plus className="h-4 w-4" />
            Create
          </Button>
        </footer>
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

function LabelledInput({
  label,
  value,
  onChange,
  placeholder,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</span>
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none"
      />
    </label>
  );
}

function uniq<T>(xs: T[]): T[] {
  return [...new Set(xs)];
}
