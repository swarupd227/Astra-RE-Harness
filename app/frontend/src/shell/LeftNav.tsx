import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Activity,
  BookOpen,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  ClipboardList,
  Cog,
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
import { useAgentActivity } from '@/agents/useAgentActivity';
import { RailProgrammes } from '@/shell/RailProgrammes';

type Item = {
  to: string;
  label: string;
  icon: typeof Home;
  /** Kept when a label changes, so the e2e suite's `nav-<label>` id stays stable. */
  testId?: string;
  /** The page itself is Admin-only: don't send other personas to a locked door. */
  adminOnly?: boolean;
};

type Section = {
  title: string;
  items: Item[];
  adminOnly?: boolean;
  /** Open on first visit; the user's choice is remembered after that. */
  defaultOpen?: boolean;
};

// The artifact views, sectioned by activity area. Exported so the mobile
// drawer (MobileNav) renders the same source of truth rather than a
// duplicated list. Labels drive the `nav-<label-kebab>` test ids, so they
// are load-bearing for the e2e suite — don't rename casually.
export const SECTIONS: Section[] = [
  {
    // Everyday work, top to bottom in the order a migration moves.
    title: 'Work',
    defaultOpen: true,
    items: [
      { to: '/home',        label: 'Dashboard',      icon: Home, testId: 'nav-home' },
      { to: '/projects',    label: 'Projects',       icon: Layers },
      { to: '/subroutines', label: 'Routines',       icon: FileSearch },
      { to: '/scaffolds',   label: 'Generated code', icon: Cog },
      { to: '/my-reviews',  label: 'My reviews',     icon: ClipboardList },
      { to: '/comments',    label: 'Comments',       icon: MessageSquare },
    ],
  },
  {
    // Is the output trustworthy: signatures, compliance, the golden set.
    title: 'Quality',
    defaultOpen: true,
    items: [
      { to: '/compliance',              label: 'Compliance',       icon: ShieldCheck },
      { to: '/platform/signatures',     label: 'Signature Health', icon: BookOpen },
      { to: '/platform/golden-dataset', label: 'Golden Dataset',   icon: Database },
      { to: '/platform/harmonisation',  label: 'Harmonisation',    icon: GitBranch },
    ],
  },
  {
    // How the platform itself is set up and running — visible to every persona.
    title: 'Platform',
    items: [
      { to: '/platform',           label: 'Overview',       icon: Sparkles, adminOnly: true },
      { to: '/platform/portfolio', label: 'Portfolio',      icon: Network, adminOnly: true },
      { to: '/platform/prompts',   label: 'Prompt Catalog', icon: FileText },
      { to: '/platform/languages', label: 'Languages',      icon: Languages },
      { to: '/system',             label: 'System health',  icon: Activity },
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

/** An item's test id: its pinned id if a label was renamed, else derived from the label. */
export function itemTestId(item: Item): string {
  return item.testId ?? navTestId(item.label);
}

/** Routes that must only be "active" on an exact match (they have sub-routes). */
const EXACT = new Set(['/', '/home', '/platform']);
export const isExactNavRoute = (to: string) => EXACT.has(to);

/** Is `pathname` inside this item's route? */
function routeMatches(to: string, pathname: string): boolean {
  if (EXACT.has(to)) return pathname === to;
  return pathname === to || pathname.startsWith(`${to}/`);
}

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
  const liveLabel = live === null ? 'Checking…' : live ? 'Online' : 'API offline';
  const location = useLocation();

  return (
    <aside
      className={clsx(
        'hidden shrink-0 flex-col border-r border-line-subtle bg-sunken transition-[width] duration-medium md:flex',
        collapsed ? 'w-14' : 'w-[272px]',
      )}
      data-testid="left-nav"
    >
      <nav
        // `relative` keeps absolutely-positioned children (the collapsed Agents list is `sr-only`, i.e. absolute) inside
        // this scroll box; without it they hang off the document and give the page a second scrollbar.
        className={clsx('relative flex-1 overflow-y-auto overflow-x-hidden py-3', collapsed ? 'px-1.5' : 'px-2.5')}
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

        {/* (c) Views — grouped by what you are doing; Work open, the rest fold away */}
        {SECTIONS.filter((s) => !s.adminOnly || isAdmin).map((section) => (
          <CollapsibleSection
            key={section.title}
            id={section.title}
            title={section.title}
            collapsed={collapsed}
            defaultOpen={!!section.defaultOpen}
            forceOpen={section.items.some((i) => routeMatches(i.to, location.pathname))}
          >
            <div className="space-y-0.5">
              {section.items.filter((item) => !item.adminOnly || isAdmin).map((item) => (
                <ActiveItem
                  key={item.to}
                  item={item}
                  collapsed={collapsed}
                  badge={item.to === '/comments' && unreadCount > 0 ? unreadCount : undefined}
                />
              ))}
            </div>
          </CollapsibleSection>
        ))}

        {/* (d) Agents — named, folded by default; the header says who is working right now */}
        {!collapsed && <AgentsSection />}
      </nav>

      {/* Footer — health, identity, version, collapse toggle */}
      <div className={clsx('border-t border-line-subtle py-3', collapsed ? 'px-1.5' : 'px-3')}>
        {!collapsed && (
          <div
            className="mb-2 flex items-center gap-2 px-1 text-caption text-ink-secondary"
            title={`${liveLabel} · build ${buildInfo.commit}`}
            data-testid="rail-footer-identity"
          >
            <span
              className={clsx('h-2 w-2 shrink-0 rounded-full', live === null ? 'bg-ink-tertiary' : live ? 'bg-status-ok' : 'bg-status-fail')}
              aria-hidden="true"
            />
            <span className="sr-only">{liveLabel}. </span>
            <span className="truncate">{whoami.data?.displayName ?? '…'}</span>
            {live === false && <span className="shrink-0 text-status-failed">offline</span>}
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

/** A titled group the user can fold. Folded state is remembered; the group holding the current page always opens. */
function CollapsibleSection({
  id,
  title,
  collapsed,
  defaultOpen,
  forceOpen,
  children,
}: {
  id: string;
  title: string;
  collapsed: boolean;
  defaultOpen: boolean;
  forceOpen: boolean;
  children: ReactNode;
}) {
  const key = `astra.rail-open.${id}`;
  const [open, setOpen] = useState<boolean>(() => {
    if (typeof window === 'undefined') return defaultOpen;
    const v = localStorage.getItem(key);
    return v === null ? defaultOpen : v === 'true';
  });
  const toggle = () =>
    setOpen((o) => {
      localStorage.setItem(key, String(!o));
      return !o;
    });
  if (collapsed) return <RailSection title={title} collapsed>{children}</RailSection>;
  const shown = open || forceOpen;
  return (
    <div className="mb-3">
      <button
        type="button"
        onClick={toggle}
        aria-expanded={shown}
        className="label mb-1 flex w-full items-center gap-1 rounded px-2.5 py-0.5 text-left hover:text-ink-primary"
        data-testid={`rail-section-${id.toLowerCase()}`}
      >
        <ChevronDown size={11} className={clsx('shrink-0 transition-transform', !shown && '-rotate-90')} aria-hidden="true" />
        <span className="flex-1">{title}</span>
      </button>
      {shown && children}
    </div>
  );
}

/** The one volt-accented item: the conversation. */
export function AskAstraLink({ collapsed, onNavigate }: { collapsed: boolean; onNavigate?: () => void }) {
  const link = (
    <NavLink
      to="/"
      end
      onClick={() => {
        onNavigate?.();
        // Home's composer is where you ask; take the user straight into it (after the route has rendered).
        window.setTimeout(() => window.dispatchEvent(new Event('astra:focus-composer')), 60);
      }}
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

/**
 * The ten agents, driven by `GET /api/v1/copilot/agents` (polled by `useAgentActivity`). Folded by default to one
 * line that says how many are working; open, each agent is a named row. An agent with `activeRuns > 0` gets the volt
 * pulsing ring — volt is reserved for "an agent is working" — and `data-active="true"`.
 */
function AgentsSection() {
  const { agents } = useAgentActivity();
  const working = agents.filter((a) => a.active).length;
  const [open, setOpen] = useState<boolean>(
    () => typeof window !== 'undefined' && localStorage.getItem('astra.rail-open.agents') === 'true',
  );
  const toggle = () =>
    setOpen((o) => {
      localStorage.setItem('astra.rail-open.agents', String(!o));
      return !o;
    });
  return (
    <div className="mb-3">
      <button
        type="button"
        onClick={toggle}
        aria-expanded={open}
        className="label mb-1 flex w-full items-center gap-1 rounded px-2.5 py-0.5 text-left hover:text-ink-primary"
        data-testid="rail-section-agents"
      >
        <ChevronDown size={11} className={clsx('shrink-0 transition-transform', !open && '-rotate-90')} aria-hidden="true" />
        <span className="flex-1">Agents ({agents.length})</span>
        {working > 0 && <span className="normal-case tracking-normal text-volt-ink">{working} working</span>}
      </button>
      <ul className={clsx(open ? 'space-y-0.5' : 'sr-only')} aria-label="Agents">
        {agents.map((a) => {
          const m = a.meta;
          const Icon = m.icon;
          const runs = a.activeRuns;
          const detail = a.active ? `${runs} ${runs === 1 ? 'run' : 'runs'}` : describeLast(a.lastActivityAt, null);
          const tooltip = `${m.name} · ${runs} ${runs === 1 ? 'run' : 'runs'} · last: ${describeLast(a.lastActivityAt, a.lastMessage)}`;
          const link = a.lastConversationId ? `/w/${a.lastConversationId}` : null;
          const row = (
            <>
              <span
                className={clsx(
                  'relative grid h-6 w-6 shrink-0 place-items-center rounded-full transition-shadow duration-medium',
                  m.disc,
                  m.tone,
                  a.active && 'shadow-glow ring-2 ring-volt/70',
                )}
                role="img"
                aria-label={`${m.name} agent${a.active ? `, ${runs} active ${runs === 1 ? 'run' : 'runs'}` : ''}`}
              >
                {a.active && (
                  <span className="absolute inset-0 rounded-full bg-volt/30 motion-safe:animate-ping" aria-hidden="true" />
                )}
                <Icon size={12} aria-hidden="true" className="relative" />
              </span>
              <span className="min-w-0 flex-1 truncate">{m.name}</span>
              <span className={clsx('shrink-0 text-micro', a.active ? 'text-volt-ink' : 'text-ink-tertiary')}>{detail}</span>
            </>
          );
          const rowClass =
            'flex items-center gap-2.5 rounded-md px-2.5 py-1 text-caption text-ink-secondary hover:bg-raised/60 hover:text-ink-primary';
          return (
            <li key={a.id} data-testid={`rail-agent-${a.id}`} data-active={a.active ? 'true' : 'false'} title={tooltip}>
              {link ? (
                <NavLink to={link} className={rowClass} aria-label={tooltip}>
                  {row}
                </NavLink>
              ) : (
                <div className={rowClass}>{row}</div>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/** "3 min ago — Surveyed 12 routines" / "never" for the agent tooltip. */
function describeLast(at: string | null, message: string | null): string {
  if (!at) return 'never';
  const ms = Date.now() - new Date(at).getTime();
  const when =
    ms < 60_000 ? 'just now'
    : ms < 3_600_000 ? `${Math.round(ms / 60_000)} min ago`
    : ms < 86_400_000 ? `${Math.round(ms / 3_600_000)} h ago`
    : `${Math.round(ms / 86_400_000)} d ago`;
  if (!message) return when;
  const short = message.length > 48 ? `${message.slice(0, 46)}…` : message;
  return `${when} — ${short}`;
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
      data-testid={itemTestId(item)}
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
