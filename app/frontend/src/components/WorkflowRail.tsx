import { Check } from 'lucide-react';
import { clsx } from 'clsx';

/**
 * The workflow spine — five steps from ingested source to generated code,
 * with who owns each one.
 *
 * Until now the only signal of "where am I" was a state badge (DRAFT /
 * IN_REVIEW / SIGNED / SCAFFOLDED) that you had to know how to decode, and
 * the only signal of "why is there no button" was the button's absence —
 * CTAs render per persona, so the wrong persona sees a dead end. This rail
 * makes both legible, and is rendered on the routine, review, and artifact
 * surfaces so the answer is the same wherever you are.
 */

export type WorkflowStepKey = 'ingest' | 'extract' | 'review' | 'sign' | 'generate';

export type Persona = 'engineer' | 'sme' | 'observer' | 'admin';

type Step = {
  key: WorkflowStepKey;
  label: string;
  /** Persona that can actually perform this step. */
  owner: Persona;
  ownerLabel: string;
};

export const WORKFLOW_STEPS: Step[] = [
  { key: 'ingest',   label: 'Ingest',      owner: 'engineer', ownerLabel: 'Engineer' },
  { key: 'extract',  label: 'Extract spec', owner: 'engineer', ownerLabel: 'Engineer' },
  { key: 'review',   label: 'SME review',  owner: 'sme',      ownerLabel: 'SME' },
  { key: 'sign',     label: 'Sign-off',    owner: 'sme',      ownerLabel: 'SME' },
  { key: 'generate', label: 'Generate code', owner: 'engineer', ownerLabel: 'Engineer' },
];

/** Lifecycle state (subroutine or spec) → the step that is in play now. */
export function currentStepFor(state: string | null | undefined): WorkflowStepKey {
  switch ((state ?? '').toUpperCase()) {
    case 'PARSED':     return 'extract';
    case 'DRAFT':      return 'review';
    case 'IN_REVIEW':  return 'review';
    case 'SIGNED':     return 'generate';
    case 'SCAFFOLDED':
    case 'COMMITTED':  return 'generate';
    default:           return 'extract';
  }
}

export type NextStep = {
  step: WorkflowStepKey;
  /** Imperative label for the CTA, e.g. "Route to SME for review". */
  action: string;
  owner: Persona;
  ownerLabel: string;
  /** One sentence on what happens next and why. */
  hint: string;
  /** Route to the surface where the action lives, given the routine id. */
  href: (subroutineId: string) => string;
  done?: boolean;
};

/**
 * What to do next, given a lifecycle state. Pages render this as the primary
 * CTA so "what now" never depends on decoding a badge.
 */
export function nextStepFor(state: string | null | undefined): NextStep {
  switch ((state ?? '').toUpperCase()) {
    case 'DRAFT':
      return {
        step: 'review',
        action: 'Route to SME for review',
        owner: 'engineer',
        ownerLabel: 'Engineer',
        hint: 'The draft spec is ready. Routing it opens the claim-by-claim review queue for the SME.',
        href: (id) => `/subroutines/${id}/spec`,
      };
    case 'IN_REVIEW':
      return {
        step: 'review',
        action: 'Review claims and sign',
        owner: 'sme',
        ownerLabel: 'SME',
        hint: 'Every claim needs a decision before the spec can be signed. Sign-off is irrevocable.',
        href: (id) => `/subroutines/${id}/review`,
      };
    case 'SIGNED':
      return {
        step: 'generate',
        action: 'Pick a target and generate',
        owner: 'engineer',
        ownerLabel: 'Engineer',
        hint: 'The signed spec is the contract. Generation projects it onto the target stack you choose.',
        href: (id) => `/subroutines/${id}/review`,
      };
    case 'SCAFFOLDED':
    case 'COMMITTED':
      return {
        step: 'generate',
        action: 'Open the generated package',
        owner: 'engineer',
        ownerLabel: 'Engineer',
        hint: 'Code has been generated from the signed spec. Open it to read the files or validate them.',
        href: (id) => `/subroutines/${id}/review`,
        done: true,
      };
    case 'PARSED':
    default:
      return {
        step: 'extract',
        action: 'Extract spec',
        owner: 'engineer',
        ownerLabel: 'Engineer',
        hint: 'Claude reads the source and streams a behavioural spec with line-cited claims.',
        href: (id) => `/subroutines/${id}/extract`,
      };
  }
}

export function WorkflowRail({
  state,
  persona,
  className,
}: {
  /** Lifecycle state of the routine or spec. */
  state: string | null | undefined;
  /** Active persona — the current step is flagged when it isn't theirs. */
  persona?: Persona | null;
  className?: string;
}) {
  const current = currentStepFor(state);
  const currentIdx = WORKFLOW_STEPS.findIndex((s) => s.key === current);
  const allDone = ['SCAFFOLDED', 'COMMITTED'].includes((state ?? '').toUpperCase());

  return (
    <nav
      aria-label="Migration workflow"
      data-testid="workflow-rail"
      className={clsx('flex flex-wrap items-center gap-1.5', className)}
    >
      {WORKFLOW_STEPS.map((step, i) => {
        const done = allDone || i < currentIdx;
        const active = !allDone && i === currentIdx;
        const notYours = active && !!persona && persona !== step.owner;
        return (
          <div key={step.key} className="flex items-center gap-1.5">
            {i > 0 && <span aria-hidden="true" className="text-ink-tertiary">›</span>}
            <span
              data-testid={`workflow-step-${step.key}`}
              data-step-state={done ? 'done' : active ? 'active' : 'pending'}
              title={notYours ? `${step.label} is the ${step.ownerLabel}'s step` : undefined}
              className={clsx(
                'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 font-mono text-caption',
                done && 'border-status-signed/40 bg-[#DCE6F5]/50 text-status-signed',
                active && 'border-accent bg-accent-muted font-semibold text-ink-primary',
                !done && !active && 'border-border-subtle bg-canvas text-ink-tertiary',
              )}
            >
              {done ? (
                <Check className="h-3 w-3" aria-hidden="true" />
              ) : (
                <span className="text-[10px] tabular-nums">{i + 1}</span>
              )}
              {step.label}
              {active && (
                <span className={clsx('text-[10px] uppercase', notYours ? 'text-status-scaffolded' : 'text-ink-tertiary')}>
                  {step.ownerLabel}
                </span>
              )}
            </span>
          </div>
        );
      })}
    </nav>
  );
}
