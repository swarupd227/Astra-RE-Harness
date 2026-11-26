import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router-dom';
import { clsx } from 'clsx';
import type { UseQueryResult } from '@tanstack/react-query';
import { absoluteTime, relativeTime } from '@/workspace/format';
import { CheckCircle2, Inbox, MessageSquare } from 'lucide-react';
import { notificationsApi, type NotificationItem, type RecentComment } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Button } from '@/components/Button';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

/**
 * Comments: mentions of you (your persona) first, and every recent thread across specs beside it — so the page is
 * useful even when nobody has mentioned you, and it shows where comments are written (on a spec's claims).
 */
export function CommentsPage() {
  const qc = useQueryClient();
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') === 'recent' ? 'recent' : 'mentions';
  const inbox = useQuery({
    queryKey: ['notifications-inbox'],
    queryFn: () => notificationsApi.list({ limit: 100 }),
    refetchInterval: 30_000,
  });
  const recent = useQuery({
    queryKey: ['comments-recent'],
    queryFn: () => notificationsApi.recentComments(50),
    enabled: tab === 'recent' || (inbox.isSuccess && (inbox.data?.data.length ?? 0) === 0),
    staleTime: 30_000,
  });

  const readAll = useMutation({
    mutationFn: () => notificationsApi.markAllRead(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['notifications-inbox'] });
      qc.invalidateQueries({ queryKey: ['notifications-unread'] });
    },
  });

  const data = inbox.data?.data ?? [];
  const unread = inbox.data?.unread ?? 0;
  const persona = inbox.data?.persona;
  const setTab = (t: 'mentions' | 'recent') => setParams(t === 'recent' ? { tab: 'recent' } : {}, { replace: true });

  return (
    <div className="mx-auto max-w-[1100px] space-y-5 p-4 lg:p-6 fadeup">
      <header className="flex flex-wrap items-start justify-between gap-4 border-b border-line-subtle pb-4">
        <div>
          <p className="label">Work</p>
          <h1 className="mt-1 text-h-lg font-semibold tracking-tight text-ink-primary">Comments</h1>
          <p className="mt-1 max-w-2xl text-body text-ink-secondary">
            Discussion on spec claims. Mentions of{' '}
            <span className="font-mono">@{persona ?? '…'}</span> land in Mentions for everyone in that role.
          </p>
        </div>
        {tab === 'mentions' && (
          <Button
            variant="secondary"
            onClick={() => readAll.mutate()}
            disabled={readAll.isPending || inbox.isPending || unread === 0}
            data-testid="read-all"
          >
            <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
            Mark all read{unread > 0 && ` (${unread})`}
          </Button>
        )}
      </header>

      <div className="flex gap-1" role="tablist" aria-label="Comments">
        <TabButton active={tab === 'mentions'} onClick={() => setTab('mentions')} testid="comments-tab-mentions">
          Mentions{unread > 0 && <span className="ml-1.5 rounded-full bg-volt px-1.5 text-[10px] font-bold text-on-volt">{unread}</span>}
        </TabButton>
        <TabButton active={tab === 'recent'} onClick={() => setTab('recent')} testid="comments-tab-recent">
          Recent threads
        </TabButton>
      </div>

      {tab === 'recent' ? (
        <RecentThreads query={recent} />
      ) : inbox.isPending ? (
        <div className="space-y-3" aria-busy="true">
          <p className="text-caption text-ink-secondary">Loading comments…</p>
          <Skeleton className="h-16 w-full" />
          <Skeleton className="h-16 w-full" />
        </div>
      ) : inbox.isError ? (
        <ErrorBlock title="Could not load inbox" message={inbox.error.message} onRetry={() => inbox.refetch()} />
      ) : data.length === 0 ? (
        <div className="space-y-4">
          <div className="rounded-xl border border-dashed border-line px-5 py-6">
            <p className="text-body font-medium text-ink-primary">No one has mentioned @{persona ?? '…'} yet</p>
            <p className="mt-1 text-caption text-ink-secondary">
              Comments are written on a spec&rsquo;s claims during review — open{' '}
              <Link to="/my-reviews" className="text-ink-link hover:underline">My reviews</Link> to see specs being
              discussed. What people are talking about right now:
            </p>
          </div>
          <RecentThreads query={recent} compact />
        </div>
      ) : (
        <Card>
          <CardHeader
            title={
              <span className="inline-flex items-center gap-2">
                <Inbox className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                {data.length} mention{data.length === 1 ? '' : 's'}
              </span>
            }
            description={`${unread} unread`}
          />
          <CardBody className="p-0">
            <ul className="divide-y divide-line-subtle" data-testid="inbox-list">
              {data.map((n) => (
                <InboxRow key={n.id} n={n} qc={qc} />
              ))}
            </ul>
          </CardBody>
        </Card>
      )}
    </div>
  );
}

function TabButton({ active, onClick, testid, children }: { active: boolean; onClick: () => void; testid: string; children: React.ReactNode }) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      className={clsx(
        'inline-flex items-center rounded-md px-3 py-1.5 text-caption font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt',
        active ? 'bg-raised text-ink-primary shadow-e1' : 'text-ink-secondary hover:text-ink-primary',
      )}
      data-testid={testid}
    >
      {children}
    </button>
  );
}

function RecentThreads({ query, compact = false }: { query: UseQueryResult<{ data: RecentComment[] }, Error>; compact?: boolean }) {
  if (query.isPending) {
    return (
      <div className="space-y-2" aria-busy="true">
        <Skeleton className="h-14 w-full" />
        <Skeleton className="h-14 w-full" />
      </div>
    );
  }
  if (query.isError) return <ErrorBlock title="Could not load recent comments" message={query.error.message} onRetry={() => query.refetch()} />;
  const rows = (query.data?.data ?? []).slice(0, compact ? 8 : 50);
  if (rows.length === 0) {
    return <p className="text-caption text-ink-secondary">No comments anywhere yet. Open a spec in review and comment on a claim to start one.</p>;
  }
  return (
    <ul className="divide-y divide-line-subtle rounded-xl border border-line-subtle bg-raised" data-testid="comments-recent">
      {rows.map((c) => (
        <li key={c.id}>
          <Link
            to={`/subroutines/${c.subroutineId}/review`}
            className="flex items-start gap-3 px-4 py-3 transition-colors hover:bg-sunken focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-volt"
          >
            <MessageSquare className="mt-0.5 h-4 w-4 shrink-0 text-ink-tertiary" aria-hidden="true" />
            <span className="min-w-0 flex-1">
              <span className="flex flex-wrap items-center gap-x-2 text-caption">
                <span className="font-medium text-ink-primary">{c.authorDisplay}</span>
                <span className="text-ink-secondary">{c.isReply ? 'replied on' : 'commented on'}</span>
                <span className="font-mono font-semibold text-ink-primary">{c.subroutineName}</span>
                <span className="text-ink-secondary">· {c.corpusName}</span>
                {c.resolved && <Badge tone="signed" className="text-[10px]">resolved</Badge>}
                <span className="ml-auto text-ink-tertiary" title={absoluteTime(c.createdAt)}>{relativeTime(c.createdAt)}</span>
              </span>
              <span className="mt-0.5 block truncate text-body text-ink-secondary">{c.body}</span>
            </span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

function InboxRow({ n, qc }: { n: NotificationItem; qc: ReturnType<typeof useQueryClient> }) {
  const unread = n.readAt === null;
  const specId = n.payload.specId;

  const markRead = useMutation({
    mutationFn: () => notificationsApi.markRead(n.id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['notifications-inbox'] });
      qc.invalidateQueries({ queryKey: ['notifications-unread'] });
    },
  });

  return (
    <li
      className={
        'group relative px-6 py-4 transition-colors duration-fast hover:bg-sunken ' +
        (unread ? 'bg-volt/10' : '')
      }
      data-testid={`inbox-row-${n.id}`}
    >
      <div className="flex items-start gap-3">
        <MessageSquare className="mt-0.5 h-4 w-4 shrink-0 text-ink-tertiary" aria-hidden="true" />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2 text-caption">
            <span className="rounded-sm bg-sunken px-1.5 py-0.5 font-mono uppercase tracking-wider text-ink-tertiary">
              {n.payload.authorPersona ?? 'unknown'}
            </span>
            <span className="font-medium text-ink-primary">{n.payload.authorDisplay ?? '—'}</span>
            <span className="font-mono text-ink-tertiary">mentioned you</span>
            {n.payload.claimPath && (
              <Badge tone="neutral" className="text-[10px]">
                on claim
              </Badge>
            )}
            {unread && <Badge tone="draft" className="text-[10px]">new</Badge>}
            <span className="ml-auto text-ink-tertiary" title={absoluteTime(n.createdAt)}>
              {relativeTime(n.createdAt)}
            </span>
          </div>
          <p className="mt-1 truncate text-body text-ink-primary">
            {n.payload.excerpt ?? '(no preview)'}
          </p>
          {n.payload.claimPath && (
            <p className="mt-0.5 truncate font-mono text-caption text-ink-tertiary">
              {n.payload.claimPath}
            </p>
          )}
          <div className="mt-2 flex items-center gap-3">
            {specId && (
              <Link
                to={`/specs/${specId}/audit`}
                onClick={() => unread && markRead.mutate()}
                className="text-caption font-medium text-volt-ink hover:underline focus-visible:outline-2 focus-visible:outline-ink-primary"
                data-testid={`view-${n.id}`}
              >
                Open spec audit
              </Link>
            )}
            {unread && (
              <button
                type="button"
                onClick={() => markRead.mutate()}
                disabled={markRead.isPending}
                className="text-caption font-medium text-ink-tertiary hover:text-ink-primary focus-visible:outline-2 focus-visible:outline-ink-primary"
                data-testid={`read-${n.id}`}
              >
                Mark read
              </button>
            )}
          </div>
        </div>
      </div>
    </li>
  );
}
