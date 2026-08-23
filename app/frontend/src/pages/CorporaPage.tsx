import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { Database, FileText, Layers, Plus } from 'lucide-react';
import { api, type CorpusListItem } from '@/lib/api';
import { Card, CardBody } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Button } from '@/components/Button';
import { ErrorBlock } from '@/components/ErrorBlock';
import { PageHero } from '@/components/PageHero';
import { Skeleton } from '@/components/Skeleton';
import { EmptyState } from '@/components/EmptyState';
import { NoCorporaIllustration } from '@/illustrations/NoCorpora';

export function CorporaPage() {
  const corpora = useQuery({ queryKey: ['corpora'], queryFn: api.listCorpora });

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10 fadeup">
      <PageHero
        tone="emerald"
        title="Projects"
        lead="A project is a versioned snapshot of your source. Upload files or a .zip, or clone from a Git repository — the language is detected automatically."
        actions={
          <Link to="/projects/new" data-testid="add-corpus">
            <Button variant="primary">
              <Plus className="h-4 w-4" aria-hidden="true" />
              Add project
            </Button>
          </Link>
        }
      />

      {corpora.isPending ? (
        <div className="grid gap-4 lg:grid-cols-2">
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-32 w-full" />
        </div>
      ) : corpora.isError ? (
        <ErrorBlock
          title="Could not load projects"
          message={corpora.error.message}
          onRetry={() => corpora.refetch()}
        />
      ) : corpora.data.data.length === 0 ? (
        <Card>
          <CardBody>
            <EmptyState
              illustration={<NoCorporaIllustration size={140} />}
              title="No projects yet"
              description="Upload source files or connect a Git repository to begin."
              action={
                <Link to="/projects/new">
                  <Button variant="primary">
                    <Plus className="h-4 w-4" aria-hidden="true" />
                    Add project
                  </Button>
                </Link>
              }
            />
          </CardBody>
        </Card>
      ) : (
        <ul className="grid gap-4 lg:grid-cols-2">
          {corpora.data.data.map((c) => (
            <li key={c.id}>
              <CorpusCard corpus={c} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

// Phase 9.2.b — per-language colour accent.
// Phase 10.1.b.2 — `sourceLanguage` is now on CorpusListItem (computed
// over the latest version's subroutines, most-common-wins with null on
// ties). The corpus-name regex is gone; this is a pure lookup.
type LanguageAccent = {
  id: string;
  label: string;
  /** Tailwind colour token used for the language pill background. */
  pillBg: string;
  pillText: string;
  /** Left-edge stripe colour for the card. */
  stripe: string;
};

const LANG_FORTRAN: LanguageAccent = {
  id: 'fortran-f77', label: 'Fortran',
  pillBg: 'bg-[#E0E7FF]', pillText: 'text-[#3730A3]',  // indigo
  stripe: 'border-l-[#6366F1]',
};
const LANG_COBOL: LanguageAccent = {
  id: 'cobol', label: 'COBOL',
  pillBg: 'bg-[#CCFBF1]', pillText: 'text-[#0F766E]',  // teal
  stripe: 'border-l-[#14B8A6]',
};
const LANG_DELPHI: LanguageAccent = {
  id: 'delphi', label: 'Delphi',
  pillBg: 'bg-[#D1FAE5]', pillText: 'text-[#065F46]',  // emerald
  stripe: 'border-l-[#10B981]',
};
const LANG_CPP: LanguageAccent = {
  id: 'cpp', label: 'C++',
  pillBg: 'bg-[#FEF3C7]', pillText: 'text-[#92400E]',  // amber
  stripe: 'border-l-[#F59E0B]',
};
// Phase 10.0.i — VB6 accent. Sky blue is the most natural fit (the
// other languages took indigo / teal / emerald / amber); echoes the
// classic VB6 form-designer header colour without colliding with any
// of the other four pills.
const LANG_VB6: LanguageAccent = {
  id: 'vb6', label: 'VB6',
  pillBg: 'bg-[#E0F2FE]', pillText: 'text-[#075985]',  // sky
  stripe: 'border-l-[#0EA5E9]',
};

function accentForLanguage(schemaId: string | null): LanguageAccent | null {
  switch (schemaId) {
    case 'fortran-f77': return LANG_FORTRAN;
    case 'cobol':       return LANG_COBOL;
    case 'delphi':      return LANG_DELPHI;
    case 'cpp':         return LANG_CPP;
    case 'vb6':         return LANG_VB6;
    default:            return null;
  }
}

function CorpusCard({ corpus }: { corpus: CorpusListItem }) {
  // Each card gets a status-colored left accent + a tinted icon background
  // so a single glance at the corpora grid tells you what's healthy vs failed.
  // Phase 9.2.b adds a language pill so the per-language story is visible
  // alongside the per-state story.
  const language = accentForLanguage(corpus.sourceLanguage);
  const accent = (() => {
    switch (corpus.state) {
      case 'PARSED':    return { edge: 'border-l-status-review', iconBg: 'bg-[#DAEFE9]', iconFg: 'text-status-review' };
      case 'PARSING':
      case 'INGESTING': return { edge: 'border-l-status-draft',  iconBg: 'bg-accent-muted', iconFg: 'text-status-draft' };
      case 'FAILED':    return { edge: 'border-l-status-failed', iconBg: 'bg-[#F4D8D7]', iconFg: 'text-status-failed' };
      default:          return { edge: 'border-l-border',        iconBg: 'bg-sunken',    iconFg: 'text-ink-secondary' };
    }
  })();
  return (
    <Link
      to={`/corpora/${corpus.id}`}
      className="block focus-visible:outline-2 focus-visible:outline-ink-primary"
    >
      <Card interactive className={`border-l-4 ${accent.edge}`}>
        <CardBody>
          <header className="flex items-start justify-between gap-3">
            <div className="flex items-center gap-3">
              <span className={`flex h-10 w-10 items-center justify-center rounded-md ${accent.iconBg} ${accent.iconFg}`}>
                <Database className="h-5 w-5" aria-hidden="true" />
              </span>
              <div>
                <h2 className="text-h-md font-semibold text-ink-primary">{corpus.name}</h2>
                <p className="mt-0.5 flex items-center gap-2 font-mono text-caption text-ink-tertiary">
                  <span>{corpus.sourceType.toUpperCase()}</span>
                  {language && (
                    <span
                      className={`inline-flex items-center rounded-sm px-1.5 py-0.5 text-[10px] font-semibold ${language.pillBg} ${language.pillText}`}
                      data-testid={`corpus-language-${language.id}`}
                      title={`Source language: ${language.label}`}
                    >
                      {language.label}
                    </span>
                  )}
                </p>
              </div>
            </div>
            <Badge tone={badgeToneForState(corpus.state)}>{corpus.state}</Badge>
          </header>
          <dl className="mt-5 grid grid-cols-3 gap-4 text-body">
            <div>
              <dt className="text-caption text-ink-tertiary">Files</dt>
              <dd className="mt-0.5 font-mono text-h-md font-semibold text-ink-primary">
                <FileText className="mr-1 inline h-4 w-4 -translate-y-0.5 text-ink-tertiary" aria-hidden="true" />
                {corpus.fileCount}
              </dd>
            </div>
            <div>
              <dt className="text-caption text-ink-tertiary">Lines of code</dt>
              <dd className="mt-0.5 font-mono text-h-md font-semibold text-ink-primary">
                <Layers className="mr-1 inline h-4 w-4 -translate-y-0.5 text-ink-tertiary" aria-hidden="true" />
                {corpus.totalLoc.toLocaleString()}
              </dd>
            </div>
            <div>
              <dt className="text-caption text-ink-tertiary">Last activity</dt>
              <dd className="mt-0.5 font-mono text-caption text-ink-secondary">
                {new Date(corpus.updatedAt).toLocaleString()}
              </dd>
            </div>
          </dl>
        </CardBody>
      </Card>
    </Link>
  );
}

function badgeToneForState(state: string): 'draft' | 'review' | 'signed' | 'failed' | 'neutral' {
  switch (state) {
    case 'PARSED':
    case 'INGESTED':
      return 'signed';
    case 'PARSING':
    case 'INGESTING':
      return 'draft';
    case 'FAILED':
      return 'failed';
    default:
      return 'neutral';
  }
}
