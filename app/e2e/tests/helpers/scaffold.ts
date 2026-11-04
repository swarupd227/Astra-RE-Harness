import type { APIRequestContext } from '@playwright/test';

export type ScaffoldIds = { id: string; specId: string; subroutineId: string; corpusId: string };

/**
 * A scaffold for tests that need generated code on screen. Returns one that
 * already exists; otherwise drives a small routine through the pipeline with
 * the platform's own API (extract → route → accept every claim → sign →
 * scaffold) — the same calls the montage demos make, no UI. Works against the
 * mock providers; against a real one it would spend tokens, so it is meant
 * for the local stack. Null when there is nothing to build from.
 */
export async function ensureScaffold(request: APIRequestContext, apiBase: string): Promise<ScaffoldIds | null> {
  const ENG = { 'X-Dev-Persona': 'engineer' };
  const SME = { 'X-Dev-Persona': 'sme' };
  const T = 180_000; // the config's 8 s action timeout also applies to page.request
  const json = async (url: string, headers = ENG) => (await request.get(`${apiBase}${url}`, { headers, timeout: T })).json();

  const first = async (): Promise<ScaffoldIds | null> => {
    const list = (await json('/api/v1/scaffolds')).data ?? [];
    const s = list[0];
    return s ? { id: s.id, specId: s.specId, subroutineId: s.subroutineId, corpusId: s.corpusId } : null;
  };

  const existing = await first();
  if (existing) return existing;

  // A small routine that has not been touched yet, preferring MINPACK (the
  // Fortran schema whose claim sections the loop below knows).
  const corpora = (await json('/api/v1/corpora')).data ?? [];
  const minpack = corpora.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) return null;
  const parsed = (await json(`/api/v1/subroutines?corpus=${minpack.id}&state=PARSED&limit=1`)).data ?? [];
  const routine = parsed[0];
  if (!routine) return null;

  await (await request.post(`${apiBase}/api/v1/subroutines/${routine.id}/extract`, { headers: ENG, data: {}, timeout: T })).text();
  const spec = await json(`/api/v1/subroutines/${routine.id}/spec`);
  if (!spec?.id) return null;

  await request.post(`${apiBase}/api/v1/specs/${spec.id}/route`, { headers: ENG, data: { routingNote: 'e2e fixture' }, timeout: T });
  const sections: Array<[string, Array<{ id: string }>]> = [
    ['invariants', spec.spec?.invariants ?? []],
    ['side_effects', spec.spec?.side_effects ?? []],
    ['edge_cases', spec.spec?.edge_cases ?? []],
    ['open_questions', spec.spec?.open_questions ?? []],
  ];
  for (const [section, claims] of sections) {
    for (const claim of claims) {
      const claimPath = `$.${section}[?(@.id=='${claim.id}')]`;
      // An open question is resolved by editing it; "question" would leave sign blocked.
      const data = section === 'open_questions'
        ? { claimPath, action: 'edit', editedText: 'Resolved for the e2e fixture.' }
        : { claimPath, action: 'accept' };
      await request.post(`${apiBase}/api/v1/specs/${spec.id}/claims/review`, { headers: SME, data, timeout: T });
    }
  }
  const sign = await request.post(`${apiBase}/api/v1/specs/${spec.id}/sign`, {
    headers: SME, data: { confirmation: 'I have reviewed every claim.' }, timeout: T,
  });
  if (!sign.ok()) return null;

  await (await request.post(`${apiBase}/api/v1/specs/${spec.id}/scaffold`, { headers: ENG, data: {}, timeout: T })).text();
  return first();
}
