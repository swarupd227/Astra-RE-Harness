import { Fragment, useEffect } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { ChevronRight, CircleHelp, Menu, Moon, Sun } from 'lucide-react';
import { Badge } from '@/components/Badge';
import { CommandBarTrigger } from '@/components/CommandBarTrigger';
import { ApiLatencyIndicator } from '@/components/ApiLatencyIndicator';
import { LogoLockup } from '@/components/Logo';
import { PersonaMenu } from '@/shell/PersonaMenu';
import { Tooltip } from '@/components/Tooltip';
import { getRouteMeta } from '@/shell/routeMeta';
import { api } from '@/lib/api';
import { useTheme } from '@/theme/ThemeProvider';

const iconButton =
  'flex h-8 w-8 items-center justify-center rounded-md text-ink-secondary transition-colors duration-fast hover:bg-raised hover:text-ink-primary';

/**
 * Glass top bar: lockup, breadcrumb, ⌘K, status pills, theme toggle, persona.
 * Lives in the dark shell for every route — the breadcrumb for a legacy
 * (light-wrapped) page is still drawn here.
 */
export function TopBar({ onOpenHelp, onOpenNav }: { onOpenHelp: () => void; onOpenNav: () => void }) {
  const provider = useQuery({
    queryKey: ['provider-settings'],
    queryFn: () => api.getProviderSettings(),
  });

  const location = useLocation();
  const meta = getRouteMeta(location.pathname);
  const { theme, toggleTheme } = useTheme();
  const providerName = provider.data?.provider?.name;
  const providerModel = provider.data?.provider?.model;

  useEffect(() => {
    document.title = `${meta.title} · Astra`;
  }, [meta.title]);

  const nextTheme = theme === 'dark' ? 'light' : 'dark';

  return (
    <header className="glass sticky top-0 z-30 shrink-0">
      <div className="flex h-14 items-center gap-3 px-3 md:px-4">
        <button
          type="button"
          onClick={onOpenNav}
          className={`${iconButton} md:hidden`}
          aria-label="Open navigation menu"
          data-testid="mobile-nav-trigger"
        >
          <Menu className="h-5 w-5" aria-hidden="true" />
        </button>

        <Link
          to="/"
          className="shrink-0 rounded-md transition-opacity duration-fast hover:opacity-80"
          aria-label="Astra — Mission Control"
        >
          <LogoLockup size="sm" />
        </Link>

        <span aria-hidden="true" className="hidden h-5 w-px bg-line-subtle sm:block" />

        <nav aria-label="Breadcrumb" className="min-w-0 flex-1">
          <ol className="flex min-w-0 items-center gap-1 text-caption text-ink-secondary">
            {meta.crumbs.map((crumb, idx) => {
              const last = idx === meta.crumbs.length - 1;
              return (
                <Fragment key={`${crumb.label}-${idx}`}>
                  {idx > 0 && (
                    <li aria-hidden="true" className="shrink-0">
                      <ChevronRight className="h-3.5 w-3.5 text-ink-tertiary" />
                    </li>
                  )}
                  <li className={last ? 'min-w-0' : 'hidden min-w-0 sm:block'}>
                    {last || !crumb.href ? (
                      <span
                        className={last ? 'block truncate font-medium text-ink-primary' : 'block truncate'}
                        aria-current={last ? 'page' : undefined}
                        data-testid={last ? 'breadcrumb-current' : undefined}
                      >
                        {crumb.label}
                      </span>
                    ) : (
                      <Link
                        to={crumb.href}
                        className="block truncate rounded-sm transition-colors duration-fast hover:text-ink-primary"
                      >
                        {crumb.label}
                      </Link>
                    )}
                  </li>
                </Fragment>
              );
            })}
          </ol>
        </nav>

        <div className="ml-auto flex shrink-0 items-center gap-2">
          <CommandBarTrigger />
          <div className="hidden lg:block">
            <ApiLatencyIndicator />
          </div>
          {provider.data && (
            <Tooltip content={`${providerName ?? '—'} · ${providerModel ?? '—'}`} side="bottom">
              <span
                className="pill hidden bg-volt/10 font-mono text-volt-ink ring-1 ring-volt/25 sm:inline-flex"
                data-testid="topbar-provider-pill"
              >
                {labelForProvider(providerName, providerModel)}
              </span>
            </Tooltip>
          )}
          {import.meta.env.DEV && <Badge tone="neutral" className="hidden font-mono md:inline-flex">DEV</Badge>}
          <Tooltip content="Keyboard shortcuts (?)">
            <button type="button" onClick={onOpenHelp} className={iconButton} aria-label="Open keyboard help">
              <CircleHelp className="h-4 w-4" aria-hidden="true" />
            </button>
          </Tooltip>
          <Tooltip content={`Switch to ${nextTheme} theme`}>
            <button
              type="button"
              onClick={toggleTheme}
              className={iconButton}
              aria-label={`Switch to ${nextTheme} theme`}
              aria-pressed={theme === 'light'}
              data-testid="theme-toggle"
            >
              {theme === 'dark' ? (
                <Sun className="h-4 w-4" aria-hidden="true" />
              ) : (
                <Moon className="h-4 w-4" aria-hidden="true" />
              )}
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
