import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ChevronRight, Languages as LanguagesIcon, Pencil, RotateCcw, Save, X } from 'lucide-react';
import { api, ApiError, type SpecSchemaSummary, type ClaimKindSummary } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Languages page — value-add #4 in the Nous platform pitch.
 *
 * Lists every legacy language schema the platform supports today, plus
 * the roadmap. Loaded schemas (Fortran F77, COBOL) show their typed
 * claim kinds in a drawer — this is the proof that the platform isn't
 * doing free-form "AI summarisation", it's producing claims against a
 * known taxonomy that downstream test-pack generators can mechanically
 * map to fixtures.
 *
 * Wraps GET /api/v1/spec-schemas (Phase #3a). Roadmap entries are
 * client-side stubs since RPG and PL/1 schemas aren't shipped yet.
 */

type RoadmapEntry = {
  id: string;
  displayName: string;
  description: string;
  status: 'roadmap';
  targetQuarter: string;
};

const ROADMAP: RoadmapEntry[] = [
  {
    id: 'rpg',
    displayName: 'RPG (AS/400 IBM i)',
    description:
      'Schema and prompts for RPG IV / Free-form RPG on IBM i. Targets COBOL-grade fixture density and the same invariant/side-effect/edge-case taxonomy.',
    status: 'roadmap',
    targetQuarter: 'Q3 2026',
  },
  {
    id: 'pl1',
    displayName: 'PL/I (mainframe)',
    description:
      'Schema and prompts for PL/I procedures on z/OS. Pair-engagement track first, then promotes to a standard ship after two production accounts ratify the taxonomy.',
    status: 'roadmap',
    targetQuarter: 'Q4 2026',
  },
];

export function LanguagesPage() {
  const queryClient = useQueryClient();
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  const list = useQuery({
    queryKey: ['spec-schemas'],
    queryFn: api.listSpecSchemas,
    staleTime: 0,
  });

  const [openSchema, setOpenSchema] = useState<SpecSchemaSummary | null>(null);

  const toggleEnabled = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      api.putSchemaOverride(id, { enabled }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['spec-schemas'] }),
  });

  const items = useMemo(() => {
    const loaded = list.data?.data ?? [];
    return [
      ...loaded.map((s) => ({ kind: 'schema' as const, data: s })),
      ...ROADMAP.map((r) => ({ kind: 'roadmap' as const, data: r })),
    ];
  }, [list.data]);

  if (list.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <Skeleton className="h-48" /><Skeleton className="h-48" />
        </div>
      </div>
    );
  }
  if (list.isError || !list.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load schemas" message={String(list.error)} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10" data-testid="languages-page">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase #3a · Spec schemas
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">Languages</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          One typed claim schema per legacy language. Every Claude
          extraction produces invariants, side effects, edge cases, and open
          questions against this taxonomy — which is what makes the
          generated test pack mechanically derivable from the spec.
        </p>
      </header>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        {items.map((item) =>
          item.kind === 'schema' ? (
            <SchemaCard
              key={item.data.id}
              schema={item.data}
              isAdmin={isAdmin}
              onOpen={() => setOpenSchema(item.data)}
              onToggleEnabled={(enabled) =>
                toggleEnabled.mutate({ id: item.data.id, enabled })
              }
              togglePending={toggleEnabled.isPending}
            />
          ) : (
            <RoadmapCard key={item.data.id} entry={item.data} />
          ),
        )}
      </div>

      {openSchema && (
        <SchemaDetailDrawer
          schema={openSchema}
          isAdmin={isAdmin}
          onClose={() => setOpenSchema(null)}
          onMutated={() => {
            queryClient.invalidateQueries({ queryKey: ['spec-schemas'] });
            setOpenSchema(null);
          }}
        />
      )}
    </div>
  );
}

function SchemaCard({
  schema,
  isAdmin,
  onOpen,
  onToggleEnabled,
  togglePending,
}: {
  schema: SpecSchemaSummary;
  isAdmin: boolean;
  onOpen: () => void;
  onToggleEnabled: (enabled: boolean) => void;
  togglePending: boolean;
}) {
  // status may be null in schema.json (Fortran/COBOL ship without an
  // explicit status string today); treat null as "production" — the
  // schema is loaded so it's shippable.
  const statusStr = schema.status ?? 'production';
  const isProd = statusStr.toLowerCase().includes('production');
  const enabled = schema.enabled ?? true;
  return (
    <Card
      interactive
      onClick={onOpen}
      data-testid={`language-card-${schema.id}`}
      className={!enabled ? 'opacity-60' : undefined}
    >
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <LanguagesIcon className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
            {schema.displayName}
          </span>
        }
        description={
          <span className="font-mono text-[11px] text-ink-tertiary">
            id: {schema.id} · {schema.claimKindCount} claim kinds
            {schema.overrideActive && (
              <span className="ml-1.5 text-status-failed">· override</span>
            )}
          </span>
        }
        action={
          <div className="flex items-center gap-2">
            <Badge tone={enabled ? (isProd ? 'success' : 'neutral') : 'failed'}>
              {enabled ? (isProd ? 'Production' : prettyStatus(statusStr)) : 'Disabled'}
            </Badge>
            {isAdmin && (
              <label
                className="inline-flex cursor-pointer items-center gap-1.5 text-caption text-ink-secondary"
                onClick={(e) => e.stopPropagation()}
              >
                <input
                  type="checkbox"
                  checked={enabled}
                  onChange={(e) => onToggleEnabled(e.target.checked)}
                  disabled={togglePending}
                  aria-label={`Enable ${schema.id}`}
                  data-testid={`schema-toggle-${schema.id}`}
                />
                <span className="font-mono text-[10px] uppercase tracking-wider">
                  enabled
                </span>
              </label>
            )}
          </div>
        }
      />
      <CardBody className="space-y-3">
        <p className="text-body text-ink-secondary">{schema.description}</p>
        <div className="flex flex-wrap items-center gap-2">
          {schema.supportedSourceExtensions.map((ext) => (
            <span
              key={ext}
              className="rounded-sm border border-border-subtle bg-sunken px-1.5 py-0.5 font-mono text-[10px] text-ink-tertiary"
            >
              {ext}
            </span>
          ))}
          <span className="text-ink-tertiary">→</span>
          {schema.compatibleTargetStacks.map((t) => (
            <span
              key={t}
              className="rounded-sm border border-border-subtle bg-sunken px-1.5 py-0.5 font-mono text-[10px] text-ink-tertiary"
            >
              {t}
            </span>
          ))}
        </div>
        <div className="inline-flex items-center gap-1 text-caption text-accent">
          View claim taxonomy <ChevronRight className="h-3.5 w-3.5" aria-hidden="true" />
        </div>
      </CardBody>
    </Card>
  );
}

function RoadmapCard({ entry }: { entry: RoadmapEntry }) {
  return (
    <Card data-testid={`language-card-${entry.id}`}>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2 text-ink-tertiary">
            <LanguagesIcon className="h-4 w-4" aria-hidden="true" />
            {entry.displayName}
          </span>
        }
        action={<Badge tone="neutral">Roadmap · {entry.targetQuarter}</Badge>}
      />
      <CardBody>
        <p className="text-body text-ink-secondary">{entry.description}</p>
      </CardBody>
    </Card>
  );
}

function SchemaDetailDrawer({
  schema,
  isAdmin,
  onClose,
  onMutated,
}: {
  schema: SpecSchemaSummary;
  isAdmin: boolean;
  onClose: () => void;
  onMutated: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState({
    displayName: schema.displayName,
    description: schema.description,
    status: schema.status ?? 'production',
    platformReadiness: schema.platformReadiness ?? '',
  });
  const [error, setError] = useState<string | null>(null);

  const save = useMutation({
    mutationFn: () =>
      api.putSchemaOverride(schema.id, {
        displayName: draft.displayName,
        description: draft.description,
        status: draft.status,
        platformReadiness: draft.platformReadiness || undefined,
      }),
    onSuccess: () => {
      setEditing(false);
      setError(null);
      onMutated();
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : String(e)),
  });

  const revert = useMutation({
    mutationFn: () => api.revertSchemaOverride(schema.id),
    onSuccess: () => {
      setEditing(false);
      setError(null);
      onMutated();
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : String(e)),
  });

  return (
    <div
      className="fixed inset-0 z-50 flex items-stretch justify-end"
      role="dialog"
      aria-modal="true"
      aria-labelledby="schema-drawer-title"
      onClick={onClose}
      data-testid="language-detail-drawer"
    >
      <div className="absolute inset-0 bg-ink-primary/40 backdrop-blur-sm" />
      <div
        className="relative ml-auto flex h-full w-full max-w-[720px] flex-col bg-raised shadow-e3"
        onClick={(e) => e.stopPropagation()}
      >
        <header className="flex items-start justify-between gap-4 border-b border-border-subtle px-6 py-4">
          <div>
            <h3 id="schema-drawer-title" className="text-h-md font-semibold text-ink-primary">
              {schema.displayName}
            </h3>
            <p className="mt-1 font-mono text-caption text-ink-tertiary">
              id: {schema.id} · {schema.status ?? 'production'}
              {schema.overrideActive && <span className="ml-1.5 text-status-failed">· override</span>}
            </p>
          </div>
          <div className="flex items-center gap-2">
            {isAdmin && !editing && (
              <>
                {schema.overrideActive && (
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => {
                      if (window.confirm(`Revert ${schema.id} override? The schema will return to its shipped defaults.`)) {
                        revert.mutate();
                      }
                    }}
                    loading={revert.isPending}
                    data-testid="schema-revert"
                  >
                    <RotateCcw className="h-4 w-4" />
                    Revert
                  </Button>
                )}
                <Button variant="primary" size="sm" onClick={() => setEditing(true)} data-testid="schema-edit">
                  <Pencil className="h-4 w-4" />
                  Edit
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
                  data-testid="schema-save"
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
          {error && <ErrorBlock title="Save failed" message={error} />}
          {editing ? (
            <div className="space-y-3">
              <Field label="Display name" value={draft.displayName} onChange={(v) => setDraft({ ...draft, displayName: v })} />
              <Field label="Status" value={draft.status} onChange={(v) => setDraft({ ...draft, status: v })} />
              <Field label="Description" value={draft.description} onChange={(v) => setDraft({ ...draft, description: v })} multiline />
              <Field label="Platform readiness" value={draft.platformReadiness} onChange={(v) => setDraft({ ...draft, platformReadiness: v })} multiline />
            </div>
          ) : (
            <>
              <p className="text-body text-ink-secondary">{schema.description}</p>
              {schema.calibratedAgainst && (
                <div className="mt-4 rounded-md border border-border-subtle bg-sunken/40 px-3 py-2 font-mono text-caption text-ink-secondary">
                  Calibrated against: {Array.isArray(schema.calibratedAgainst)
                    ? schema.calibratedAgainst.join(' · ')
                    : schema.calibratedAgainst}
                </div>
              )}
              <ClaimKindTable kinds={schema.claimKinds} />
              {schema.platformReadiness && (
                <div className="mt-6">
                  <div className="text-caption uppercase tracking-wide text-ink-tertiary">
                    Platform readiness
                  </div>
                  <p className="mt-1 text-body text-ink-secondary">{schema.platformReadiness}</p>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function Field({
  label,
  value,
  onChange,
  multiline = false,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  multiline?: boolean;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</span>
      {multiline ? (
        <textarea
          value={value}
          onChange={(e) => onChange(e.target.value)}
          rows={3}
          className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none"
        />
      ) : (
        <input
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none"
        />
      )}
    </label>
  );
}

function ClaimKindTable({ kinds }: { kinds: ClaimKindSummary[] }) {
  return (
    <div className="mt-6">
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">Claim kinds</div>
      <div className="mt-2 overflow-hidden rounded-md border border-border-subtle">
        <table className="w-full text-body">
          <thead className="bg-sunken/60 text-caption text-ink-tertiary">
            <tr>
              <th className="px-3 py-2 text-left font-medium">prefix</th>
              <th className="px-3 py-2 text-left font-medium">label</th>
              <th className="px-3 py-2 text-left font-medium">description</th>
            </tr>
          </thead>
          <tbody>
            {kinds.map((k) => (
              <tr key={k.id} className="border-t border-border-subtle">
                <td className="px-3 py-1.5 font-mono text-caption text-ink-secondary">{k.idPrefix}</td>
                <td className="px-3 py-1.5 text-ink-primary">{k.label}</td>
                <td className="px-3 py-1.5 text-ink-secondary">{k.description}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function prettyStatus(s: string): string {
  const idx = s.indexOf(' · ');
  return idx > 0 ? s.slice(0, idx) : s;
}
