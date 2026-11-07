import type { ComponentType } from 'react';
import { DefaultEvidenceBody, type EvidenceBodyProps } from '@/evidence/views/DefaultBody';
import { ConfigView } from '@/evidence/views/ConfigView';
import { DocumentView } from '@/evidence/views/DocumentView';
import { TicketView } from '@/evidence/views/TicketView';
import { TranscriptView } from '@/evidence/views/TranscriptView';

export type { EvidenceBodyProps };
export { DefaultEvidenceBody };

/**
 * How the body of an evidence card is drawn, by artefact kind. A kind without an entry here gets the
 * default: the cited lines in a numbered code view, or the chunk's text as it is. A kind with a
 * richer view (a transcript with a play button, a ticket as a record) registers it here and nothing
 * else changes.
 */
export const EVIDENCE_VIEWS: Partial<Record<string, ComponentType<EvidenceBodyProps>>> = {
  transcript: TranscriptView,
  ticket: TicketView,
  config: ConfigView,
  document: DocumentView,
};

export function EvidenceBody({ detail }: EvidenceBodyProps) {
  const View = EVIDENCE_VIEWS[detail.kind] ?? DefaultEvidenceBody;
  return <View detail={detail} />;
}
