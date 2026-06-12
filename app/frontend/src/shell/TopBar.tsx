import { Link, useLocation, useMatch } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { CircleHelp } from 'lucide-react';
import { Badge } from '@/components/Badge';
import { CommandBarTrigger } from '@/components/CommandBarTrigger';
import { ApiLatencyIndicator } from '@/components/ApiLatencyIndicator';
import { PersonaMenu } from '@/shell/PersonaMenu';
import { Tooltip } from '@/components/Tooltip';
import { api } from '@/lib/api';

// ACE-style TopBar — light surface, breadcrumb on the left, status pills
// + persona switcher on the right. Sits above a thin brand-stripe (the
// orange-to-indigo identity sliver).
export function TopBar({ onOpenHelp }: { onOpenHelp: () => void }) {
  const provider = useQuery({
    queryKey: ['provider-settings'],
    queryFn: () => api.getProviderSettings(),
  });

  const location = useLocation();
  const breadcrumb = inferBreadcrumb(location.pathname);
  const providerName = provider.data?.provider?.name;
  const providerModel = provider.data?.provider?.model;

  return (
    <header className="sticky top-0 z-30 bg-raised">
      {/* Brand stripe — orange → amber → indigo identity sliver */}
      <div aria-hidden className="h-[2px] w-full bg-brand-stripe" />
      <div className="flex h-12 items-center gap-3 border-b border-border-subtle bg-raised px-5">
        <div className="flex min-w-0 items-center gap-2 text-sm">
          <Link
            to="/"
            className="font-semibold text-ink-secondary transition-colors duration-fast hover:text-ink-primary"
          >
            Astra
          </Link>
          <span className="text-ink-tertiary">/</span>
          <span className="truncate font-semibold text-ink-primary" data-testid="breadcrumb-current">
            {breadcrumb}
          </span>
        </div>

        <div className="ml-2 hidden xl:block">
          <CommandBarTrigger />
        </div>

        <div className="ml-auto flex items-center gap-2">
          <ApiLatencyIndicator />
          {provider.data && (
            <Tooltip
              content={`${providerName ?? '—'} · ${providerModel ?? '—'}`}
              side="bottom"
            >
              <span
                className="pill bg-brand-50 text-brand-700 ring-1 ring-brand-300/40 font-mono"
                data-testid="topbar-provider-pill"
              >
                {labelForProvider(providerName, providerModel)}
              </span>
            </Tooltip>
          )}
          <Badge tone="neutral" className="font-mono">DEV</Badge>
          <Tooltip content="Keyboard shortcuts (?)">
            <button
              type="button"
              onClick={onOpenHelp}
              className="flex h-7 w-7 items-center justify-center rounded-md text-ink-secondary transition-colors duration-fast hover:bg-sunken hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-accent"
              aria-label="Open keyboard help"
            >
              <CircleHelp className="h-4 w-4" aria-hidden="true" />
            </button>
          </Tooltip>
          <PersonaMenu />
        </div>
      </div>
    </header>
  );
}

function labelForProvider(name?: string, model?: string): string {
  if (!name) return '…';
  if (name === 'anthropic') return 'Claude (Anthropic)';
  if (name === 'mock') return 'mock · offline';
  if (name === 'fail-mock') return 'mock · chaos';
  if (name === 'openai_compatible' || name === 'openai-compatible') return 'OpenAI-compatible';
  return model ? `${name} · ${model}` : name;
}

// Cheap heuristic — map the current path to a friendly screen name.
// (We could read this from a route-level meta field, but inferring keeps
// every route definition free of UI concerns.)
function inferBreadcrumb(pathname: string): string {
  if (pathname === '/') return 'Home';
  if (pathname === '/system') return 'System';
  if (pathname.startsWith('/projects')) {
    if (pathname.match(/\/projects\/new$/)) return 'New project';
    if (pathname.match(/\/projects\/[^/]+$/)) return 'Project';
    return 'Projects';
  }
  if (pathname.startsWith('/corpora/')) {
    if (pathname.endsWith('/dependency-graph')) return 'Dependency graph';
    if (pathname.endsWith('/migration-plan')) return 'Migration plan';
    return 'Project';
  }
  if (pathname === '/corpora') return 'Projects';
  if (pathname.startsWith('/subroutines/')) {
    if (pathname.endsWith('/extract')) return 'Extract spec';
    if (pathname.endsWith('/spec')) return 'Draft spec';
    if (pathname.endsWith('/review')) return 'Spec review';
    return 'Subroutine';
  }
  if (pathname === '/subroutines') return 'Subroutines';
  if (pathname.startsWith('/specs/')) {
    if (pathname.endsWith('/audit')) return 'Audit trail';
    if (pathname.endsWith('/scaffold')) return 'Generate scaffold';
    return 'Spec';
  }
  if (pathname.startsWith('/scaffolds/')) {
    if (pathname.endsWith('/validation')) return 'Validation';
    return 'Scaffold artifact';
  }
  if (pathname === '/my-reviews') return 'My reviews';
  if (pathname === '/comments') return 'Comments';
  if (pathname === '/compliance') return 'Compliance feed';
  if (pathname === '/platform') return 'Platform overview';
  if (pathname.startsWith('/platform/')) {
    const leaf = pathname.split('/').pop() ?? '';
    return prettyPlatformLeaf(leaf);
  }
  return 'Astra';
}

function prettyPlatformLeaf(leaf: string): string {
  switch (leaf) {
    case 'portfolio':      return 'Portfolio dashboard';
    case 'golden-dataset': return 'Golden Dataset';
    case 'prompts':        return 'Prompt Catalog';
    case 'harmonisation':  return 'Harmonisation';
    case 'languages':      return 'Languages';
    case 'validation':     return 'Validation Policy';
    case 'signatures':     return 'Signature Health';
    case 'roles':          return 'Roles & Permissions';
    default:               return leaf.charAt(0).toUpperCase() + leaf.slice(1);
  }
}
