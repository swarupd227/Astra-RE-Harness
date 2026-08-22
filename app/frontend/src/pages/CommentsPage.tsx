import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { CheckCircle2, Inbox, MessageSquare } from 'lucide-react';
import { notificationsApi, type NotificationItem } from '@/lib/api';
import { Card, CardBody, CardHeader } from '@/components/Card';
import { Button } from '@/components/Button';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { EmptyState } from '@/components/EmptyState';
import { NoResultsIllustration } from '@/illustrations/NoResults';

/**
 * Phase C.7 — Mentions inbox. Shows comment.mention notifications for the
 * current persona. Click any row to jump to the spec the comment lives on
 * (Phase D will deep-link to the exact claim once we add a /specs/{id}/review
 * URL that scrolls + highlights).
 */
export function CommentsPage() {
  const qc = useQueryClient();
  const inbox = useQuery({
    queryKey: ['notifications-inbox'],
    queryFn: () => notificationsApi.list({ limit: 100 }),
    refetchInterval: 30_000,
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

  return (
    <div className="mx-auto max-w-[900px] space-y-6 p-6 lg:p-10">
      <header className="flex items-start justify-between gap-4">
        <div>
          <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">
            Phase C.7 · Mentions inbox
          </p>
          <h1 className="mt-2 text-display font-semibold text-ink-primary">Comments</h1>
          <p className="mt-2 max-w-2xl text-body-lg text-ink-secondary">
            Threaded comments on specs and claims live alongside the review surface. This page is your
            inbox — every <span className="font-mono">@{inbox.data?.persona ?? '…'}</span> mention
            dispatched from any spec lands here.
          </p>
        </div>
        <Button
          variant="secondary"
          onClick={() => readAll.mutate()}
          disabled={readAll.isPending || unread === 0}
          data-testid="read-all"
        >
          <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
          Mark all read{unread > 0 && ` (${unread})`}
        </Button>
      </header>

      {inbox.isPending ? (
        <div className="space-y-3">
          <Skeleton className="h-20 w-full" />
          <Skeleton className="h-20 w-full" />
        </div>
      ) : inbox.isError ? (
        <ErrorBlock title="Could not load inbox" message={inbox.error.message} onRetry={() => inbox.refetch()} />
      ) : data.length === 0 ? (
        <Card>
          <CardBody>
            <EmptyState
              illustration={<NoResultsIllustration size={140} />}
              title="No mentions yet"
              description={`Comments that @-mention you (persona: ${inbox.data?.persona ?? '…'}) will appear here.`}
            />
          </CardBody>
        </Card>
      ) : (
        <Card>
          <CardHeader
            title={
              <span className="inline-flex items-center gap-2">
                <Inbox className="h-4 w-4 text-ink-tertiary" aria-hidden="true" />
                {data.length} notification{data.length === 1 ? '' : 's'}
              </span>
            }
            description={`${unread} unread`}
          />
          <CardBody className="p-0">
            <ul className="divide-y divide-border-subtle" data-testid="inbox-list">
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
        (unread ? 'bg-accent-muted/20' : '')
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
            <span className="ml-auto font-mono text-ink-tertiary">
              {new Date(n.createdAt).toLocaleString()}
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
                className="text-caption font-medium text-accent hover:underline focus-visible:outline-2 focus-visible:outline-ink-primary"
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
