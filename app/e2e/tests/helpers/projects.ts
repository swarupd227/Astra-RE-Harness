/**
 * Helpers the artefact and evidence specs share: make a small throw-away project, switch artefact
 * kinds on, attach files, read the per-kind summary. Each project gets a unique name, so specs never
 * collide with each other or with the seeded demo projects.
 */
import { expect, type Page } from '@playwright/test';

export const API_BASE = process.env.API_BASE ?? 'http://127.0.0.1:38080';
export const JSON_ENG = { 'X-Dev-Persona': 'engineer', 'Content-Type': 'application/json' };

/** One Fortran routine on lines 1-5. */
export const FORTRAN = `\
      SUBROUTINE E2EART(X, Y)
      INTEGER X, Y
      Y = X + 1
      RETURN
      END
`;

/** Two routines: ALPHA on lines 1-4 and BETA on lines 5-8. */
export const FORTRAN_TWO = `\
      SUBROUTINE ALPHA(X)
      X = X + 1
      RETURN
      END
      SUBROUTINE BETA(X)
      X = X * 2
      RETURN
      END
`;

export type Item = { path: string; data: string | Buffer };

export type FileResult = {
  path: string;
  kind: string | null;
  status: 'added' | 'replaced' | 'unchanged' | 'rejected';
  code: string | null;
  message: string | null;
  sizeBytes: number;
  ambiguous: boolean;
  chunks: number;
};

export async function newProject(
  page: Page,
  kinds: string[] = [],
  opts: { source?: string; path?: string; prefix?: string } = {},
): Promise<string> {
  const res = await page.request.post(`${API_BASE}/api/v1/ingest/text`, {
    headers: JSON_ENG,
    data: {
      name: `${opts.prefix ?? 'artefacts-e2e'}-${Date.now()}-${Math.floor(Math.random() * 1e4)}`,
      files: [{ path: opts.path ?? 'e2eart.f', content: opts.source ?? FORTRAN }],
    },
  });
  expect(res.ok()).toBe(true);
  const { corpusId } = await res.json();
  if (kinds.length > 0) await setKinds(page, corpusId, kinds);
  return corpusId as string;
}

export async function setKinds(page: Page, id: string, kinds: string[]) {
  const put = await page.request.put(`${API_BASE}/api/v1/corpora/${id}/settings`, {
    headers: JSON_ENG,
    data: {
      artefactKinds: kinds,
      pack: null,
      banner: null,
      metadata: { tier: null, knowledgeHolder: null, wave: null },
      expectedCounts: {},
      demo: false,
    },
  });
  expect(put.status()).toBe(200);
}

/** Multipart through Node's own fetch: Playwright's `multipart` option cannot repeat a field name. */
export async function attach(id: string, items: Item[], opts: { persona?: string; kind?: string } = {}) {
  const fd = new FormData();
  for (const it of items) {
    const bytes = typeof it.data === 'string' ? Buffer.from(it.data, 'utf8') : it.data;
    fd.append('files', new Blob([bytes]), it.path.split('/').pop() ?? 'file');
    fd.append('path', it.path);
  }
  if (opts.kind) fd.append('kind', opts.kind);
  const res = await fetch(`${API_BASE}/api/v1/corpora/${id}/artefacts`, {
    method: 'POST',
    headers: { 'X-Dev-Persona': opts.persona ?? 'engineer' },
    body: fd,
  });
  return { status: res.status, body: await res.json() };
}

export function byPath(files: FileResult[], path: string): FileResult {
  const f = files.find((x) => x.path === path);
  if (!f) throw new Error(`no result for ${path}; got ${files.map((x) => x.path).join(', ')}`);
  return f;
}

export async function summary(page: Page, id: string, persona = 'engineer') {
  return page.request
    .get(`${API_BASE}/api/v1/corpora/${id}/artefacts`, { headers: { 'X-Dev-Persona': persona } })
    .then((r) => r.json());
}

export type EvidenceRow = {
  shortId: string;
  kind: string;
  kindLabel: string;
  trustTier: string;
  label: string;
  path: string;
  locator: string;
  lineStart: number | null;
  lineEnd: number | null;
  fileId: string;
};

export async function listEvidence(
  page: Page,
  id: string,
  query: Record<string, string | number> = {},
  persona = 'engineer',
) {
  const qs = new URLSearchParams(Object.entries(query).map(([k, v]) => [k, String(v)])).toString();
  const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/evidence${qs ? `?${qs}` : ''}`, {
    headers: { 'X-Dev-Persona': persona },
  });
  expect(res.status()).toBe(200);
  return (await res.json()) as {
    data: EvidenceRow[];
    total: number;
    kinds: { kind: string; label: string; count: number }[];
  };
}

/** `count` numbered lines, "line 1" … — enough for several 40-line windows. */
export function numberedLines(count: number, prefix = 'line'): string {
  return Array.from({ length: count }, (_, i) => `${prefix} ${i + 1}`).join('\n') + '\n';
}
