import { useQuery } from '@tanstack/react-query';
import { api, type EvidenceSummary } from '@/lib/api';

type Waiter = { resolve: (s: EvidenceSummary | null) => void; reject: (e: unknown) => void };

// A page of statements can hold dozens of citation chips. Each one asks for its own summary, and
// the asks made in the same moment go out as one request per project.
const queue = new Map<string, Map<string, Waiter[]>>();
let timer: ReturnType<typeof setTimeout> | null = null;
const BATCH = 100;

function flush() {
  timer = null;
  const batches = [...queue];
  queue.clear();
  for (const [corpusId, byId] of batches) {
    const ids = [...byId.keys()];
    for (let i = 0; i < ids.length; i += BATCH) {
      const group = ids.slice(i, i + BATCH);
      api
        .resolveEvidence(corpusId, group)
        .then((r) => {
          for (const id of group) for (const w of byId.get(id) ?? []) w.resolve(r.data[id] ?? null);
        })
        .catch((e) => {
          for (const id of group) for (const w of byId.get(id) ?? []) w.reject(e);
        });
    }
  }
}

function resolveSummary(corpusId: string, shortId: string): Promise<EvidenceSummary | null> {
  return new Promise((resolve, reject) => {
    let byId = queue.get(corpusId);
    if (!byId) queue.set(corpusId, (byId = new Map()));
    const waiters = byId.get(shortId) ?? [];
    waiters.push({ resolve, reject });
    byId.set(shortId, waiters);
    timer ??= setTimeout(flush, 10);
  });
}

/** The summary behind a citation chip: null when the id does not resolve in the project's current version. */
export function useEvidenceSummary(corpusId: string | undefined, shortId: string) {
  return useQuery({
    queryKey: ['evidence-summary', corpusId, shortId],
    queryFn: () => resolveSummary(corpusId!, shortId),
    enabled: !!corpusId,
    staleTime: 5 * 60_000,
  });
}
