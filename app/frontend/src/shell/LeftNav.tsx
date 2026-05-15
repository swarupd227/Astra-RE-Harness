import { NavLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Activity,
  ClipboardList,
  Database,
  FileSearch,
  FileText,
  Home,
  Languages,
  MessageSquare,
  ShieldCheck,
  SlidersHorizontal,
  Users,
} from 'lucide-react';
import { clsx } from 'clsx';
import { LogoGlyph } from '@/components/Logo';
import { Tooltip } from '@/components/Tooltip';
import { PhaseChip, phaseWindow, type PhaseId } from '@/components/PhaseChip';
import { buildInfo } from '@/lib/version';
import { api, notificationsApi } from '@/lib/api';

type Item = {
  to: string;
  label: string;
  icon: typeof Home;
  phase?: PhaseId;
  hint?: string;
};

const items: Item[] = [
  { to: '/', label: 'Home', icon: Home },
  { to: '/system', label: 'System', icon: Activity },
  { to: '/projects', label: 'Projects', icon: Database },
  { to: '/my-reviews', label: 'My reviews', icon: ClipboardList },
  {
    to: '/subroutines',
    label: 'Subroutines',
    icon: FileSearch,
  },
  {
    to: '/comments',
    label: 'Comments',
    icon: MessageSquare,
  },
  {
    to: '/compliance',
    label: 'Compliance',
    icon: ShieldCheck,
  },
];

// Phase #4 / Platform — Admin-only config surfaces. Items appear on the
// nav only when the current persona is "admin"; each item links to its
// own page (some still showing a "Coming in 2.X" disabled state until
// the page lands).
type PlatformItem = Item & { comingIn?: string };

const platformItems: PlatformItem[] = [
  { to: '/platform/prompts',    label: 'Prompt Catalog',     icon: FileText },
  { to: '/platform/languages',  label: 'Languages',          icon: Languages,          comingIn: '2.2' },
  { to: '/platform/validation', label: 'Validation Policy',  icon: SlidersHorizontal,  comingIn: '2.3' },
  { to: '/platform/signatures', label: 'Signature Health',   icon: ShieldCheck,        comingIn: '2.4' },
  { to: '/platform/roles',      label: 'Roles & Permissions', icon: Users,             comingIn: '2.5' },
];

export function LeftNav() {
  // Poll the unread count every 30s. Cheap: a single COUNT on an indexed
  // (recipient_persona, read_at) query. Re-firing on persona switch is handled
  // by react-query because PersonaMenu triggers a full-window reload.
  const unread = useQuery({
    queryKey: ['notifications-unread'],
    queryFn: () => notificationsApi.unreadCount(),
    refetchInterval: 30_000,
  });
  const unreadCount = unread.data?.unread ?? 0;

  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  return (
    <aside className="hidden w-60 shrink-0 flex-col justify-between border-r border-border-subtle bg-raised md:flex">
      <nav aria-label="Primary" className="flex flex-col gap-1 p-3">
        {items.map((item) =>
          item.phase ? (
            <DisabledItem key={item.to} item={item} />
          ) : (
            <ActiveItem
              key={item.to}
              item={item}
              badge={item.to === '/comments' && unreadCount > 0 ? unreadCount : undefined}
            />
          ),
        )}

        {isAdmin && (
          <div
            className="mt-3 border-t border-border-subtle pt-3"
            data-testid="nav-platform-section"
          >
            <p className="px-3 pb-1 font-mono text-[10px] uppercase tracking-wider text-ink-tertiary">
              Platform · Admin
            </p>
            {platformItems.map((item) =>
              item.comingIn ? (
                <PlatformPlaceholder key={item.to} item={item} />
              ) : (
                <ActiveItem key={item.to} item={item} />
              ),
            )}
          </div>
        )}
      </nav>

      <footer className="border-t border-border-subtle p-4">
        <div className="flex items-center gap-2">
          <LogoGlyph size={20} className="text-ink-tertiary" />
          <div className="min-w-0">
            <p className="truncate font-mono text-caption font-semibold text-ink-secondary">
              Astra RE Harness
            </p>
            <p className="truncate font-mono text-[10px] text-ink-tertiary">
              v{buildInfo.version} · Phase {buildInfo.phase} · {buildInfo.builtAt}
            </p>
          </div>
        </div>
      </footer>
    </aside>
  );
}

function ActiveItem({ item, badge }: { item: Item; badge?: number }) {
  const Icon = item.icon;
  return (
    <NavLink
      to={item.to}
      end={item.to === '/'}
      className={({ isActive }) =>
        clsx(
          // The 3px left border is always reserved; it goes invisible when the
          // item is inactive so the row positions don't shift on hover/active.
          'flex items-center gap-3 rounded-md border-l-[3px] pl-2.5 pr-3 py-2 text-body transition-colors duration-fast',
          isActive
            ? 'border-accent bg-accent-muted/40 font-semibold text-ink-primary'
            : 'border-transparent text-ink-secondary hover:bg-sunken hover:text-ink-primary',
        )
      }
      data-testid={`nav-${item.label.toLowerCase().replace(/\s+/g, '-')}`}
    >
      {({ isActive }) => (
        <>
          <Icon className={clsx('h-4 w-4', isActive && 'text-accent')} aria-hidden="true" />
          <span className="flex-1">{item.label}</span>
          {badge !== undefined && (
            <span
              className="rounded-full bg-accent px-1.5 py-0.5 font-mono text-[10px] font-semibold text-white"
              aria-label={`${badge} unread`}
              data-testid="nav-unread-badge"
            >
              {badge > 99 ? '99+' : badge}
            </span>
          )}
        </>
      )}
    </NavLink>
  );
}

function PlatformPlaceholder({ item }: { item: PlatformItem }) {
  const Icon = item.icon;
  return (
    <Tooltip
      side="bottom"
      content={
        <span className="block max-w-[260px] whitespace-normal text-left leading-snug">
          <span className="block font-semibold">{item.label}</span>
          <span className="mt-1 block text-ink-inverse/80">
            Coming in Phase {item.comingIn}.
          </span>
        </span>
      }
    >
      <span
        aria-disabled="true"
        className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-body text-ink-tertiary"
        data-testid={`nav-${item.label.toLowerCase().replace(/[\s&]+/g, '-')}`}
      >
        <Icon className="h-4 w-4 opacity-70" aria-hidden="true" />
        <span className="flex-1">{item.label}</span>
        <span className="rounded-sm border border-border-subtle bg-sunken px-1.5 py-0.5 font-mono text-[9px] uppercase tracking-wider text-ink-tertiary">
          {item.comingIn}
        </span>
      </span>
    </Tooltip>
  );
}

function DisabledItem({ item }: { item: Item }) {
  const Icon = item.icon;
  return (
    <Tooltip
      side="bottom"
      content={
        <span className="block max-w-[260px] whitespace-normal text-left leading-snug">
          <span className="block font-semibold">{phaseWindow(item.phase!)}</span>
          {item.hint && <span className="mt-1 block text-ink-inverse/80">{item.hint}</span>}
        </span>
      }
    >
      <span
        aria-disabled="true"
        className="flex w-full items-center gap-3 rounded-md px-3 py-2 text-body text-ink-tertiary"
      >
        <Icon className="h-4 w-4 opacity-70" aria-hidden="true" />
        <span className="flex-1">{item.label}</span>
        {item.phase && <PhaseChip phase={item.phase} />}
      </span>
    </Tooltip>
  );
}
