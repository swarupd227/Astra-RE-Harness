import { useEffect, useState, type ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Activity,
  BookOpen,
  ChevronLeft,
  ChevronRight,
  ClipboardList,
  Cog,
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
import { AGENT_ORDER, agentMeta } from '@/agents/agents';
import { RailProgrammes } from '@/shell/RailProgrammes';

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

// The artifact views, sectioned by activity area. Exported so the mobile
// drawer (MobileNav) renders the same source of truth rather than a
// duplicated list. Labels drive the `nav-<label-kebab>` test ids, so they
// are load-bearing for the e2e suite — don't rename casually.
export const SECTIONS: Section[] = [
  {
    title: 'Views',
    items: [
      { to: '/home',        label: 'Home',           icon: Home },
      { to: '/projects',    label: 'Projects',       icon: Layers },
      { to: '/subroutines', label: 'Routines',       icon: FileSearch },
      { to: '/scaffolds',   label: 'Generated code', icon: Cog },
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
      { to: '/compliance',  label: 'Compliance',    icon: ShieldCheck },
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

/** `nav-<label-kebab>` — the id the e2e suite clicks. */
export function navTestId(label: string): string {
  return `nav-${label.toLowerCase().replace(/\s+/g, '-')}`;
}

/** Routes that must only be "active" on an exact match (they have sub-routes). */
const EXACT = new Set(['/', '/home', '/platform']);
export const isExactNavRoute = (to: string) => EXACT.has(to);

export const navItemClass = (collapsed: boolean) =>
  ({ isActive }: { isActive: boolean }) =>
    clsx(
      'flex items-center rounded-md py-1.5 text-caption font-medium transition-colors duration-fast',
      collapsed ? 'justify-center px-2' : 'gap-2.5 px-2.5',
      isActive
        ? 'bg-raised text-ink-primary shadow-e1'
        : 'text-ink-secondary hover:bg-raised/60 hover:text-ink-primary',
    );

/**
 * The rail. Ask Astra (the conversation) first, then the programme threads,
 * the agents, and finally the artifact views — the old navigation, now one
 * step down from the conversation that drives it.
 */
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
  // footer "live" dot. Three states, not two: while the first probe is in
  // flight we show "Checking…" rather than flashing "offline" on every load.
  const health = useQuery({
    queryKey: ['health-probe'],
    queryFn: () => api.health(),
    refetchInterval: 60_000,
  });
  const live = health.isPending ? null : health.data?.status === 'ok';
  const liveColor = live === null ? 'text-ink-tertiary' : live ? 'text-status-ok' : 'text-status-fail';
  const liveLabel = live === null ? 'Checking…' : live ? 'Audit-logged · evidence-ready' : 'API offline';

  return (
    <aside
      className={clsx(
        'hidden shrink-0 flex-col border-r border-line-subtle bg-sunken transition-[width] duration-medium md:flex',
        collapsed ? 'w-14' : 'w-64',
      )}
      data-testid="left-nav"
    >
      <nav
        className={clsx('flex-1 overflow-y-auto overflow-x-hidden py-3', collapsed ? 'px-1.5' : 'px-2.5')}
        aria-label="Primary"
      >
        {/* (a) Ask Astra — the conversation is the front door */}
        <div className="mb-4">
          <AskAstraLink collapsed={collapsed} />
        </div>

        {/* (b) Programmes — one thread per programme */}
        <RailSection title="Programmes" collapsed={collapsed}>
          <RailProgrammes collapsed={collapsed} />
        </RailSection>

        {/* (c) Agents — decorative for now */}
        {!collapsed && (
          <RailSection title="Agents" collapsed={false}>
            <AgentsRow />
          </RailSection>
        )}

        {/* (d) Views — the artifact views */}
        {SECTIONS.filter((s) => !s.adminOnly || isAdmin).map((section) => (
          <RailSection key={section.title} title={section.title} collapsed={collapsed}>
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
          </RailSection>
        ))}
      </nav>

      {/* Footer — health, identity, version, collapse toggle */}
      <div className={clsx('border-t border-line-subtle py-3', collapsed ? 'px-1.5' : 'px-3')}>
        {!collapsed && (
          <div className="mb-3 space-y-1.5">
            <div className="flex items-center gap-2 text-caption text-ink-secondary">
              <ShieldCheck size={13} className={clsx('shrink-0', liveColor)} aria-hidden="true" />
              {liveLabel}
            </div>
            <div className="flex items-center gap-2 text-caption text-ink-secondary">
              <Cpu size={13} className={clsx('shrink-0', liveColor)} aria-hidden="true" />
              <span className="truncate font-mono">
                {whoami.data?.displayName ? `signed in as ${whoami.data.displayName}` : '…'}
              </span>
            </div>
            <div className="text-[9px] text-ink-tertiary">
              v{buildInfo.version} · {buildInfo.builtAt}
            </div>
          </div>
        )}
        <button
          type="button"
          onClick={() => setCollapsed((c) => !c)}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-expanded={!collapsed}
          className={clsx(
            'flex w-full items-center rounded-md py-1.5 text-caption text-ink-tertiary transition-colors duration-fast hover:bg-raised/60 hover:text-ink-primary',
            collapsed ? 'justify-center px-2' : 'gap-2 px-2',
          )}
          data-testid="sidebar-collapse-toggle"
        >
          {collapsed ? <ChevronRight size={15} aria-hidden="true" /> : (<><ChevronLeft size={15} aria-hidden="true" /> Collapse</>)}
        </button>
      </div>
    </aside>
  );
}

/** Section heading + body; the heading disappears when collapsed. */
function RailSection({ title, collapsed, children }: { title: string; collapsed: boolean; children: ReactNode }) {
  return (
    <div className="mb-4">
      {collapsed ? (
        <div aria-hidden="true" className="mx-2 mb-2 border-t border-line-subtle" />
      ) : (
        <p className="label mb-1 px-2.5">{title}</p>
      )}
      {children}
    </div>
  );
}

/** The one volt-accented item: the conversation. */
export function AskAstraLink({ collapsed, onNavigate }: { collapsed: boolean; onNavigate?: () => void }) {
  const link = (
    <NavLink
      to="/"
      end
      onClick={onNavigate}
      className={({ isActive }) =>
        clsx(
          'flex items-center rounded-md py-2 text-body font-semibold transition-colors duration-fast',
          collapsed ? 'justify-center px-2' : 'gap-2.5 px-2.5',
          isActive
            ? 'bg-volt/10 text-ink-primary ring-1 ring-inset ring-volt/30'
            : 'text-ink-primary hover:bg-raised/60',
        )
      }
      data-testid="rail-ask-astra"
      aria-label={collapsed ? 'Ask Astra' : undefined}
    >
      <Sparkles size={16} className="shrink-0 text-volt" aria-hidden="true" />
      {!collapsed && <span className="truncate">Ask Astra</span>}
    </NavLink>
  );
  if (collapsed) {
    return (
      <Tooltip content="Ask Astra" side="right">
        {link}
      </Tooltip>
    );
  }
  return link;
}

/** Compact row of the ten agent avatars. Decorative for now. */
function AgentsRow() {
  return (
    <ul className="flex flex-wrap gap-1.5 px-2.5" aria-label="Agents">
      {AGENT_ORDER.map((id) => {
        const m = agentMeta(id);
        const Icon = m.icon;
        return (
          <li key={id} className="flex">
            <Tooltip content={m.name} side="top">
              <span
                className={clsx('grid h-7 w-7 place-items-center rounded-full', m.disc, m.tone)}
                role="img"
                aria-label={`${m.name} agent`}
              >
                <Icon size={14} aria-hidden="true" />
              </span>
            </Tooltip>
          </li>
        );
      })}
    </ul>
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
  const inner = (
    <>
      <Icon size={15} className="shrink-0" aria-hidden="true" />
      {!collapsed && <span className="flex-1 truncate">{item.label}</span>}
      {!collapsed && badge !== undefined && (
        <span
          className="rounded-full bg-volt px-1.5 py-0.5 text-[9px] font-bold text-on-volt"
          aria-label={`${badge} unread`}
          data-testid="nav-unread-badge"
        >
          {badge > 99 ? '99+' : badge}
        </span>
      )}
    </>
  );

  const link = (
    <NavLink
      to={item.to}
      end={EXACT.has(item.to)}
      className={navItemClass(collapsed)}
      data-testid={navTestId(item.label)}
      aria-label={collapsed ? item.label : undefined}
    >
      {inner}
    </NavLink>
  );

  if (collapsed) {
    return (
      <Tooltip content={item.label} side="right">
        {link}
      </Tooltip>
    );
  }
  return link;
}
