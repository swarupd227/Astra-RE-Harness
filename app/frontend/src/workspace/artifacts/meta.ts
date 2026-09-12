/**
 * Per-kind metadata for artifact cards: icon, human label, and the
 * "Open full view" route (contract §3, Increment 2 additions).
 */
import {
  BarChart3,
  BookOpen,
  Boxes,
  Braces,
  ClipboardList,
  FileCheck2,
  FileText,
  FolderTree,
  Gauge,
  LayoutGrid,
  ListTree,
  ShieldCheck,
  Waves,
  type LucideIcon,
} from 'lucide-react';
import type { Artifact } from '@/lib/conversations';
import { str } from '../format';

type KindMeta = { label: string; icon: LucideIcon };

const KINDS: Record<string, KindMeta> = {
  funnel: { label: 'Programme funnel', icon: BarChart3 },
  runProgress: { label: 'Run', icon: Gauge },
  routineList: { label: 'Routines', icon: ListTree },
  clusterGrid: { label: 'Pattern clusters', icon: LayoutGrid },
  specSummary: { label: 'Spec', icon: FileCheck2 },
  programmeList: { label: 'Programmes', icon: Boxes },
  routine: { label: 'Routine', icon: Braces },
  gateResults: { label: 'Validation gates', icon: ShieldCheck },
  scaffoldTree: { label: 'Generated code', icon: FolderTree },
  text: { label: 'Note', icon: FileText },
  assessment: { label: 'Assessment', icon: ClipboardList },
  planWaves: { label: 'Migration plan', icon: Waves },
  docSection: { label: 'Documentation', icon: BookOpen },
};

export function artifactKindLabel(kind: string): string {
  return KINDS[kind]?.label ?? kind;
}

export function artifactIcon(kind: string): LucideIcon {
  return KINDS[kind]?.icon ?? FileText;
}

/** A short, human title for the card header, derived from props. */
export function artifactTitle(artifact: Artifact): string {
  const p = artifact.props ?? {};
  switch (artifact.kind) {
    case 'funnel':
      return str(p.corpusName, artifactKindLabel(artifact.kind));
    case 'runProgress':
      return str(p.label, 'Run');
    case 'routineList': {
      const q = str(p.query);
      return q ? `Routines matching “${q}”` : 'Routines';
    }
    case 'clusterGrid':
      return 'Pattern clusters';
    case 'specSummary':
      return str(p.routineName) ? `Spec · ${str(p.routineName)}` : 'Spec';
    case 'programmeList':
      return 'Programmes';
    case 'routine':
      return str(p.name, 'Routine');
    case 'gateResults':
      return str(p.routineName) ? `Gates · ${str(p.routineName)}` : 'Validation gates';
    case 'scaffoldTree':
      return str(p.routineName) ? `Generated · ${str(p.routineName)}` : 'Generated code';
    case 'text':
      return str(p.title, 'Note');
    case 'assessment':
      return str(p.corpusName) ? `Assessment · ${str(p.corpusName)}` : 'Assessment';
    case 'planWaves':
      return str(p.strategyName) ? `Migration plan · ${str(p.strategyName)}` : 'Migration plan';
    case 'docSection':
      return str(p.title, 'Documentation');
    default:
      return artifact.kind;
  }
}

/** "Open full view" target per contract §3 (+ Increment 2). Null when there is none. */
export function artifactLink(artifact: Artifact): { label: string; href: string } | null {
  const p = artifact.props ?? {};
  const ref = artifact.refId;
  switch (artifact.kind) {
    case 'funnel':
      return ref ? { label: 'Open project', href: `/projects/${ref}` } : null;
    case 'routine':
      return ref ? { label: 'Open routine', href: `/subroutines/${ref}` } : null;
    case 'specSummary': {
      const sid = str(p.subroutineId);
      return sid ? { label: 'Open spec review', href: `/subroutines/${sid}/review` } : null;
    }
    case 'clusterGrid':
      return ref ? { label: 'Open pattern analysis', href: `/projects/${ref}/pattern-analysis` } : null;
    case 'gateResults':
      return ref ? { label: 'Open validation report', href: `/scaffolds/${ref}/validation` } : null;
    case 'scaffoldTree':
      return ref ? { label: 'Open scaffold', href: `/scaffolds/${ref}` } : null;
    case 'runProgress': {
      const kind = str(p.kind);
      const corpusId = str(p.corpusId);
      if (kind === 'pattern-analysis' && corpusId) {
        return { label: 'Open pattern analysis', href: `/projects/${corpusId}/pattern-analysis` };
      }
      if (kind === 'assessment' && corpusId) {
        return { label: 'Open assessment', href: `/projects/${corpusId}/assessment` };
      }
      return null;
    }
    case 'assessment': {
      const href = str(p.href) || (ref ? `/projects/${ref}/assessment` : '');
      return href ? { label: 'Open assessment', href } : null;
    }
    case 'planWaves': {
      const corpusId = str(p.corpusId);
      return corpusId ? { label: 'Open migration plan', href: `/corpora/${corpusId}/migration-plan` } : null;
    }
    case 'docSection': {
      const href = str(p.href) || (str(p.corpusId) ? `/projects/${str(p.corpusId)}/docs` : '');
      return href ? { label: 'Open documentation', href } : null;
    }
    default:
      return null;
  }
}
