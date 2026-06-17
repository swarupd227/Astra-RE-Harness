import { useQuery } from '@tanstack/react-query';
import { Boxes, CheckCircle2, FlaskConical, Lock } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type ArchetypeManifest } from '@/lib/api';
import { Skeleton } from '@/components/Skeleton';
import { Badge } from '@/components/Badge';

/**
 * Targets selector — value-add #3 in the Nous platform pitch.
 *
 * Lists every scaffold archetype the platform knows about and lets the
 * engineer pick the target stack before kicking off scaffold generation.
 * Production-status archetypes are selectable; preview/roadmap ones are
 * shown but disabled with a status badge so the buyer sees "we ship Java
 * Spring too — it's just gated behind a Nous engagement."
 *
 * One card per **target stack** — the actual archetype within that
 * stack is chosen at scaffold time by `ArchetypeRegistry.PickForSubroutine`
 * using the spec's `target_archetype_hint` field (per ADR-036). When a
 * stack has N>1 archetypes we surface the variant count + names on the
 * card so the user knows what paradigms are reachable.
 */
export function TargetSelector({
  value,
  onChange,
}: {
  value: string;
  onChange: (targetStack: string) => void;
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

  // Group archetypes by stack. Headline archetype on each card is the
  // first production-status entry (so demo seeds light up nicely); if no
  // production archetype exists for a stack, we just take the first.
  const byStack = new Map<string, ArchetypeManifest[]>();
  for (const a of q.data.data) {
    const arr = byStack.get(a.targetStack) ?? [];
    arr.push(a);
    byStack.set(a.targetStack, arr);
  }

  return (
    <div data-testid="target-selector" className="space-y-2">
      <div className="text-caption uppercase tracking-wide text-ink-tertiary">
        <span className="inline-flex items-center gap-1.5">
          <Boxes className="h-3 w-3" aria-hidden="true" />
          Target stack
        </span>
      </div>
      <div className="flex flex-wrap gap-3">
        {[...byStack.entries()].map(([stack, archetypes]) => {
          const headline =
            archetypes.find((a) => a.status?.toLowerCase().startsWith('production')) ??
            archetypes[0];
          return (
            <TargetCard
              key={stack}
              archetype={headline}
              variants={archetypes}
              selected={stack === value}
              onSelect={() => onChange(stack)}
            />
          );
        })}
      </div>
    </div>
  );
}

function TargetCard({
  archetype,
  variants,
  selected,
  onSelect,
}: {
  archetype: ArchetypeManifest;
  variants: ArchetypeManifest[];
  selected: boolean;
  onSelect: () => void;
}) {
  const isProduction = archetype.status.toLowerCase().startsWith('production');
  const isGated = !isProduction;
  const otherVariants = variants.filter((a) => a.id !== archetype.id);

  return (
    <button
      type="button"
      onClick={isGated ? undefined : onSelect}
      disabled={isGated}
      aria-pressed={selected}
      data-testid={`target-card-${archetype.targetStack}`}
      className={clsx(
        'group relative flex w-72 flex-col items-start gap-2 rounded-md border p-3 text-left transition-all',
        selected
          ? 'border-accent bg-accent-muted shadow-e1'
          : 'border-border-subtle bg-raised hover:border-border hover:shadow-e1',
        isGated && 'cursor-not-allowed opacity-70',
      )}
    >
      <div className="flex w-full items-center justify-between gap-2">
        <span className="font-mono text-caption font-semibold uppercase tracking-wide text-ink-primary">
          {prettyStack(archetype.targetStack)}
        </span>
        {selected && !isGated && (
          <CheckCircle2 className="h-4 w-4 text-accent" aria-hidden="true" />
        )}
        {isGated && (
          <Lock className="h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
        )}
      </div>
      <span className="text-body text-ink-secondary">{archetype.displayName}</span>
      {otherVariants.length > 0 && (
        <span
          className="text-caption text-ink-tertiary"
          data-testid={`target-variants-${archetype.targetStack}`}
          title={variants.map((v) => paradigmFromId(v.id)).join(', ')}
        >
          +{otherVariants.length} more{' '}
          {otherVariants.length === 1 ? 'variant' : 'variants'} (
          {variants.map((v) => paradigmFromId(v.id)).join(' / ')}
          ) — picked from spec hint
        </span>
      )}
      <div className="flex flex-wrap items-center gap-2">
        {isProduction ? (
          <Badge tone="success">
            <CheckCircle2 className="h-3 w-3" aria-hidden="true" />
            Production
          </Badge>
        ) : (
          <Badge tone="neutral">
            <FlaskConical className="h-3 w-3" aria-hidden="true" />
            {prettyStatus(archetype.status)}
          </Badge>
        )}
        <span className="font-mono text-[11px] text-ink-tertiary">
          {archetype.fileCount} files
        </span>
      </div>
    </button>
  );
}

/**
 * Convert a `canonical-<sourceLang>-<paradigm>` archetype id into a
 * short paradigm token suitable for the variant list. Falls back to
 * the trailing path segment so unknown archetypes still print
 * something readable.
 */
export function paradigmFromId(archetypeId: string): string {
  const tail = archetypeId.split('-').pop() ?? archetypeId;
  switch (tail.toLowerCase()) {
    case 'winforms':  return 'WinForms';
    case 'blazor':    return 'Blazor';
    case 'minapi':    return 'Min API';
    case 'fmt':       return 'fmt';
    case 'idsmtp':    return 'Indy SMTP';
    case 'spring':    return 'Spring';
    case 'net8':      return 'net8';
    default:          return tail;
  }
}

export function prettyStack(s: string): string {
  switch (s) {
    case 'dotnet8':
      return '.NET 8';
    case 'dotnet10':
      return '.NET 10';
    case 'java-spring':
      return 'Java Spring';
    default:
      return s;
  }
}

function prettyStatus(s: string): string {
  // Compact display — strip everything after the first " · "
  const idx = s.indexOf(' · ');
  return idx > 0 ? s.slice(0, idx) : s;
}
