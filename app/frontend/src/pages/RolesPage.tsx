import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Check, Minus, Users } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type PersonaDef, type PersonaActionDef } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
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
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10" data-testid="roles-page">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
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
      <div className="overflow-hidden rounded-md border border-border-subtle">
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
