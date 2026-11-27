import { useMemo, useState } from 'react';
import { Loader2, ShieldAlert } from 'lucide-react';
import { API_BASE, type QualityCategory, type QualityFinding, type QualityMetrics, type QualitySeverity, type ValidationRun } from '@/lib/api';
import { Card, CardBody } from '@/components/Card';
import { withToken } from '@/lib/auth';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';

const SEVERITIES: QualitySeverity[] = ['blocker', 'critical', 'major', 'minor', 'info'];
const CATEGORIES: { id: QualityCategory | 'all'; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'security', label: 'Security' },
  { id: 'standards', label: 'Coding standards' },
  { id: 'maintainability', label: 'Maintainability' },
  { id: 'review', label: 'Spec & correctness' },
];

const TOOL_LABELS: Record<string, string> = {
  spotbugs: 'SpotBugs + FindSecBugs',
  pmd: 'PMD',
  checkstyle: 'Checkstyle',
  ruff: 'ruff',
  bandit: 'bandit',
  dependencies: 'Known vulnerabilities (OSV.dev)',
  'java-tools': 'Java tools',
  'python-tools': 'Python tools',
};

const SEVERITY_TEXT: Record<QualitySeverity, string> = {
  blocker: 'text-status-fail',
  critical: 'text-status-fail',
  major: 'text-status-warn',
  minor: 'text-ink-secondary',
  info: 'text-ink-tertiary',
};

/**
 * The QUALITY gate on the validation report: a verdict line, counts by severity, and the findings grouped by
 * category — each with where it is, why it matters and how to fix it. Blocker and critical findings fail the gate.
 */
export function QualityFindingsCard({
  run,
  aiReviewRun,
  onRun,
  isRunning,
  errorMessage,
}: {
  run: ValidationRun | undefined;
  /** The most recent QUALITY run that included the AI review — shown when `run` itself has none. */
  aiReviewRun?: ValidationRun;
  onRun: () => void;
  isRunning: boolean;
  errorMessage: string | undefined;
}) {
  const latest = (run?.metrics ?? null) as QualityMetrics | null;
  const earlierAi = !latest?.aiReview && aiReviewRun && aiReviewRun.id !== run?.id
    ? (aiReviewRun.metrics as QualityMetrics | null)
    : null;
  // The rule findings come from the latest run; the AI findings from the latest run that had a review.
  const metrics: QualityMetrics | null = latest && earlierAi
    ? {
        ...latest,
        aiReview: earlierAi.aiReview ?? null,
        findings: [...latest.findings, ...earlierAi.findings.filter((f) => f.source === 'ai')],
        total: latest.total + earlierAi.findings.filter((f) => f.source === 'ai').length,
        bySeverity: Object.fromEntries(
          Object.entries(latest.bySeverity).map(([k, v]) => [k, v + earlierAi.findings.filter((f) => f.source === 'ai' && f.severity === k).length]),
        ) as QualityMetrics['bySeverity'],
        byCategory: Object.fromEntries(
          Object.entries(latest.byCategory).map(([k, v]) => [k, v + earlierAi.findings.filter((f) => f.source === 'ai' && f.category === k).length]),
        ) as QualityMetrics['byCategory'],
      }
    : latest;
  const [category, setCategory] = useState<QualityCategory | 'all'>('all');
  const [open, setOpen] = useState<string | null>(null);
  const findings = useMemo(
    () => (metrics?.findings ?? []).filter((f) => category === 'all' || f.category === category),
    [metrics, category],
  );
  const edge =
    run?.status === 'PASSED' ? 'border-l-status-ok' : run?.status === 'FAILED' || run?.status === 'ERRORED' ? 'border-l-status-fail' : 'border-l-line-subtle';

  return (
    <Card className={`border-l-2 ${edge}`} data-testid="validation-card-quality">
      <CardBody className="space-y-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="flex items-start gap-3">
            <span className="mt-0.5 flex h-9 w-9 items-center justify-center rounded-md bg-volt/10 text-volt-ink">
              <ShieldAlert className="h-5 w-5" aria-hidden="true" />
            </span>
            <div>
              <h2 className="text-h-md font-semibold text-ink-primary">Quality</h2>
              <p className="mt-0.5 text-caption text-ink-tertiary">
                Security, coding standards and maintainability · blocker and critical findings fail the gate
              </p>
            </div>
          </div>
          <div className="flex items-center gap-2">
            {run && (
              <Badge tone={run.status === 'PASSED' ? 'signed' : run.status === 'RUNNING' ? 'draft' : 'failed'}>
                {run.status === 'PASSED' ? 'passed' : run.status.toLowerCase()}
              </Badge>
            )}
            <Button variant="secondary" onClick={onRun} disabled={isRunning} data-testid="run-quality">
              {isRunning && <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />}
              {run ? 'Run again' : 'Run quality checks'}
            </Button>
          </div>
        </div>

        {errorMessage && <p className="text-caption text-status-fail">{errorMessage}</p>}

        {!run ? (
          <p className="text-body text-ink-tertiary">
            Not run yet. The check reads the generated package for secrets, injection-prone queries, unsafe calls,
            disabled certificate checks and open CORS, and for the coding standards a reviewer raises first.
          </p>
        ) : (
          <>
            <p className="text-body text-ink-secondary" data-testid="quality-summary">{run.summary}</p>
            {metrics?.tools && Object.keys(metrics.tools).length > 0 && (
              <p className="font-mono text-caption text-ink-tertiary" data-testid="quality-tools">
                {Object.entries(metrics.tools)
                  .filter(([name]) => name !== 'compile')
                  .map(([name, t]) => (t.ok ? `${TOOL_LABELS[name] ?? name} ${t.count}` : `${TOOL_LABELS[name] ?? name} did not run`))
                  .join(' · ')}
              </p>
            )}
            {metrics?.sbom && run && (
              <a
                href={withToken(`${API_BASE}/api/v1/scaffolds/${run.scaffoldId}/sbom`)}
                target="_blank"
                rel="noreferrer"
                className="inline-block text-caption text-ink-secondary underline decoration-line-subtle underline-offset-2 hover:text-ink-primary"
                data-testid="quality-sbom"
              >
                Bill of materials (CycloneDX) · {metrics.sbom.components} libraries
              </a>
            )}
            {metrics?.aiReview && (
              <p className="text-caption text-ink-tertiary" data-testid="quality-ai-review">
                {metrics.aiReview.status === 'reviewed'
                  ? `AI review (${metrics.aiReview.model ?? 'model'}): ${metrics.aiReview.kept} finding${metrics.aiReview.kept === 1 ? '' : 's'} kept of ${metrics.aiReview.proposed} proposed, each checked against the code it quotes; advisory, never blocking · US$ ${metrics.aiReview.costUsd.toFixed(3)}`
                  : metrics.aiReview.note ?? `AI review ${metrics.aiReview.status}`}
                {earlierAi && aiReviewRun && ` · from the review on ${new Date(aiReviewRun.startedAt).toLocaleString()}`}
              </p>
            )}
            {metrics && metrics.total > 0 && (
              <>
                <dl className="flex flex-wrap gap-x-5 gap-y-1 font-mono text-caption">
                  {SEVERITIES.map((s) => (
                    <div key={s} className="flex items-baseline gap-1.5">
                      <dt className="text-ink-tertiary">{s}</dt>
                      <dd className={metrics.bySeverity[s] > 0 ? SEVERITY_TEXT[s] : 'text-ink-tertiary'}>{metrics.bySeverity[s]}</dd>
                    </div>
                  ))}
                </dl>
                <div className="flex flex-wrap gap-1.5" role="tablist" aria-label="Finding category">
                  {CATEGORIES.map((c) => {
                    const count = c.id === 'all' ? metrics.findings.length : metrics.findings.filter((f) => f.category === c.id).length;
                    if (c.id === 'review' && count === 0) return null;
                    const active = category === c.id;
                    return (
                      <button
                        key={c.id}
                        type="button"
                        role="tab"
                        aria-selected={active}
                        onClick={() => setCategory(c.id)}
                        className={`rounded-md border px-2.5 py-1 text-caption ${active ? 'border-volt bg-volt/10 text-ink-primary' : 'border-line-subtle text-ink-secondary hover:bg-sunken'}`}
                        data-testid={`quality-tab-${c.id}`}
                      >
                        {c.label} · {count}
                      </button>
                    );
                  })}
                </div>
                <ul className="divide-y divide-line-subtle rounded-md border border-line-subtle" data-testid="quality-findings">
                  {findings.map((f, i) => (
                    <FindingRow key={`${f.rule}-${f.file}-${f.line}-${i}`} finding={f} open={open === `${i}`} onToggle={() => setOpen(open === `${i}` ? null : `${i}`)} />
                  ))}
                  {findings.length === 0 && <li className="px-3 py-3 text-caption text-ink-tertiary">Nothing in this category.</li>}
                </ul>
                {metrics.truncated && (
                  <p className="text-caption text-ink-tertiary">Only the first findings are listed; the full report is in the run's log below.</p>
                )}
              </>
            )}
          </>
        )}
      </CardBody>
    </Card>
  );
}

function FindingRow({ finding: f, open, onToggle }: { finding: QualityFinding; open: boolean; onToggle: () => void }) {
  return (
    <li>
      <button type="button" onClick={onToggle} aria-expanded={open} className="flex w-full items-start gap-3 px-3 py-2 text-left hover:bg-sunken">
        <span className={`w-16 shrink-0 font-mono text-caption ${SEVERITY_TEXT[f.severity]}`}>{f.severity}</span>
        <span className="min-w-0 flex-1">
          <span className="block text-body text-ink-primary">
            {f.message}
            {f.source === 'ai' && (
              <span className="ml-2 rounded-sm bg-status-info/15 px-1.5 py-0.5 align-middle font-mono text-[10px] text-status-info">AI review</span>
            )}
          </span>
          <span className="block truncate font-mono text-caption text-ink-tertiary">
            {f.file}{f.line ? `:${f.line}` : ''} · {f.rule}
          </span>
        </span>
      </button>
      {open && (
        <div className="space-y-1 px-3 pb-3 pl-[5.75rem] text-caption">
          <p className="text-ink-secondary"><span className="text-ink-tertiary">Why it matters: </span>{f.why}</p>
          <p className="text-ink-secondary"><span className="text-ink-tertiary">Fix: </span>{f.fix}</p>
        </div>
      )}
    </li>
  );
}
