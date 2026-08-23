import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, Pencil, RotateCcw, ShieldAlert, SlidersHorizontal, Save, X } from 'lucide-react';
import { api, ApiError, type ValidationGate, type ValidationPolicy, type ValidationPolicyOverride } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Validation Policy — value-add #7 in the Nous platform pitch.
 *
 * Read-mode shows the merged effective policy (canonical defaults
 * + admin override). Admin-only Edit mode lets the admin:
 *   - toggle each gate's "required" flag
 *   - edit coverage thresholds (free text)
 *   - edit retry/flake defaults
 * Save → PUT /api/v1/validation/policy persists to platform_configs and
 * audit-logs as validation.policy_updated. Revert clears the override.
 */
export function ValidationPolicyPage() {
  const queryClient = useQueryClient();
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  const q = useQuery({
    queryKey: ['validation-policy'],
    queryFn: api.getValidationPolicy,
    staleTime: 0,
  });

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<ValidationPolicy | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Seed the draft when entering edit mode.
  useEffect(() => {
    if (editing && q.data) setDraft(structuredClone(q.data) as ValidationPolicy);
  }, [editing, q.data]);

  const save = useMutation({
    mutationFn: () => {
      if (!draft) throw new Error('no draft');
      const body: ValidationPolicyOverride = {
        gates: draft.gates.map((g) => ({
          id: g.id,
          required: g.required,
          coverageThreshold: g.coverageThreshold,
          retryPolicy: g.retryPolicy,
          blockingCommitOnFailure: g.blockingCommitOnFailure,
        })),
        retryDefaults: {
          transientFlakeWindow: draft.retryDefaults.transientFlakeWindow,
          autoRetryCount: draft.retryDefaults.autoRetryCount,
          note: draft.retryDefaults.note,
        },
      };
      return api.updateValidationPolicy(body);
    },
    onSuccess: () => {
      setEditing(false);
      setError(null);
      queryClient.invalidateQueries({ queryKey: ['validation-policy'] });
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : String(e)),
  });

  const revert = useMutation({
    mutationFn: () => api.revertValidationPolicy(),
    onSuccess: () => {
      setEditing(false);
      setError(null);
      queryClient.invalidateQueries({ queryKey: ['validation-policy'] });
    },
    onError: (e) => setError(e instanceof ApiError ? e.message : String(e)),
  });

  if (q.isPending) {
    return (
      <div className="mx-auto max-w-[1100px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (q.isError || !q.data) {
    return (
      <div className="mx-auto max-w-[1100px] p-6 lg:p-10">
        <ErrorBlock title="Could not load validation policy" message={String(q.error)} />
      </div>
    );
  }
  const p = editing && draft ? draft : q.data;

  const updateGate = (id: string, patch: Partial<ValidationGate>) => {
    if (!draft) return;
    setDraft({
      ...draft,
      gates: draft.gates.map((g) => (g.id === id ? { ...g, ...patch } : g)),
    });
  };

  const updateRetry = (patch: Partial<ValidationPolicy['retryDefaults']>) => {
    if (!draft) return;
    setDraft({ ...draft, retryDefaults: { ...draft.retryDefaults, ...patch } });
  };

  return (
    <div className="mx-auto max-w-[1100px] space-y-6 p-6 lg:p-10 fadeup" data-testid="validation-policy-page">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
            Validation policy
          </p>
          <h1 className="mt-1 text-display font-semibold text-ink-primary">Validation Policy</h1>
          <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
            Three independent gates run against every generated scaffold.
            The commit-to-Git action is blocked until each required gate
            reports PASSED on the same scaffold revision.
          </p>
          {q.data.overrideActive && !editing && (
            <p className="mt-2 inline-flex items-center gap-2 text-caption text-status-failed">
              <ShieldAlert className="h-3.5 w-3.5" aria-hidden="true" />
              Admin override active — last edited by{' '}
              <span className="font-mono">{q.data.overrideUpdatedBy ?? '—'}</span>
              {q.data.overrideUpdatedAt && (
                <>
                  {' '}at{' '}
                  <span className="font-mono">
                    {new Date(q.data.overrideUpdatedAt).toISOString().slice(0, 16).replace('T', ' ')}
                  </span>
                </>
              )}
            </p>
          )}
        </div>
        {isAdmin && (
          <div className="flex items-center gap-2">
            {!editing && (
              <>
                {q.data.overrideActive && (
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => {
                      if (window.confirm('Revert to canonical Nous policy? The current admin override will be deleted.')) {
                        revert.mutate();
                      }
                    }}
                    loading={revert.isPending}
                    data-testid="policy-revert"
                  >
                    <RotateCcw className="h-4 w-4" />
                    Revert override
                  </Button>
                )}
                <Button variant="primary" size="sm" onClick={() => setEditing(true)} data-testid="policy-edit">
                  <Pencil className="h-4 w-4" />
                  Edit policy
                </Button>
              </>
            )}
            {editing && (
              <>
                <Button
                  variant="primary"
                  size="sm"
                  onClick={() => save.mutate()}
                  loading={save.isPending}
                  data-testid="policy-save"
                >
                  <Save className="h-4 w-4" />
                  Save override
                </Button>
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => {
                    setEditing(false);
                    setError(null);
                  }}
                >
                  <X className="h-4 w-4" />
                  Cancel
                </Button>
              </>
            )}
          </div>
        )}
      </header>

      {error && <ErrorBlock title="Could not save policy" message={error} />}

      <Card>
        <CardHeader
          title={
            <span className="inline-flex items-center gap-2">
              <SlidersHorizontal className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
              Commit gate
            </span>
          }
          description={p.commitGate.description}
          action={
            <Badge tone={p.commitGate.requireAllGreen ? 'success' : 'neutral'}>
              {p.commitGate.requireAllGreen ? 'All-green required' : 'Permissive'}
            </Badge>
          }
        />
        <CardBody>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-3 text-caption">
            <Meta label="Scope" value={p.scope} />
            <Meta label="Policy version" value={p.version} />
            <Meta label="Owned by" value={p.ownedBy} />
          </div>
        </CardBody>
      </Card>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        {p.gates.map((g) => (
          <GateCard
            key={g.id}
            gate={g}
            editing={editing}
            onChange={(patch) => updateGate(g.id, patch)}
          />
        ))}
      </div>

      <Card>
        <CardHeader
          title={
            <span className="inline-flex items-center gap-2">
              <ShieldAlert className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
              Retry &amp; flake policy
            </span>
          }
          description={p.retryDefaults.note}
        />
        <CardBody>
          {editing ? (
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <EditableMeta
                label="Auto-retry count"
                value={String(p.retryDefaults.autoRetryCount)}
                onChange={(v) => updateRetry({ autoRetryCount: Math.max(0, Math.min(10, Number(v) || 0)) })}
                type="number"
              />
              <EditableMeta
                label="Transient flake window"
                value={p.retryDefaults.transientFlakeWindow}
                onChange={(v) => updateRetry({ transientFlakeWindow: v })}
              />
              <div className="sm:col-span-2">
                <EditableMeta
                  label="Note"
                  value={p.retryDefaults.note}
                  onChange={(v) => updateRetry({ note: v })}
                  multiline
                />
              </div>
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 text-caption">
              <Meta label="Auto-retry count" value={String(p.retryDefaults.autoRetryCount)} />
              <Meta label="Transient flake window" value={p.retryDefaults.transientFlakeWindow} />
            </div>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

function GateCard({
  gate,
  editing,
  onChange,
}: {
  gate: ValidationGate;
  editing: boolean;
  onChange: (patch: Partial<ValidationGate>) => void;
}) {
  return (
    <Card data-testid={`policy-gate-${gate.id}`}>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <CheckCircle2 className="h-4 w-4 text-status-review" aria-hidden="true" />
            {gate.label}
          </span>
        }
        action={
          editing ? (
            <label className="inline-flex items-center gap-1.5 text-caption text-ink-secondary">
              <input
                type="checkbox"
                checked={gate.required}
                onChange={(e) => onChange({ required: e.target.checked })}
                data-testid={`policy-gate-${gate.id}-required-toggle`}
              />
              required
            </label>
          ) : (
            <Badge tone={gate.required ? 'success' : 'neutral'}>
              {gate.required ? 'Required' : 'Optional'}
            </Badge>
          )
        }
      />
      <CardBody className="space-y-3 text-body">
        <p className="text-ink-secondary">{gate.description}</p>
        {editing ? (
          <>
            <EditableMeta
              label="Coverage threshold"
              value={gate.coverageThreshold ?? ''}
              onChange={(v) => onChange({ coverageThreshold: v })}
              multiline
            />
            <EditableMeta
              label="Retry policy"
              value={gate.retryPolicy}
              onChange={(v) => onChange({ retryPolicy: v })}
            />
            <EditableMeta
              label="Blocks commit on failure"
              value={gate.blockingCommitOnFailure}
              onChange={(v) => onChange({ blockingCommitOnFailure: v })}
            />
          </>
        ) : (
          <>
            {gate.coverageThreshold && <Meta label="Coverage threshold" value={gate.coverageThreshold} />}
            <Meta label="Retry policy" value={gate.retryPolicy} />
            <Meta label="Blocks commit on failure" value={gate.blockingCommitOnFailure} />
          </>
        )}
      </CardBody>
    </Card>
  );
}

function Meta({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</div>
      <div className="mt-0.5 text-body text-ink-primary">{value}</div>
    </div>
  );
}

function EditableMeta({
  label,
  value,
  onChange,
  multiline = false,
  type = 'text',
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  multiline?: boolean;
  type?: 'text' | 'number';
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</span>
      {multiline ? (
        <textarea
          value={value}
          onChange={(e) => onChange(e.target.value)}
          rows={2}
          className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none"
        />
      ) : (
        <input
          type={type}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none"
        />
      )}
    </label>
  );
}
