import {
  AudioLines,
  CalendarClock,
  Database,
  FileCode,
  FileText,
  MessageSquareText,
  Settings2,
  Ticket,
  Workflow,
  type LucideIcon,
} from 'lucide-react';
import { clsx } from 'clsx';

const ICONS: Record<string, LucideIcon> = {
  code: FileCode,
  sql: Database,
  job: CalendarClock,
  etl: Workflow,
  config: Settings2,
  transcript: MessageSquareText,
  ticket: Ticket,
  document: FileText,
  audio: AudioLines,
};

// The prefix of a short id says what kind of evidence it is (mirrors Evidence/EvidenceIds.cs).
const KIND_BY_PREFIX: Record<string, string> = {
  SRC: 'code',
  SQL: 'sql',
  JOB: 'job',
  ETL: 'etl',
  CFG: 'config',
  TRN: 'transcript',
  TKT: 'ticket',
  DOC: 'document',
  AUD: 'audio',
};

export function kindOfShortId(shortId: string): string | undefined {
  return KIND_BY_PREFIX[shortId.slice(0, 3).toUpperCase()];
}

export function KindIcon({ kind, className }: { kind: string | undefined; className?: string }) {
  const Icon = (kind && ICONS[kind]) || FileText;
  return <Icon className={clsx('h-3.5 w-3.5 shrink-0', className)} aria-hidden="true" />;
}
