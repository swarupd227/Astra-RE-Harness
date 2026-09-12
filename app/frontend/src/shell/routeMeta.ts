import { matchPath } from 'react-router-dom';

/**
 * One table: URL pattern → document title + breadcrumb trail. The TopBar is
 * the only consumer; nothing here knows about components, so routes in
 * App.tsx stay free of UI concerns.
 *
 * `href` may contain `:param` placeholders, filled from the matched URL.
 */
export type Crumb = { label: string; href?: string };
export type RouteMeta = { pattern: string; title: string; crumbs: Crumb[] };

const ASK = { label: 'Ask Astra', href: '/' };
const PROJECTS = { label: 'Projects', href: '/projects' };
const ROUTINES = { label: 'Routines', href: '/subroutines' };
const SCAFFOLDS = { label: 'Generated code', href: '/scaffolds' };
const PLATFORM = { label: 'Platform', href: '/platform' };
const PROJECT = { label: 'Project', href: '/projects/:id' };
const ROUTINE = { label: 'Routine', href: '/subroutines/:id' };
const SCAFFOLD = { label: 'Scaffold artifact', href: '/scaffolds/:id' };

// Order matters only where patterns overlap; matchPath is exact (end: true)
// so `/projects` never swallows `/projects/new`.
export const ROUTE_META: RouteMeta[] = [
  { pattern: '/', title: 'Mission Control', crumbs: [{ label: 'Mission Control' }] },
  { pattern: '/w/:conversationId', title: 'Programme', crumbs: [ASK, { label: 'Programme' }] },
  { pattern: '/home', title: 'Home', crumbs: [{ label: 'Home' }] },
  { pattern: '/system', title: 'System health', crumbs: [{ label: 'System health' }] },

  // "Projects" is the user-facing word; /corpora is a legacy alias so old
  // bookmarks + the e2e suite still resolve.
  { pattern: '/projects', title: 'Projects', crumbs: [{ label: 'Projects' }] },
  { pattern: '/corpora', title: 'Projects', crumbs: [{ label: 'Projects' }] },
  { pattern: '/projects/new', title: 'New project', crumbs: [PROJECTS, { label: 'New project' }] },
  { pattern: '/corpora/new', title: 'New project', crumbs: [PROJECTS, { label: 'New project' }] },
  { pattern: '/projects/:id', title: 'Project', crumbs: [PROJECTS, { label: 'Project' }] },
  { pattern: '/corpora/:id', title: 'Project', crumbs: [PROJECTS, { label: 'Project' }] },
  { pattern: '/projects/:id/docs', title: 'Documentation', crumbs: [PROJECTS, PROJECT, { label: 'Documentation' }] },
  { pattern: '/corpora/:id/docs', title: 'Documentation', crumbs: [PROJECTS, PROJECT, { label: 'Documentation' }] },
  { pattern: '/projects/:id/pattern-analysis', title: 'Pattern analysis', crumbs: [PROJECTS, PROJECT, { label: 'Pattern analysis' }] },
  { pattern: '/corpora/:id/pattern-analysis', title: 'Pattern analysis', crumbs: [PROJECTS, PROJECT, { label: 'Pattern analysis' }] },
  { pattern: '/corpora/:id/dependency-graph', title: 'Dependency graph', crumbs: [PROJECTS, PROJECT, { label: 'Dependency graph' }] },
  { pattern: '/corpora/:id/migration-plan', title: 'Migration plan', crumbs: [PROJECTS, PROJECT, { label: 'Migration plan' }] },
  { pattern: '/projects/:id/assessment', title: 'Assessment', crumbs: [PROJECTS, PROJECT, { label: 'Assessment' }] },
  { pattern: '/corpora/:id/assessment', title: 'Assessment', crumbs: [PROJECTS, PROJECT, { label: 'Assessment' }] },

  { pattern: '/subroutines', title: 'Routines', crumbs: [{ label: 'Routines' }] },
  { pattern: '/subroutines/:id', title: 'Routine', crumbs: [ROUTINES, { label: 'Routine' }] },
  { pattern: '/subroutines/:id/extract', title: 'Extract spec', crumbs: [ROUTINES, ROUTINE, { label: 'Extract' }] },
  { pattern: '/subroutines/:id/spec', title: 'Draft spec', crumbs: [ROUTINES, ROUTINE, { label: 'Draft spec' }] },
  { pattern: '/subroutines/:id/review', title: 'Spec review', crumbs: [ROUTINES, ROUTINE, { label: 'Spec review' }] },

  { pattern: '/specs/:id/audit', title: 'Audit trail', crumbs: [ROUTINES, { label: 'Spec' }, { label: 'Audit trail' }] },
  { pattern: '/specs/:id/scaffold', title: 'Generate scaffold', crumbs: [ROUTINES, { label: 'Spec' }, { label: 'Generate scaffold' }] },

  { pattern: '/scaffolds', title: 'Generated code', crumbs: [{ label: 'Generated code' }] },
  { pattern: '/scaffolds/:id', title: 'Scaffold artifact', crumbs: [SCAFFOLDS, { label: 'Scaffold artifact' }] },
  { pattern: '/scaffolds/:id/validation', title: 'Validation', crumbs: [SCAFFOLDS, SCAFFOLD, { label: 'Validation' }] },

  { pattern: '/my-reviews', title: 'My reviews', crumbs: [{ label: 'My reviews' }] },
  { pattern: '/comments', title: 'Comments', crumbs: [{ label: 'Comments' }] },
  { pattern: '/compliance', title: 'Compliance feed', crumbs: [{ label: 'Compliance feed' }] },

  { pattern: '/platform', title: 'Platform overview', crumbs: [{ label: 'Platform overview' }] },
  { pattern: '/platform/portfolio', title: 'Portfolio dashboard', crumbs: [PLATFORM, { label: 'Portfolio dashboard' }] },
  { pattern: '/platform/golden-dataset', title: 'Golden Dataset', crumbs: [PLATFORM, { label: 'Golden Dataset' }] },
  { pattern: '/platform/prompts', title: 'Prompt Catalog', crumbs: [PLATFORM, { label: 'Prompt Catalog' }] },
  { pattern: '/platform/harmonisation', title: 'Harmonisation', crumbs: [PLATFORM, { label: 'Harmonisation' }] },
  { pattern: '/platform/languages', title: 'Languages', crumbs: [PLATFORM, { label: 'Languages' }] },
  { pattern: '/platform/signatures', title: 'Signature Health', crumbs: [PLATFORM, { label: 'Signature Health' }] },
  { pattern: '/platform/validation', title: 'Validation Policy', crumbs: [PLATFORM, { label: 'Validation Policy' }] },
  { pattern: '/platform/roles', title: 'Roles & Permissions', crumbs: [PLATFORM, { label: 'Roles & Permissions' }] },
  { pattern: '/platform/llm', title: 'LLM Provider', crumbs: [PLATFORM, { label: 'LLM Provider' }] },
];

const NOT_FOUND: { title: string; crumbs: Crumb[] } = { title: 'Not found', crumbs: [{ label: 'Not found' }] };

function fill(href: string, params: Record<string, string | undefined>): string {
  return href.replace(/:([A-Za-z0-9_]+)/g, (_, key: string) => encodeURIComponent(params[key] ?? ''));
}

/** Resolve the title + breadcrumb trail for a pathname. The last crumb is the current page. */
export function getRouteMeta(pathname: string): { title: string; crumbs: Crumb[] } {
  for (const meta of ROUTE_META) {
    const match = matchPath({ path: meta.pattern, end: true }, pathname);
    if (!match) continue;
    const params = match.params as Record<string, string | undefined>;
    return {
      title: meta.title,
      crumbs: meta.crumbs.map((c) => (c.href ? { label: c.label, href: fill(c.href, params) } : { label: c.label })),
    };
  }
  return NOT_FOUND;
}
