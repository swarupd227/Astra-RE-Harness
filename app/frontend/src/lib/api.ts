import type { Persona } from '@/tokens/tokens';

const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');

const PERSONA_KEY = 'astra.devPersona';

export function getPersona(): Persona {
  const v = localStorage.getItem(PERSONA_KEY);
  if (v === 'engineer' || v === 'sme' || v === 'observer' || v === 'admin') return v;
  return 'engineer';
}

export function setPersona(p: Persona) {
  localStorage.setItem(PERSONA_KEY, p);
}

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly details?: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

export async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set('X-Dev-Persona', getPersona());
  if (init?.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');

  const res = await fetch(`${API_BASE}${path}`, { ...init, headers });
  const text = await res.text();
  const isJson = res.headers.get('content-type')?.includes('application/json');
  const body = isJson && text ? JSON.parse(text) : text;

  if (!res.ok) {
    const err = isJson ? body?.error : null;
    throw new ApiError(
      res.status,
      err?.code ?? 'http.error',
      err?.message ?? `Request failed (${res.status})`,
      err?.details,
    );
  }
  return body as T;
}

// ─── Endpoint helpers (Phase A surface) ──────────────────────────────
export type HealthResponse = { status: string; service: string };
export type ReadinessDep = { name: string; status: 'ok' | 'down'; error: string | null };
export type ReadinessResponse = {
  status: 'ready' | 'degraded';
  service: string;
  dependencies: ReadinessDep[];
};
export type WhoamiResponse = {
  persona: Persona;
  displayName: string;
  isBypass: boolean;
  bypassEnabled: boolean;
  defaultPersona: string;
};

// ─── Personas & roles (value-add #5: clear separation of duties) ────
export type PersonaDef = {
  id: 'engineer' | 'sme' | 'observer' | 'admin';
  displayName: string;
  charter: string;
  ownsStages: string[];
};

export type PersonaActionDef = {
  id: string;
  label: string;
  description: string;
  category: string;
  allowedPersonas: string[];
};

export type PersonaMatrix = {
  personas: { id: string; displayName: string }[];
  actions: PersonaActionDef[];
};

// ─── Spec schemas (value-add #4: multi-language support) ────────────
export type ClaimKindSummary = {
  id: string;
  label: string;
  idPrefix: string;
  displayTone?: string;
  description: string;
};

export type SpecSchemaSummary = {
  id: string;
  displayName: string;
  description: string;
  supportedSourceExtensions: string[];
  compatibleTargetStacks: string[];
  claimKindCount: number;
  claimKinds: ClaimKindSummary[];
  owner?: string;
  calibratedAgainst?: string;
  status: string;
  platformReadiness?: string;
};

// ─── Prompt library (value-add #2: calibrated prompts) ──────────────
export type PromptSummary = {
  sourceSchema: string;
  targetStack: string;
  kind: string;
  version: string;
  promptId: string;
  owner?: string;
  status?: string;
  modelPreference?: string;
  path: string;
};

export type PromptDetail = PromptSummary & {
  frontmatter: Record<string, string>;
  systemTemplate: string;
  userTemplate: string;
};

// ─── Compliance feed (value-add #6: audit log) ──────────────────────
export type ComplianceColumn = {
  id: string;
  header: string;
  description: string;
};

export type ComplianceFormat = {
  id: string;
  displayName: string;
  description: string;
  status: string;
  contentType: string;
  extension: string;
  columnCount: number;
  columns: ComplianceColumn[];
};

// ─── Archetypes (value-add #3: ready-made code templates) ───────────
export type ArchetypeManifest = {
  id: string;
  targetStack: string;
  displayName: string;
  description: string;
  compatibleSchemas: string[];
  owner?: string;
  /** "production" | "preview · pair-engagement only" | etc. */
  status: string;
  platformReadiness?: string;
  fileCount: number;
};

// ─── Provider Settings (value-add #1: AI hygiene visibility) ─────────
export type ProviderSettings = {
  provider: {
    name: string;
    displayName: string;
    model: string;
    endpointHostname: string | null;
    apiVersion: string | null;
    maxOutputTokens: number | null;
  };
  residency: {
    configVersion: string;
    zdr: boolean;
    noTraining: boolean;
    noRetention: boolean;
    enterpriseEndpoint: boolean;
    offline: boolean;
  };
  promptLibrary: {
    schemaId: string;
    targetStack: string;
    extractPromptId: string | null;
    extractPromptVersion: string | null;
  };
};

// ─── Evidence Trail types ────────────────────────────────────────────
export type EvidenceResponse = {
  id: string;
  state: string;
  createdAt: string;
  updatedAt: string;
  subroutine: {
    id: string;
    name: string;
    signature: string;
    lineStart: number;
    lineEnd: number;
    state: string;
    commonBlockRefs: string[] | null;
    calledSubroutines: string[] | null;
    file: { id: string; relativePath: string; lineCount: number; fileHash: string } | null;
    corpus: { id: string; name: string; sourceType: string; sourceUrl: string | null } | null;
    sourceVersion: { id: string; gitCommitHash: string | null; ingestedAt: string } | null;
  } | null;
  llmCall: {
    id: string;
    provider: string;
    model: string;
    promptTemplateId: string;
    promptTemplateVersion: string;
    providerConfigVersion: string;
    inputTokens: number;
    outputTokens: number;
    latencyMs: number;
    costUsd: number;
    calledAt: string;
  } | null;
  review: { total: number; byAction: Record<string, number> };
  signature: {
    id: string;
    signedAt: string;
    signerDisplay: string;
    algorithm: string;
    keyId: string;
    specCanonicalHash: string;
    sourceVersionHash: string;
    signatureBase64: string;
    signedBlobUri: string;
  } | null;
  scaffold: {
    id: string;
    state: string;
    targetPlatform: string;
    fileCount: number;
    totalLines: number;
    todoCount: number;
    gitBranch: string | null;
    gitCommitHash: string | null;
    gitCommitUrl: string | null;
    generatedAt: string;
  } | null;
};

export type SignedManifest = {
  manifestVersion?: number;
  specId: string;
  subroutineId: string;
  sourceVersionId: string;
  sourceVersionHash: string;
  specCanonicalHash: string;
  algorithm: string;
  keyId: string;
  signatureBase64: string;
  signedAt: string;
  signerDisplay: string;
  /** Canonical-JSON bytes verbatim — present for signatures made after the manifest-v1 change. */
  specCanonical?: string;
  spec: unknown;
};

export type PublicKeyResponse = {
  keyId: string;
  algorithm: string;
  publicKeyPem: string;
  createdAt?: string;
  source: 'db' | 'signer-cache';
};

// ─── System stats (home rollup) ──────────────────────────────────────
export type SystemStats = {
  corpora: { total: number; files: number; totalLoc: number; byState: Record<string, number> };
  subroutines: { total: number; byState: Record<string, number> };
  specs: { total: number; byState: Record<string, number> };
  scaffolds: { total: number; todoTotal: number };
  llm: {
    totalCalls: number;
    totalCostUsd: number;
    totalInputTokens: number;
    totalOutputTokens: number;
    avgLatencyMs: number | null;
    lastCalledAt: string | null;
  };
};

// ─── Phase B.1 types ─────────────────────────────────────────────────
export type CorpusListItem = {
  id: string;
  name: string;
  sourceType: 'git' | 'upload';
  state: string;
  fileCount: number;
  totalLoc: number;
  updatedAt: string;
};
export type CorpusDetail = {
  id: string;
  name: string;
  sourceType: string;
  state: string;
  fileCount: number;
  totalLoc: number;
  createdAt: string;
  updatedAt: string;
  latestVersion: {
    id: string;
    ingestedAt: string;
    files: {
      id: string;
      relativePath: string;
      lineCount: number;
      fileHash: string;
      subroutines: {
        id: string;
        name: string;
        lineStart: number;
        lineEnd: number;
        state: string;
        carriedForward?: boolean;
        previousSpecId?: string | null;
      }[];
    }[];
  } | null;
};
export type SubroutineDetail = {
  id: string;
  name: string;
  signature: string;
  lineStart: number;
  lineEnd: number;
  state: string;
  commonBlockRefs: string[] | null;
  calledSubroutines: string[] | null;
  ioPatterns: { isam_reads?: string[]; isam_writes?: string[]; notifications?: string[] } | null;
  file: { id: string; relativePath: string; lineCount: number; fileHash: string };
  corpus: { id: string; name: string; state: string };
  version: { id: string; ingestedAt: string };
};
export type SubroutineSource = {
  relativePath: string;
  fileHash: string;
  lineCount: number;
  content: string;
};

// ─── Phase B.2/B.3 types ─────────────────────────────────────────────
export type SpecClaim = {
  id: string;
  claim?: string;
  description?: string;
  question?: string;
  citations?: { lines: string }[];
  confidence?: string;
  behavior?: string;
  status?: string;
  // SME-applied annotations:
  sme_action?: 'accept' | 'edit' | 'reject' | 'question';
  sme_reason?: string;
  original_claim?: string;
  original_description?: string;
  original_question?: string;
};

export type ClaimReview = {
  id: string;
  claimPath: string;
  action: 'accept' | 'edit' | 'reject' | 'question';
  reason: string | null;
  editedText: string | null;
  reviewedAt: string;
};

export type SignatureInfo = {
  id: string;
  signedAt: string;
  signerDisplay: string;
  algorithm: string;
  keyId: string;
  specCanonicalHash: string;
  sourceVersionHash: string;
  signedBlobUri: string;
  signatureBase64: string;
};

export type SpecResponse = {
  id: string;
  subroutineId: string;
  sourceVersionId: string;
  state: string;
  createdAt: string;
  updatedAt: string;
  spec: {
    routine?: string;
    source_path?: string;
    source_lines?: string;
    summary?: string;
    inputs?: SpecClaim[];
    outputs?: SpecClaim[];
    invariants?: SpecClaim[];
    side_effects?: SpecClaim[];
    edge_cases?: SpecClaim[];
    open_questions?: SpecClaim[];
  };
  claimReviews: ClaimReview[];
  signature: SignatureInfo | null;
  llmCall: {
    id: string;
    provider: string;
    model: string;
    promptTemplateId: string;
    promptTemplateVersion: string;
    providerConfigVersion: string;
    inputTokens: number;
    outputTokens: number;
    latencyMs: number;
    costUsd: number;
    status: string;
    calledAt: string;
  } | null;
};

export const api = {
  health: () => apiFetch<HealthResponse>('/health'),
  readiness: () => apiFetch<ReadinessResponse>('/health/ready'),
  whoami: () => apiFetch<WhoamiResponse>('/api/v1/whoami'),
  systemStats: () => apiFetch<SystemStats>('/api/v1/system/stats'),

  // Phase #4 / value-add #1: provider trust signals
  getProviderSettings: () => apiFetch<ProviderSettings>('/api/v1/providers/settings'),

  // Phase #4 / value-add #4: spec-schema discovery (multi-language)
  listSpecSchemas: () => apiFetch<{ data: SpecSchemaSummary[] }>('/api/v1/spec-schemas'),

  // Phase #4 / value-add #5: persona model & permissions matrix
  listPersonas: () => apiFetch<{ data: PersonaDef[] }>('/api/v1/personas'),
  getPersonaMatrix: () => apiFetch<PersonaMatrix>('/api/v1/personas/matrix'),

  // Phase #4 / value-add #2: prompt library
  listPrompts: () => apiFetch<{ data: PromptSummary[] }>('/api/v1/prompts'),
  getPrompt: (source: string, target: string, kind: string, version?: string) =>
    apiFetch<PromptDetail>(
      version
        ? `/api/v1/prompts/${source}/${target}/${kind}/${version}`
        : `/api/v1/prompts/${source}/${target}/${kind}`,
    ),

  // Phase #4 / value-add #3: scaffold archetype catalog
  listArchetypes: () => apiFetch<{ data: ArchetypeManifest[] }>('/api/v1/archetypes'),

  // Phase #4 / value-add #6: SOX/HIPAA/PCI evidence bundles
  listComplianceFormats: () =>
    apiFetch<{ data: ComplianceFormat[] }>('/api/v1/compliance/formats'),
  // Downloads as a Blob so we can attach a synthetic <a> click for the
  // browser save dialog. Sends the X-Dev-Persona header that apiFetch
  // would set, since plain href= cannot carry custom headers.
  downloadComplianceFeed: async (params: {
    format: string;
    since?: string;
    until?: string;
    severity?: string;
    limit?: number;
  }): Promise<{ blob: Blob; fileName: string; rowCount: number }> => {
    const qs = new URLSearchParams();
    qs.set('format', params.format);
    if (params.since) qs.set('since', params.since);
    if (params.until) qs.set('until', params.until);
    if (params.severity) qs.set('severity', params.severity);
    if (params.limit) qs.set('limit', String(params.limit));
    const res = await fetch(`${API_BASE}/api/v1/compliance/feed?${qs}`, {
      headers: { 'X-Dev-Persona': getPersona() },
    });
    if (!res.ok) {
      let msg = `Feed download failed (${res.status})`;
      try {
        const j = await res.json();
        msg = j?.error?.message ?? msg;
      } catch { /* ignore */ }
      throw new Error(msg);
    }
    const cd = res.headers.get('Content-Disposition') ?? '';
    const fileNameMatch = /filename="([^"]+)"/.exec(cd);
    const fileName = fileNameMatch?.[1] ?? `compliance-${params.format}.csv`;
    const rowCount = Number(res.headers.get('X-Astra-Row-Count') ?? '0');
    const blob = await res.blob();
    return { blob, fileName, rowCount };
  },

  // Phase C UX polish: evidence-trail endpoints
  getSpec: (specId: string) => apiFetch<EvidenceResponse>(`/api/v1/specs/${specId}`),
  getSignedManifest: (specId: string) =>
    apiFetch<SignedManifest>(`/api/v1/specs/${specId}/signed-manifest`),
  getPublicKey: (keyId: string) => apiFetch<PublicKeyResponse>(`/api/v1/signing-keys/${keyId}/public`),

  listCorpora: () => apiFetch<{ data: CorpusListItem[] }>('/api/v1/corpora'),
  getCorpus: (id: string) => apiFetch<CorpusDetail>(`/api/v1/corpora/${id}`),
  getSubroutine: (id: string) => apiFetch<SubroutineDetail>(`/api/v1/subroutines/${id}`),
  getSubroutineSource: (id: string) => apiFetch<SubroutineSource>(`/api/v1/subroutines/${id}/source`),

  // Phase C.12: cross-corpus subroutine search
  searchSubroutines: (params: {
    q?: string;
    corpus?: string;
    state?: string;
    limit?: number;
    offset?: number;
  } = {}) => {
    const qs = new URLSearchParams();
    if (params.q) qs.set('q', params.q);
    if (params.corpus) qs.set('corpus', params.corpus);
    if (params.state) qs.set('state', params.state);
    if (params.limit) qs.set('limit', String(params.limit));
    if (params.offset) qs.set('offset', String(params.offset));
    const suffix = qs.toString() ? `?${qs.toString()}` : '';
    return apiFetch<SubroutineSearchResponse>(`/api/v1/subroutines${suffix}`);
  },
  getSpecForSubroutine: (id: string) => apiFetch<SpecResponse>(`/api/v1/subroutines/${id}/spec`),

  routeSpec: (specId: string, body: { reviewerIds?: string[]; routingNote?: string }) =>
    apiFetch<{ id: string; state: string }>(`/api/v1/specs/${specId}/route`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  reviewClaim: (specId: string, body: { claimPath: string; action: string; reason?: string; editedText?: string }) =>
    apiFetch<ClaimReview>(`/api/v1/specs/${specId}/claims/review`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  signSpec: (specId: string, confirmation: string) =>
    apiFetch<SignatureInfo & { state: string }>(`/api/v1/specs/${specId}/sign`, {
      method: 'POST',
      body: JSON.stringify({ confirmation }),
    }),

  getScaffoldForSpec: (specId: string) =>
    apiFetch<ScaffoldResponse>(`/api/v1/specs/${specId}/scaffold`),
  getScaffold: (id: string) => apiFetch<ScaffoldResponse>(`/api/v1/scaffolds/${id}`),
  commitScaffold: (id: string, body: { branch?: string; commitMessage?: string }) =>
    apiFetch<{ id: string; state: string; branch: string; commitHash: string; commitUrl: string; stub: boolean }>(
      `/api/v1/scaffolds/${id}/commit`,
      { method: 'POST', body: JSON.stringify(body) },
    ),

  // ─── Phase C.1: ingest ────────────────────────────────────────────
  ingestText: (body: { name: string; files: { path: string; content: string }[] }) =>
    apiFetch<IngestResult>('/api/v1/ingest/text', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  ingestUpload: async (name: string, files: File[]): Promise<IngestResult> => {
    const fd = new FormData();
    fd.append('name', name);
    for (const f of files) fd.append('files', f);
    const headers = new Headers();
    headers.set('X-Dev-Persona', getPersona());
    const res = await fetch(`${API_BASE}/api/v1/ingest/upload`, {
      method: 'POST',
      headers,
      body: fd,
    });
    const text = await res.text();
    const isJson = res.headers.get('content-type')?.includes('application/json');
    const body = isJson && text ? JSON.parse(text) : text;
    if (!res.ok) {
      const err = isJson ? body?.error : null;
      throw new ApiError(
        res.status,
        err?.code ?? 'http.error',
        err?.message ?? `Upload failed (${res.status})`,
        err?.details,
      );
    }
    return body as IngestResult;
  },
  ingestGit: (body: { name: string; url: string; branch?: string; sourceRoot?: string }) =>
    apiFetch<IngestGitResult>('/api/v1/ingest/git', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  // ─── Phase C.3: re-sync ───────────────────────────────────────────
  reingestUpload: async (corpusId: string, files: File[]): Promise<ReingestResult> => {
    const fd = new FormData();
    for (const f of files) fd.append('files', f);
    const headers = new Headers();
    headers.set('X-Dev-Persona', getPersona());
    const res = await fetch(`${API_BASE}/api/v1/corpora/${corpusId}/reingest/upload`, {
      method: 'POST',
      headers,
      body: fd,
    });
    const text = await res.text();
    const isJson = res.headers.get('content-type')?.includes('application/json');
    const body = isJson && text ? JSON.parse(text) : text;
    if (!res.ok) {
      const err = isJson ? body?.error : null;
      throw new ApiError(
        res.status,
        err?.code ?? 'http.error',
        err?.message ?? `Re-sync failed (${res.status})`,
        err?.details,
      );
    }
    return body as ReingestResult;
  },
  reingestGit: (corpusId: string, body: { url: string; branch?: string; sourceRoot?: string }) =>
    apiFetch<ReingestResult & { gitCommitHash: string | null }>(
      `/api/v1/corpora/${corpusId}/reingest/git`,
      {
        method: 'POST',
        // The reingest/git endpoint reuses the IngestGitRequest shape, so the
        // `name` is optional but not used — pass through what the user gave
        // us if any (the URL itself is what matters server-side).
        body: JSON.stringify({ name: '', ...body }),
      },
    ),

  // ─── Phase #2a/2b/2c: post-migration validation ────────────────────
  listValidationRuns: (scaffoldId: string) =>
    apiFetch<ValidationRunsResponse>(`/api/v1/scaffolds/${scaffoldId}/validation`),
  validateCompile: (scaffoldId: string) =>
    apiFetch<ValidationRun>(`/api/v1/scaffolds/${scaffoldId}/validate/compile`, { method: 'POST' }),
  generateTestPack: (scaffoldId: string) =>
    apiFetch<TestPackGenerationResult>(`/api/v1/scaffolds/${scaffoldId}/generate-test-pack`, {
      method: 'POST',
    }),
  validateTestPack: (scaffoldId: string) =>
    apiFetch<ValidationRun>(`/api/v1/scaffolds/${scaffoldId}/validate/test-pack`, { method: 'POST' }),
  validateEquivalence: (scaffoldId: string) =>
    apiFetch<ValidationRun>(`/api/v1/scaffolds/${scaffoldId}/validate/equivalence`, { method: 'POST' }),
  getValidationRunLog: async (runId: string): Promise<string> => {
    const headers = new Headers();
    headers.set('X-Dev-Persona', getPersona());
    const res = await fetch(`${API_BASE}/api/v1/validation-runs/${runId}/log`, { headers });
    if (!res.ok) throw new ApiError(res.status, 'http.error', `Could not fetch log (${res.status})`);
    return await res.text();
  },
};

// ─── Validation types ─────────────────────────────────────────────────

export type ValidationStage = 'COMPILE' | 'TEST_PACK' | 'EQUIVALENCE';
export type ValidationStatus = 'RUNNING' | 'PASSED' | 'FAILED' | 'ERRORED';

export type ValidationRun = {
  id: string;
  scaffoldId: string;
  specId: string;
  stage: ValidationStage;
  status: ValidationStatus;
  summary: string;
  errorCode: string | null;
  logBlobUri: string | null;
  metrics: Record<string, unknown> | null;
  startedAt: string;
  completedAt: string | null;
};

export type ValidationRunsResponse = {
  scaffoldId: string;
  specId: string;
  runs: ValidationRun[];
};

export type TestPackGenerationResult = {
  scaffoldId: string;
  testFilePath: string;
  counts: {
    invariants: number;
    sideEffects: number;
    edgeCases: number;
    openQuestions: number;
    total: number;
  };
};

// ─── Phase C.7: comments + notifications ─────────────────────────────
export const commentsApi = {
  list: (specId: string, claimPath?: string) => {
    const qs = claimPath ? `?claimPath=${encodeURIComponent(claimPath)}` : '';
    return apiFetch<{ data: CommentItem[] }>(`/api/v1/specs/${specId}/comments${qs}`);
  },
  post: (specId: string, body: { body: string; claimPath?: string; parentCommentId?: string }) =>
    apiFetch<CommentItem>(`/api/v1/specs/${specId}/comments`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  edit: (commentId: string, newBody: string) =>
    apiFetch<CommentItem>(`/api/v1/comments/${commentId}`, {
      method: 'PATCH',
      body: JSON.stringify({ body: newBody }),
    }),
  resolve: (commentId: string, unresolve = false) =>
    apiFetch<CommentItem>(`/api/v1/comments/${commentId}/resolve`, {
      method: 'POST',
      body: JSON.stringify({ unresolve }),
    }),
  delete: (commentId: string) =>
    apiFetch<void>(`/api/v1/comments/${commentId}`, { method: 'DELETE' }),
};

export const notificationsApi = {
  list: (opts: { unreadOnly?: boolean; limit?: number; offset?: number } = {}) => {
    const qs = new URLSearchParams();
    if (opts.unreadOnly) qs.set('unreadOnly', 'true');
    if (opts.limit) qs.set('limit', String(opts.limit));
    if (opts.offset) qs.set('offset', String(opts.offset));
    const suffix = qs.toString() ? `?${qs.toString()}` : '';
    return apiFetch<NotificationInbox>(`/api/v1/notifications${suffix}`);
  },
  unreadCount: () =>
    apiFetch<{ unread: number; persona: string }>(`/api/v1/notifications/unread-count`),
  markRead: (id: string) =>
    apiFetch<{ id: string; readAt: string }>(`/api/v1/notifications/${id}/read`, { method: 'POST' }),
  markAllRead: () =>
    apiFetch<{ markedRead: number; persona: string }>(`/api/v1/notifications/read-all`, { method: 'POST' }),
};

export type CommentItem = {
  id: string;
  specId: string;
  claimPath: string | null;
  parentCommentId: string | null;
  body: string;
  deleted: boolean;
  authorPersona: string;
  authorDisplay: string;
  mentionedPersonas: string[];
  createdAt: string;
  editedAt: string | null;
  resolvedAt: string | null;
  resolvedByPersona: string | null;
};

export type NotificationItem = {
  id: string;
  recipientPersona: string;
  type: string;
  targetType: string;
  targetId: string;
  payload: {
    specId?: string;
    claimPath?: string | null;
    commentId?: string;
    parentCommentId?: string | null;
    excerpt?: string;
    authorPersona?: string;
    authorDisplay?: string;
    reason?: string;
  };
  createdAt: string;
  readAt: string | null;
  actorPersona: string | null;
  actorDisplay: string | null;
};

export type NotificationInbox = {
  data: NotificationItem[];
  total: number;
  unread: number;
  persona: string;
};

// ─── Phase C.1 types ─────────────────────────────────────────────────
export type IngestResult = {
  corpusId: string;
  state: 'INGESTING' | 'PARSING' | 'PARSED' | 'FAILED';
  fileCount: number;
  totalLoc: number;
  subroutineCount: number;
  warnings: string[];
  errorMessage: string | null;
};

export type IngestGitResult = IngestResult & {
  gitCommitHash: string | null;
};

// ─── Phase C.12 types ────────────────────────────────────────────────
export type SubroutineSearchHit = {
  id: string;
  name: string;
  signature: string;
  lineStart: number;
  lineEnd: number;
  state: string;
  file: { id: string; relativePath: string; lineCount: number };
  corpus: { id: string; name: string };
};

export type SubroutineSearchResponse = {
  data: SubroutineSearchHit[];
  total: number;
  limit: number;
  offset: number;
  hasMore: boolean;
  query: string | null;
};

// ─── Phase C.3 types ─────────────────────────────────────────────────
export type ReingestResult = {
  corpusId: string;
  state: 'INGESTING' | 'PARSING' | 'PARSED' | 'FAILED';
  fileCount: number;
  totalLoc: number;
  subroutineCount: number;
  carriedForwardCount: number;
  supersededCount: number;
  warnings: string[];
  errorMessage: string | null;
};

// ─── Phase B.4 types ─────────────────────────────────────────────────
export type ScaffoldFile = {
  path: string;
  language: string;
  content: string;
  lineCount: number;
  todoCount: number;
  derivedFromClaimIds: string[];
};

export type ScaffoldResponse = {
  id: string;
  specId: string;
  state: string;
  targetPlatform: string;
  fileCount: number;
  totalLines: number;
  todoCount: number;
  generatedAt: string;
  packageBlobUri: string;
  git: {
    branch: string;
    commitHash: string;
    commitUrl: string;
  } | null;
  llmCall: {
    provider: string;
    model: string;
    promptTemplateId: string;
    promptTemplateVersion: string;
    providerConfigVersion: string;
    inputTokens: number;
    outputTokens: number;
    latencyMs: number;
    costUsd: number;
  } | null;
  files: ScaffoldFile[];
};

/** Build the canonical claim path used by the API. */
export function claimPathFor(section: string, id: string): string {
  return `$.${section}[?(@.id=='${id}')]`;
}

// ─── Phase B.3.3: audit + my reviews ─────────────────────────────────
export type AuditEvent = {
  id: string;
  eventType: string;
  actorPersona: string;
  actorDisplay: string;
  targetType: string;
  targetId: string | null;
  occurredAt: string;
  payload: Record<string, unknown>;
};

export const auditApi = {
  forSpec: (specId: string) =>
    apiFetch<{ data: AuditEvent[] }>(`/api/v1/specs/${specId}/audit`),
  global: (params?: { type?: string; actor?: string; targetType?: string; limit?: number }) => {
    const q = new URLSearchParams();
    if (params?.type) q.set('type', params.type);
    if (params?.actor) q.set('actor', params.actor);
    if (params?.targetType) q.set('targetType', params.targetType);
    if (params?.limit) q.set('limit', String(params.limit));
    const suffix = q.toString() ? `?${q.toString()}` : '';
    return apiFetch<{ data: AuditEvent[] }>(`/api/v1/audit${suffix}`);
  },
};

export type MyReviewItem = {
  specId: string;
  subroutineId: string;
  subroutineName: string;
  corpusName: string;
  relativePath: string;
  state: string;
  updatedAt: string;
  routedAt: string;
  totalClaims: number;
  processedClaims: number;
  invariantsCount: number;
  edgeCaseCount: number;
  openQuestionCount: number;
  estimatedReviewMinutes: number;
  signature: {
    signedAt: string;
    signerDisplay: string;
    algorithm: string;
    specCanonicalHash: string;
  } | null;
};

export type MyReviewsResponse = {
  persona: string;
  counts: { awaiting: number; inProgress: number; signed: number };
  awaiting: MyReviewItem[];
  inProgress: MyReviewItem[];
  signed: MyReviewItem[];
};

export const myReviewsApi = {
  list: () => apiFetch<MyReviewsResponse>(`/api/v1/my-reviews`),
};
