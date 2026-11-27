import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError, api, getPersona, type ProjectSettings } from '@/lib/api';
import { ArtefactKindPicker } from '@/components/ArtefactKindPicker';
import { Button } from '@/components/Button';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { Skeleton } from '@/components/Skeleton';
import { prettyStack } from '@/lib/targetStacks';

// The stacks a project can migrate to. Empty = each routine goes to its language's default (PHP and COBOL to Java,
// the .NET-bound languages to .NET 10); the server checks the value.
const TARGET_STACKS = ['java-spring', 'dotnet10', 'dotnet8', 'react-python', 'angular-java', 'angular-dotnet8'];

const inputClass =
  'mt-1 w-full rounded-md border border-line bg-raised px-3 py-2 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-volt focus:outline-none focus:ring-2 focus:ring-volt/20 disabled:cursor-not-allowed disabled:opacity-60';

type Form = {
  artefactKinds: string[];
  tier: string;
  knowledgeHolder: string;
  waveName: string;
  waveEnd: string;
  banner: string;
  test: boolean;
  targetStack: string;
};

function toForm(s: ProjectSettings): Form {
  return {
    artefactKinds: s.artefactKinds,
    tier: s.metadata.tier === null ? '' : String(s.metadata.tier),
    knowledgeHolder: s.metadata.knowledgeHolder ?? '',
    waveName: s.metadata.wave?.name ?? '',
    waveEnd: s.metadata.wave?.endDate ?? '',
    banner: s.banner ?? '',
    test: !!s.test,
    targetStack: s.targetStack ?? '',
  };
}

// Fields this page does not edit (pack, expected counts, demo) travel through unchanged.
function toSettings(base: ProjectSettings, f: Form): ProjectSettings {
  const name = f.waveName.trim();
  const end = f.waveEnd.trim();
  return {
    ...base,
    artefactKinds: f.artefactKinds,
    banner: f.banner.trim() || null,
    test: f.test,
    targetStack: f.targetStack || null,
    metadata: {
      tier: f.tier === '' ? null : Number(f.tier),
      knowledgeHolder: f.knowledgeHolder.trim() || null,
      wave: name || end ? { name: name || null, endDate: end || null } : null,
    },
  };
}

export function ProjectSettingsPage() {
  const { id = '' } = useParams();
  const qc = useQueryClient();
  const canEdit = ['engineer', 'admin'].includes(getPersona());

  const corpus = useQuery({ queryKey: ['corpus', id], queryFn: () => api.getCorpus(id), enabled: !!id });
  const settings = useQuery({
    queryKey: ['corpus-settings', id],
    queryFn: () => api.getCorpusSettings(id),
    enabled: !!id,
  });

  const [form, setForm] = useState<Form | null>(null);
  useEffect(() => {
    if (settings.data) setForm(toForm(settings.data));
  }, [settings.data]);

  // Leaving the page with unsaved edits loses them — let the browser ask first.
  useEffect(() => {
    if (!form || !settings.data) return;
    const changed = JSON.stringify(form) !== JSON.stringify(toForm(settings.data));
    if (!changed) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [form, settings.data]);

  const save = useMutation<ProjectSettings, ApiError>({
    mutationFn: () => api.putCorpusSettings(id, toSettings(settings.data!, form!)),
    onSuccess: (saved) => {
      qc.setQueryData(['corpus-settings', id], saved);
      qc.invalidateQueries({ queryKey: ['corpus', id] });
    },
  });

  if (settings.isPending || corpus.isPending || !form) {
    return (
      <div className="mx-auto max-w-[820px] space-y-4 p-4 lg:p-6">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }
  if (settings.isError) {
    return (
      <div className="mx-auto max-w-[820px] p-4 lg:p-6">
        <ErrorBlock title="Could not load project settings" message={settings.error.message} onRetry={() => settings.refetch()} />
      </div>
    );
  }

  const set = (patch: Partial<Form>) => setForm({ ...form, ...patch });
  const dirty = JSON.stringify(form) !== JSON.stringify(toForm(settings.data));
  const disabled = !canEdit || save.isPending;

  return (
    <div className="mx-auto max-w-[820px] space-y-6 p-4 lg:p-6 fadeup" data-testid="project-settings-page">
      <PageHero
        eyebrow={corpus.data?.name ?? id}
        title="Project settings"
        lead="Which kinds of evidence this project takes besides source code, and how it is described and labelled."
        actions={
          <Link
            to={`/corpora/${id}`}
            className="text-caption text-ink-secondary hover:text-ink-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          >
            Back to project
          </Link>
        }
      />

      <RenameCard id={id} current={corpus.data?.name ?? ''} canEdit={canEdit} />

      {!canEdit && (
        <p className="rounded-md border border-line-subtle bg-sunken px-4 py-3 text-caption text-ink-secondary" data-testid="settings-readonly">
          Settings are changed by an Engineer or an Admin. You are viewing them read-only.
        </p>
      )}

      <Card>
        <CardHeader
          title="Artefact kinds"
          description="Source code is always part of a project. Switch on the others this project holds evidence in."
        />
        <CardBody>
          <ArtefactKindPicker value={form.artefactKinds} onChange={(artefactKinds) => set({ artefactKinds })} disabled={disabled} />
        </CardBody>
      </Card>

      <Card>
        <CardHeader
          title="Migration target"
          description="The stack this project's code is migrated to. Specs are extracted for it, and code is generated for it unless a code unit asks for another."
        />
        <CardBody>
          <label htmlFor="settings-target-stack" className="text-body font-medium text-ink-primary">Target stack</label>
          <select
            id="settings-target-stack"
            value={form.targetStack}
            onChange={(e) => set({ targetStack: e.target.value })}
            disabled={disabled}
            className={inputClass}
            data-testid="settings-target-stack"
          >
            <option value="">Each language's default</option>
            {[...TARGET_STACKS, ...(form.targetStack && !TARGET_STACKS.includes(form.targetStack) ? [form.targetStack] : [])].map((s) => (
              <option key={s} value={s}>{prettyStack(s)}</option>
            ))}
          </select>
        </CardBody>
      </Card>

      <Card>
        <CardHeader title="Programme details" description="Used by the portfolio and dependency views." />
        <CardBody className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <div>
              <label htmlFor="settings-tier" className="text-body font-medium text-ink-primary">Tier</label>
              <select
                id="settings-tier"
                value={form.tier}
                onChange={(e) => set({ tier: e.target.value })}
                disabled={disabled}
                className={inputClass}
                data-testid="settings-tier"
              >
                <option value="">Not set</option>
                <option value="1">Tier 1 (most critical)</option>
                <option value="2">Tier 2</option>
                <option value="3">Tier 3</option>
              </select>
            </div>
            <div>
              <label htmlFor="settings-holder" className="text-body font-medium text-ink-primary">Knowledge holder</label>
              <input
                id="settings-holder"
                type="text"
                value={form.knowledgeHolder}
                onChange={(e) => set({ knowledgeHolder: e.target.value })}
                disabled={disabled}
                maxLength={120}
                placeholder="The person or vendor who holds the knowledge"
                className={inputClass}
                data-testid="settings-knowledge-holder"
              />
            </div>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <div>
              <label htmlFor="settings-wave-name" className="text-body font-medium text-ink-primary">Transition wave</label>
              <input
                id="settings-wave-name"
                type="text"
                value={form.waveName}
                onChange={(e) => set({ waveName: e.target.value })}
                disabled={disabled}
                maxLength={80}
                placeholder="e.g. Wave 1"
                className={inputClass}
                data-testid="settings-wave-name"
              />
            </div>
            <div>
              <label htmlFor="settings-wave-end" className="text-body font-medium text-ink-primary">Wave end date</label>
              <input
                id="settings-wave-end"
                type="date"
                value={form.waveEnd}
                onChange={(e) => set({ waveEnd: e.target.value })}
                disabled={disabled}
                className={inputClass}
                data-testid="settings-wave-end"
              />
            </div>
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHeader title="Display" description="Shown on a slim bar across every page of this project." />
        <CardBody>
          <label htmlFor="settings-banner" className="text-body font-medium text-ink-primary">Banner text</label>
          <input
            id="settings-banner"
            type="text"
            value={form.banner}
            onChange={(e) => set({ banner: e.target.value })}
            disabled={disabled}
            maxLength={200}
            placeholder="e.g. Synthetic data for demonstration"
            className={inputClass}
            data-testid="settings-banner"
          />
          <label className="mt-4 flex items-start gap-2.5 text-body text-ink-primary">
            <input
              type="checkbox"
              checked={form.test}
              onChange={(e) => set({ test: e.target.checked })}
              disabled={disabled}
              className="mt-1 accent-volt"
              data-testid="settings-test"
            />
            <span>
              <span className="font-medium">Test project</span>
              <span className="block text-caption text-ink-secondary">
                A QA or scratch project. The Projects list hides it unless “Show test projects” is on. Nothing is deleted.
              </span>
            </span>
          </label>
        </CardBody>
      </Card>

      {save.isError && (
        <ErrorBlock title="Settings were not saved" message={save.error.message} onRetry={() => save.reset()} />
      )}
      {save.isSuccess && (
        <p role="status" className="text-caption text-status-ok" data-testid="settings-saved">
          Saved.
        </p>
      )}

      {/* Always in reach (the kind list above is long); enabled only when something changed. */}
      <div className="sticky bottom-0 -mx-4 flex items-center justify-end gap-3 border-t border-line-subtle bg-canvas/95 px-4 py-3 backdrop-blur lg:-mx-6 lg:px-6">
        <span className="mr-auto text-caption text-ink-secondary" data-testid="settings-dirty">
          {dirty ? 'Unsaved changes' : save.isSuccess ? '' : 'No changes'}
        </span>
        <Button variant="primary" onClick={() => save.mutate()} disabled={disabled || !dirty} loading={save.isPending} data-testid="settings-save">
          Save settings
        </Button>
      </div>
    </div>
  );
}

/** Rename the project (unique names; recorded in the audit trail). */
function RenameCard({ id, current, canEdit }: { id: string; current: string; canEdit: boolean }) {
  const qc = useQueryClient();
  const [name, setName] = useState(current);
  useEffect(() => setName(current), [current]);
  const rename = useMutation<{ id: string; name: string }, ApiError>({
    mutationFn: () => api.renameCorpus(id, name.trim()),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['corpus', id] });
      qc.invalidateQueries({ queryKey: ['corpora'] });
      qc.invalidateQueries({ queryKey: ['copilot', 'overview'] });
    },
  });
  const changed = name.trim().length > 0 && name.trim() !== current;
  return (
    <Card>
      <CardHeader title="Name" description="Shown in the projects list, the rail and every picker. Must be unique." />
      <CardBody className="flex flex-wrap items-center gap-3">
        <input
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          disabled={!canEdit || rename.isPending}
          maxLength={200}
          aria-label="Project name"
          className={inputClass + ' min-w-[260px] flex-1'}
          data-testid="settings-rename-input"
        />
        <Button
          variant="secondary"
          onClick={() => rename.mutate()}
          disabled={!canEdit || !changed}
          loading={rename.isPending}
          data-testid="settings-rename-save"
        >
          Rename
        </Button>
        {rename.isError && <p className="basis-full text-caption text-status-failed">{rename.error.message}</p>}
        {rename.isSuccess && !changed && <p className="basis-full text-caption text-status-ok">Renamed.</p>}
      </CardBody>
    </Card>
  );
}
