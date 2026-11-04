import { matchPath } from 'react-router-dom';
import type { AgentId } from '@/lib/conversations';
import type { Starter } from '@/workspace/ThreadPanel';

/**
 * One table: URL pattern → which agent the page belongs to, whose thread the
 * dock opens, and what to offer as starters. Routes that already own an
 * agent (the Workspace, spec review) or have no programme behind them are
 * simply absent.
 *
 * `scope` says how the thread is found: `corpus` (id in the URL),
 * `routine` (the routine's own corpus), `spec` (id in the URL is a spec —
 * that spec's own thread, the one spec review uses), `scaffold` (id in the
 * URL is generated code; its spec's thread) or `global` (Mission Control's).
 *
 * Starters are read-only on purpose and each one is something a tool can
 * answer (`get_spec`, `get_validation_results` …): a starter that promises
 * what no tool can ground just invites the model to improvise.
 */
export type DockScope = 'corpus' | 'routine' | 'spec' | 'scaffold' | 'global';

export type DockMeta = {
  pattern: string;
  scope: DockScope;
  agent: AgentId;
  agentName: string;
  /** What the page shows, for the empty state ("Ask the Planning agent about the migration plan."). */
  about: string;
  starters: (ctx: { routine?: string }) => Starter[];
};

const s = (label: string, prefill = false): Starter => ({ label, intent: label, ...(prefill ? { prefill } : {}) });

const corpusPage = (
  path: string,
  agent: AgentId,
  agentName: string,
  about: string,
  starters: Starter[],
): DockMeta[] =>
  ['corpora', 'projects'].map((root) => ({
    pattern: `/${root}/:id${path}`,
    scope: 'corpus' as const,
    agent,
    agentName,
    about,
    starters: () => starters,
  }));

const specPage = (
  pattern: string,
  scope: 'spec' | 'scaffold',
  agent: AgentId,
  agentName: string,
  about: string,
  starters: Starter[],
): DockMeta[] => [{ pattern, scope, agent, agentName, about, starters: () => starters }];

export const DOCK_META: DockMeta[] = [
  ...corpusPage('', 'programme', 'Programme', 'this project', [
    s("What's the status of this programme?"),
    s('Which routines are still unsigned?'),
    s('Show me the riskiest routines'),
  ]),
  ...corpusPage('/board', 'programme', 'Programme', 'the flow board', [
    s("What's stuck, and where?"),
    s("What's built but not verified yet?"),
    s('Sign every routine still in review'),
  ]),
  ...corpusPage('/migration-plan', 'planning', 'Planning', 'the migration plan', [
    s('Explain the migration plan wave by wave'),
    s('What should we migrate first, and why?'),
    s('Which routines block the later waves?'),
  ]),
  ...corpusPage('/dependency-graph', 'discovery', 'Discovery', 'the dependency graph', [
    s('Which routines are called the most?'),
    s('Are there dependency cycles?'),
    s('Which modules are the most tangled?'),
  ]),
  ...corpusPage('/pattern-analysis', 'discovery', 'Discovery', 'the pattern analysis', [
    s('Summarise the pattern clusters'),
    s('Which patterns are trivial accessors?'),
    s('Survey the patterns in this programme'),
  ]),
  ...corpusPage('/docs', 'discovery', 'Discovery', 'the documentation', [
    s("What's documented so far?"),
    s('Search the docs for ', true),
    s('Generate the documentation'),
  ]),
  ...corpusPage('/assessment', 'architecture', 'Architecture', 'the assessment', [
    s('Summarise the assessment'),
    s('What are the biggest risks?'),
    s('Run the assessment'),
  ]),
  ...['/subroutines/:id', '/subroutines/:id/extract', '/subroutines/:id/spec'].map((pattern) => ({
    pattern,
    scope: 'routine' as const,
    agent: 'spec' as AgentId,
    agentName: 'Spec',
    about: 'this routine',
    starters: ({ routine }: { routine?: string }) => [
      s(routine ? `What does ${routine} do?` : 'What does this routine do?'),
      s(routine ? `Who calls ${routine}?` : 'Who calls this routine?'),
      s(routine ? `Extract a spec for ${routine}` : 'Extract a spec for this routine'),
    ],
  })),
  ...specPage('/scaffolds/:id', 'scaffold', 'migration', 'Migration', 'this generated code', [
    s('Which validation gates pass on this code?'),
    s('Which spec claims does this code implement?'),
    s('Who signed the spec this was generated from?'),
  ]),
  ...specPage('/scaffolds/:id/validation', 'scaffold', 'validation', 'Validation', 'this validation report', [
    s('Which gates have passed, and which still need to run?'),
    s('What spec is this code checked against?'),
  ]),
  ...specPage('/specs/:id/scaffold', 'spec', 'migration', 'Migration', 'this code generation', [
    s('Explain the spec this code is generated from'),
    s('Have the gates run on this code yet?'),
  ]),
  ...specPage('/specs/:id/audit', 'spec', 'spec', 'Spec', "this spec's audit trail", [
    s('Who signed this spec, and when?'),
    s('Summarise the review decisions on this spec'),
  ]),
  {
    pattern: '/platform/portfolio',
    scope: 'global',
    agent: 'programme',
    agentName: 'Programme',
    about: 'the portfolio',
    starters: () => [
      s("What's the status of every programme?"),
      s('Which programme has the most unsigned specs?'),
      s('Show me the riskiest routines'),
    ],
  },
];

export type DockMatch = { meta: DockMeta; id: string | undefined };

export function matchDock(pathname: string): DockMatch | null {
  for (const meta of DOCK_META) {
    const m = matchPath({ path: meta.pattern, end: true }, pathname);
    if (m) return { meta, id: m.params.id };
  }
  return null;
}
