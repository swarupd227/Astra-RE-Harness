import { clsx } from 'clsx';

const STAGES = [
  { key: 'priming', label: 'Priming context' },
  { key: 'loading_source', label: 'Loading source' },
  { key: 'streaming', label: 'Streaming response' },
  { key: 'validating', label: 'Validating' },
  { key: 'persisting', label: 'Persisting' },
];

export function StageIndicator({ currentStage }: { currentStage: string | null }) {
  const currentIdx = currentStage
    ? STAGES.findIndex((s) => s.key === currentStage)
    : -1;

  return (
    <ol className="flex items-center gap-2" aria-label="Extraction stages">
      {STAGES.map((s, i) => {
        const state =
          currentIdx === -1
            ? 'idle'
            : i < currentIdx
              ? 'done'
              : i === currentIdx
                ? 'active'
                : 'pending';
        return (
          <li key={s.key} className="flex items-center gap-2">
            <span
              className={clsx(
                'flex h-5 w-5 items-center justify-center rounded-full border font-mono text-[10px] transition-colors duration-fast',
                state === 'done' && 'border-status-review bg-status-review text-white',
                state === 'active' && 'border-accent bg-accent text-white motion-safe:animate-pulse',
                state === 'pending' && 'border-border-subtle bg-canvas text-ink-tertiary',
                state === 'idle' && 'border-border-subtle bg-canvas text-ink-tertiary',
              )}
              aria-current={state === 'active' ? 'step' : undefined}
            >
              {i + 1}
            </span>
            <span
              className={clsx(
                'hidden font-mono text-[11px] uppercase tracking-wide xl:inline',
                state === 'active'
                  ? 'text-ink-primary'
                  : state === 'done'
                    ? 'text-ink-secondary'
                    : 'text-ink-tertiary',
              )}
            >
              {s.label}
            </span>
            {i < STAGES.length - 1 && (
              <span
                className={clsx(
                  'mx-1 h-px w-6 transition-colors duration-fast',
                  state === 'done' ? 'bg-status-review' : 'bg-border-subtle',
                )}
                aria-hidden="true"
              />
            )}
          </li>
        );
      })}
    </ol>
  );
}
