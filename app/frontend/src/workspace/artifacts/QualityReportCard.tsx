import { clsx } from 'clsx';
import { FileDown } from 'lucide-react';
import { API_BASE } from '@/lib/api';
import { StatePill } from '../StatePill';
import { absoluteTime, arr, num, numOrNull, obj, relativeTime, stateTone, str, TONE_BG, TONE_BORDER, TONE_TEXT, type Tone } from '../format';
import type { ArtifactRenderProps } from './registry';

const CHECK_LABEL: Record<string, string> = {
  ran: 'Ran',
  'failed-to-run': 'Could not run',
  'not-run': 'Not run',
  'not-applicable': 'Not applicable',
  'not-available': 'Not available yet',
};

function checkTone(status: string): Tone {
  if (status === 'ran') return 'ok';
  if (status === 'failed-to-run') return 'fail';
  if (status === 'not-run') return 'warn';
  return 'neutral';
}

function PdfLink({ href, testId }: { href: string; testId: string }) {
  return (
    <a
      href={`${API_BASE}${href}`}
      target="_blank"
      rel="noreferrer"
      data-testid={testId}
      className="inline-flex items-center gap-1.5 rounded-md border border-line px-2.5 py-1 text-caption text-ink-primary hover:border-line-strong focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
    >
      <FileDown className="h-3.5 w-3.5" aria-hidden />
      Download PDF
    </a>
  );
}

/** `qualityReport` — what was checked on one routine's generated code, with which tool and version, and the verdict. */
export function QualityReportCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const subject = obj(p.subject);
  const verdict = obj(p.verdict);
  const spec = obj(p.spec);
  const findings = obj(p.findings);
  const bySeverity = obj(findings.bySeverity);
  const tests = obj(p.tests);
  const deps = obj(p.dependencies);
  const pane = size === 'pane';
  const scaffoldId = str(subject.scaffoldId) || (artifact.refId ?? '');
  const vTone = stateTone(str(verdict.status));
  const checks = arr(p.checks).map(obj);
  const shown = pane ? checks : checks.filter((c) => str(c.status) !== 'not-applicable');

  return (
    <div className="space-y-3" data-testid="quality-report-card">
      <div className="flex flex-wrap items-center gap-2 text-caption">
        <span className="font-mono text-body text-ink-primary">{str(subject.routine, 'Routine')}</span>
        {str(subject.targetStack) && <span className="text-ink-tertiary">→ {str(subject.targetStack)}</span>}
        <span className="ml-auto">{scaffoldId && <PdfLink href={`/api/v1/scaffolds/${scaffoldId}/quality-report.pdf`} testId="quality-report-pdf" />}</span>
      </div>

      <div className={clsx('rounded-lg border px-3 py-2', TONE_BORDER[vTone], TONE_BG[vTone])} data-testid="quality-report-verdict">
        <div className="flex items-center gap-2">
          <StatePill state={str(verdict.status)} size="xs" />
          <span className="text-caption text-ink-secondary">
            Spec {str(spec.state).toLowerCase() || 'unknown'} · {num(spec.claimCount)} claims
            {str(spec.signedBy) && ` · signed by ${str(spec.signedBy)}`}
          </span>
        </div>
        <p className="mt-1 text-body text-ink-primary">{str(verdict.reason)}</p>
      </div>

      <div className={clsx('grid gap-2', pane ? 'grid-cols-1' : 'grid-cols-2')}>
        {arr(p.gates).map(obj).map((g) => {
          const tone = stateTone(str(g.status));
          return (
            <div key={str(g.stage)} className={clsx('rounded-lg border px-3 py-2', TONE_BORDER[tone], TONE_BG[tone])}>
              <div className="flex items-center justify-between gap-2">
                <span className="text-caption font-medium text-ink-primary">{str(g.title)}</span>
                <StatePill state={str(g.status)} size="xs" />
              </div>
              <div className="mt-1 text-micro text-ink-secondary">
                <span className={clsx(!pane && 'line-clamp-2')}>{str(g.summary)}</span>
                {str(g.startedAt) && (
                  <span className="block text-ink-tertiary" title={absoluteTime(str(g.startedAt))}>
                    {relativeTime(str(g.startedAt))}
                  </span>
                )}
              </div>
            </div>
          );
        })}
      </div>

      <div>
        <div className="mb-1 text-micro uppercase tracking-wide text-ink-tertiary">Checks inside the quality gate</div>
        <ul className="divide-y divide-line rounded-lg border border-line" data-testid="quality-report-checks">
          {shown.map((c) => {
            const status = str(c.status);
            const tone = checkTone(status);
            const n = numOrNull(c.findings);
            return (
              <li key={str(c.id)} className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 px-3 py-1.5 text-caption">
                <span className="text-ink-primary">{str(c.name)}</span>
                {str(c.version) && <span className="font-mono text-micro text-ink-tertiary">{str(c.version)}</span>}
                <span className={clsx('ml-auto', TONE_TEXT[tone])}>
                  {CHECK_LABEL[status] ?? status}
                  {n !== null && status === 'ran' && ` · ${n} finding${n === 1 ? '' : 's'}`}
                </span>
                {str(c.note) && pane && <span className="basis-full text-micro text-ink-tertiary">{str(c.note)}</span>}
              </li>
            );
          })}
        </ul>
      </div>

      <div className="flex flex-wrap gap-x-4 gap-y-1 text-caption text-ink-secondary">
        <span>
          {num(findings.total)} findings · <span className={num(findings.blocking) > 0 ? 'text-status-fail' : ''}>{num(findings.blocking)} blocking</span>
          {['critical', 'major', 'minor'].map((s) => (num(bySeverity[s]) > 0 ? ` · ${num(bySeverity[s])} ${s}` : '')).join('')}
        </span>
        {numOrNull(tests.total) !== null && (
          <span>
            Tests {num(tests.passed)}/{num(tests.total)} passed
            {numOrNull(tests.lineCoveragePercent) !== null && ` · ${num(tests.lineCoveragePercent).toFixed(1)}% lines covered`}
          </span>
        )}
        {numOrNull(deps.components) !== null && (
          <span>
            {num(deps.components)} libraries · {num(deps.vulnerable)} with a known vulnerability
          </span>
        )}
      </div>
    </div>
  );
}

/** `qualityRollup` — one row per routine in a programme, with its verdict. */
export function QualityRollupCard({ artifact }: ArtifactRenderProps) {
  const p = artifact.props;
  const corpusId = str(p.corpusId) || (artifact.refId ?? '');
  const rows = arr(p.rows).map(obj);
  return (
    <div className="space-y-3" data-testid="quality-rollup-card">
      <div className="flex flex-wrap items-center gap-2 text-caption text-ink-secondary">
        <span>
          {num(p.routines)} routines · <span className="text-status-ok">{num(p.passed)} passed</span> ·{' '}
          <span className="text-status-fail">{num(p.failed)} failed</span> · <span className="text-status-warn">{num(p.incomplete)} incomplete</span>
        </span>
        <span className="ml-auto">{corpusId && <PdfLink href={`/api/v1/corpora/${corpusId}/quality-report?format=pdf`} testId="quality-rollup-pdf" />}</span>
      </div>
      <ul className="divide-y divide-line rounded-lg border border-line">
        {rows.slice(0, 50).map((r) => (
          <li key={str(r.scaffoldId)} className="flex flex-wrap items-center gap-2 px-3 py-1.5 text-caption">
            <span className="font-mono text-ink-primary">{str(r.routine)}</span>
            <span className="text-micro text-ink-tertiary">{str(r.targetStack)}</span>
            <span className="ml-auto flex items-center gap-2">
              {num(r.blocking) > 0 && <span className="text-status-fail">{num(r.blocking)} blocking</span>}
              <StatePill state={str(r.verdict)} size="xs" />
              <a
                href={`${API_BASE}/api/v1/scaffolds/${str(r.scaffoldId)}/quality-report.pdf`}
                target="_blank"
                rel="noreferrer"
                className="text-micro text-ink-tertiary underline decoration-line-subtle underline-offset-2 hover:text-ink-primary"
              >
                PDF
              </a>
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
