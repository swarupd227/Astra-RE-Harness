import { Link, NavLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { clsx } from 'clsx';
import { Tooltip } from '@/components/Tooltip';
import { Skeleton } from '@/components/Skeleton';
import { conversationsApi, type Conversation } from '@/lib/conversations';

/**
 * The "Programmes" block of the rail: one link per programme thread, plus
 * "New programme". Shared by the desktop rail and the mobile drawer.
 *
 * Read-only consumer of `conversationsApi.list()`; if the conversations API
 * is unreachable the block degrades to a quiet notice rather than an error.
 */
export function RailProgrammes({
  collapsed = false,
  onNavigate,
}: {
  collapsed?: boolean;
  onNavigate?: () => void;
}) {
  const threads = useQuery({
    queryKey: ['conversations'],
    queryFn: () => conversationsApi.list(),
    retry: false,
    staleTime: 30_000,
    refetchInterval: 60_000,
  });

  const programmes = (threads.data?.data ?? []).filter(
    (c): c is Conversation & { corpusId: string } => c.kind === 'programme' && !!c.corpusId,
  );

  const newLink = (
    <Link
      to="/projects/new"
      onClick={onNavigate}
      className={clsx(
        'flex items-center rounded-md py-1.5 text-caption text-ink-tertiary transition-colors duration-fast hover:bg-raised/60 hover:text-ink-primary',
        collapsed ? 'justify-center px-2' : 'gap-2.5 px-2.5',
      )}
      data-testid="rail-new-programme"
      aria-label="New programme"
    >
      <span className="grid h-6 w-6 shrink-0 place-items-center rounded-md border border-dashed border-line">
        <Plus size={12} aria-hidden="true" />
      </span>
      {!collapsed && <span className="truncate">New programme</span>}
    </Link>
  );

  if (threads.isPending) {
    return (
      <div className={clsx('space-y-1', collapsed ? 'px-2' : 'px-2.5')} aria-busy="true">
        {[0, 1, 2].map((i) => (
          <Skeleton key={i} className={collapsed ? 'h-6 w-6 rounded-md' : 'h-9 w-full'} />
        ))}
      </div>
    );
  }

  return (
    <div className="space-y-0.5">
      {threads.isError ? (
        !collapsed && (
          <p className="px-2.5 py-1 text-micro text-ink-tertiary">Programmes unavailable</p>
        )
      ) : programmes.length === 0 ? (
        !collapsed && <p className="px-2.5 py-1 text-micro text-ink-tertiary">No programmes yet</p>
      ) : (
        programmes.map((c) => (
          <ProgrammeLink key={c.id} conversation={c} collapsed={collapsed} onNavigate={onNavigate} />
        ))
      )}
      {newLink}
    </div>
  );
}

function ProgrammeLink({
  conversation,
  collapsed,
  onNavigate,
}: {
  conversation: Conversation & { corpusId: string };
  collapsed: boolean;
  onNavigate?: () => void;
}) {
  const title = conversation.programme?.name ?? conversation.title;
  const preview = conversation.lastMessagePreview ?? 'No messages yet';
  const initials = title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? '')
    .join('');

  const className = ({ isActive }: { isActive: boolean }) =>
    clsx(
      'flex items-center rounded-md transition-colors duration-fast',
      collapsed ? 'justify-center px-2 py-1.5' : 'gap-2.5 px-2.5 py-1.5',
      isActive ? 'bg-raised text-ink-primary shadow-e1' : 'text-ink-secondary hover:bg-raised/60 hover:text-ink-primary',
    );

  const link = (
    <NavLink
      to={`/w/${conversation.id}`}
      onClick={onNavigate}
      className={className}
      data-testid={`rail-programme-${conversation.corpusId}`}
      aria-label={collapsed ? title : undefined}
    >
      <span
        className="grid h-6 w-6 shrink-0 place-items-center rounded-md bg-sand-200/15 font-mono text-[10px] font-semibold text-sand-200"
        aria-hidden="true"
      >
        {initials || '·'}
      </span>
      {!collapsed && (
        <span className="min-w-0 flex-1">
          <span className="block truncate text-caption font-medium leading-tight">{title}</span>
          <span className="block truncate text-micro leading-tight text-ink-tertiary">{preview}</span>
        </span>
      )}
    </NavLink>
  );

  if (collapsed) {
    return (
      <Tooltip content={title} side="right">
        {link}
      </Tooltip>
    );
  }
  return link;
}
