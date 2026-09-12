import { useEffect, useState } from 'react';
import { clsx } from 'clsx';
import { ArrowUpRight } from 'lucide-react';
import { Link } from 'react-router-dom';
import { openRunStream, type AgentId } from '@/lib/conversations';
import { AgentAvatar } from '../AgentAvatar';
import { StatePill } from '../StatePill';
import {
  arr,
  formatEta,
  formatState,
  isTerminalRunState,
  num,
  numOrNull,
  obj,
  relativeTime,
  stateTone,
  str,
} from '../format';
import type { ArtifactRenderProps } from './registry';

type Live = {
  state: string;
  stage: string | null;
  done: number | null;
  total: number | null;
  failed: number;
  etaSeconds: number | null;
  summary: string | null;
  lastLog: string | null;
  ended: boolean;
};

function initial(p: Record<string, unknown>): Live {
  return {
    state: str(p.state, 'RUNNING'),
    stage: str(p.stage) || null,
    done: numOrNull(p.done),
    total: numOrNull(p.total),
    failed: 0,
    etaSeconds: numOrNull(p.etaSeconds),
    summary: str(p.summary) || null,
    lastLog: null,
    ended: isTerminalRunState(str(p.state)),
  };
}

/**
 * `runProgress` — a live run. Subscribes to `GET /api/v1/runs/{runId}/events`
 * and updates itself from `progress` / `stage` / `state` / `done`.
 */
export function RunProgressCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const runId = artifact.refId;
  const [live, setLive] = useState<Live>(() => initial(p));
  const pane = size === 'pane';

  // Re-seed if the backend re-emits the card with fresher props.
  useEffect(() => {
    setLive((prev) => (prev.ended ? prev : { ...prev, ...initial(p), lastLog: prev.lastLog }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId, p.state, p.done, p.total, p.stage, p.summary]);

  useEffect(() => {
    if (!runId || isTerminalRunState(str(p.state))) return;
    const close = openRunStream(
      runId,
      0,
      (evt) => {
        const d = obj(evt.data);
        setLive((prev) => {
          switch (evt.type) {
            case 'progress':
              return {
                ...prev,
                done: numOrNull(d.done) ?? prev.done,
                total: numOrNull(d.total) ?? prev.total,
                failed: num(d.failed, prev.failed),
                etaSeconds: numOrNull(d.etaSeconds),
                stage: evt.stage || prev.stage,
              };
            case 'stage': {
              const label = str(d.label) || evt.stage || prev.stage;
              const step = numOrNull(d.step);
              const of = numOrNull(d.of);
              return {
                ...prev,
                stage: label && step && of ? `${label} (${step}/${of})` : label,
                etaSeconds: null,
              };
            }
            case 'state':
              return {
                ...prev,
                state: str(d.state, prev.state),
                summary: str(d.summary) || evt.message || prev.summary,
                ended: isTerminalRunState(str(d.state, prev.state)),
              };
            case 'log':
              return evt.message ? { ...prev, lastLog: evt.message } : prev;
            case 'done':
              return { ...prev, ended: true };
            default:
              return prev;
          }
        });
      },
      () => setLive((prev) => ({ ...prev, ended: true })),
    );
    return close;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId]);

  const active = !live.ended && !isTerminalRunState(live.state);
  const tone = stateTone(live.state);
  const pct =
    live.total && live.total > 0 && live.done != null
      ? Math.min(100, Math.round((live.done / live.total) * 100))
      : null;
  const eta = formatEta(live.etaSeconds);
  const links = arr<{ label?: unknown; href?: unknown }>(p.links)
    .map((l) => ({ label: str(l?.label), href: str(l?.href) }))
    .filter((l) => l.label && l.href);

  return (
    <div className="space-y-3" data-run-state={live.state}>
      <div className="flex items-center gap-2.5">
        <AgentAvatar agent={(str(p.agent) || null) as AgentId | null} size="sm" working={active} />
        <div className="min-w-0 flex-1">
          <div className="truncate text-body font-medium text-ink-primary">{str(p.label, formatState(str(p.kind)))}</div>
          <div className="truncate text-micro text-ink-tertiary">
            {formatState(str(p.kind))}
            {str(p.startedAt) && <> · started {relativeTime(str(p.startedAt))}</>}
          </div>
        </div>
        <StatePill state={live.state} />
      </div>

      <div>
        <div
          className="h-1.5 w-full overflow-hidden rounded-full bg-sunken ring-1 ring-inset ring-line-subtle"
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={pct ?? undefined}
        >
          <div
            className={clsx(
              'h-full rounded-full transition-[width] duration-slow',
              active ? 'bg-volt' : tone === 'fail' ? 'bg-status-fail' : 'bg-status-ok',
              active && pct == null && 'w-1/3 animate-pulse motion-reduce:animate-none',
            )}
            style={pct != null ? { width: `${pct}%` } : undefined}
          />
        </div>
        <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-0.5 text-caption text-ink-secondary">
          {live.done != null && live.total != null && (
            <span className="font-mono tabular-nums text-ink-primary">
              {live.done}/{live.total}
              {live.failed > 0 && <span className="text-status-fail"> · {live.failed} failed</span>}
            </span>
          )}
          {live.stage && <span className="truncate">{live.stage}</span>}
          {active && eta && <span className="text-ink-tertiary">{eta}</span>}
          {!active && !live.summary && <span className="text-ink-tertiary">{formatState(live.state)}</span>}
        </div>
      </div>

      {live.summary && (
        <p className={clsx('text-caption text-ink-secondary', !pane && 'line-clamp-3')}>{live.summary}</p>
      )}
      {active && live.lastLog && (
        <p className="truncate font-mono text-micro text-ink-tertiary" title={live.lastLog}>
          {live.lastLog}
        </p>
      )}

      {links.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {links.map((l) =>
            l.href.startsWith('/') ? (
              <Link
                key={l.href}
                to={l.href}
                onClick={(e) => e.stopPropagation()}
                className="inline-flex items-center gap-1 rounded-md border border-line-subtle bg-sunken px-2 py-1 text-micro text-ink-secondary hover:border-line hover:text-ink-primary"
              >
                {l.label}
                <ArrowUpRight size={11} aria-hidden="true" />
              </Link>
            ) : (
              <a
                key={l.href}
                href={l.href}
                target="_blank"
                rel="noreferrer"
                onClick={(e) => e.stopPropagation()}
                className="inline-flex items-center gap-1 rounded-md border border-line-subtle bg-sunken px-2 py-1 text-micro text-ink-secondary hover:border-line hover:text-ink-primary"
              >
                {l.label}
                <ArrowUpRight size={11} aria-hidden="true" />
              </a>
            ),
          )}
        </div>
      )}
    </div>
  );
}
