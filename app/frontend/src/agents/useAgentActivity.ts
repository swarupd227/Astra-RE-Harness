/**
 * Who is busy right now. Polls `GET /api/v1/copilot/agents` every 30 s and
 * folds the answer onto the static agent roster, so a consumer (the rail)
 * always gets every agent — with zero activity until the endpoint answers.
 */
import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { conversationsApi, type AgentActivity, type AgentId } from '@/lib/conversations';
import { AGENT_ORDER, AGENTS, type AgentMeta } from './agents';

export type AgentActivityRow = AgentActivity & {
  meta: AgentMeta;
  /** `activeRuns > 0` — the rail pulses the avatar and stamps `data-active="true"`. */
  active: boolean;
};

export const AGENT_ACTIVITY_KEY = ['copilot', 'agents'] as const;

const EMPTY: AgentActivity[] = [];

export function useAgentActivity() {
  const query = useQuery({
    queryKey: AGENT_ACTIVITY_KEY,
    queryFn: () => conversationsApi.agents(),
    refetchInterval: 30_000,
    staleTime: 15_000,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const wire = query.data?.agents ?? EMPTY;

  const agents = useMemo<AgentActivityRow[]>(() => {
    const byId = new Map(wire.map((a) => [a.id, a] as const));
    return AGENT_ORDER.map((id) => {
      const meta = AGENTS[id];
      const a = byId.get(id);
      return {
        id,
        name: a?.name || meta.name,
        activeRuns: a?.activeRuns ?? 0,
        activeLabels: a?.activeLabels ?? [],
        lastActivityAt: a?.lastActivityAt ?? null,
        lastMessage: a?.lastMessage ?? null,
        lastConversationId: a?.lastConversationId ?? null,
        meta,
        active: (a?.activeRuns ?? 0) > 0,
      };
    });
  }, [wire]);

  const byId = useMemo(() => new Map(agents.map((a) => [a.id, a] as const)), [agents]);
  const activeCount = agents.filter((a) => a.active).length;

  return {
    agents,
    byId,
    activeCount,
    isActive: (id: AgentId) => byId.get(id)?.active ?? false,
    isLoading: query.isLoading,
    error: (query.error as Error | null) ?? null,
    refetch: query.refetch,
  };
}
