/**
 * `assembly` — one application assembled from a module's code units: whether it builds and its tests pass together,
 * which units went in, which were left out and why, what is held once, and where the units disagreed. Refreshes
 * itself from `GET /api/v1/assemblies/{id}` while it runs. Its buttons send the same sentences a person would type.
 */
import { useEffect, useState } from 'react';
import { Download } from 'lucide-react';
import { API_BASE, apiFetch } from '@/lib/api';
import { withToken } from '@/lib/auth';
import { StatePill } from '../StatePill';
import { useThreadActions } from '../ThreadActions';
import { arr, formatInt, num, obj, str } from '../format';
import type { ArtifactRenderProps } from './registry';

type Props = Record<string, unknown>;

const PILL_STATE: Record<string, string> = { PASSED: 'SUCCEEDED', FAILED: 'FAILED', ERRORED: 'FAILED', RUNNING: 'RUNNING' };

function useAssembly(id: string | null, seed: Props): Props {
  const [live, setLive] = useState<Props>(seed);
  useEffect(() => {
    if (!id) return;
    let closed = false;
    let timer: number | undefined;
    const tick = async () => {
      try {
        const res = await apiFetch<Props>(`/api/v1/assemblies/${id}`);
        if (closed) return;
        setLive(res);
        if (str(res.state) !== 'RUNNING') return;
      } catch {
        if (closed) return;
      }
      timer = window.setTimeout(tick, 3000);
    };
    void tick();
    return () => {
      closed = true;
      window.clearTimeout(timer);
    };
  }, [id]);
  return live;
}

function gateWord(status: string): string {
  if (status === 'PASSED') return 'passes';
  if (status === 'FAILED') return 'fails';
  if (status === 'ERRORED') return 'could not run';
  return 'not run';
}

export function AssemblyCard({ artifact, size, onIntent }: ArtifactRenderProps) {
  const pane = size === 'pane';
  const p = useAssembly(artifact.refId ?? (str(artifact.props.id) || null), artifact.props);
  const threadActions = useThreadActions();
  const send = onIntent ?? threadActions.sendIntent;

  const state = str(p.state, 'RUNNING');
  const wrapper = obj(p.report);
  const report = obj(wrapper.report);
  const units = arr(report.units).map(obj);
  const excluded = arr(wrapper.excluded).map(obj);
  const sharedItems = arr(report.shared).map(obj);
  const conflicts = arr(report.conflicts).map(obj);
  const notes = arr(report.notes).map((n) => String(n));
  const limit = pane ? 40 : 5;
  const download = str(p.downloadUrl);

  return (
    <div className="space-y-3" data-testid="assembly-card" data-assembly-id={str(p.id)} data-assembly-state={state}>
      <div className="flex flex-wrap items-center gap-2 text-caption">
        <span className="font-medium text-ink-primary">{str(p.name, 'Assembled application')}</span>
        <StatePill state={PILL_STATE[state] ?? state} size="xs" label={state === 'PASSED' ? 'Builds · tests pass' : undefined} />
        <span className="ml-auto font-mono tabular-nums text-ink-secondary">
          {formatInt(num(p.unitCount))} code unit{num(p.unitCount) === 1 ? '' : 's'} · {formatInt(num(p.fileCount))} files
        </span>
      </div>

      <p className="text-caption leading-[1.5] text-ink-secondary" data-testid="assembly-summary" aria-live="polite">
        {str(p.summary)}
      </p>

      {state !== 'RUNNING' && (
        <dl className="grid grid-cols-2 gap-2 text-caption" data-testid="assembly-gates">
          <div className="rounded-md border border-line-subtle px-2.5 py-1.5">
            <dt className="text-micro text-ink-tertiary">Build</dt>
            <dd className="text-ink-primary" data-testid="assembly-compile" data-status={str(p.compileStatus)}>
              {gateWord(str(p.compileStatus))}
            </dd>
          </div>
          <div className="rounded-md border border-line-subtle px-2.5 py-1.5">
            <dt className="text-micro text-ink-tertiary">Tests together</dt>
            <dd className="text-ink-primary" data-testid="assembly-tests" data-status={str(p.testStatus)}>
              {num(p.testsTotal) > 0
                ? `${formatInt(num(p.testsTotal) - num(p.testsFailed))} of ${formatInt(num(p.testsTotal))} pass`
                : gateWord(str(p.testStatus))}
            </dd>
          </div>
        </dl>
      )}

      {units.length > 0 && (
        <div className="space-y-1" data-testid="assembly-units" data-count={units.length}>
          <p className="label">In the application</p>
          <ul className="space-y-0.5">
            {units.slice(0, limit).map((u) => (
              <li key={str(u.unitId)} className="flex items-baseline gap-2 text-micro">
                <span className="font-mono text-ink-primary">{str(u.unitName)}</span>
                <span className="truncate text-ink-tertiary">{str(u.sourcePath)}</span>
                <span className="ml-auto shrink-0 font-mono text-ink-tertiary">{str(u.package).split('.').pop()}</span>
              </li>
            ))}
            {units.length > limit && <li className="text-micro text-ink-tertiary">+{units.length - limit} more</li>}
          </ul>
        </div>
      )}

      {excluded.length > 0 && (
        <div className="space-y-1" data-testid="assembly-excluded" data-count={excluded.length}>
          <p className="label">Left out</p>
          <ul className="space-y-0.5">
            {excluded.slice(0, limit).map((u) => (
              <li key={str(u.unitId)} className="text-micro text-ink-secondary">
                <span className="font-mono text-ink-primary">{str(u.unitName)}</span> — {str(u.reason)}
              </li>
            ))}
            {excluded.length > limit && <li className="text-micro text-ink-tertiary">+{excluded.length - limit} more</li>}
          </ul>
        </div>
      )}

      {(sharedItems.length > 0 || conflicts.length > 0) && (
        <div className="grid gap-2 sm:grid-cols-2">
          {sharedItems.length > 0 && (
            <div className="space-y-1" data-testid="assembly-shared" data-count={sharedItems.length}>
              <p className="label">Held once</p>
              <ul className="space-y-0.5">
                {sharedItems.slice(0, limit).map((s, i) => (
                  <li key={`${str(s.name)}-${i}`} className="text-micro text-ink-secondary">
                    {str(s.kind)} <span className="font-mono text-ink-primary">{str(s.name)}</span> · {arr(s.units).length} units
                  </li>
                ))}
              </ul>
            </div>
          )}
          {conflicts.length > 0 && (
            <div className="space-y-1" data-testid="assembly-conflicts" data-count={conflicts.length}>
              <p className="label">Disagreements</p>
              <ul className="space-y-0.5">
                {conflicts.slice(0, limit).map((c, i) => (
                  <li key={`${str(c.name)}-${i}`} className="text-micro text-ink-secondary" title={str(c.detail)}>
                    {str(c.kind)} <span className="font-mono text-ink-primary">{str(c.name)}</span>
                    {pane && <> — {str(c.detail)}</>}
                  </li>
                ))}
                {conflicts.length > limit && <li className="text-micro text-ink-tertiary">+{conflicts.length - limit} more</li>}
              </ul>
            </div>
          )}
        </div>
      )}

      {pane && notes.length > 0 && (
        <ul className="list-disc space-y-0.5 pl-4 text-micro text-ink-tertiary" data-testid="assembly-notes">
          {notes.map((n) => <li key={n}>{n}</li>)}
        </ul>
      )}

      <div className="flex flex-wrap gap-1.5">
        {download && (
          <a
            href={withToken(`${API_BASE}${download}`)}
            className="inline-flex items-center gap-1.5 rounded-md border border-line-subtle px-2 py-1 text-micro text-ink-primary hover:border-line-strong"
            data-testid="assembly-download"
          >
            <Download className="h-3 w-3" aria-hidden="true" />
            Download project
          </a>
        )}
        {send && state === 'FAILED' && (
          <button
            type="button"
            className="inline-flex items-center gap-1.5 rounded-md border border-line-subtle px-2 py-1 text-micro text-ink-primary hover:border-line-strong"
            onClick={() => send(`Explain why the assembled application ${str(p.name)} failed and what would fix it`)}
            data-testid="assembly-why"
          >
            Why did it fail?
          </button>
        )}
      </div>
    </div>
  );
}
