import { ChevronRight, Database, FileSearch, Sparkles, ClipboardCheck, Cog } from 'lucide-react';
import { StageCard, type StageProps } from './StageCard';

const STAGES: StageProps[] = [
  {
    index: 1,
    name: 'Ingest',
    tagline: 'Pull Fortran source from Git or upload. Hash, version, persist.',
    icon: Database,
    phase: 'C',
  },
  {
    index: 2,
    name: 'Parse',
    tagline: 'fparser2 AST + structural index: subs, COMMON, call graph, ISAM I/O.',
    icon: FileSearch,
    phase: 'C',
  },
  {
    index: 3,
    name: 'Extract',
    tagline: 'Anthropic Claude drafts behavioural specs with line-cited claims.',
    icon: Sparkles,
    phase: 'B',
    active: true,
  },
  {
    index: 4,
    name: 'Review',
    tagline: 'SME accepts, edits, rejects each claim. HSM-signed when complete.',
    icon: ClipboardCheck,
    phase: 'B',
  },
  {
    index: 5,
    name: 'Scaffold',
    tagline: 'Azure OpenAI emits .NET service skeleton + xUnit fixtures from signed spec.',
    icon: Cog,
    phase: 'B',
  },
];

export function PipelineHero() {
  return (
    <section
      aria-labelledby="pipeline-heading"
      className="rounded-lg border border-border-subtle bg-canvas/40 p-6 lg:p-8"
    >
      <header className="mb-6 max-w-3xl">
        <p className="text-caption font-medium uppercase tracking-wider text-accent">
          The pipeline
        </p>
        <h2 id="pipeline-heading" className="mt-2 text-h-lg font-semibold text-ink-primary">
          Five stages, signed at every transition.
        </h2>
        <p className="mt-2 text-body text-ink-secondary">
          Fortran source enters; a signed contract and scaffolded .NET package come out the other
          side. Every claim cites source. Every signature is HSM-backed. Nothing in this product
          floats.
        </p>
      </header>

      <ol
        className="grid gap-3 lg:grid-cols-5"
        aria-label="Pipeline stages"
      >
        {STAGES.map((stage, i) => (
          <li key={stage.name} className="relative">
            <StageCard {...stage} />
            {i < STAGES.length - 1 && (
              <span
                aria-hidden="true"
                className="absolute right-[-9px] top-1/2 hidden -translate-y-1/2 lg:block"
              >
                <ChevronRight className="h-4 w-4 text-ink-tertiary" />
              </span>
            )}
          </li>
        ))}
      </ol>
    </section>
  );
}
