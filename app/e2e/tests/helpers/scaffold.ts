import type { APIRequestContext } from '@playwright/test';

export type ScaffoldIds = { id: string; specId: string; subroutineId: string; corpusId: string };

/** The synthetic one-routine project (CONSUME_ROLL). It exists when the API runs with `Database__SeedDemo=true`. */
export const SEED_CORPUS = 'Roll-stock inventory demo (Fortran F77)';

const ENG = { 'X-Dev-Persona': 'engineer' };
const SME = { 'X-Dev-Persona': 'sme' };
const T = 180_000; // the config's action timeout also applies to page.request

async function getJson(request: APIRequestContext, url: string) {
  const res = await request.get(url, { headers: ENG, timeout: T });
  return res.ok() ? res.json() : null;
}

/**
 * Takes a routine to a signed spec with the platform's own API (extract → route → accept every claim
 * → sign) — the same calls the montage demos make, no UI. Picks up wherever the routine already is.
 * Returns the spec id, or null when signing was refused.
 */
async function signRoutine(request: APIRequestContext, apiBase: string, routineId: string): Promise<string | null> {
  const specUrl = `${apiBase}/api/v1/subroutines/${routineId}/spec`;
  let spec = await getJson(request, specUrl);
  if (spec?.state === 'SIGNED') return spec.id;

  if (!spec?.id || !['DRAFT', 'IN_REVIEW'].includes(spec.state)) {
    await (await request.post(`${apiBase}/api/v1/subroutines/${routineId}/extract`, { headers: ENG, data: {}, timeout: T })).text();
    spec = await getJson(request, specUrl);
    if (!spec?.id) return null;
  }
  if (spec.state === 'DRAFT') {
    await request.post(`${apiBase}/api/v1/specs/${spec.id}/route`, { headers: ENG, data: { routingNote: 'e2e fixture' }, timeout: T });
  }

  const sections: Array<[string, string, Array<{ id?: string }>]> = [
    ['invariants', 'INV', spec.spec?.invariants ?? []],
    ['side_effects', 'SE', spec.spec?.side_effects ?? []],
    ['edge_cases', 'EC', spec.spec?.edge_cases ?? []],
    ['open_questions', 'Q', spec.spec?.open_questions ?? []],
  ];
  for (const [section, prefix, claims] of sections) {
    for (const [i, claim] of claims.entries()) {
      // Spec review numbers a claim the model left without an id.
      const claimPath = `$.${section}[?(@.id=='${claim.id ?? `${prefix}-${i + 1}`}')]`;
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
  return sign.ok() ? spec.id : null;
}

/** The seed project's routine and its id, or a clear failure when the seed is switched off. */
async function seedRoutine(request: APIRequestContext, apiBase: string): Promise<{ corpusId: string; subroutineId: string }> {
  const corpora = (await getJson(request, `${apiBase}/api/v1/corpora`))?.data ?? [];
  const seed = corpora.find((c: { name: string }) => c.name === SEED_CORPUS);
  if (!seed) throw new Error(`No "${SEED_CORPUS}" project: run the API with Database__SeedDemo=true (DATABASE_SEED_DEMO=true).`);
  const detail = await getJson(request, `${apiBase}/api/v1/corpora/${seed.id}`);
  return { corpusId: seed.id, subroutineId: detail.latestVersion.files[0].subroutines[0].id };
}

/** The seed routine with a signed spec on the current source version, signing it first if needed. */
export async function ensureSeedSignedSpec(request: APIRequestContext, apiBase: string) {
  const routine = await seedRoutine(request, apiBase);
  const specId = await signRoutine(request, apiBase, routine.subroutineId);
  if (!specId) throw new Error('The seed routine could not be signed.');
  return { ...routine, specId };
}

/** The seed routine with generated code, built first if needed. */
export async function ensureSeedScaffold(request: APIRequestContext, apiBase: string): Promise<ScaffoldIds> {
  const signed = await ensureSeedSignedSpec(request, apiBase);
  const url = `${apiBase}/api/v1/specs/${signed.specId}/scaffold`;
  let scaffold = await getJson(request, url);
  if (!scaffold?.id) {
    await (await request.post(url, { headers: ENG, data: {}, timeout: T })).text();
    scaffold = await getJson(request, url);
  }
  if (!scaffold?.id) throw new Error('No code was generated for the seed routine.');
  return { id: scaffold.id, ...signed };
}

/**
 * A scaffold for tests that need generated code on screen. Returns one that
 * already exists; otherwise drives a small routine through the pipeline.
 * Works against the mock providers; against a real one it would spend tokens,
 * so it is meant for the local stack. Null when there is nothing to build from.
 */
export async function ensureScaffold(request: APIRequestContext, apiBase: string): Promise<ScaffoldIds | null> {
  const json = async (url: string) => (await request.get(`${apiBase}${url}`, { headers: ENG, timeout: T })).json();

  const first = async (): Promise<ScaffoldIds | null> => {
    const list = (await json('/api/v1/scaffolds')).data ?? [];
    const s = list[0];
    return s ? { id: s.id, specId: s.specId, subroutineId: s.subroutineId, corpusId: s.corpusId } : null;
  };

  const existing = await first();
  if (existing) return existing;

  // A small routine that has not been touched yet, preferring MINPACK (a real
  // Fortran corpus the local stack seeds by default).
  const corpora = (await json('/api/v1/corpora')).data ?? [];
  const minpack = corpora.find((c: { name: string }) => c.name.includes('MINPACK'));
  if (!minpack) return null;
  const parsed = (await json(`/api/v1/subroutines?corpus=${minpack.id}&state=PARSED&limit=1`)).data ?? [];
  const routine = parsed[0];
  if (!routine) return null;

  const specId = await signRoutine(request, apiBase, routine.id);
  if (!specId) return null;

  await (await request.post(`${apiBase}/api/v1/specs/${specId}/scaffold`, { headers: ENG, data: {}, timeout: T })).text();
  return first();
}
