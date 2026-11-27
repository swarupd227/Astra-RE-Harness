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
import { ChevronRight } from 'lucide-react';
import { clsx } from 'clsx';
import { api, type FlowBoardColumn, type FlowBoardColumnKey, type FlowBoardRoutine } from '@/lib/api';
import { PageHero } from '@/components/PageHero';
import { Badge } from '@/components/Badge';
import { Skeleton } from '@/components/Skeleton';
import { ErrorBlock } from '@/components/ErrorBlock';
import { columnStyle } from './columns';

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
      <div className="mx-auto max-w-[1600px] space-y-4 p-4 lg:p-6">
        <Skeleton className="h-12 w-96" />
        <Skeleton className="h-[600px] w-full" />
      </div>
    );
  }
  if (board.isError) {
    return (
      <div className="mx-auto max-w-[1600px] p-4 lg:p-6">
        <ErrorBlock title="Could not load the flow board" message={String(board.error)} onRetry={() => board.refetch()} />
      </div>
    );
  }

  const b = board.data;
  return (
    <div className="mx-auto max-w-[1900px] space-y-6 p-4 lg:p-6 fadeup" data-testid="flow-board-page">
      <PageHero
        eyebrow={b.corpusName}
        title="Code unit flow board"
        lead={`${b.totalRoutines.toLocaleString()} code units · ${b.columns
          .filter((c) => c.total > 0)
          .map((c) => `${c.total.toLocaleString()} ${columnLabel(c).toLowerCase()}`)
          .join(' · ')}`}
      />
      {/* Empty stages shrink to a labelled strip so the stages that hold routines get the width. */}
      <div
        className="grid min-w-0 gap-3 overflow-x-auto pb-2"
        style={{ gridTemplateColumns: b.columns.map((c) => (c.total > 0 ? 'minmax(240px, 1fr)' : '52px')).join(' ') }}
        data-testid="flow-board-columns"
      >
        {b.columns.map((col) => (
          <BoardColumn key={col.key} corpusId={id} column={col} />
        ))}
      </div>
    </div>
  );
}

/** The board's stage names in the words the rest of the app uses. */
function columnLabel(c: FlowBoardColumn): string {
  const k = c.key.toLowerCase();
  if (k === 'built' || k === 'scaffolded') return 'With code';
  if (k === 'parsed') return 'Not started';
  return c.label;
}

function BoardColumn({ corpusId, column }: { corpusId: string; column: FlowBoardColumn }) {
  const style = columnStyle(column.key);
  const Icon = style.icon;
  const reduced = useReducedMotion();

  if (column.total === 0) {
    return (
      <div
        className="flex min-h-[200px] flex-col items-center gap-2 rounded-lg border border-dashed border-line-subtle bg-raised/40 py-3"
        data-testid={`flow-column-${column.key}`}
        title={`${columnLabel(column)}: nothing here`}
      >
        <Icon className={clsx('h-4 w-4 shrink-0', style.text)} aria-hidden="true" />
        <span className="font-mono text-micro text-ink-tertiary">0</span>
        <span className="text-caption text-ink-secondary [writing-mode:vertical-rl]">{columnLabel(column)}</span>
      </div>
    );
  }

  return (
    <div className="flex min-h-0 flex-col rounded-lg border border-line-subtle bg-raised" data-testid={`flow-column-${column.key}`}>
      <div className={clsx('flex shrink-0 items-center gap-2 border-b border-line-subtle px-3 py-2.5', style.border)}>
        <Icon className={clsx('h-4 w-4 shrink-0', style.text, style.spin && column.total > 0 && 'animate-spin')} aria-hidden="true" />
        <h2 className="min-w-0 flex-1 truncate text-caption font-semibold text-ink-primary">{columnLabel(column)}</h2>
        <Badge tone="neutral">{column.total.toLocaleString()}</Badge>
      </div>
      <ul className="min-h-0 flex-1 space-y-1.5 overflow-y-auto p-2" style={{ maxHeight: 640 }}>
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
      <span className="truncate font-mono text-micro text-ink-tertiary" title={`${routine.filePath}:${routine.lineStart}`}>
        {routine.filePath.split('/').pop()} · L{routine.lineStart}
      </span>
    </Link>
  );
}
