import { Link } from 'react-router-dom';
import {
  ArrowRight,
  FileText,
  Languages,
  ShieldCheck,
  SlidersHorizontal,
  Users,
} from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Platform index — value-add #2/3/4/5/6/8 entry point.
 *
 * The /platform/* sub-pages each surface one Nous-shipped asset that
 * production deployments care about: tuned prompts, multi-language
 * schemas, validation policy, signature health, role assignments. The
 * index gives an Admin a single map of what's configurable.
 *
 * Visible only to the Admin persona; non-admin personas don't see the
 * nav entry that links here.
 */

type Tile = {
  to: string;
  title: string;
  description: string;
  icon: typeof FileText;
  status: 'live' | 'coming';
  comingIn?: string;
};

const tiles: Tile[] = [
  {
    to: '/platform/prompts',
    title: 'Prompt Catalog',
    description:
      'Versioned prompt library per source language and target stack — view frontmatter, system + user templates.',
    icon: FileText,
    status: 'live',
  },
  {
    to: '/platform/languages',
    title: 'Languages',
    description:
      'Supported legacy languages and their typed claim schemas. Fortran live; COBOL pilot; RPG and PL/1 on the roadmap.',
    icon: Languages,
    status: 'coming',
    comingIn: '2.2',
  },
  {
    to: '/platform/validation',
    title: 'Validation Policy',
    description:
      'Per-project gate configuration — which of compile, test pack, equivalence are required; coverage thresholds; retry policy.',
    icon: SlidersHorizontal,
    status: 'coming',
    comingIn: '2.3',
  },
  {
    to: '/platform/signatures',
    title: 'Signature Health',
    description:
      'Cross-project health board: every signed routine, green if source unchanged since signing, red on byte-level drift.',
    icon: ShieldCheck,
    status: 'coming',
    comingIn: '2.4',
  },
  {
    to: '/platform/roles',
    title: 'Roles & Permissions',
    description:
      'Who-can-do-what matrix across the Engineer, SME, Observer, and Admin personas. Add/remove users and re-assign roles.',
    icon: Users,
    status: 'coming',
    comingIn: '2.5',
  },
];

export function PlatformIndexPage() {
  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  if (whoami.isPending) return null;
  if (whoami.data?.persona !== 'admin') {
    return (
      <div className="mx-auto max-w-[1000px] p-6 lg:p-10">
        <ErrorBlock
          title="Admin only"
          message="The Platform configuration surfaces are only visible to the Admin persona. Switch persona from the top-right menu to continue."
        />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-[1200px] space-y-6 p-6 lg:p-10" data-testid="platform-index-page">
      <header>
        <p className="font-mono text-caption uppercase tracking-wider text-ink-tertiary">
          Phase #4 · Platform configuration
        </p>
        <h1 className="mt-1 text-display font-semibold text-ink-primary">Platform</h1>
        <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
          Every Nous-shipped asset that makes this deployment audit-grade —
          tuned prompts, multi-language schemas, validation gates, signature
          drift, role assignments — is configured from here.
        </p>
      </header>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
        {tiles.map((t) => (
          <TileCard key={t.to} tile={t} />
        ))}
      </div>
    </div>
  );
}

function TileCard({ tile }: { tile: Tile }) {
  const Icon = tile.icon;
  const isLive = tile.status === 'live';
  return (
    <Card className="h-full" data-testid={`platform-tile-${slug(tile.title)}`}>
      <CardHeader
        title={
          <span className="inline-flex items-center gap-2">
            <span className="flex h-8 w-8 items-center justify-center rounded-md bg-accent-muted text-accent">
              <Icon className="h-4 w-4" aria-hidden="true" />
            </span>
            {tile.title}
          </span>
        }
        action={
          isLive ? (
            <Badge tone="success">Live</Badge>
          ) : (
            <Badge tone="neutral">Phase {tile.comingIn}</Badge>
          )
        }
      />
      <CardBody className="space-y-3">
        <p className="text-body text-ink-secondary">{tile.description}</p>
        {isLive ? (
          <Link
            to={tile.to}
            className="inline-flex items-center gap-1 text-caption font-medium text-accent hover:underline"
          >
            Open <ArrowRight className="h-3.5 w-3.5" aria-hidden="true" />
          </Link>
        ) : (
          <span className="inline-flex items-center gap-1 font-mono text-caption text-ink-tertiary">
            Coming in Phase {tile.comingIn}
          </span>
        )}
      </CardBody>
    </Card>
  );
}

function slug(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '');
}
