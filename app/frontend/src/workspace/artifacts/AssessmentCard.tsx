/**
 * `assessment` — the 10-minute Assessment of a programme: inventory KPIs,
 * effort and risk bands with their drivers, dependency hotspots, the
 * pattern picture and a recommendation. Card size shows the top half
 * (KPIs, bands, recommendation line); the pane shows everything.
 */
import { clsx } from 'clsx';
import { AlertTriangle, ArrowUpRight, Flame, GitFork } from 'lucide-react';
import { Link } from 'react-router-dom';
import { arr, formatInt, formatLanguage, num, obj, relativeTime, str, strs, TONE_BG, TONE_BORDER, TONE_TEXT, type Tone } from '../format';
import type { ArtifactRenderProps } from './registry';

type Hotspot = { id: string; name: string; callers: number; transitiveCallers: number; inCycle: boolean };

function hotspots(v: unknown): Hotspot[] {
  return arr(v).map((raw) => {
    const h = obj(raw);
    return {
      id: str(h.id),
      name: str(h.name, '—'),
      callers: num(h.callers),
      transitiveCallers: num(h.transitiveCallers),
      inCycle: h.inCycle === true,
    };
  });
}

export function bandTone(band: string): Tone {
  switch (band.toUpperCase()) {
    case 'S':
      return 'ok';
    case 'M':
      return 'info';
    case 'L':
      return 'warn';
    case 'XL':
      return 'fail';
    default:
      return 'neutral';
  }
}

const MODE_LABELS: Record<string, string> = {
  'faithful-1to1': 'Faithful 1:1',
  faithful: 'Faithful 1:1',
  replatform: 'Replatform',
  modernize: 'Modernize',
  modernise: 'Modernize',
  strangler: 'Strangler fig',
};

export function modeLabel(mode: string): string {
  return MODE_LABELS[mode.toLowerCase()] ?? (mode ? mode.charAt(0).toUpperCase() + mode.slice(1).replace(/[-_]/g, ' ') : '—');
}

export function AssessmentCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const pane = size === 'pane';
  const inventory = obj(p.inventory);
  const languages = arr(inventory.languages).map((l) => {
    const o = obj(l);
    return { language: str(o.language), routines: num(o.routines) };
  });
  const spots = hotspots(p.hotspots);
  const cycles = num(p.cycles);
  const patterns = obj(p.patterns);
  const topClusters = arr(patterns.topClusters).map((c) => {
    const o = obj(c);
    return { label: str(o.label, 'Untitled pattern'), memberCount: num(o.memberCount) };
  });
  const analysed = patterns.analysed !== false && (num(patterns.clusters) > 0 || patterns.analysed === true);
  const effort = obj(p.effort);
  const risk = obj(p.risk);
  const weeks = obj(effort.personWeeks);
  const rec = obj(p.recommendation);
  const effortBand = str(effort.band, '—');
  const riskBand = str(risk.band, '—');
  const generatedAt = str(p.generatedAt) || null;
  const href = str(p.href) || (artifact.refId ? `/projects/${artifact.refId}/assessment` : '');

  const kpis: Array<{ label: string; value: string }> = [
    { label: 'Routines', value: formatInt(num(inventory.routines)) },
    { label: 'Files', value: formatInt(num(inventory.files)) },
    { label: 'Lines of code', value: formatInt(num(inventory.loc)) },
    { label: 'Hotspots', value: formatInt(spots.length) },
    { label: 'Cycles', value: formatInt(cycles) },
  ];

  return (
    <div className="space-y-3" data-testid="assessment-card" data-effort={effortBand} data-risk={riskBand}>
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className={clsx('font-medium text-ink-primary', pane ? 'text-h-sm' : 'text-body')}>
          {str(p.corpusName, 'Programme')}
        </span>
        <span className="text-caption text-ink-tertiary">{formatLanguage(str(p.sourceLanguage) || null)}</span>
        {generatedAt && (
          <span className="ml-auto text-micro text-ink-tertiary" title={generatedAt}>
            assessed {relativeTime(generatedAt)}
          </span>
        )}
      </div>

      {/* KPI strip */}
      <div className="grid grid-cols-5 gap-1.5" data-testid="assessment-kpis">
        {kpis.map((k) => (
          <div key={k.label} className="min-w-0 rounded-lg border border-line-subtle bg-sunken px-2 py-1.5">
            <div className="font-mono text-body tabular-nums text-ink-primary">{k.value}</div>
            <div className="truncate text-micro text-ink-tertiary">{k.label}</div>
          </div>
        ))}
      </div>

      {/* Effort + risk bands */}
      <div className="grid gap-2 sm:grid-cols-2">
        <Band
          title="Effort"
          band={effortBand}
          score={num(effort.score)}
          detail={
            num(weeks.low) > 0 || num(weeks.high) > 0
              ? `${formatInt(Math.round(num(weeks.low)))}–${formatInt(Math.round(num(weeks.high)))} person-weeks with the platform`
              : null
          }
          drivers={strs(effort.drivers)}
          pane={pane}
        />
        <Band title="Risk" band={riskBand} score={num(risk.score)} detail={null} drivers={strs(risk.drivers)} pane={pane} />
      </div>

      {/* Recommendation */}
      {(str(rec.mode) || str(rec.summary)) && (
        <div
          className="rounded-lg border border-volt/30 bg-volt/[.06] px-3 py-2.5"
          data-testid="assessment-recommendation"
        >
          <div className="flex flex-wrap items-center gap-2 text-caption">
            <span className="text-micro font-medium uppercase tracking-wide text-ink-tertiary">Recommend</span>
            <span className="font-medium text-ink-primary">{modeLabel(str(rec.mode))}</span>
            {str(rec.targetStack) && (
              <>
                <span className="text-ink-tertiary">on</span>
                <span className="rounded border border-line-subtle bg-raised px-1.5 py-px font-mono text-micro text-ink-secondary">
                  {str(rec.targetStack)}
                </span>
              </>
            )}
          </div>
          {str(rec.summary) && (
            <p className={clsx('mt-1 text-caption leading-[1.5] text-ink-secondary', !pane && 'line-clamp-2')}>{str(rec.summary)}</p>
          )}
        </div>
      )}

      {pane && (
        <>
          {/* Hotspots */}
          <section>
            <h4 className="mb-1.5 flex items-center gap-1.5 text-micro font-medium uppercase tracking-wide text-ink-tertiary">
              <Flame size={12} aria-hidden="true" />
              Dependency hotspots
              <span className="font-mono normal-case tracking-normal">{spots.length}</span>
            </h4>
            {spots.length === 0 ? (
              <p className="text-caption text-ink-tertiary">No hotspots — nothing is called by a large share of the estate.</p>
            ) : (
              <ul className="divide-y divide-line-subtle rounded-lg border border-line-subtle">
                {spots.map((h, i) => (
                  <li key={h.id || `${h.name}-${i}`} className="flex items-center gap-2 px-2.5 py-1.5 text-caption">
                    {h.id ? (
                      <Link
                        to={`/subroutines/${h.id}`}
                        onClick={(e) => e.stopPropagation()}
                        className="min-w-0 flex-1 truncate font-mono text-ink-primary underline-offset-2 hover:underline"
                      >
                        {h.name}
                      </Link>
                    ) : (
                      <span className="min-w-0 flex-1 truncate font-mono text-ink-primary">{h.name}</span>
                    )}
                    {h.inCycle && (
                      <span className="inline-flex items-center gap-1 rounded-full border border-status-warn/40 px-1.5 py-px text-micro text-status-warn" title="Part of a call cycle">
                        <GitFork size={10} aria-hidden="true" />
                        cycle
                      </span>
                    )}
                    <span className="shrink-0 font-mono text-micro tabular-nums text-ink-secondary" title="Direct callers">
                      {formatInt(h.callers)} callers
                    </span>
                    <span className="shrink-0 font-mono text-micro tabular-nums text-ink-tertiary" title="Transitive callers">
                      {formatInt(h.transitiveCallers)} transitive
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {/* Patterns */}
          <section>
            <h4 className="mb-1.5 text-micro font-medium uppercase tracking-wide text-ink-tertiary">Patterns</h4>
            {!analysed ? (
              <p className="text-caption text-ink-tertiary">No pattern analysis yet — survey the patterns to sharpen the estimate.</p>
            ) : (
              <div className="space-y-1.5">
                <p className="text-caption text-ink-secondary">
                  <span className="font-mono tabular-nums text-ink-primary">{formatInt(num(patterns.clusters))}</span> pattern
                  {num(patterns.clusters) === 1 ? '' : 's'} ·{' '}
                  <span className="font-mono tabular-nums text-ink-primary">{formatInt(num(patterns.multiMember))}</span> shared ·{' '}
                  <span className="font-mono tabular-nums text-ink-primary">{formatInt(num(patterns.singletons))}</span> singleton
                  {num(patterns.singletons) === 1 ? '' : 's'}
                </p>
                {topClusters.length > 0 && (
                  <div className="flex flex-wrap gap-1">
                    {topClusters.map((c, i) => (
                      <span key={`${c.label}-${i}`} className="inline-flex items-center gap-1.5 rounded-full border border-line-subtle bg-sunken px-2 py-0.5 text-micro text-ink-secondary">
                        <span className="truncate">{c.label}</span>
                        <span className="font-mono tabular-nums text-ink-primary">{formatInt(c.memberCount)}</span>
                      </span>
                    ))}
                  </div>
                )}
              </div>
            )}
          </section>

          {/* Languages */}
          {languages.length > 1 && (
            <section>
              <h4 className="mb-1.5 text-micro font-medium uppercase tracking-wide text-ink-tertiary">Languages</h4>
              <div className="flex flex-wrap gap-1">
                {languages.map((l) => (
                  <span key={l.language} className="inline-flex items-center gap-1.5 rounded-full border border-line-subtle bg-sunken px-2 py-0.5 text-micro text-ink-secondary">
                    {formatLanguage(l.language)}
                    <span className="font-mono tabular-nums text-ink-primary">{formatInt(l.routines)}</span>
                  </span>
                ))}
              </div>
            </section>
          )}
        </>
      )}

      {!pane && href && (
        <div className="flex items-center justify-between text-micro text-ink-tertiary">
          <span>Open in the pane for hotspots, patterns and drivers.</span>
          <Link
            to={href}
            onClick={(e) => e.stopPropagation()}
            className="inline-flex items-center gap-1 rounded-md border border-line-subtle bg-sunken px-2 py-1 text-ink-secondary hover:border-line hover:text-ink-primary"
          >
            Full assessment
            <ArrowUpRight size={11} aria-hidden="true" />
          </Link>
        </div>
      )}
    </div>
  );
}

function Band({
  title,
  band,
  score,
  detail,
  drivers,
  pane,
}: {
  title: string;
  band: string;
  score: number;
  detail: string | null;
  drivers: string[];
  pane: boolean;
}) {
  const tone = bandTone(band);
  const shown = pane ? drivers : drivers.slice(0, 1);
  return (
    <div className={clsx('rounded-lg border px-3 py-2.5', TONE_BORDER[tone], TONE_BG[tone])} data-testid={`assessment-${title.toLowerCase()}`}>
      <div className="flex items-center gap-2">
        <span className="text-micro font-medium uppercase tracking-wide text-ink-tertiary">{title}</span>
        <span className={clsx('font-mono text-h-md font-semibold leading-none', TONE_TEXT[tone])}>{band}</span>
        {score > 0 && (
          <span className="ml-auto font-mono text-micro tabular-nums text-ink-tertiary" title="Score out of 5">
            {score}/5
          </span>
        )}
      </div>
      {detail && <p className="mt-1 text-caption text-ink-secondary">{detail}</p>}
      {shown.length > 0 && (
        <ul className="mt-1.5 space-y-0.5">
          {shown.map((d, i) => (
            <li key={i} className={clsx('flex items-start gap-1.5 text-micro leading-[1.5] text-ink-secondary', !pane && 'line-clamp-1')}>
              <AlertTriangle size={10} className="mt-[3px] shrink-0 text-ink-tertiary" aria-hidden="true" />
              <span>{d}</span>
            </li>
          ))}
          {!pane && drivers.length > 1 && (
            <li className="text-micro text-ink-tertiary">+{drivers.length - 1} more driver{drivers.length - 1 === 1 ? '' : 's'}</li>
          )}
        </ul>
      )}
    </div>
  );
}
