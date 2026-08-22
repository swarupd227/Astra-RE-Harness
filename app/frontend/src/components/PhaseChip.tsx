import { clsx } from 'clsx';

export type PhaseId = 'A' | 'B' | 'C' | 'D' | 'E';

const phaseMeta: Record<PhaseId, { label: string; tone: string; window: string }> = {
  A: { label: 'Phase A', tone: 'bg-[#DCE6F5] text-status-signed border-status-signed/40', window: 'Foundations · May 2026' },
  B: { label: 'Phase B', tone: 'bg-accent-muted text-status-draft border-accent/40', window: 'Demo slice · Jun 2026' },
  C: { label: 'Phase C', tone: 'bg-[#DAEFE9] text-status-review border-status-review/40', window: 'Full pipeline · Jul 2026' },
  D: { label: 'Phase D', tone: 'bg-[#F2E5C2] text-status-scaffolded border-status-scaffolded/40', window: 'Hardening · Aug 2026' },
  E: { label: 'Phase E', tone: 'bg-sunken text-ink-secondary border-border', window: 'Production cutover · Aug 2026' },
};

export function PhaseChip({
  phase,
  active,
  className,
}: {
  phase: PhaseId;
  active?: boolean;
  className?: string;
}) {
  const meta = phaseMeta[phase];
  return (
    <span
      className={clsx(
        'inline-flex items-center gap-1.5 rounded-sm border px-1.5 py-0.5 font-mono text-micro uppercase tracking-wide',
        meta.tone,
        active && 'ring-2 ring-accent/30 ring-offset-1',
        className,
      )}
      title={meta.window}
    >
      {meta.label}
    </span>
  );
}

export function phaseWindow(phase: PhaseId): string {
  return phaseMeta[phase].window;
}
