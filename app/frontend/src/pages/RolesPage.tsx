import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, Minus, Trash2, UserPlus, Users } from 'lucide-react';
import { clsx } from 'clsx';
import { api, ApiError, type PersonaDef, type PersonaId, type PersonaActionDef, type UserRow } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Roles &amp; Permissions — value-add #5 in the Nous platform pitch.
 *
 * Renders two things side-by-side:
 *   1. A persona card per role with the charter and owned pipeline stages.
 *   2. The action capability matrix — who can do what, grouped by category
 *      (Pipeline / Review / Audit / Platform).
 *
 * Static today (the matrix is curated to match the endpoint-level persona
 * checks already in the code). Phase D wires real RBAC and turns these
 * static rows into editable policy.
 */
export function RolesPage() {
  const personas = useQuery({
    queryKey: ['personas'],
    queryFn: api.listPersonas,
    staleTime: 5 * 60_000,
  });
  const matrix = useQuery({
    queryKey: ['personas-matrix'],
    queryFn: api.getPersonaMatrix,
    staleTime: 5 * 60_000,
  });

  const byCategory = useMemo(() => {
    const m = new Map<string, PersonaActionDef[]>();
    for (const a of matrix.data?.actions ?? []) {
      if (!m.has(a.category)) m.set(a.category, []);
      m.get(a.category)!.push(a);
    }
    return [...m.entries()];
  }, [matrix.data]);

  if (personas.isPending || matrix.isPending) {
    return (
      <div className="mx-auto max-w-[1200px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-10 w-72" />
        <Skeleton className="h-48 w-full" />
      </div>
    );
  }
  if (personas.isError || matrix.isError || !personas.data || !matrix.data) {
    return (
      <div className="mx-auto max-w-[1200px] p-6 lg:p-10">
        <ErrorBlock title="Could not load roles" message={String(personas.error ?? matrix.error)} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup" data-testid="roles-page">
      <header>
        <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
          Phase #4 · Roles &amp; permissions
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">Roles &amp; Permissions</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Four personas with clear separation of duty — Engineer, SME,
          Observer, Admin. Permissions are enforced at the API; the matrix
          below shows the canonical capability set every action falls into.
        </p>
      </header>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-4">
        {personas.data.data.map((p) => (
          <PersonaCard key={p.id} persona={p} />
        ))}
      </div>

      <UsersPanel />

      <Card>
        <CardHeader
          title={
            <span className="inline-flex items-center gap-2">
              <Users className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
              Capability matrix
            </span>
          }
          description="Each action is allowed for one or more personas. Cells with a check are allowed by the current API policy."
        />
        <CardBody className="space-y-6">
          {byCategory.map(([category, actions]) => (
            <CategoryBlock
              key={category}
              category={category}
              actions={actions}
              personas={matrix.data.personas}
            />
          ))}
        </CardBody>
      </Card>
    </div>
  );
}

/**
 * Users management panel — Phase #4.1 CRUD on the Users table.
 *
 * Lists every user with a persona-dropdown for in-place reassignment and
 * a delete button. The "Add user" form sits in a collapsing section at
 * the top.
 */
function UsersPanel() {
  const queryClient = useQueryClient();
  const users = useQuery({
    queryKey: ['users'],
    queryFn: api.listUsers,
    staleTime: 0,
  });

  const [adding, setAdding] = useState(false);
  const [newEmail, setNewEmail] = useState('');
  const [newDisplay, setNewDisplay] = useState('');
  const [newPersona, setNewPersona] = useState<PersonaId>('engineer');
  const [formError, setFormError] = useState<string | null>(null);

  const reset = () => {
    setAdding(false);
    setNewEmail('');
    setNewDisplay('');
    setNewPersona('engineer');
    setFormError(null);
  };

  const create = useMutation({
    mutationFn: () => api.createUser({ email: newEmail, displayName: newDisplay, persona: newPersona }),
    onSuccess: () => {
      reset();
      queryClient.invalidateQueries({ queryKey: ['users'] });
    },
    onError: (e) => {
      setFormError(e instanceof ApiError ? e.message : String(e));
    },
  });

  const update = useMutation({
    mutationFn: ({ id, persona }: { id: string; persona: PersonaId }) =>
      api.updateUserPersona(id, persona),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteUser(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  });

  return (
    <Card data-testid="users-panel">
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <Users className="h-4 w-4 text-ink-secondary" aria-hidden="true" />
            Users
          </span>
        }
        description="Every user belongs to one persona. Adjust below; every change is written to the audit log."
        action={
          !adding && (
            <Button variant="primary" size="sm" onClick={() => setAdding(true)}>
              <UserPlus className="h-4 w-4" />
              Add user
            </Button>
          )
        }
      />
      <CardBody className="space-y-4">
        {adding && (
          <div
            className="rounded-md border border-border-subtle bg-sunken/40 p-4"
            data-testid="user-add-form"
          >
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
              <LabelledInput
                label="Email"
                value={newEmail}
                onChange={setNewEmail}
                placeholder="alice@example.com"
                type="email"
              />
              <LabelledInput
                label="Display name"
                value={newDisplay}
                onChange={setNewDisplay}
                placeholder="Alice Engineer"
              />
              <LabelledSelect
                label="Persona"
                value={newPersona}
                onChange={(v) => setNewPersona(v as PersonaId)}
                options={[
                  { label: 'Engineer', value: 'engineer' },
                  { label: 'SME', value: 'sme' },
                  { label: 'Observer', value: 'observer' },
                  { label: 'Admin', value: 'admin' },
                ]}
              />
            </div>
            {formError && (
              <p className="mt-2 text-caption text-status-failed">{formError}</p>
            )}
            <div className="mt-3 flex items-center gap-2">
              <Button
                variant="primary"
                size="sm"
                onClick={() => {
                  setFormError(null);
                  create.mutate();
                }}
                loading={create.isPending}
                disabled={!newEmail || !newDisplay}
              >
                Create user
              </Button>
              <Button variant="secondary" size="sm" onClick={reset}>
                Cancel
              </Button>
            </div>
          </div>
        )}

        {users.isPending ? (
          <Skeleton className="h-32 w-full" />
        ) : users.isError || !users.data ? (
          <ErrorBlock title="Could not load users" message={String(users.error)} />
        ) : users.data.data.length === 0 ? (
          <p className="text-body text-ink-secondary">
            No users yet. Click "Add user" to create the first one.
          </p>
        ) : (
          <div className="overflow-x-auto rounded-md border border-border-subtle">
            <table className="w-full text-body">
              <thead className="bg-sunken/60 text-caption text-ink-tertiary">
                <tr>
                  <th className="px-3 py-2 text-left font-medium">Display name</th>
                  <th className="px-3 py-2 text-left font-medium">Email</th>
                  <th className="px-3 py-2 text-left font-medium">Persona</th>
                  <th className="px-3 py-2"></th>
                </tr>
              </thead>
              <tbody>
                {users.data.data.map((u) => (
                  <UserRowItem
                    key={u.id}
                    user={u}
                    onPersonaChange={(persona) => {
                      if (window.confirm(`Change ${u.displayName}'s role to ${persona}?`)) {
                        update.mutate({ id: u.id, persona });
                      }
                    }}
                    onDelete={() => {
                      if (window.confirm(`Delete user ${u.displayName} (${u.email})?`)) {
                        remove.mutate(u.id);
                      }
                    }}
                    pending={update.isPending || remove.isPending}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardBody>
    </Card>
  );
}

function UserRowItem({
  user,
  onPersonaChange,
  onDelete,
  pending,
}: {
  user: UserRow;
  onPersonaChange: (persona: PersonaId) => void;
  onDelete: () => void;
  pending: boolean;
}) {
  return (
    <tr className="border-t border-border-subtle" data-testid={`user-row-${user.id}`}>
      <td className="px-3 py-2 text-ink-primary">{user.displayName}</td>
      <td className="px-3 py-2 font-mono text-caption text-ink-secondary">{user.email}</td>
      <td className="px-3 py-2">
        <select
          value={user.persona}
          onChange={(e) => onPersonaChange(e.target.value as PersonaId)}
          disabled={pending}
          aria-label="Persona"
          className="rounded-md border border-border-subtle bg-raised px-2 py-1 text-caption text-ink-primary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20"
        >
          <option value="engineer">Engineer</option>
          <option value="sme">SME</option>
          <option value="observer">Observer</option>
          <option value="admin">Admin</option>
        </select>
        {user.persona === 'admin' && <Badge tone="signed" className="ml-2">Admin</Badge>}
      </td>
      <td className="px-3 py-2 text-right">
        <button
          type="button"
          onClick={onDelete}
          disabled={pending}
          className="inline-flex items-center gap-1 text-caption font-medium text-status-failed hover:underline disabled:opacity-50"
          aria-label={`Delete ${user.displayName}`}
        >
          <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
          Delete
        </button>
      </td>
    </tr>
  );
}

function LabelledInput({
  label,
  value,
  onChange,
  placeholder,
  type = 'text',
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  type?: 'text' | 'email';
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</span>
      <input
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20"
      />
    </label>
  );
}

function LabelledSelect({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: { label: string; value: string }[];
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-caption uppercase tracking-wide text-ink-tertiary">{label}</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-md border border-border-subtle bg-raised px-3 py-1.5 text-body text-ink-primary focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/20"
      >
        {options.map((o) => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>
    </label>
  );
}

function PersonaCard({ persona }: { persona: PersonaDef }) {
  return (
    <Card data-testid={`persona-card-${persona.id}`}>
      <CardHeader
        title={persona.displayName}
        description={<span className="font-mono text-[11px] text-ink-tertiary">id: {persona.id}</span>}
      />
      <CardBody className="space-y-3">
        <p className="text-body text-ink-secondary">{persona.charter}</p>
        <div>
          <div className="text-caption uppercase tracking-wide text-ink-tertiary">Owns</div>
          <ul className="mt-1 space-y-0.5">
            {persona.ownsStages.map((s) => (
              <li key={s} className="font-mono text-caption text-ink-secondary">· {s}</li>
            ))}
          </ul>
        </div>
      </CardBody>
    </Card>
  );
}

function CategoryBlock({
  category,
  actions,
  personas,
}: {
  category: string;
  actions: PersonaActionDef[];
  personas: { id: string; displayName: string }[];
}) {
  return (
    <div data-testid={`matrix-category-${category.toLowerCase()}`}>
      <div className="mb-2 text-caption uppercase tracking-wide text-ink-tertiary">{category}</div>
      <div className="overflow-x-auto rounded-md border border-border-subtle">
        <table className="w-full text-body">
          <thead className="bg-sunken/60 text-caption text-ink-tertiary">
            <tr>
              <th className="px-3 py-2 text-left font-medium">Action</th>
              {personas.map((p) => (
                <th key={p.id} className="px-3 py-2 text-center font-medium">{p.displayName}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {actions.map((a) => (
              <tr key={a.id} className="border-t border-border-subtle">
                <td className="px-3 py-1.5">
                  <div className="text-ink-primary">{a.label}</div>
                  <div className="text-caption text-ink-tertiary">{a.description}</div>
                </td>
                {personas.map((p) => {
                  const allowed = a.allowedPersonas.includes(p.id);
                  return (
                    <td
                      key={p.id}
                      className={clsx(
                        'px-3 py-1.5 text-center',
                        allowed ? 'text-status-review' : 'text-ink-tertiary',
                      )}
                      data-testid={`matrix-cell-${a.id}-${p.id}`}
                      aria-label={allowed ? `${p.displayName} can ${a.label}` : `${p.displayName} cannot ${a.label}`}
                    >
                      {allowed ? (
                        <Check className="mx-auto h-4 w-4" aria-hidden="true" />
                      ) : (
                        <Minus className="mx-auto h-4 w-4 opacity-40" aria-hidden="true" />
                      )}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
