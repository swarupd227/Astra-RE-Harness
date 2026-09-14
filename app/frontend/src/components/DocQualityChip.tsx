import { useState } from 'react';
import { ChevronDown, ChevronRight, ShieldAlert, ShieldCheck } from 'lucide-react';
import { clsx } from 'clsx';
import type { DocQuality } from '@/lib/api';

function tone(q: DocQuality): 'ok' | 'warn' | 'fail' {
  const failedChecks = q.checks.some((c) => !c.passed);
  if (q.score === null) return failedChecks ? 'warn' : 'ok';
  if (q.score < q.threshold) return 'fail';
  return failedChecks ? 'warn' : 'ok';
}

const TONE = {
  ok: 'border-status-ok/40 bg-status-ok/10 text-status-ok',
  warn: 'border-status-warn/40 bg-status-warn/10 text-status-warn',
  fail: 'border-status-fail/40 bg-status-fail/10 text-status-fail',
} as const;

/**
 * The critic's verdict on a doc section, in one chip: score against the
 * threshold and how many deterministic checks passed. Expands to the
 * per-axis scores, the fixes the critic asked for, and any failed check's
 * problems — so an SME sees why a section is flagged before signing it.
 */
export function DocQualityChip({ quality }: { quality: DocQuality }) {
  const [open, setOpen] = useState(false);
  const passed = quality.checks.filter((c) => c.passed).length;
  const t = tone(quality);
  const label = quality.score === null
    ? `${passed}/${quality.checks.length} checks`
    : `Quality ${quality.score}/100 · ${passed}/${quality.checks.length} checks`;
  return (
    <div className="relative" data-testid="doc-quality">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className={clsx('inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 font-mono text-[11px]', TONE[t])}
        title={quality.critique?.summary ?? undefined}
      >
        {t === 'ok' ? <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" /> : <ShieldAlert className="h-3.5 w-3.5" aria-hidden="true" />}
        {label}
        {quality.revised && <span className="text-ink-tertiary">· revised</span>}
        {open ? <ChevronDown className="h-3 w-3" aria-hidden="true" /> : <ChevronRight className="h-3 w-3" aria-hidden="true" />}
      </button>
      {open && (
        <div className="absolute left-0 top-full z-20 mt-1.5 w-[360px] rounded-lg border border-line bg-raised p-3 text-caption text-ink-secondary shadow-lg">
          {quality.critique && (
            <>
              <p className="text-ink-primary">{quality.critique.summary}</p>
              <dl className="mt-2 grid grid-cols-4 gap-2 font-mono text-[11px]">
                {(['accuracy', 'completeness', 'clarity', 'structure'] as const).map((k) => (
                  <div key={k}>
                    <dt className="text-ink-tertiary">{k}</dt>
                    <dd className="text-ink-primary">{quality.critique!.scores[k]}</dd>
                  </div>
                ))}
              </dl>
              {quality.critique.fixes.length > 0 && (
                <ul className="mt-2 list-disc space-y-0.5 pl-4">
                  {quality.critique.fixes.slice(0, 5).map((f, i) => <li key={i}>{f}</li>)}
                </ul>
              )}
            </>
          )}
          <ul className="mt-2 space-y-1">
            {quality.checks.map((c) => (
              <li key={c.name} className={clsx('flex flex-col', !c.passed && 'text-status-warn')}>
                <span className="font-mono text-[11px]">{c.passed ? '✓' : '✗'} {c.name}</span>
                {!c.passed && c.problems.slice(0, 3).map((p, i) => <span key={i} className="pl-4 text-ink-tertiary">{p}</span>)}
              </li>
            ))}
          </ul>
          {quality.note && <p className="mt-2 text-ink-tertiary">{quality.note}</p>}
          <p className="mt-2 text-[10px] text-ink-tertiary">threshold {quality.threshold}{quality.criticModel ? ` · critic ${quality.criticModel}` : ''}</p>
        </div>
      )}
    </div>
  );
}
