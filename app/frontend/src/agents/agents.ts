/**
 * The named agents a user talks to. Each maps to real code on the API side
 * (see the WS2 plan): Discovery = ingest/survey/cluster, Spec = extraction
 * + review, Migration = scaffolds, Validation = gates, Planning = waves,
 * Programme = cross-programme status/telemetry, Astra = the orchestrator
 * that routes intents to the others.
 */
import {
  Bot,
  Compass,
  DraftingCompass,
  FileCheck2,
  Hammer,
  LayoutDashboard,
  Map as MapIcon,
  Rocket,
  ShieldCheck,
  Database,
} from 'lucide-react';
import type { AgentId } from '@/lib/conversations';

export type AgentMeta = {
  id: AgentId;
  name: string;
  tagline: string;
  icon: typeof Bot;
  /** Tailwind text-colour class for the avatar/accents. */
  tone: string;
  /** Tailwind bg class (10–15% alpha) for the avatar disc. */
  disc: string;
};

export const AGENTS: Record<AgentId, AgentMeta> = {
  orchestrator: {
    id: 'orchestrator',
    name: 'Astra',
    tagline: 'Routes what you ask to the right agent and keeps the thread honest.',
    icon: Bot,
    tone: 'text-volt',
    disc: 'bg-volt/15',
  },
  discovery: {
    id: 'discovery',
    name: 'Discovery',
    tagline: 'Parses the estate, surveys every routine, finds the patterns.',
    icon: Compass,
    tone: 'text-status-info',
    disc: 'bg-status-info/15',
  },
  architecture: {
    id: 'architecture',
    name: 'Architecture',
    tagline: 'Drafts the modernization blueprint and proposes consolidations.',
    icon: DraftingCompass,
    tone: 'text-sand-200',
    disc: 'bg-sand-200/15',
  },
  planning: {
    id: 'planning',
    name: 'Planning',
    tagline: 'Turns the dependency graph into migration waves.',
    icon: MapIcon,
    tone: 'text-sand-300',
    disc: 'bg-sand-300/15',
  },
  spec: {
    id: 'spec',
    name: 'Spec',
    tagline: 'Reads a routine and writes the behavioural claims an SME signs.',
    icon: FileCheck2,
    tone: 'text-status-ok',
    disc: 'bg-status-ok/15',
  },
  migration: {
    id: 'migration',
    name: 'Migration',
    tagline: 'Generates target code from signed specs.',
    icon: Hammer,
    tone: 'text-status-warn',
    disc: 'bg-status-warn/15',
  },
  data: {
    id: 'data',
    name: 'Data',
    tagline: 'Schemas, CRUD matrix, target data model.',
    icon: Database,
    tone: 'text-sand-300',
    disc: 'bg-sand-300/15',
  },
  validation: {
    id: 'validation',
    name: 'Validation',
    tagline: 'Compiles, runs the test pack, proves equivalence.',
    icon: ShieldCheck,
    tone: 'text-status-ok',
    disc: 'bg-status-ok/15',
  },
  release: {
    id: 'release',
    name: 'Release',
    tagline: 'Commits, cutover checklist, sign-offs.',
    icon: Rocket,
    tone: 'text-sand-200',
    disc: 'bg-sand-200/15',
  },
  programme: {
    id: 'programme',
    name: 'Programme',
    tagline: 'Cross-programme status, cost and telemetry.',
    icon: LayoutDashboard,
    tone: 'text-sand-200',
    disc: 'bg-sand-200/15',
  },
};

export const AGENT_ORDER: AgentId[] = [
  'orchestrator',
  'discovery',
  'spec',
  'migration',
  'validation',
  'planning',
  'architecture',
  'data',
  'release',
  'programme',
];

export function agentMeta(id: AgentId | null | undefined): AgentMeta {
  return AGENTS[id ?? 'orchestrator'] ?? AGENTS.orchestrator;
}
