/**
 * `planWaves` — a migration plan as a swimlane of waves: number, name,
 * routine count (bar scaled to the largest wave) and a status pill.
 */
import { clsx } from 'clsx';
import { StatePill } from '../StatePill';
import { arr, formatInt, num, obj, str, TONE_DOT, type Tone } from '../format';
import type { ArtifactRenderProps } from './registry';

type Wave = { waveNumber: number; name: string; routineCount: number; status: string };

function waves(v: unknown): Wave[] {
  return arr(v)
    .map((raw, i) => {
      const w = obj(raw);
      return {
        waveNumber: num(w.waveNumber, i + 1),
        name: str(w.name, `Wave ${num(w.waveNumber, i + 1)}`),
        routineCount: num(w.routineCount),
        status: str(w.status, 'planned'),
      };
    })
    .sort((a, b) => a.waveNumber - b.waveNumber);
}

function waveTone(status: string): Tone {
  const s = status.toLowerCase();
  if (s === 'completed' || s === 'done') return 'ok';
  if (s === 'in_progress' || s === 'in-progress' || s === 'running') return 'info';
  return 'neutral';
}

const WAVE_SWATCH = ['bg-wave-1', 'bg-wave-2', 'bg-wave-3', 'bg-wave-4', 'bg-wave-5'];

export function PlanWavesCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const pane = size === 'pane';
  const list = waves(p.waves);
  const shown = pane ? list : list.slice(0, 5);
  const max = Math.max(1, ...list.map((w) => w.routineCount));
  const totalWaves = num(p.totalWaves, list.length);
  const totalRoutines = num(p.totalRoutines, list.reduce((n, w) => n + w.routineCount, 0));
  const completed = list.filter((w) => waveTone(w.status) === 'ok').length;

  return (
    <div className="space-y-3" data-testid="plan-waves-card" data-plan-status={str(p.status) || undefined}>
      <div className="flex flex-wrap items-center gap-2 text-caption">
        <span className="font-medium text-ink-primary">{str(p.strategyName, 'Migration plan')}</span>
        {str(p.status) && <StatePill state={str(p.status)} size="xs" />}
        <span className="ml-auto font-mono tabular-nums text-ink-secondary">
          {formatInt(totalWaves)} wave{totalWaves === 1 ? '' : 's'} · {formatInt(totalRoutines)} routines
          {completed > 0 && <> · {formatInt(completed)} done</>}
        </span>
      </div>

      {pane && str(p.summary) && <p className="text-caption leading-[1.5] text-ink-secondary">{str(p.summary)}</p>}

      {list.length === 0 ? (
        <p className="text-caption text-ink-tertiary">No waves yet — generate a migration plan first.</p>
      ) : (
        <ol className="space-y-1.5" aria-label="Waves">
          {shown.map((w, i) => {
            const tone = waveTone(w.status);
            const swatch = WAVE_SWATCH[Math.min(WAVE_SWATCH.length - 1, Math.max(0, w.waveNumber - 1))];
            return (
              <li key={`${w.waveNumber}-${i}`} className="flex items-center gap-2.5" data-testid="plan-wave" data-wave={w.waveNumber}>
                <span
                  className={clsx(
                    'inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-md font-mono text-micro font-semibold tabular-nums text-on-volt',
                    swatch,
                  )}
                  aria-label={`Wave ${w.waveNumber}`}
                >
                  {w.waveNumber}
                </span>
                <div className="min-w-0 flex-1">
                  <div className="flex items-baseline gap-2">
                    <span className="min-w-0 truncate text-caption text-ink-primary">{w.name}</span>
                    <span className="ml-auto shrink-0 font-mono text-micro tabular-nums text-ink-secondary">
                      {formatInt(w.routineCount)} routine{w.routineCount === 1 ? '' : 's'}
                    </span>
                  </div>
                  <div className="mt-1 h-1 w-full overflow-hidden rounded-full bg-sunken ring-1 ring-inset ring-line-subtle" aria-hidden="true">
                    <div
                      className={clsx('h-full rounded-full', tone === 'ok' ? TONE_DOT.ok : tone === 'info' ? TONE_DOT.info : swatch, tone === 'neutral' && 'opacity-60')}
                      style={{ width: `${Math.max(2, Math.round((w.routineCount / max) * 100))}%` }}
                    />
                  </div>
                </div>
                <StatePill state={w.status} tone={tone} size="xs" />
              </li>
            );
          })}
        </ol>
      )}
      {!pane && list.length > shown.length && (
        <div className="text-micro text-ink-tertiary">+{list.length - shown.length} more wave{list.length - shown.length === 1 ? '' : 's'} — open in the pane to see all</div>
      )}
    </div>
  );
}
