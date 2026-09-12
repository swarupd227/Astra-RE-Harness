import type { ReactNode } from 'react';
import type { Artifact } from '@/lib/conversations';
import { pretty } from '../format';
import { FunnelCard } from './FunnelCard';
import { RunProgressCard } from './RunProgressCard';
import { RoutineListCard } from './RoutineListCard';
import { ClusterGridCard } from './ClusterGridCard';
import { SpecSummaryCard } from './SpecSummaryCard';
import { ProgrammeListCard } from './ProgrammeListCard';
import { RoutineCard } from './RoutineCard';
import { GateResultsCard } from './GateResultsCard';
import { ScaffoldTreeCard } from './ScaffoldTreeCard';
import { TextCard } from './TextCard';
import { AssessmentCard } from './AssessmentCard';
import { PlanWavesCard } from './PlanWavesCard';
import { DocSectionCard } from './DocSectionCard';

export type ArtifactSize = 'card' | 'pane';

export type ArtifactRenderProps = {
  artifact: Artifact;
  size: ArtifactSize;
  /**
   * Send natural language as the next user turn. Cards that offer a
   * conversational affordance ("Why?", "Resume") call this; hosts that
   * cannot send (a static page) leave it undefined and the card hides the
   * affordance.
   */
  onIntent?: (intent: string) => void;
};

export type RenderOptions = { size: ArtifactSize; onIntent?: (intent: string) => void };

/**
 * One switch over `artifact.kind`. Cards are defensive about props; an
 * unknown kind falls back to a TextCard showing the raw JSON so nothing
 * the backend emits is ever invisible.
 */
export function renderArtifact(artifact: Artifact, opts: RenderOptions): ReactNode {
  const { size, onIntent } = opts;
  const a: Artifact = { ...artifact, props: artifact.props ?? {} };
  switch (a.kind) {
    case 'funnel':
      return <FunnelCard artifact={a} size={size} onIntent={onIntent} />;
    case 'runProgress':
      return <RunProgressCard artifact={a} size={size} onIntent={onIntent} />;
    case 'routineList':
      return <RoutineListCard artifact={a} size={size} onIntent={onIntent} />;
    case 'clusterGrid':
      return <ClusterGridCard artifact={a} size={size} onIntent={onIntent} />;
    case 'specSummary':
      return <SpecSummaryCard artifact={a} size={size} onIntent={onIntent} />;
    case 'programmeList':
      return <ProgrammeListCard artifact={a} size={size} onIntent={onIntent} />;
    case 'routine':
      return <RoutineCard artifact={a} size={size} onIntent={onIntent} />;
    case 'gateResults':
      return <GateResultsCard artifact={a} size={size} onIntent={onIntent} />;
    case 'scaffoldTree':
      return <ScaffoldTreeCard artifact={a} size={size} onIntent={onIntent} />;
    case 'text':
      return <TextCard artifact={a} size={size} onIntent={onIntent} />;
    case 'assessment':
      return <AssessmentCard artifact={a} size={size} onIntent={onIntent} />;
    case 'planWaves':
      return <PlanWavesCard artifact={a} size={size} onIntent={onIntent} />;
    case 'docSection':
      return <DocSectionCard artifact={a} size={size} onIntent={onIntent} />;
    default: {
      const fallback: Artifact = {
        kind: 'text',
        refId: a.refId,
        props: {
          title: `Unknown artifact “${String(a.kind)}”`,
          markdown: '```json\n' + pretty(a.props) + '\n```',
        },
      };
      return <TextCard artifact={fallback} size={size} />;
    }
  }
}
