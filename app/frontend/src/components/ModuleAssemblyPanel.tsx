import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Package } from 'lucide-react';
import { assemblyApi, getPersona } from '@/lib/api';
import { Button } from '@/components/Button';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { ErrorBlock } from '@/components/ErrorBlock';
import { renderArtifact } from '@/workspace/artifacts/registry';

const inputClass =
  'w-full rounded-md border border-line bg-raised px-3 py-1.5 text-body text-ink-primary placeholder:text-ink-tertiary focus:border-volt focus:outline-none focus:ring-2 focus:ring-volt/20';

/**
 * The project page's "one application" panel: the same action as typing "assemble the X module" in the thread. Picks a
 * module by folder, then merges the checked packages of its code units into one project and builds and tests it.
 */
export function ModuleAssemblyPanel({ corpusId }: { corpusId: string }) {
  const qc = useQueryClient();
  const engineer = getPersona() === 'engineer';
  const [pathPrefix, setPathPrefix] = useState('');
  const [requireTests, setRequireTests] = useState(true);
  const list = useQuery({
    queryKey: ['assemblies', corpusId],
    queryFn: () => assemblyApi.list(corpusId),
    refetchInterval: (q) => (q.state.data?.data.some((a) => a.state === 'RUNNING') ? 4000 : false),
  });
  const start = useMutation({
    mutationFn: () => assemblyApi.start(corpusId, { pathPrefix: pathPrefix.trim() || undefined, requireTests }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['assemblies', corpusId] }),
  });
  const rows = list.data?.data ?? [];

  return (
    <Card>
      <CardHeader
        title="One application"
        description="Merge the checked code of a module's code units into a single project, then build it and run all their tests together."
      />
      <CardBody className="space-y-4">
        {engineer && (
          <form
            className="flex flex-wrap items-end gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              start.mutate();
            }}
            data-testid="assembly-form"
          >
            <label className="min-w-[14rem] flex-1 text-caption text-ink-secondary">
              Module folder
              <input
                className={inputClass + ' mt-1 font-mono'}
                value={pathPrefix}
                onChange={(e) => setPathPrefix(e.target.value)}
                placeholder="e.g. htdocs/societe/ or societe — empty for the whole project"
                data-testid="assembly-path"
              />
            </label>
            <label className="flex items-center gap-2 pb-2 text-caption text-ink-secondary">
              <input
                type="checkbox"
                checked={requireTests}
                onChange={(e) => setRequireTests(e.target.checked)}
                className="accent-volt"
                data-testid="assembly-require-tests"
              />
              Only code units whose tests pass
            </label>
            <Button type="submit" variant="primary" loading={start.isPending} data-testid="assembly-start">
              <Package className="h-4 w-4" aria-hidden="true" />
              Assemble
            </Button>
          </form>
        )}
        {start.error && <ErrorBlock title="Could not start the assembly" message={String((start.error as Error).message)} />}
        {list.error && <ErrorBlock title="Could not load assembled applications" message={String((list.error as Error).message)} />}
        {rows.length === 0 && !list.isPending && (
          <p className="text-caption text-ink-tertiary" data-testid="assembly-empty">
            Nothing assembled yet.{engineer ? '' : ' An engineer assembles applications.'}
          </p>
        )}
        <ul className="space-y-3" data-testid="assembly-list">
          {rows.slice(0, 5).map((a) => (
            <li key={a.id} className="rounded-lg border border-line-subtle p-3">
              {renderArtifact({ kind: 'assembly', refId: a.id, props: a as unknown as Record<string, unknown> }, { size: 'card' })}
            </li>
          ))}
        </ul>
      </CardBody>
    </Card>
  );
}
