/**
 * `workRun` — many agents working through many routines. Shows each stage's
 * progress and width, who is waiting for sign-off, failures and cost, and
 * refreshes itself from `GET /api/v1/work-runs/{id}`. Its buttons send the same
 * sentences a person would type.
 */
import { useEffect, useState } from 'react';
import { clsx } from 'clsx';
import { Pause, Play, RotateCcw, X } from 'lucide-react';
import { Link } from 'react-router-dom';
import { apiFetch } from '@/lib/api';
import { StatePill } from '../StatePill';
import { useThreadActions } from '../ThreadActions';
import { arr, formatDuration, formatInt, formatMoney, num, numOrNull, obj, str, TONE_DOT } from '../format';
import type { ArtifactRenderProps } from './registry';

type Props = Record<string, unknown>;

const STAGE_LABEL: Record<string, string> = {
  extract: 'Draft specs',
  signoff: 'SME sign-off',
  scaffold: 'Generate code',
  compile: 'Compile',
  'test-pack': 'Test pack',
};

function isOver(state: string) {
  return state === 'COMPLETED' || state === 'CANCELLED';
}

/** The latest snapshot of the run: seeded from the card's props, then polled. */
function useRunSnapshot(runId: string | null, seed: Props): Props {
  const [live, setLive] = useState<Props>(seed);

  useEffect(() => {
    if (!runId) return;
    let closed = false;
    let timer: number | undefined;
    let failures = 0;
    const tick = async () => {
      let delay = 2000;
      try {
        const res = await apiFetch<{ run: Props }>(`/api/v1/work-runs/${runId}`);
        if (closed) return;
        failures = 0;
        setLive(res.run);
        const state = str(res.run.state);
        // A finished run can be reopened by "retry", so keep a slow look at one with failures.
        if (isOver(state) && num(res.run.failedCount) === 0) return;
        if (isOver(state) || state === 'PAUSED' || res.run.idleOnPeople === true) delay = 8000;
      } catch {
        if (closed) return;
        // An API restart looks like this; the run is still in the queue, so keep asking.
        failures += 1;
        delay = Math.min(15_000, 2000 * failures);
      }
      timer = window.setTimeout(tick, delay);
    };
    void tick();
    return () => {
      closed = true;
      window.clearTimeout(timer);
    };
  }, [runId]);

  return live;
}

export function WorkRunCard({ artifact, size, onIntent }: ArtifactRenderProps) {
  const pane = size === 'pane';
  const p = useRunSnapshot(artifact.refId, artifact.props);
  const threadActions = useThreadActions();
  const send = onIntent ?? threadActions.sendIntent;

  const state = str(p.state, 'RUNNING');
  const stages = arr(p.stages).map(obj);
  const waiting = arr(p.waitingForSignoff).map(obj);
  const failures = arr(p.failures).map(obj);
  const running = arr(p.running).map(obj);
  const waitingCount = num(p.waitingForSignoffCount, waiting.length);
  const failed = num(p.failedCount, failures.length);
  const budget = numOrNull(p.budgetUsd);
  const metrics = obj(p.metrics);
  const idle = p.idleOnPeople === true;
  const runRef = str(p.runId, artifact.refId ?? '').slice(0, 8);

  const action = (label: string, intent: string, Icon: typeof Pause) => (
    <button
      type="button"
      className="inline-flex items-center gap-1.5 rounded-md border border-line-subtle px-2 py-1 text-micro text-ink-primary hover:border-line-strong"
      onClick={() => send?.(intent)}
      data-testid={`work-run-${label.toLowerCase().replace(/\s+/g, '-')}`}
    >
      <Icon className="h-3 w-3" aria-hidden="true" />
      {label}
    </button>
  );

  return (
    <div className="space-y-3" data-testid="work-run-card" data-run-id={str(p.runId)} data-run-state={state}>
      <div className="flex flex-wrap items-center gap-2 text-caption">
        <span className="font-medium text-ink-primary">{str(p.label, 'Run')}</span>
        <StatePill state={state} size="xs" label={idle ? 'Waiting for sign-off' : undefined} tone={idle ? 'warn' : undefined} />
        <span className="ml-auto font-mono tabular-nums text-ink-secondary">
          {formatInt(num(p.routines))} code unit{num(p.routines) === 1 ? '' : 's'} · {formatMoney(num(p.costUsd))}
          {budget !== null && <> of {formatMoney(budget)}</>}
        </span>
      </div>

      <p className="text-caption leading-[1.5] text-ink-secondary" data-testid="work-run-summary" aria-live="polite">
        {str(p.summary)}
      </p>

      <ol className="space-y-1.5" aria-label="Stages">
        {stages.map((s) => {
          const stage = str(s.stage);
          const total = Math.max(1, num(s.total));
          const done = num(s.succeeded);
          const active = num(s.running);
          const bad = num(s.failed);
          const human = stage === 'signoff';
          const pct = (n: number) => `${(n / total) * 100}%`;
          return (
            <li
              key={stage}
              className="grid grid-cols-[7.5rem_1fr_auto] items-center gap-2.5"
              data-testid="work-run-stage"
              data-stage={stage}
              data-done={done}
              data-running={active}
              data-total={num(s.total)}
            >
              <span className="truncate text-caption text-ink-primary">{STAGE_LABEL[stage] ?? stage}</span>
              <div className="flex h-1.5 overflow-hidden rounded-full bg-sunken ring-1 ring-inset ring-line-subtle" aria-hidden="true">
                <div className={clsx('h-full', TONE_DOT.ok)} style={{ width: pct(done) }} />
                <div className="h-full animate-pulse bg-volt motion-reduce:animate-none" style={{ width: pct(active) }} />
                <div className={clsx('h-full', TONE_DOT.fail)} style={{ width: pct(bad) }} />
              </div>
              <span className="text-right font-mono text-micro tabular-nums text-ink-secondary">
                {done}/{num(s.total)}
                {active > 0 && <> · {active} {human ? 'open' : 'working'}</>}
                {bad > 0 && <> · {bad} failed</>}
              </span>
            </li>
          );
        })}
      </ol>

      {running.length > 0 && !isOver(state) && (
        <p className="text-micro text-ink-tertiary" data-testid="work-run-working">
          Working on {running.slice(0, pane ? 12 : 4).map((r) => str(r.name)).join(', ')}
          {running.length > (pane ? 12 : 4) && ` and ${running.length - (pane ? 12 : 4)} more`}
        </p>
      )}

      {waitingCount > 0 && (
        <div className="space-y-1" data-testid="work-run-waiting" data-count={waitingCount}>
          <p className="text-caption text-ink-primary">{waitingCount} waiting for an SME to sign</p>
          <ul className="flex flex-wrap gap-1.5">
            {waiting.slice(0, pane ? 25 : 6).map((w) => (
              <li key={str(w.id)}>
                <Link
                  to={`/subroutines/${str(w.subroutineId)}/review`}
                  className="rounded-md border border-line-subtle px-1.5 py-0.5 font-mono text-micro text-ink-secondary hover:border-line-strong hover:text-ink-primary"
                >
                  {str(w.name)}
                </Link>
              </li>
            ))}
            {waitingCount > Math.min(waiting.length, pane ? 25 : 6) && (
              <li className="self-center text-micro text-ink-tertiary" data-testid="work-run-waiting-more">
                +{waitingCount - Math.min(waiting.length, pane ? 25 : 6)} more in the review queue
              </li>
            )}
          </ul>
        </div>
      )}

      {failures.length > 0 && (
        <ul className="space-y-1" aria-label="Failures" data-testid="work-run-failures">
          {failures.slice(0, pane ? 12 : 3).map((f) => (
            <li key={str(f.id)} className="text-micro text-ink-secondary">
              <span className="font-mono text-ink-primary">{str(f.name)}</span> · {STAGE_LABEL[str(f.kind)] ?? str(f.kind)}: {str(f.error)}
            </li>
          ))}
        </ul>
      )}

      {num(metrics.items) > 0 && (
        <p className="font-mono text-micro tabular-nums text-ink-tertiary" data-testid="work-run-timing" data-peak={num(metrics.peakConcurrency)}>
          agent time {formatDuration(num(metrics.busyMs))} parallel · {formatDuration(num(metrics.serialMs))} one at a time ·{' '}
          {num(metrics.speedup, 1)}× · peak {num(metrics.peakConcurrency)} at once · {formatInt(num(p.calls))} model calls ·{' '}
          {formatInt(num(p.inputTokens) + num(p.outputTokens))} tokens
        </p>
      )}

      {send && (
        <div className="flex flex-wrap gap-1.5">
          {state === 'RUNNING' && action('Pause', `Pause the wave run ${runRef}`, Pause)}
          {state === 'PAUSED' && action('Resume', `Resume the wave run ${runRef}`, Play)}
          {(state === 'RUNNING' || state === 'PAUSED') && action('Cancel', `Cancel the wave run ${runRef}`, X)}
          {failed > 0 && state !== 'CANCELLED' && action('Retry failed', `Retry the failed steps of the wave run ${runRef}`, RotateCcw)}
        </div>
      )}
    </div>
  );
}
