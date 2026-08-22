import { useEffect, useState } from 'react';
import { NavLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Activity,
  BookOpen,
  ChevronLeft,
  ChevronRight,
  ClipboardList,
  Cpu,
  Database,
  FileSearch,
  FileText,
  GitBranch,
  Home,
  KeyRound,
  Languages,
  Layers,
  MessageSquare,
  Network,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  Users,
} from 'lucide-react';
import { clsx } from 'clsx';
import { Tooltip } from '@/components/Tooltip';
import { buildInfo } from '@/lib/version';
import { api, notificationsApi } from '@/lib/api';

type Item = {
  to: string;
  label: string;
  icon: typeof Home;
};

type Section = {
  title: string;
  items: Item[];
  adminOnly?: boolean;
};

// ACE pattern: dark indigo sidebar, sectioned by activity area, with
// a collapsible flag persisted in localStorage. Compact 13px nav rows.
// Exported so the mobile nav drawer (MobileNav) renders the same source of
// truth rather than a duplicated list.
export const SECTIONS: Section[] = [
  {
    title: 'Workspace',
    items: [
      { to: '/',            label: 'Home',         icon: Home },
      { to: '/projects',    label: 'Projects',     icon: Layers },
      { to: '/subroutines', label: 'Subroutines',  icon: FileSearch },
    ],
  },
  {
    title: 'My queue',
    items: [
      { to: '/my-reviews',  label: 'My reviews',   icon: ClipboardList },
      { to: '/comments',    label: 'Comments',     icon: MessageSquare },
    ],
  },
  {
    title: 'Governance',
    items: [
      { to: '/compliance',  label: 'Compliance',   icon: ShieldCheck },
      { to: '/system',      label: 'System health', icon: Activity },
    ],
  },
  {
    // Read-only analytics + catalogs — visible to every persona so the
    // platform surfaces are discoverable, not just to admins.
    title: 'Platform',
    items: [
      { to: '/platform',                label: 'Overview',         icon: Sparkles },
      { to: '/platform/portfolio',      label: 'Portfolio',        icon: Network },
      { to: '/platform/golden-dataset', label: 'Golden Dataset',   icon: Database },
      { to: '/platform/prompts',        label: 'Prompt Catalog',   icon: FileText },
      { to: '/platform/signatures',     label: 'Signature Health', icon: BookOpen },
      { to: '/platform/harmonisation',  label: 'Harmonisation',    icon: GitBranch },
      { to: '/platform/languages',      label: 'Languages',        icon: Languages },
    ],
  },
  {
    // Privileged configuration — remains admin-gated.
    title: 'Admin',
    adminOnly: true,
    items: [
      { to: '/platform/validation',     label: 'Validation Policy',    icon: SlidersHorizontal },
      { to: '/platform/roles',          label: 'Roles & Permissions',  icon: Users },
      { to: '/platform/llm',            label: 'LLM Provider',         icon: KeyRound },
    ],
  },
];

export function LeftNav() {
  const [collapsed, setCollapsed] = useState(
    () => typeof window !== 'undefined' && localStorage.getItem('astra.sidebar-collapsed') === 'true',
  );
  useEffect(() => {
    localStorage.setItem('astra.sidebar-collapsed', String(collapsed));
  }, [collapsed]);

  const unread = useQuery({
    queryKey: ['notifications-unread'],
    queryFn: () => notificationsApi.unreadCount(),
    refetchInterval: 30_000,
  });
  const unreadCount = unread.data?.unread ?? 0;

  const whoami = useQuery({ queryKey: ['whoami'], queryFn: api.whoami });
  const isAdmin = whoami.data?.persona === 'admin';

  // Probe a cheap endpoint to know the platform is reachable — flips the
  // footer "live" dot green/rose.
  const health = useQuery({
    queryKey: ['health-probe'],
    queryFn: () => api.health(),
    refetchInterval: 60_000,
  });
  const live = health.data?.status === 'ok';

  return (
    <aside
      className={clsx(
        'hidden shrink-0 flex-col bg-ace-900 text-slate-200 transition-all duration-200 md:flex',
        collapsed ? 'w-14' : 'w-60',
      )}
      data-testid="left-nav"
    >
      {/* Brand block — orange square + lockup */}
      <div className={clsx('border-b border-white/10 py-4', collapsed ? 'px-2' : 'px-4')}>
        <div className="flex items-center gap-2.5">
          <div className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-brand-mark font-extrabold text-white shadow-card">
            A
          </div>
          {!collapsed && (
            <div className="min-w-0">
              <div className="truncate text-[13px] font-bold leading-tight text-white">Astra Enterprise</div>
              <div className="truncate text-micro text-slate-400 leading-tight">Reverse-engineering harness</div>
            </div>
          )}
        </div>
        {!collapsed && (
          <div className="mt-3 text-micro uppercase tracking-wider text-slate-500">
            Multi-language · multi-target
          </div>
        )}
      </div>

      {/* Nav */}
      <nav
        className={clsx('flex-1 overflow-y-auto py-3', collapsed ? 'px-1' : 'px-2.5')}
        aria-label="Primary"
      >
        {SECTIONS.filter((s) => !s.adminOnly || isAdmin).map((section) => (
          <div key={section.title} className="mb-3">
            {!collapsed && (
              <p className="px-2 pb-1 text-micro font-semibold uppercase tracking-wider text-slate-500">
                {section.title}
              </p>
            )}
            <div className="space-y-0.5">
              {section.items.map((item) => (
                <ActiveItem
                  key={item.to}
                  item={item}
                  collapsed={collapsed}
                  badge={item.to === '/comments' && unreadCount > 0 ? unreadCount : undefined}
                />
              ))}
            </div>
          </div>
        ))}
      </nav>

      {/* Footer — health pills + collapse toggle */}
      <div className={clsx('border-t border-white/10 py-3', collapsed ? 'px-1' : 'px-3')}>
        {!collapsed && (
          <div className="mb-3 space-y-1.5">
            <div className="flex items-center gap-2 text-caption text-slate-400">
              <ShieldCheck
                size={13}
                className={clsx('shrink-0', live ? 'text-emerald-400' : 'text-rose-400')}
              />
              {live ? 'Audit-logged · evidence-ready' : 'API offline'}
            </div>
            <div className="flex items-center gap-2 text-caption text-slate-400">
              <Cpu size={13} className={clsx('shrink-0', live ? 'text-emerald-400' : 'text-rose-400')} />
              <span className="truncate font-mono">
                {whoami.data?.displayName ? `signed in as ${whoami.data.displayName}` : '…'}
              </span>
            </div>
            <div className="text-[9px] text-slate-600">
              v{buildInfo.version} · {buildInfo.builtAt}
            </div>
          </div>
        )}
        <button
          type="button"
          onClick={() => setCollapsed((c) => !c)}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          className={clsx(
            'flex w-full items-center rounded-md py-1.5 text-caption text-slate-400 transition-colors hover:bg-white/5 hover:text-white',
            collapsed ? 'justify-center px-2' : 'gap-2 px-2',
          )}
          data-testid="sidebar-collapse-toggle"
        >
          {collapsed ? <ChevronRight size={15} /> : (<><ChevronLeft size={15} /> Collapse</>)}
        </button>
      </div>
    </aside>
  );
}

function ActiveItem({
  item,
  collapsed,
  badge,
}: {
  item: Item;
  collapsed: boolean;
  badge?: number;
}) {
  const Icon = item.icon;
  const inner = () => (
    <>
      <Icon size={15} className="shrink-0" />
      {!collapsed && <span className="flex-1 truncate">{item.label}</span>}
      {!collapsed && badge !== undefined && (
        <span
          className="rounded-full bg-brand-500 px-1.5 py-0.5 text-[9px] font-bold text-white"
          aria-label={`${badge} unread`}
          data-testid="nav-unread-badge"
        >
          {badge > 99 ? '99+' : badge}
        </span>
      )}
    </>
  );
  const className = ({ isActive }: { isActive: boolean }) =>
    clsx(
      'flex items-center rounded-md py-2 text-[12px] font-medium transition-colors',
      collapsed ? 'justify-center px-2' : 'gap-2.5 px-2.5',
      isActive
        ? 'bg-white/10 text-white'
        : 'text-slate-300 hover:bg-white/5 hover:text-white',
    );

  if (collapsed) {
    return (
      <Tooltip content={item.label} side="right">
        <NavLink
          to={item.to}
          end={item.to === '/'}
          className={className}
          data-testid={`nav-${item.label.toLowerCase().replace(/\s+/g, '-')}`}
        >
          {inner}
        </NavLink>
      </Tooltip>
    );
  }
  return (
    <NavLink
      to={item.to}
      end={item.to === '/'}
      className={className}
      data-testid={`nav-${item.label.toLowerCase().replace(/\s+/g, '-')}`}
    >
      {inner}
    </NavLink>
  );
}
