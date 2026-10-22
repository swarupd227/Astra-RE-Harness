import { useQuery } from '@tanstack/react-query';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { api, type ArtefactKindInfo } from '@/lib/api';

/**
 * Which kinds of evidence a project takes besides source code. The kinds come from the
 * API registry, so a new kind appears here without a frontend change. Source code is
 * shown as always on.
 */
export function ArtefactKindPicker({
  value,
  onChange,
  disabled = false,
}: {
  value: string[];
  onChange: (next: string[]) => void;
  disabled?: boolean;
}) {
  const kinds = useQuery({
    queryKey: ['artefact-kinds'],
    queryFn: () => api.listArtefactKinds(),
    staleTime: Infinity,
  });

  if (kinds.isPending) return <Skeleton className="h-40 w-full" />;
  if (kinds.isError) {
    return (
      <p className="text-caption text-status-fail" role="alert">
        Could not load the artefact kinds: {kinds.error.message}
      </p>
    );
  }

  const toggle = (id: string, on: boolean) =>
    onChange(on ? [...value, id] : value.filter((k) => k !== id));

  return (
    <ul className="divide-y divide-line-subtle rounded-md border border-line-subtle bg-raised" data-testid="artefact-kind-picker">
      {kinds.data.data.map((k) => (
        <KindRow
          key={k.id}
          kind={k}
          checked={k.core || value.includes(k.id)}
          disabled={disabled || k.core}
          onToggle={(on) => toggle(k.id, on)}
        />
      ))}
    </ul>
  );
}

function KindRow({
  kind,
  checked,
  disabled,
  onToggle,
}: {
  kind: ArtefactKindInfo;
  checked: boolean;
  disabled: boolean;
  onToggle: (on: boolean) => void;
}) {
  const inputId = `artefact-kind-${kind.id}`;
  const exts = kind.extensions.length > 6 ? `${kind.extensions.slice(0, 6).join(' ')} …` : kind.extensions.join(' ');
  return (
    <li className="flex items-start gap-3 px-4 py-3">
      <input
        id={inputId}
        type="checkbox"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onToggle(e.target.checked)}
        className="mt-1 h-4 w-4 rounded border-line accent-volt focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt disabled:cursor-not-allowed disabled:opacity-60"
        data-testid={inputId}
      />
      <label htmlFor={inputId} className="min-w-0 flex-1 cursor-pointer">
        <span className="flex flex-wrap items-center gap-2">
          <span className="text-body font-medium text-ink-primary">{kind.label}</span>
          {kind.core && <Badge tone="neutral">Always on</Badge>}
          <Badge tone={kind.trustTier === 'structured' ? 'info' : 'neutral'}>
            {kind.trustTier === 'structured' ? 'Structured' : 'Narrative'}
          </Badge>
        </span>
        <span className="mt-0.5 block text-caption text-ink-secondary">{kind.description}</span>
        <span className="mt-0.5 block truncate font-mono text-micro text-ink-tertiary">{exts}</span>
      </label>
    </li>
  );
}
