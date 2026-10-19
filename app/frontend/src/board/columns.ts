import {
  CheckCircle2,
  Cog,
  Eye,
  FileCode,
  GitCommit,
  Loader2,
  PenLine,
  ShieldCheck,
  type LucideIcon,
} from 'lucide-react';
import type { FlowBoardColumnKey } from '@/lib/api';

/**
 * How each pipeline stage looks — shared by the flow board page and the
 * `flowBoard` card so the card in a thread reads as the page it opens.
 * Tones follow Badge.tsx's per-state colours; extracting is volt because an
 * agent is working there (the one thing volt means besides focus/CTA).
 */
export type ColumnStyle = {
  icon: LucideIcon;
  text: string;
  border: string;
  bar: string;
  /** Spin the icon — only meaningful while something is actually in the stage. */
  spin?: boolean;
};

export const COLUMN_STYLE: Record<FlowBoardColumnKey, ColumnStyle> = {
  parsed: { icon: FileCode, text: 'text-ink-tertiary', border: 'border-line', bar: 'bg-sand-400' },
  extracting: { icon: Loader2, text: 'text-volt', border: 'border-volt/40', bar: 'bg-volt', spin: true },
  draft: { icon: PenLine, text: 'text-status-warn', border: 'border-status-warn/40', bar: 'bg-status-warn' },
  in_review: { icon: Eye, text: 'text-status-ok', border: 'border-status-ok/40', bar: 'bg-status-ok' },
  signed: { icon: ShieldCheck, text: 'text-status-info', border: 'border-status-info/40', bar: 'bg-status-info' },
  built: { icon: Cog, text: 'text-status-warn', border: 'border-status-warn/40', bar: 'bg-status-warn' },
  verified: { icon: CheckCircle2, text: 'text-status-ok', border: 'border-status-ok/40', bar: 'bg-status-ok' },
  committed: { icon: GitCommit, text: 'text-status-ok', border: 'border-status-ok/40', bar: 'bg-status-ok' },
};

/** A stage the server knows but this build doesn't — render it plainly rather than not at all. */
export const FALLBACK_COLUMN_STYLE: ColumnStyle = COLUMN_STYLE.parsed;

export function columnStyle(key: string): ColumnStyle {
  return COLUMN_STYLE[key as FlowBoardColumnKey] ?? FALLBACK_COLUMN_STYLE;
}
