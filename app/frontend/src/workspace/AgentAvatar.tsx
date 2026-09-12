import { clsx } from 'clsx';
import { agentMeta } from '@/agents/agents';
import type { AgentId } from '@/lib/conversations';

const SIZES = {
  sm: { box: 'h-6 w-6', icon: 13 },
  md: { box: 'h-8 w-8', icon: 16 },
  lg: { box: 'h-10 w-10', icon: 20 },
} as const;

/**
 * Round agent disc. `working` adds the volt pulsing ring — volt is reserved
 * for "an agent is working", primary CTAs and focus, nothing else.
 */
export function AgentAvatar({
  agent,
  size = 'md',
  working = false,
  className,
}: {
  agent: AgentId | null | undefined;
  size?: keyof typeof SIZES;
  working?: boolean;
  className?: string;
}) {
  const meta = agentMeta(agent);
  const Icon = meta.icon;
  const s = SIZES[size];
  return (
    <span className={clsx('relative inline-flex shrink-0', className)} title={meta.name}>
      {working && (
        <>
          <span
            className="absolute inset-0 animate-ping rounded-full bg-volt/30 motion-reduce:animate-none"
            aria-hidden="true"
          />
          <span className="absolute -inset-0.5 rounded-full ring-2 ring-volt/70" aria-hidden="true" />
        </>
      )}
      <span
        className={clsx(
          'relative inline-flex items-center justify-center rounded-full',
          s.box,
          meta.disc,
          meta.tone,
        )}
      >
        <Icon size={s.icon} strokeWidth={1.75} aria-hidden="true" />
      </span>
    </span>
  );
}
