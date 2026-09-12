import { clsx } from 'clsx';
import { StatePill } from '../StatePill';
import { absoluteTime, arr, formatState, obj, relativeTime, stateTone, str, TONE_BG, TONE_BORDER } from '../format';
import type { ArtifactRenderProps } from './registry';

const GATE_LABELS: Record<string, string> = {
  compile: 'Compile',
  'test-pack': 'Test pack',
  test_pack: 'Test pack',
  testpack: 'Test pack',
  equivalence: 'Equivalence',
  falsifying: 'Falsifying inputs',
  'property-based': 'Falsifying inputs',
};

const DEFAULT_GATES = ['compile', 'test-pack', 'equivalence', 'falsifying'];

type Gate = { stage: string; status: string; summary: string; completedAt: string };

function gates(v: unknown): Gate[] {
  const list = arr(v).map((raw) => {
    const g = obj(raw);
    return { stage: str(g.stage), status: str(g.status), summary: str(g.summary), completedAt: str(g.completedAt) };
  });
  // Always show the four gates, even if a stage never ran.
  const byStage = new Map(list.map((g) => [g.stage.toLowerCase(), g]));
  const ordered: Gate[] = DEFAULT_GATES.map(
    (s) => byStage.get(s) ?? byStage.get(s.replace('-', '_')) ?? { stage: s, status: 'NOT_STARTED', summary: '', completedAt: '' },
  );
  for (const g of list) {
    const k = g.stage.toLowerCase();
    if (!DEFAULT_GATES.includes(k) && !DEFAULT_GATES.includes(k.replace('_', '-'))) ordered.push(g);
  }
  return ordered;
}

/** `gateResults` — the four validation gates for one scaffold. */
export function GateResultsCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const list = gates(p.gates);
  const pane = size === 'pane';
  const passed = list.filter((g) => stateTone(g.status) === 'ok').length;

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2 text-caption">
        <span className="font-mono text-body text-ink-primary">{str(p.routineName, 'Routine')}</span>
        {str(p.targetPlatform) && <span className="text-ink-tertiary">→ {str(p.targetPlatform)}</span>}
        <span className="ml-auto font-mono tabular-nums text-ink-secondary">
          {passed}/{list.length} green
        </span>
      </div>
      <div className={clsx('grid gap-2', pane ? 'grid-cols-1' : 'grid-cols-2')}>
        {list.map((g) => {
          const tone = stateTone(g.status);
          return (
            <div key={g.stage} className={clsx('rounded-lg border px-3 py-2', TONE_BORDER[tone], TONE_BG[tone])}>
              <div className="flex items-center justify-between gap-2">
                <span className="text-caption font-medium text-ink-primary">
                  {GATE_LABELS[g.stage.toLowerCase()] ?? formatState(g.stage)}
                </span>
                <StatePill state={g.status} size="xs" />
              </div>
              {(g.summary || g.completedAt) && (
                <div className="mt-1 text-micro text-ink-secondary">
                  {g.summary && <span className={clsx(!pane && 'line-clamp-2')}>{g.summary}</span>}
                  {g.completedAt && (
                    <span className="block text-ink-tertiary" title={absoluteTime(g.completedAt)}>
                      {relativeTime(g.completedAt)}
                    </span>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
