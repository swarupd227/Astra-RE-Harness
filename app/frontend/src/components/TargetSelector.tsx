import { useQuery } from '@tanstack/react-query';
import { Boxes, CheckCircle2, FlaskConical, Lock } from 'lucide-react';
import { clsx } from 'clsx';
import { api } from '@/lib/api';
import { Skeleton } from '@/components/Skeleton';
import { Badge } from '@/components/Badge';
import {
  buildStackOptions,
  paradigmFromId,
  prettySchema,
  prettyStack,
  prettyStatus,
  type StackOption,
} from '@/lib/targetStacks';

/**
 * Targets selector — value-add #3 in the Nous platform pitch.
 *
 * Lists every scaffold archetype the platform knows about and lets the
 * engineer pick the target stack before kicking off scaffold generation.
 * Stacks that can't build THIS routine are shown but disabled with the
 * reason spelled out, so the buyer still sees the breadth of targets
 * without being able to pick one the server will reject.
 *
 * `sourceLanguage` is what makes a card honest: eligibility is
 * production-status AND schema-compatibility (see lib/targetStacks.ts).
 * Omit it and every stack is judged on status alone — the old behaviour,
 * kept only for callers that genuinely have no routine in hand.
 *
 * One card per **target stack** — the actual archetype within that stack is
 * chosen at scaffold time by `ArchetypeRegistry.PickForSubroutine` using the
 * spec's `target_archetype_hint` field (per ADR-036). When a stack has N>1
 * archetypes we surface the variant count + names on the card so the user
 * knows what paradigms are reachable.
 */
export function TargetSelector({
  value,
  onChange,
  sourceLanguage,
  compact = false,
}: {
  value: string;
  onChange: (targetStack: string) => void;
  sourceLanguage?: string | null;
  /** Denser layout for the routine surface, where the picker is a side note. */
  compact?: boolean;
}) {
  const q = useQuery({
    queryKey: ['archetypes'],
    queryFn: api.listArchetypes,
    staleTime: 5 * 60_000,
  });

  if (q.isPending) {
    return (
      <div className="flex gap-3" data-testid="target-selector">
        <Skeleton className="h-24 w-72" />
        <Skeleton className="h-24 w-72" />
      </div>
    );
  }
  if (q.isError || !q.data) return null;

  const options = buildStackOptions(q.data.data, sourceLanguage);

  return (
    <div data-testid="target-selector" className="space-y-2">
      <div className="flex flex-wrap items-baseline gap-x-2 text-caption uppercase tracking-wide text-ink-tertiary">
        <span className="inline-flex items-center gap-1.5">
          <Boxes className="h-3 w-3" aria-hidden="true" />
          Target stack
        </span>
        {sourceLanguage && (
          <span className="normal-case tracking-normal text-ink-tertiary">
            · eligibility for {prettySchema(sourceLanguage)} sources
          </span>
        )}
      </div>
      <div className="flex flex-wrap gap-3">
        {options.map((opt) => (
          <TargetCard
            key={opt.stack}
            option={opt}
            selected={opt.stack === value}
            compact={compact}
            onSelect={() => onChange(opt.stack)}
          />
        ))}
      </div>
    </div>
  );
}

function TargetCard({
  option,
  selected,
  compact,
  onSelect,
}: {
  option: StackOption;
  selected: boolean;
  compact: boolean;
  onSelect: () => void;
}) {
  const { headline, variants, selectable, blockedReason } = option;
  const otherVariants = variants.filter((a) => a.id !== headline.id);

  return (
    <button
      type="button"
      onClick={selectable ? onSelect : undefined}
      disabled={!selectable}
      aria-pressed={selected}
      // Disabled buttons swallow hover in some browsers, so the reason is
      // also rendered as visible text below — the title is a bonus, not the
      // only channel.
      title={blockedReason ?? undefined}
      data-testid={`target-card-${option.stack}`}
      data-selectable={selectable ? 'true' : 'false'}
      className={clsx(
        'group relative flex flex-col items-start gap-2 rounded-md border p-3 text-left transition-all',
        compact ? 'w-full sm:w-60' : 'w-72',
        selected
          ? 'border-accent bg-accent-muted shadow-e1'
          : 'border-border-subtle bg-raised hover:border-border hover:shadow-e1',
        !selectable && 'cursor-not-allowed opacity-70',
      )}
    >
      <div className="flex w-full items-center justify-between gap-2">
        <span className="font-mono text-caption font-semibold uppercase tracking-wide text-ink-primary">
          {prettyStack(option.stack)}
        </span>
        {selected && selectable && (
          <CheckCircle2 className="h-4 w-4 text-accent" aria-hidden="true" />
        )}
        {!selectable && <Lock className="h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />}
      </div>
      <span className="text-body text-ink-secondary">{headline.displayName}</span>
      {!compact && otherVariants.length > 0 && (
        <span
          className="text-caption text-ink-tertiary"
          data-testid={`target-variants-${option.stack}`}
          title={variants.map((v) => paradigmFromId(v.id)).join(', ')}
        >
          +{otherVariants.length} more{' '}
          {otherVariants.length === 1 ? 'variant' : 'variants'} (
          {variants.map((v) => paradigmFromId(v.id)).join(' / ')}
          ) — picked from spec hint
        </span>
      )}
      <div className="flex flex-wrap items-center gap-2">
        {selectable ? (
          <Badge tone="success">
            <CheckCircle2 className="h-3 w-3" aria-hidden="true" />
            Production
          </Badge>
        ) : (
          <Badge tone="neutral">
            <FlaskConical className="h-3 w-3" aria-hidden="true" />
            {prettyStatus(headline.status)}
          </Badge>
        )}
        <span className="font-mono text-[11px] text-ink-tertiary">
          {headline.fileCount} files
        </span>
      </div>
      {blockedReason && (
        <span
          className="text-caption text-ink-tertiary"
          data-testid={`target-blocked-${option.stack}`}
        >
          {blockedReason}
        </span>
      )}
    </button>
  );
}

// Re-exported for the pages that imported these from here before the
// eligibility rules moved into lib/targetStacks.ts.
export { paradigmFromId, prettyStack };
