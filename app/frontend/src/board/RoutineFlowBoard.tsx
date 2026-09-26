/**
 * Routine flow board — every routine in a corpus, one column per pipeline
 * stage (parsed through committed). Read-only: there is no drag handle,
 * because nothing on this board moves by being dragged — a card changes
 * column because an agent (or a human review action) changed its state,
 * the same way every other artifact view shows the result of an action
 * rather than being the place the action is taken. Clicking a card opens
 * the routine; asking the Programme agent (docked, top bar) is how you act
 * on it from here.
 */
import { useMemo } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { AnimatePresence, motion, useReducedMotion } from 'framer-motion';
import {
  CheckCircle2,
  ChevronRight,
  Cog,
  Eye,
  FileCode,
  GitCommit,
  Loader2,
  PenLine,
  ShieldCheck,
} from 'lucide-react';
import { clsx } from 'clsx';
import { api, type FlowBoardColumn, type FlowBoardColumnKey, type FlowBoardRoutine } from '@/lib/api';
import { PageHero } from '@/components/PageHero';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';

// The raw Subroutine.State value a "view the rest" link should filter by —
// as fine-grained as the routines list endpoint goes. Built/verified/
// committed all collapse to SCAFFOLDED there (see FlowBoardEndpoints.cs);
// the link is still useful, just not as precise as the column itself.
const STATE_FILTER: Record<FlowBoardColumnKey, string> = {
  parsed: 'PARSED',
  extracting: 'EXTRACTING',
  draft: 'DRAFT',
  in_review: 'IN_REVIEW',
  signed: 'SIGNED',
  built: 'SCAFFOLDED',
  verified: 'SCAFFOLDED',
  committed: 'SCAFFOLDED',
};

const COLUMN_STYLE: Record<
  FlowBoardColumnKey,
  { icon: typeof FileCode; text: string; border: string; spin?: boolean }
> = {
  parsed: { icon: FileCode, text: 'text-ink-tertiary', border: 'border-line' },
  extracting: { icon: Loader2, text: 'text-volt', border: 'border-volt/40', spin: true },
  draft: { icon: PenLine, text: 'text-status-warn', border: 'border-status-warn/40' },
  in_review: { icon: Eye, text: 'text-status-ok', border: 'border-status-ok/40' },
  signed: { icon: ShieldCheck, text: 'text-status-info', border: 'border-status-info/40' },
  built: { icon: Cog, text: 'text-status-warn', border: 'border-status-warn/40' },
  verified: { icon: CheckCircle2, text: 'text-status-ok', border: 'border-status-ok/40' },
  committed: { icon: GitCommit, text: 'text-status-ok', border: 'border-status-ok/40' },
};

export function RoutineFlowBoard() {
  const { id = '' } = useParams();
  const board = useQuery({
    queryKey: ['flow-board', id],
    queryFn: () => api.getFlowBoard(id),
    enabled: !!id,
    // Cheap aggregate query; poll so a run finishing elsewhere (a sign, a
    // scaffold, a gate) shows up here without a manual refresh.
    refetchInterval: 30_000,
  });

  if (board.isPending) {
    return (
      <div className="mx-auto max-w-[1600px] space-y-4 p-6 lg:p-10">
        <Skeleton className="h-12 w-96" />
        <Skeleton className="h-[600px] w-full" />
      </div>
    );
  }
  if (board.isError) {
    return (
      <div className="mx-auto max-w-[1600px] p-6 lg:p-10">
        <ErrorBlock title="Could not load the flow board" message={String(board.error)} onRetry={() => board.refetch()} />
      </div>
    );
  }

  const b = board.data;
  return (
    <div className="mx-auto max-w-[1900px] space-y-6 p-6 lg:p-10 fadeup" data-testid="flow-board-page">
      <PageHero
        eyebrow={b.corpusName}
        title="Routine flow board"
        lead={`${b.totalRoutines.toLocaleString()} routines, from parsed source to committed code. A card moves when an agent or a review action changes its state — nothing here is dragged.`}
      />
      <div className="grid min-w-0 grid-flow-col auto-cols-[260px] gap-4 overflow-x-auto pb-2" data-testid="flow-board-columns">
        {b.columns.map((col) => (
          <BoardColumn key={col.key} corpusId={id} column={col} />
        ))}
      </div>
    </div>
  );
}

function BoardColumn({ corpusId, column }: { corpusId: string; column: FlowBoardColumn }) {
  const style = COLUMN_STYLE[column.key];
  const Icon = style.icon;
  const reduced = useReducedMotion();

  return (
    <div className="flex min-h-0 flex-col rounded-lg border border-line-subtle bg-raised" data-testid={`flow-column-${column.key}`}>
      <div className={clsx('flex shrink-0 items-center gap-2 border-b border-line-subtle px-3 py-2.5', style.border)}>
        <Icon className={clsx('h-4 w-4 shrink-0', style.text, style.spin && 'animate-spin')} aria-hidden="true" />
        <h2 className="min-w-0 flex-1 truncate text-caption font-semibold text-ink-primary">{column.label}</h2>
        <Badge tone="neutral">{column.total.toLocaleString()}</Badge>
      </div>
      <ul className="min-h-0 flex-1 space-y-1.5 overflow-y-auto p-2" style={{ maxHeight: 640 }}>
        {column.routines.length === 0 && (
          <li className="px-2 py-6 text-center text-caption text-ink-tertiary">Nothing here yet.</li>
        )}
        <AnimatePresence initial={false}>
          {column.routines.map((r) => (
            <motion.li
              key={r.id}
              layout
              initial={reduced ? false : { opacity: 0 }}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              transition={{ duration: 0.22, ease: 'easeOut' }}
            >
              <RoutinePill routine={r} />
            </motion.li>
          ))}
        </AnimatePresence>
      </ul>
      {column.hasMore && (
        <Link
          to={`/subroutines?corpus=${encodeURIComponent(corpusId)}&state=${STATE_FILTER[column.key]}`}
          className="flex shrink-0 items-center justify-between gap-1 border-t border-line-subtle px-3 py-2 text-caption text-ink-secondary hover:bg-sunken hover:text-ink-primary"
          data-testid={`flow-column-more-${column.key}`}
        >
          <span>+{(column.total - column.routines.length).toLocaleString()} more</span>
          <ChevronRight className="h-3.5 w-3.5" aria-hidden="true" />
        </Link>
      )}
    </div>
  );
}

function RoutinePill({ routine }: { routine: FlowBoardRoutine }) {
  const failed = useMemo(() => routine.scaffoldState === 'FAILED', [routine.scaffoldState]);
  return (
    <Link
      to={`/subroutines/${routine.id}`}
      className="group flex flex-col gap-0.5 rounded-md border border-line-subtle bg-canvas px-2.5 py-1.5 hover:border-line-strong hover:bg-sunken"
      data-testid={`flow-pill-${routine.id}`}
    >
      <span className="flex items-center gap-1.5">
        {failed && (
          <span
            className="h-1.5 w-1.5 shrink-0 rounded-full bg-status-fail"
            title="The scaffold's most recent gate run failed"
            aria-label="Gate failed"
          />
        )}
        <span className="min-w-0 truncate font-mono text-caption font-medium text-ink-primary">{routine.name}</span>
      </span>
      <span className="truncate font-mono text-micro text-ink-tertiary">
        {routine.filePath}:{routine.lineStart}
      </span>
    </Link>
  );
}
