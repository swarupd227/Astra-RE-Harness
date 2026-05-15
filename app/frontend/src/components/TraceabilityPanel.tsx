import { ArrowUpRight } from 'lucide-react';
import type { SpecResponse } from '@/lib/api';

/**
 * Phase B.4 right-side panel on the Scaffold Artifact view. Maps a file's
 * `derivedFromClaimIds` to the actual claim text from the signed spec, so
 * the engineer can see "this method came from INV-3" without leaving the
 * page.
 */
export function TraceabilityPanel({
  spec,
  claimIds,
}: {
  spec: SpecResponse | null;
  claimIds: string[];
}) {
  if (!spec) {
    return (
      <div className="p-6 text-caption text-ink-tertiary">
        Spec not loaded — claim mappings unavailable.
      </div>
    );
  }

  if (claimIds.length === 0) {
    return (
      <div className="p-6">
        <p className="text-caption text-ink-tertiary">
          This file has no spec claims attached. It is structural scaffolding.
        </p>
      </div>
    );
  }

  const claims = claimIds
    .map((id) => resolve(spec, id))
    .filter((c): c is { id: string; section: string; body: string } => c !== null);

  return (
    <div className="space-y-4 p-5">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Derived from
        </p>
        <h3 className="mt-1 text-h-md font-semibold text-ink-primary">
          {claims.length} signed-spec {claims.length === 1 ? 'claim' : 'claims'}
        </h3>
      </header>

      <ul className="space-y-2">
        {claims.map((c) => (
          <li
            key={c.id}
            className="rounded-md border border-border-subtle bg-canvas p-3 transition-colors duration-fast hover:bg-sunken"
          >
            <div className="flex items-center gap-2 text-caption">
              <span className="rounded-sm bg-sunken px-1.5 py-0.5 font-mono uppercase text-ink-secondary">
                {c.id}
              </span>
              <span className="font-mono uppercase text-ink-tertiary">{c.section}</span>
              <ArrowUpRight className="ml-auto h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
            </div>
            <p className="mt-1.5 text-body text-ink-primary">{c.body}</p>
          </li>
        ))}
      </ul>

      <p className="font-mono text-caption text-ink-tertiary">
        Signature key {spec.signature?.keyId ?? '—'} · hash {spec.signature?.specCanonicalHash.slice(0, 22) ?? '—'}…
      </p>
    </div>
  );
}

function resolve(spec: SpecResponse, id: string): { id: string; section: string; body: string } | null {
  const sections: { key: keyof SpecResponse['spec']; label: string; bodyKey: 'claim' | 'description' | 'question' | 'semantic' }[] = [
    { key: 'invariants', label: 'invariant', bodyKey: 'claim' },
    { key: 'side_effects', label: 'side effect', bodyKey: 'description' },
    { key: 'edge_cases', label: 'edge case', bodyKey: 'description' },
    { key: 'open_questions', label: 'open question', bodyKey: 'question' },
    { key: 'inputs', label: 'input', bodyKey: 'semantic' },
    { key: 'outputs', label: 'output', bodyKey: 'semantic' },
  ];
  for (const sec of sections) {
    const arr = spec.spec[sec.key];
    if (!arr || !Array.isArray(arr)) continue;
    const hit = (arr as Array<Record<string, unknown>>).find((c) => c.id === id);
    if (hit) {
      return {
        id,
        section: sec.label,
        body: String(hit[sec.bodyKey] ?? hit.claim ?? hit.description ?? hit.question ?? id),
      };
    }
  }
  return null;
}
