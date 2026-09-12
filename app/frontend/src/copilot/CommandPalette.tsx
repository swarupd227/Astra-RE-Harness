/**
 * ⌘K — the natural-language entry point. Type anything and the first row
 * is "Ask Astra: …", which routes the text to the orchestrator in the
 * current context (the open programme thread, else Mission Control).
 * Programmes, navigation and theme sit underneath as fallbacks.
 */
import { useEffect, useState } from 'react';
import { Command } from 'cmdk';
import { useQuery } from '@tanstack/react-query';
import { useLocation, useNavigate } from 'react-router-dom';
import {
  Boxes,
  Braces,
  CornerDownLeft,
  FileCheck2,
  FolderGit2,
  Home,
  LayoutGrid,
  MessageSquare,
  Moon,
  ScrollText,
  Search,
  Sparkles,
  Sun,
  type LucideIcon,
} from 'lucide-react';
import { conversationsApi } from '@/lib/conversations';
import { closeCommandPalette, openCommandPalette, setPendingIntent, useCommandPalette } from './paletteStore';

type NavItem = { label: string; href: string; icon: LucideIcon; keywords?: string };

const GO_TO: NavItem[] = [
  { label: 'Home', href: '/home', icon: Home, keywords: 'start dashboard' },
  { label: 'Projects', href: '/projects', icon: FolderGit2, keywords: 'programmes corpora' },
  { label: 'Routines', href: '/subroutines', icon: Braces, keywords: 'subroutines functions' },
  { label: 'Generated code', href: '/scaffolds', icon: LayoutGrid, keywords: 'scaffolds output' },
  { label: 'My reviews', href: '/my-reviews', icon: FileCheck2, keywords: 'specs sign review queue' },
  { label: 'Compliance', href: '/compliance', icon: ScrollText, keywords: 'audit evidence sox hipaa pci' },
  { label: 'Platform', href: '/platform', icon: Boxes, keywords: 'settings prompts languages roles' },
  { label: 'System health', href: '/system', icon: Sparkles, keywords: 'status readiness' },
];

const THEME_KEY = 'astra.theme';

function applyTheme(theme: 'light' | 'dark') {
  document.documentElement.dataset.theme = theme;
  try {
    localStorage.setItem(THEME_KEY, theme);
  } catch {
    /* private mode */
  }
}

export function CommandPalette() {
  const { open, initialQuery } = useCommandPalette();
  const [query, setQuery] = useState('');
  const navigate = useNavigate();
  const location = useLocation();

  useEffect(() => {
    if (open) setQuery(initialQuery ?? '');
  }, [open, initialQuery]);

  // ⌘K / Ctrl+K opens (idempotent, so a second binding in the shell is harmless).
  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        openCommandPalette();
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, []);

  const programmes = useQuery({
    queryKey: ['conversations'],
    queryFn: () => conversationsApi.list(),
    enabled: open,
    staleTime: 30_000,
  });

  if (!open) return null;

  const trimmed = query.trim();
  const onThread = /^\/w\/[^/]+/.test(location.pathname);

  const go = (href: string) => {
    closeCommandPalette();
    navigate(href);
  };

  const ask = (text: string) => {
    const t = text.trim();
    if (!t) return;
    setPendingIntent(t);
    closeCommandPalette();
    navigate(onThread ? location.pathname : '/');
  };

  const theme = (t: 'light' | 'dark') => {
    applyTheme(t);
    closeCommandPalette();
  };

  const threads = (programmes.data?.data ?? []).filter((c) => c.kind === 'programme');

  return (
    <div
      className="fixed inset-0 z-[60] flex items-start justify-center px-4 pt-[14vh]"
      role="dialog"
      aria-modal="true"
      aria-label="Command palette"
      data-testid="command-palette"
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          e.preventDefault();
          e.stopPropagation();
          closeCommandPalette();
        }
      }}
    >
      <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={closeCommandPalette} aria-hidden="true" />
      <Command
        label="Command palette"
        loop
        className="relative flex w-full max-w-[620px] flex-col overflow-hidden rounded-xl border border-line bg-raised text-ink-primary shadow-e3"
      >
        <div className="flex items-center gap-3 border-b border-line-subtle px-4">
          <Search size={16} className="shrink-0 text-ink-tertiary" aria-hidden="true" />
          <Command.Input
            autoFocus
            value={query}
            onValueChange={setQuery}
            placeholder="Ask Astra anything, jump to a programme, or type a command…"
            data-testid="command-palette-input"
            className="h-12 w-full bg-transparent text-body text-ink-primary outline-none placeholder:text-ink-tertiary"
          />
          <kbd className="hidden shrink-0 rounded border border-line-subtle bg-sunken px-1.5 py-0.5 font-mono text-micro text-ink-tertiary sm:inline">
            esc
          </kbd>
        </div>

        <Command.List
          className={[
            'max-h-[min(60vh,480px)] overflow-y-auto p-2',
            '[&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:pt-2',
            '[&_[cmdk-group-heading]]:text-micro [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:uppercase',
            '[&_[cmdk-group-heading]]:tracking-wide [&_[cmdk-group-heading]]:text-ink-tertiary',
          ].join(' ')}
        >
          <Command.Empty className="px-3 py-8 text-center text-caption text-ink-tertiary">
            Nothing matches — press ⏎ to ask Astra instead.
          </Command.Empty>

          {trimmed && (
            <Command.Group heading="Ask">
              <Command.Item
                forceMount
                value={`ask ${trimmed}`}
                onSelect={() => ask(trimmed)}
                data-testid="command-palette-ask"
                className={ITEM}
              >
                <span className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-volt/15 text-volt">
                  <Sparkles size={14} aria-hidden="true" />
                </span>
                <span className="min-w-0 flex-1 truncate">
                  <span className="text-ink-secondary">Ask Astra: </span>
                  <span className="text-ink-primary">“{trimmed}”</span>
                </span>
                <span className="hidden shrink-0 items-center gap-1 text-micro text-ink-tertiary sm:inline-flex">
                  {onThread ? 'this programme' : 'Mission Control'}
                  <CornerDownLeft size={12} aria-hidden="true" />
                </span>
              </Command.Item>
            </Command.Group>
          )}

          {(threads.length > 0 || programmes.isLoading) && (
            <Command.Group heading="Programmes">
              {programmes.isLoading && threads.length === 0 && (
                <Command.Loading>
                  <div className="px-3 py-2 text-caption text-ink-tertiary">Loading programmes…</div>
                </Command.Loading>
              )}
              {threads.map((c) => (
                <Command.Item
                  key={c.id}
                  value={`programme ${c.title} ${c.programme?.name ?? ''} ${c.programme?.sourceLanguage ?? ''}`}
                  keywords={[c.programme?.name ?? '', c.programme?.sourceLanguage ?? '']}
                  onSelect={() => go(`/w/${c.id}`)}
                  data-testid={`command-palette-programme-${c.corpusId ?? c.id}`}
                  className={ITEM}
                >
                  <span className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-sunken text-ink-secondary">
                    <MessageSquare size={14} aria-hidden="true" />
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-ink-primary">{c.programme?.name || c.title}</span>
                    {c.lastMessagePreview && (
                      <span className="block truncate text-micro text-ink-tertiary">{c.lastMessagePreview}</span>
                    )}
                  </span>
                  {c.programme?.sourceLanguage && (
                    <span className="shrink-0 text-micro text-ink-tertiary">{c.programme.sourceLanguage}</span>
                  )}
                </Command.Item>
              ))}
            </Command.Group>
          )}

          <Command.Group heading="Go to">
            {GO_TO.map((n) => {
              const Icon = n.icon;
              return (
                <Command.Item
                  key={n.href}
                  value={`go ${n.label}`}
                  keywords={n.keywords ? n.keywords.split(' ') : undefined}
                  onSelect={() => go(n.href)}
                  data-testid={`command-palette-go-${n.label.toLowerCase().replace(/\s+/g, '-')}`}
                  className={ITEM}
                >
                  <span className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-sunken text-ink-secondary">
                    <Icon size={14} aria-hidden="true" />
                  </span>
                  <span className="min-w-0 flex-1 truncate text-ink-primary">{n.label}</span>
                  <span className="shrink-0 font-mono text-micro text-ink-tertiary">{n.href}</span>
                </Command.Item>
              );
            })}
          </Command.Group>

          <Command.Group heading="Theme">
            <Command.Item value="theme dark" keywords={['night']} onSelect={() => theme('dark')} className={ITEM} data-testid="command-palette-theme-dark">
              <span className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-sunken text-ink-secondary">
                <Moon size={14} aria-hidden="true" />
              </span>
              <span className="flex-1 text-ink-primary">Dark theme</span>
            </Command.Item>
            <Command.Item value="theme light" keywords={['day']} onSelect={() => theme('light')} className={ITEM} data-testid="command-palette-theme-light">
              <span className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md bg-sunken text-ink-secondary">
                <Sun size={14} aria-hidden="true" />
              </span>
              <span className="flex-1 text-ink-primary">Light theme</span>
            </Command.Item>
          </Command.Group>
        </Command.List>

        <div className="flex items-center justify-between border-t border-line-subtle px-4 py-2 text-micro text-ink-tertiary">
          <span>
            <kbd className="rounded border border-line-subtle bg-sunken px-1 font-mono">↑↓</kbd> move{' '}
            <kbd className="ml-1.5 rounded border border-line-subtle bg-sunken px-1 font-mono">⏎</kbd> select
          </span>
          <span>Astra · by Artizent</span>
        </div>
      </Command>
    </div>
  );
}

const ITEM = [
  'flex cursor-pointer select-none items-center gap-3 rounded-lg px-2.5 py-2 text-body text-ink-secondary',
  'aria-selected:bg-volt/10 aria-selected:text-ink-primary',
  'data-[disabled=true]:cursor-not-allowed data-[disabled=true]:opacity-50',
].join(' ');
