import { clsx } from 'clsx';
import { ChevronRight } from 'lucide-react';
import type { ReactNode } from 'react';

export type DraftSpec = {
  routine?: string;
  source_path?: string;
  source_lines?: string;
  summary?: string;
  inputs?: { id: string; name: string; type: string; semantic: string }[];
  outputs?: { id: string; name: string; type: string; semantic: string }[];
  invariants?: { id: string; claim: string; citations: { lines: string }[]; confidence: string }[];
  side_effects?: { description: string; citations: { lines: string }[] }[];
  edge_cases?: { description: string; citations: { lines: string }[]; behavior: string; confidence: string }[];
  open_questions?: { id: string; question: string; status: string }[];
};

export function StreamingSpecPanel({
  draft,
  onCitationClick,
  activeCitation,
}: {
  draft: DraftSpec;
  onCitationClick?: (lines: string) => void;
  activeCitation?: string | null;
}) {
  return (
    <div className="space-y-6 px-6 py-5">
      {draft.summary && (
        <Section title="Summary">
          <p className="text-body leading-relaxed text-ink-primary">{draft.summary}</p>
        </Section>
      )}

      {(draft.inputs?.length ?? 0) > 0 && (
        <Section title="Inputs">
          <ParamList items={draft.inputs!} onCite={onCitationClick} active={activeCitation} />
        </Section>
      )}
      {(draft.outputs?.length ?? 0) > 0 && (
        <Section title="Outputs">
          <ParamList items={draft.outputs!} onCite={onCitationClick} active={activeCitation} />
        </Section>
      )}

      {(draft.invariants?.length ?? 0) > 0 && (
        <Section title="Invariants" count={draft.invariants!.length}>
          <ul className="space-y-2">
            {draft.invariants!.map((inv) => (
              <ClaimCard
                key={inv.id}
                id={inv.id}
                body={inv.claim}
                citations={inv.citations}
                confidence={inv.confidence}
                onCite={onCitationClick}
                active={activeCitation}
              />
            ))}
          </ul>
        </Section>
      )}

      {(draft.side_effects?.length ?? 0) > 0 && (
        <Section title="Side effects" count={draft.side_effects!.length}>
          <ul className="space-y-2">
            {draft.side_effects!.map((se, i) => (
              <ClaimCard
                key={i}
                id={`SE-${i + 1}`}
                body={se.description}
                citations={se.citations}
                onCite={onCitationClick}
                active={activeCitation}
              />
            ))}
          </ul>
        </Section>
      )}

      {(draft.edge_cases?.length ?? 0) > 0 && (
        <Section title="Edge cases" count={draft.edge_cases!.length}>
          <ul className="space-y-2">
            {draft.edge_cases!.map((ec, i) => (
              <ClaimCard
                key={i}
                id={`EC-${i + 1}`}
                body={ec.description}
                supplemental={`Behavior: ${ec.behavior}`}
                citations={ec.citations}
                confidence={ec.confidence}
                onCite={onCitationClick}
                active={activeCitation}
              />
            ))}
          </ul>
        </Section>
      )}

      {(draft.open_questions?.length ?? 0) > 0 && (
        <Section title="Open questions" count={draft.open_questions!.length} tone="warning">
          <ul className="space-y-2">
            {draft.open_questions!.map((q) => (
              <li key={q.id} className="rounded-md border border-status-scaffolded/40 bg-[#FBF1D9] p-3">
                <div className="flex items-center gap-2 text-caption">
                  <span className="rounded-sm bg-status-scaffolded/20 px-1.5 py-0.5 font-mono uppercase text-status-scaffolded">
                    {q.id}
                  </span>
                  <span className="font-mono uppercase text-ink-tertiary">{q.status}</span>
                </div>
                <p className="mt-2 text-body text-ink-primary">{q.question}</p>
              </li>
            ))}
          </ul>
        </Section>
      )}
    </div>
  );
}

function Section({
  title,
  count,
  tone,
  children,
}: {
  title: string;
  count?: number;
  tone?: 'warning';
  children: ReactNode;
}) {
  return (
    <section className="motion-safe:animate-fade-in">
      <header className="mb-2 flex items-center gap-2">
        <h3
          className={clsx(
            'font-mono text-caption uppercase tracking-wider',
            tone === 'warning' ? 'text-status-scaffolded' : 'text-ink-tertiary',
          )}
        >
          {title}
        </h3>
        {count !== undefined && (
          <span className="rounded-sm bg-sunken px-1.5 py-0.5 font-mono text-[10px] text-ink-secondary">
            {count}
          </span>
        )}
      </header>
      {children}
    </section>
  );
}

function ParamList({
  items,
  onCite,
  active,
}: {
  items: { id: string; name: string; type: string; semantic: string }[];
  onCite?: (lines: string) => void;
  active?: string | null;
}) {
  return (
    <ul className="space-y-1.5">
      {items.map((p) => (
        <li
          key={p.id}
          className="flex items-baseline gap-3 rounded-md border border-border-subtle bg-raised px-3 py-2"
        >
          <span className="font-mono text-body font-semibold text-ink-primary">{p.name}</span>
          <span className="font-mono text-caption text-ink-tertiary">{p.type}</span>
          <span className="text-caption text-ink-secondary">{p.semantic}</span>
        </li>
      ))}
    </ul>
  );
}

function ClaimCard({
  id,
  body,
  supplemental,
  citations,
  confidence,
  onCite,
  active,
}: {
  id: string;
  body: string;
  supplemental?: string;
  citations: { lines: string }[];
  confidence?: string;
  onCite?: (lines: string) => void;
  active?: string | null;
}) {
  return (
    <li className="rounded-md border border-border-subtle bg-raised p-3 motion-safe:animate-fade-in">
      <div className="flex items-center gap-2 text-caption">
        <span className="rounded-sm bg-sunken px-1.5 py-0.5 font-mono uppercase text-ink-secondary">
          {id}
        </span>
        {confidence && (
          <span
            className={clsx(
              'rounded-sm px-1.5 py-0.5 font-mono uppercase',
              confidence === 'high' && 'bg-[#DAEFE9] text-status-review',
              confidence === 'medium' && 'bg-accent-muted text-status-draft',
              confidence === 'low' && 'bg-[#F4D8D7] text-status-failed',
            )}
          >
            {confidence}
          </span>
        )}
      </div>
      <p className="mt-2 text-body text-ink-primary">{body}</p>
      {supplemental && (
        <p className="mt-1 text-caption italic text-ink-secondary">{supplemental}</p>
      )}
      <div className="mt-2 flex flex-wrap gap-1.5">
        {citations.map((c, i) => (
          <button
            key={i}
            type="button"
            onClick={() => onCite?.(c.lines)}
            className={clsx(
              'inline-flex items-center gap-1 rounded-sm border px-1.5 py-0.5 font-mono text-[11px] transition-colors duration-fast hover:bg-accent-muted',
              active === c.lines
                ? 'border-accent bg-accent-muted text-status-draft'
                : 'border-border-subtle bg-canvas text-ink-secondary',
            )}
            aria-label={`Cited lines ${c.lines}`}
          >
            <ChevronRight className="h-3 w-3" />
            L{c.lines}
          </button>
        ))}
      </div>
    </li>
  );
}
