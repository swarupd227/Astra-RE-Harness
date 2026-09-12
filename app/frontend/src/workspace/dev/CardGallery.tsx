/**
 * Dev-only gallery: every Increment 2 fixture at card and pane size, side by
 * side. Mounted by AssessmentPage behind `?fixture=1` when `import.meta.env.DEV`.
 */
import { useState } from 'react';
import { ArtifactCard } from '../artifacts/ArtifactCard';
import { renderArtifact } from '../artifacts/registry';
import { artifactTitle } from '../artifacts/meta';
import { ThreadActionsProvider } from '../ThreadActions';
import { ALL_FIXTURES } from './fixtures';

export function CardGallery() {
  const [log, setLog] = useState<string[]>([]);
  const record = (s: string) => setLog((l) => [s, ...l].slice(0, 8));
  return (
    <ThreadActionsProvider
      value={{
        sendIntent: (intent) => record(`intent: ${intent}`),
        openArtifact: (kind, refId) => {
          record(`openArtifact: ${kind} ${refId}`);
          return false;
        },
      }}
    >
      <div className="space-y-8" data-testid="card-gallery">
        <div className="rounded-lg border border-line-subtle bg-sunken px-3 py-2 text-caption text-ink-secondary">
          Fixture gallery (dev only). Intents and artifact lookups are logged here instead of sent.
          {log.length > 0 && (
            <ul className="mt-1 space-y-0.5 font-mono text-micro text-ink-tertiary">
              {log.map((l, i) => (
                <li key={i}>{l}</li>
              ))}
            </ul>
          )}
        </div>
        {ALL_FIXTURES.map((a) => (
          <section key={a.kind} className="space-y-3">
            <h2 className="text-h-sm font-semibold text-ink-primary">
              {artifactTitle(a)} <span className="font-mono text-micro text-ink-tertiary">{a.kind}</span>
            </h2>
            <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
              <div>
                <div className="mb-1 text-micro uppercase tracking-wide text-ink-tertiary">card</div>
                <ArtifactCard artifact={a} onIntent={(i) => record(`intent: ${i}`)} />
              </div>
              <div>
                <div className="mb-1 text-micro uppercase tracking-wide text-ink-tertiary">pane</div>
                <div className="rounded-xl border border-line-subtle bg-raised p-4">
                  {renderArtifact(a, { size: 'pane', onIntent: (i) => record(`intent: ${i}`) })}
                </div>
              </div>
            </div>
          </section>
        ))}
      </div>
    </ThreadActionsProvider>
  );
}
