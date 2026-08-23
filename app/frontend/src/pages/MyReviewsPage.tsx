import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { ChevronRight, ClipboardCheck, FileCode, ShieldCheck, Timer } from 'lucide-react';
import { myReviewsApi, type MyReviewItem } from '@/lib/api';
import { Card, CardBody } from '@/components/Card';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { EmptyState } from '@/components/EmptyState';
import { AwaitingDataIllustration } from '@/illustrations/AwaitingData';
import { useState } from 'react';
import { clsx } from 'clsx';
import { formatState } from '@/lib/labels';

export function MyReviewsPage() {
  const reviews = useQuery({ queryKey: ['my-reviews'], queryFn: myReviewsApi.list });

  if (reviews.isPending) {
    return (
      <div className="mx-auto max-w-[1100px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-12 w-64" />
        <Skeleton className="h-32 w-full" />
        <Skeleton className="h-32 w-full" />
      </div>
    );
  }
  if (reviews.isError) {
    return <div className="mx-auto max-w-[1100px] p-6 lg:p-10"><ErrorBlock title="Could not load reviews" message={reviews.error.message} onRetry={() => reviews.refetch()} /></div>;
  }
  const r = reviews.data;

  return (
    <div className="mx-auto max-w-[1100px] space-y-6 p-6 lg:p-10">
      <header>
        <p className="text-caption font-medium uppercase tracking-wider text-ink-tertiary">Review queue</p>
        <h1 className="mt-2 text-display font-semibold text-ink-primary">My reviews</h1>
        <div className="mt-3 flex flex-wrap gap-2 font-mono text-caption">
          <CountChip n={r.counts.awaiting} label="awaiting" tone="draft" />
          <CountChip n={r.counts.inProgress} label="in progress" tone="review" />
          <CountChip n={r.counts.signed} label="signed" tone="signed" />
        </div>
      </header>

      <Section title="Awaiting review" items={r.awaiting} emptyTitle="Nothing awaiting your attention" emptyDesc="When an engineer routes a draft spec, it lands here with a complexity estimate." />
      <Section title="In progress" items={r.inProgress} emptyTitle="No reviews in flight" emptyDesc="Specs you've started but not yet signed appear here." />
      <SignedGroup items={r.signed} />
    </div>
  );
}

function Section({ title, items, emptyTitle, emptyDesc }: { title: string; items: MyReviewItem[]; emptyTitle: string; emptyDesc: string }) {
  return (
    <section>
      <h2 className="mb-2 text-caption font-medium uppercase tracking-wider text-ink-tertiary">
        {title} <span className="ml-1 rounded-sm bg-sunken px-1.5 py-0.5 text-[10px] text-ink-secondary">{items.length}</span>
      </h2>
      {items.length === 0 ? (
        <Card><CardBody><EmptyState illustration={<AwaitingDataIllustration size={120} />} title={emptyTitle} description={emptyDesc} /></CardBody></Card>
      ) : (
        <ul className="space-y-3">{items.map((it) => <li key={it.specId}><ReviewCard item={it} /></li>)}</ul>
      )}
    </section>
  );
}

function SignedGroup({ items }: { items: MyReviewItem[] }) {
  const [open, setOpen] = useState(items.length <= 3);
  return (
    <section>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="flex w-full items-center gap-2 text-left text-caption font-medium uppercase tracking-wider text-ink-tertiary hover:text-ink-secondary"
      >
        <ChevronRight className={clsx('h-3.5 w-3.5 transition-transform', open && 'rotate-90')} />
        Signed
        <span className="rounded-sm bg-sunken px-1.5 py-0.5 text-[10px] text-ink-secondary">{items.length}</span>
      </button>
      {open && (items.length === 0 ? (
        <Card className="mt-2"><CardBody>
          <p className="text-body text-ink-secondary">Your sign-offs land here. They are HSM-backed and bound to the source version.</p>
        </CardBody></Card>
      ) : (
        <ul className="mt-2 space-y-3">{items.map((it) => <li key={it.specId}><ReviewCard item={it} /></li>)}</ul>
      ))}
    </section>
  );
}

function CountChip({ n, label, tone }: { n: number; label: string; tone: 'draft' | 'review' | 'signed' }) {
  return (
    <span className={clsx(
      'inline-flex items-center gap-1.5 rounded-sm border px-2 py-0.5',
      tone === 'draft' && 'border-status-draft/40 bg-accent-muted text-status-draft',
      tone === 'review' && 'border-status-review/40 bg-[#DAEFE9] text-status-review',
      tone === 'signed' && 'border-status-signed/40 bg-[#DCE6F5] text-status-signed',
    )}>
      <span className="font-semibold">{n}</span>
      <span className="text-ink-tertiary">{label}</span>
    </span>
  );
}

function ReviewCard({ item }: { item: MyReviewItem }) {
  const isSigned = item.state === 'SIGNED';
  const href = isSigned ? `/subroutines/${item.subroutineId}/review` : `/subroutines/${item.subroutineId}/review`;
  return (
    <Link to={href} className="block focus-visible:outline-2 focus-visible:outline-ink-primary">
      <Card interactive>
        <CardBody>
          <header className="flex items-start justify-between gap-3">
            <div className="flex items-center gap-3">
              <span className={clsx(
                'flex h-10 w-10 items-center justify-center rounded-md',
                isSigned ? 'bg-[#DCE6F5] text-status-signed' : 'bg-accent-muted text-accent',
              )}>
                {isSigned ? <ShieldCheck className="h-5 w-5" /> : <ClipboardCheck className="h-5 w-5" />}
              </span>
              <div>
                <h3 className="font-mono text-h-md font-semibold text-ink-primary">{item.subroutineName}</h3>
                <p className="mt-0.5 font-mono text-caption text-ink-tertiary">
                  {item.corpusName} <span className="text-ink-tertiary/60">·</span> {item.relativePath}
                </p>
              </div>
            </div>
            <Badge tone={isSigned ? 'signed' : 'review'}>{formatState(item.state)}</Badge>
          </header>

          <div className="mt-4 flex flex-wrap items-center gap-x-5 gap-y-1 font-mono text-caption text-ink-secondary">
            <span><FileCode className="mr-1 inline h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />{item.invariantsCount} invariants</span>
            <span>{item.edgeCaseCount} edge cases</span>
            <span>{item.openQuestionCount} open questions</span>
            {!isSigned && (
              <span className="ml-auto inline-flex items-center gap-1.5 text-ink-secondary">
                <Timer className="h-3.5 w-3.5 text-ink-tertiary" aria-hidden="true" />
                ~{item.estimatedReviewMinutes} min · {item.processedClaims}/{item.totalClaims} done
              </span>
            )}
            {isSigned && item.signature && (
              <span className="ml-auto truncate text-ink-tertiary">
                signed {new Date(item.signature.signedAt).toLocaleString()} by {item.signature.signerDisplay}
              </span>
            )}
          </div>
        </CardBody>
      </Card>
    </Link>
  );
}
