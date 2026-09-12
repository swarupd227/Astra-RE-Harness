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

export type ArtifactSize = 'card' | 'pane';
export type ArtifactRenderProps = { artifact: Artifact; size: ArtifactSize };

/**
 * One switch over `artifact.kind`. Cards are defensive about props; an
 * unknown kind falls back to a TextCard showing the raw JSON so nothing
 * the backend emits is ever invisible.
 */
export function renderArtifact(artifact: Artifact, opts: { size: ArtifactSize }): ReactNode {
  const size = opts.size;
  const a: Artifact = { ...artifact, props: artifact.props ?? {} };
  switch (a.kind) {
    case 'funnel':
      return <FunnelCard artifact={a} size={size} />;
    case 'runProgress':
      return <RunProgressCard artifact={a} size={size} />;
    case 'routineList':
      return <RoutineListCard artifact={a} size={size} />;
    case 'clusterGrid':
      return <ClusterGridCard artifact={a} size={size} />;
    case 'specSummary':
      return <SpecSummaryCard artifact={a} size={size} />;
    case 'programmeList':
      return <ProgrammeListCard artifact={a} size={size} />;
    case 'routine':
      return <RoutineCard artifact={a} size={size} />;
    case 'gateResults':
      return <GateResultsCard artifact={a} size={size} />;
    case 'scaffoldTree':
      return <ScaffoldTreeCard artifact={a} size={size} />;
    case 'text':
      return <TextCard artifact={a} size={size} />;
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
