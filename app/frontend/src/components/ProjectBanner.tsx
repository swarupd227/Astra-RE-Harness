import { matchPath, useLocation } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';

// The project a route belongs to, for the two route families that carry a project id.
function projectIdOf(pathname: string): string | null {
  for (const pattern of ['/corpora/:id/*', '/projects/:id/*', '/corpora/:id', '/projects/:id']) {
    const id = matchPath({ path: pattern, end: true }, pathname)?.params.id;
    if (id && id !== 'new') return id;
  }
  return null;
}

/**
 * A slim bar under the top bar with the project's own banner text (for example
 * "Synthetic data for demonstration"). Renders nothing when the route has no project
 * or the project sets no banner, and never blocks the page if the settings call fails.
 */
export function ProjectBanner() {
  const { pathname } = useLocation();
  const id = projectIdOf(pathname);
  const settings = useQuery({
    queryKey: ['corpus-settings', id],
    queryFn: () => api.getCorpusSettings(id as string),
    enabled: !!id,
    staleTime: 60_000,
    retry: false,
  });

  const text = settings.data?.banner;
  if (!id || !text) return null;
  return (
    <div
      role="status"
      data-testid="project-banner"
      className="shrink-0 border-b border-status-warn/30 bg-status-warn/10 px-4 py-1 text-center text-caption font-medium text-status-warn"
    >
      {text}
    </div>
  );
}
