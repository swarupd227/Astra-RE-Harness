import { clsx } from 'clsx';
import { StatePill } from '../StatePill';
import { absoluteTime, arr, formatInt, num, obj, relativeTime, str, TONE_DOT, type Tone } from '../format';
import type { ArtifactRenderProps } from './registry';

type Claim = {
  section: string;
  id: string;
  text: string;
  review: 'accept' | 'reject' | 'edit' | 'question' | null;
  citation: string | null;
};

const SECTION_LABELS: Record<string, string> = {
  inputs: 'Inputs',
  outputs: 'Outputs',
  invariants: 'Invariants',
  side_effects: 'Side effects',
  sideEffects: 'Side effects',
  edge_cases: 'Edge cases',
  edgeCases: 'Edge cases',
  open_questions: 'Open questions',
  openQuestions: 'Open questions',
  section_contracts: 'Section contracts',
};

const SECTION_ORDER = ['inputs', 'outputs', 'invariants', 'side_effects', 'sideEffects', 'edge_cases', 'edgeCases', 'open_questions', 'openQuestions'];

function reviewTone(r: Claim['review']): Tone {
  switch (r) {
    case 'accept':
      return 'ok';
    case 'reject':
      return 'fail';
    case 'edit':
      return 'warn';
    case 'question':
      return 'info';
    default:
      return 'neutral';
  }
}

function reviewLabel(r: Claim['review']): string {
  switch (r) {
    case 'accept':
      return 'Accepted';
    case 'reject':
      return 'Rejected';
    case 'edit':
      return 'Edited';
    case 'question':
      return 'Questioned';
    default:
      return 'Unreviewed';
  }
}

function claims(v: unknown): Claim[] {
  return arr(v).map((raw, i) => {
    const c = obj(raw);
    const review = str(c.review) as Claim['review'] | '';
    return {
      section: str(c.section, 'claims'),
      id: str(c.id, `claim-${i}`),
      text: str(c.text),
      review: review === 'accept' || review === 'reject' || review === 'edit' || review === 'question' ? review : null,
      citation: str(c.citation) || null,
    };
  });
}

function sectionLabel(key: string): string {
  return SECTION_LABELS[key] ?? key.charAt(0).toUpperCase() + key.slice(1).replace(/[_-]/g, ' ');
}

/** `specSummary` — claims grouped by section, review dots, citations, sign-off. */
export function SpecSummaryCard({ artifact, size }: ArtifactRenderProps) {
  const p = artifact.props;
  const counts = obj(p.counts);
  const all = claims(p.claims);
  const pane = size === 'pane';
  const signedAt = str(p.signedAt) || null;
  const signer = str(p.signerDisplay) || null;

  const grouped = new Map<string, Claim[]>();
  for (const c of all) {
    const list = grouped.get(c.section) ?? [];
    list.push(c);
    grouped.set(c.section, list);
  }
  const sections = [...grouped.keys()].sort((a, b) => {
    const ia = SECTION_ORDER.indexOf(a);
    const ib = SECTION_ORDER.indexOf(b);
    return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib);
  });

  // Card size: a taste of the spec (first six claims across sections).
  let budget = pane ? Number.POSITIVE_INFINITY : 6;

  const stats: Array<{ label: string; value: number }> = [
    { label: 'Invariants', value: num(counts.invariants) },
    { label: 'Side effects', value: num(counts.sideEffects) },
    { label: 'Edge cases', value: num(counts.edgeCases) },
    { label: 'Open questions', value: num(counts.openQuestions) },
  ];

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-body text-ink-primary">{str(p.routineName, 'Routine')}</span>
        <StatePill state={str(p.state)} size="xs" />
      </div>

      <div className="grid grid-cols-4 gap-2">
        {stats.map((s) => (
          <div key={s.label} className="rounded-lg border border-line-subtle bg-sunken px-2 py-1.5">
            <div className="font-mono text-body tabular-nums text-ink-primary">{formatInt(s.value)}</div>
            <div className="truncate text-micro text-ink-tertiary">{s.label}</div>
          </div>
        ))}
      </div>

      {all.length > 0 && (
        <div className="space-y-3">
          {sections.map((sec) => {
            if (budget <= 0) return null;
            const list = grouped.get(sec) ?? [];
            const shown = list.slice(0, budget);
            budget -= shown.length;
            return (
              <div key={sec}>
                <div className="mb-1 text-micro font-medium uppercase tracking-wide text-ink-tertiary">
                  {sectionLabel(sec)}
                  <span className="ml-1.5 font-mono normal-case tracking-normal">{list.length}</span>
                </div>
                <ul className="space-y-1.5">
                  {shown.map((c) => (
                    <li key={c.id} className="flex items-start gap-2 text-caption">
                      <span
                        className={clsx('mt-[7px] h-1.5 w-1.5 shrink-0 rounded-full', TONE_DOT[reviewTone(c.review)])}
                        title={reviewLabel(c.review)}
                        aria-label={reviewLabel(c.review)}
                      />
                      <span className={clsx('min-w-0 flex-1 leading-[1.5] text-ink-primary/90', !pane && 'line-clamp-2')}>
                        {c.text || <span className="italic text-ink-tertiary">(empty claim)</span>}
                      </span>
                      {c.citation && (
                        <span
                          className="shrink-0 rounded border border-line-subtle bg-sunken px-1 py-px font-mono text-micro text-ink-tertiary"
                          title={`Source lines ${c.citation}`}
                        >
                          {c.citation}
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            );
          })}
          {!pane && all.length > 6 && (
            <div className="text-micro text-ink-tertiary">+{all.length - 6} more claims — open in the pane to see all</div>
          )}
        </div>
      )}

      <div className="flex items-center gap-2 border-t border-line-subtle pt-2 text-caption">
        {signedAt ? (
          <>
            <span className={clsx('h-1.5 w-1.5 rounded-full', TONE_DOT.ok)} aria-hidden="true" />
            <span className="text-ink-secondary">
              Signed {signer ? <>by <span className="text-ink-primary">{signer}</span> </> : null}
              <span title={absoluteTime(signedAt)}>{relativeTime(signedAt)}</span>
            </span>
          </>
        ) : (
          <>
            <span className={clsx('h-1.5 w-1.5 rounded-full', TONE_DOT.warn)} aria-hidden="true" />
            <span className="text-ink-tertiary">Not yet signed</span>
          </>
        )}
      </div>
    </div>
  );
}
