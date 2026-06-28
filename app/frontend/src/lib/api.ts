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

// ─── Signature Health (value-add #8: drift detection) ───────────────
export type SignatureHealth = {
  specId: string;
  subroutineId: string;
  routineName: string;
  corpusId: string;
  corpusName: string;
  state: 'healthy' | 'drift' | 'unsigned';
  signedAt: string | null;
  signedSourceVersionId?: string;
  currentSourceVersionId?: string;
  signedSourceHash?: string;
  driftAgeDays?: number;
  signerDisplay?: string;
};

export type SignatureHealthPortfolio = {
  totalSigned: number;
  drifted: number;
  rows: SignatureHealth[];
};

// ─── Validation policy (value-add #7: three independent gates) ──────
export type ValidationGate = {
  id: string;
  label: string;
  description: string;
  required: boolean;
  coverageThreshold: string | null;
  retryPolicy: string;
  blockingCommitOnFailure: string;
};

export type ValidationPolicy = {
  scope: string;
  version: string;
  ownedBy: string;
  appliesTo: string;
  commitGate: { requireAllGreen: boolean; description: string };
  gates: ValidationGate[];
  retryDefaults: { transientFlakeWindow: string; autoRetryCount: number; note: string };
  overrideActive?: boolean;
  overrideUpdatedAt?: string | null;
  overrideUpdatedBy?: string | null;
};

export type ValidationPolicyOverride = {
  gates?: Array<Partial<ValidationGate> & { id: string }>;
  commitGate?: { requireAllGreen: boolean; description?: string };
  retryDefaults?: { transientFlakeWindow?: string; autoRetryCount: number; note?: string };
};

// ─── Personas & roles (value-add #5: clear separation of duties) ────
export type PersonaId = 'engineer' | 'sme' | 'observer' | 'admin';

export type PersonaDef = {
  id: PersonaId;
  displayName: string;
  charter: string;
  ownsStages: string[];
};

export type UserRow = {
  id: string;
  email: string;
  displayName: string;
  persona: PersonaId;
  createdAt: string;
  updatedAt: string;
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
  /** May be a single label or a list of corpora the schema was tuned against. */
  calibratedAgainst?: string | string[] | null;
  status: string | null;
  platformReadiness?: string | null;
  /** Effective enabled state (merged canonical + override). Defaults to true. */
  enabled?: boolean;
  /** True when an admin override is in place for this language. */
  overrideActive?: boolean;
};

export type SchemaOverrideRequest = {
  enabled?: boolean;
  displayName?: string;
  description?: string;
  status?: string;
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
  /** Raw markdown body of the prompt file (frontmatter + sections). */
  body?: string | null;
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

// ─── Golden dataset (Phase 6.0: prompt regression measurement) ──────
export type GoldenDatasetExpectedClaim = {
  kind: 'invariant' | 'section_contract' | 'io_side_effect' | 'edge_case' | 'open_question';
  id: string;
  pattern: string;
};

export type GoldenDatasetSummary = {
  id: string;
  entryId: string;
  schemaId: string;
  title: string;
  trapCategory: string;
  difficulty: string;
  status: string;
  sourcePath: string;
  sourceLines: string;
  expectedClaimCount: number;
  hasCanonicalInputs: boolean;
  notes: string;
  updatedAt: string;
  updatedBy: string | null;
  latestRun: {
    id: string;
    promptId: string;
    promptVersion: string;
    modelName: string;
    score: number;
    matched: number;
    total: number;
    completedAt: string;
  } | null;
};

export type GoldenDatasetEntry = {
  id: string;
  entryId: string;
  schemaId: string;
  title: string;
  trapCategory: string;
  difficulty: string;
  sourcePath: string;
  sourceLines: string;
  sourceContent: string;
  expectedClaims: GoldenDatasetExpectedClaim[];
  canonicalInputs: unknown[];
  notes: string;
  status: string;
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
};

export type GoldenDatasetUpsert = Partial<{
  entryId: string;
  schemaId: string;
  title: string;
  trapCategory: string;
  difficulty: string;
  sourcePath: string;
  sourceLines: string;
  sourceContent: string;
  expectedClaims: GoldenDatasetExpectedClaim[];
  canonicalInputs: unknown[];
  notes: string;
  status: string;
}>;

export type GoldenDatasetRun = {
  id: string;
  entryId: string;
  promptId: string;
  promptVersion: string;
  modelName: string;
  score: number;
  matched: number;
  total: number;
  detail: Array<{
    expectedClaimId: string;
    kind: string;
    pattern: string;
    matched: boolean;
    matchedAgainst: string | null;
  }>;
  startedAt: string;
  completedAt: string;
  triggeredBy: string | null;
};

export type GoldenDatasetRunSummary = {
  id: string;
  entryId: string;
  promptId: string;
  promptVersion: string;
  modelName: string;
  score: number;
  matched: number;
  total: number;
  completedAt: string;
};

export type GoldenDatasetScoreAll = {
  inputCount: number;
  aggregateMatched: number;
  aggregateTotal: number;
  aggregateScore: number;
  runs: GoldenDatasetRun[];
};

// ─── Dependency graph (Phase 8.0.a) ──────────────────────────────────
export type DependencyGraphNode = {
  id: string;
  name: string;
  sourcePath: string;
  state: string;
  specId: string | null;
  isRoot: boolean;
  isLeaf: boolean;
  sccId: string | null;
  calleeCount: number;
  callerCount: number;
};

export type DependencyGraphEdge = {
  from: string;
  to: string;
  type: 'call' | 'shared-storage';
  viaBlock: string | null;
};

export type DependencyGraphSCC = { id: string; members: string[] };

export type DependencyGraphResponse = {
  corpusId: string;
  sourceVersionId: string;
  nodes: DependencyGraphNode[];
  edges: DependencyGraphEdge[];
  externalCallees: string[];
  sccs: DependencyGraphSCC[];
  stats: {
    nodeCount: number;
    callEdgeCount: number;
    sharedStorageEdgeCount: number;
    sccCount: number;
    cyclicSccCount: number;
    externalCalleeCount: number;
    leafCount: number;
    rootCount: number;
  };
};

// ─── Portfolio dashboard (Phase 8.0.d) ───────────────────────────────
export type PortfolioCorpusRow = {
  corpusId: string;
  name: string;
  state: string;
  fileCount: number;
  totalLoc: number;
  lastActivity: string;
  counts: {
    total: number;
    signed: number;
    scaffolded: number;
    committed: number;
    draft: number;
    parsed: number;
  };
  planStatus: 'draft' | 'approved' | 'archived' | null;
  planId: string | null;
  totalWaves: number | null;
};

export type PortfolioActivity = {
  eventType: string;
  actorDisplay: string;
  actorPersona: string;
  targetType: string;
  targetId: string | null;
  occurredAt: string;
};

export type PortfolioSummaryResponse = {
  totals: {
    corpusCount: number;
    totalRoutines: number;
    signedCount: number;
    scaffoldedCount: number;
    committedCount: number;
    draftCount: number;
    parsedCount: number;
  };
  llmTotals: {
    callCount: number;
    inputTokens: number;
    outputTokens: number;
    costUsd: number;
  };
  corpora: PortfolioCorpusRow[];
  recent: PortfolioActivity[];
};

// ─── Per-routine migration context (Phase 8.0.c) ─────────────────────
export type BlastRadiusEntry = {
  id: string;
  name: string;
  state: string;
  reason: 'direct-caller' | 'transitive-caller' | 'shared-storage' | 'direct-caller+shared-storage';
  sharedBlocks: string[];
};

export type BlastRadiusResponse = {
  subroutineId: string;
  subroutineName: string;
  directCallerCount: number;
  transitiveCallerCount: number;
  sharedStorageConsumerCount: number;
  stateBreakdown: { signed: number; scaffolded: number; draft: number; parsed: number };
  affected: BlastRadiusEntry[];
};

export type MigrationReadinessResponse = {
  subroutineId: string;
  subroutineName: string;
  classification: 'safe-leaf' | 'safe-with-deps-done' | 'blocked-on-deps' | 'coordinated-only';
  reasons: string[];
  blockingRoutineIds: string[];
  calleeCount: number;
  callerCount: number;
  sharedBlockNames: string[];
};

export type WaveAssignmentResponse = {
  planId: string;
  planStatus: 'draft' | 'approved' | 'archived';
  strategyName: string;
  waveNumber: number;
  totalWaves: number;
  waveName: string;
};

// ─── Migration plan strategies (Phase 8.0.e) ─────────────────────────
export type MigrationPlanStrategy = {
  name: string;
  description: string;
  isDefault: boolean;
  optionsSchema: Record<string, unknown>;
};

// ─── Migration plan (Phase 8.0.b) ────────────────────────────────────
export type MigrationPlanSummary = {
  id: string;
  corpusId: string;
  sourceVersionId: string;
  status: 'draft' | 'approved' | 'archived';
  strategyName: string;
  totalRoutines: number;
  totalWaves: number;
  summary: string;
  generatedBy: string | null;
  approvedBy: string | null;
  createdAt: string;
  approvedAt: string | null;
  archivedAt: string | null;
};

export type MigrationPlanWaveRoutine = {
  id: string;
  name: string;
  state: string;
  specId: string | null;
};

export type MigrationPlanWave = {
  id: string;
  waveNumber: number;
  name: string;
  status: 'planned' | 'in_progress' | 'completed';
  routineCount: number;
  liveCounts: { signed: number; scaffolded: number; committed: number };
  routines: MigrationPlanWaveRoutine[];
};

export type MigrationPlanDetail = {
  plan: MigrationPlanSummary;
  waves: MigrationPlanWave[];
};

// ─── Harmonisation (Phase 7.1: cross-routine consistency check) ──────
export type HarmonisationRunSummary = {
  id: string;
  corpusId: string;
  sourceVersionId: string;
  status: 'RUNNING' | 'COMPLETED' | 'FAILED';
  promptId: string;
  promptVersion: string;
  modelName: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  specCount: number;
  findingCount: number;
  summary: string;
  errorMessage: string | null;
  triggeredBy: string | null;
  startedAt: string;
  completedAt: string;
};

export type HarmonisationFinding = {
  id: string;
  harmonisationRunId: string;
  category: string;
  severity: 'low' | 'medium' | 'high';
  title: string;
  detail: string;
  affectedSpecIds: string[];
  status: 'open' | 'accepted' | 'dismissed';
  adminNote: string | null;
  createdAt: string;
  updatedAt: string;
  updatedBy: string | null;
};

export type HarmonisationRunDetail = {
  run: HarmonisationRunSummary;
  findings: HarmonisationFinding[];
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
  /**
   * Phase 10.1.b.2 — dominant source language across the corpus's
   * LATEST SourceVersion. Returns a SourceLanguageDetector constant
   * ('fortran-f77' / 'cobol' / 'delphi' / 'cpp' / 'vb6'); null on
   * ties or empty corpora.
   */
  sourceLanguage: string | null;
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
  /**
   * Phase 10.1.b.1 — spec-schema id detected at ingest (one of the
   * SourceLanguageDetector constants: 'fortran-f77', 'cobol', 'delphi',
   * 'cpp', 'vb6'). Already projected by SubroutineEndpoints.cs:123;
   * the previous frontend type just omitted it, forcing surrounding
   * pages to guess from `corpus.name` regex. Nullable for forward-
   * compat with subroutines parsed before this column was populated.
   */
  sourceLanguage: string | null;
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
  // Phase #4.4 admin override (admin-only)
  putSchemaOverride: (id: string, body: SchemaOverrideRequest) =>
    apiFetch<SpecSchemaSummary>(`/api/v1/spec-schemas/${id}/override`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  revertSchemaOverride: (id: string) =>
    apiFetch<void>(`/api/v1/spec-schemas/${id}/override`, { method: 'DELETE' }),

  // Phase #4 / value-add #5: persona model & permissions matrix
  listPersonas: () => apiFetch<{ data: PersonaDef[] }>('/api/v1/personas'),
  getPersonaMatrix: () => apiFetch<PersonaMatrix>('/api/v1/personas/matrix'),

  // Phase #4 / users CRUD (admin-only)
  listUsers: () => apiFetch<{ data: UserRow[] }>('/api/v1/users'),
  createUser: (body: { email: string; displayName: string; persona: PersonaId }) =>
    apiFetch<UserRow>('/api/v1/users', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  updateUserPersona: (id: string, persona: PersonaId) =>
    apiFetch<{ id: string; persona: PersonaId; previousPersona?: PersonaId }>(
      `/api/v1/users/${id}/persona`,
      { method: 'PUT', body: JSON.stringify({ persona }) },
    ),
  deleteUser: (id: string) =>
    apiFetch<void>(`/api/v1/users/${id}`, { method: 'DELETE' }),

  // Phase #4 / value-add #7: validation policy
  getValidationPolicy: () => apiFetch<ValidationPolicy>('/api/v1/validation/policy'),
  updateValidationPolicy: (body: ValidationPolicyOverride) =>
    apiFetch<ValidationPolicy>('/api/v1/validation/policy', {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  revertValidationPolicy: () =>
    apiFetch<void>('/api/v1/validation/policy/override', { method: 'DELETE' }),

  // Phase #4 / value-add #8: signature health (drift detection)
  getSignatureHealth: (specId: string) =>
    apiFetch<SignatureHealth>(`/api/v1/specs/${specId}/signature-health`),
  getSignatureHealthPortfolio: () =>
    apiFetch<SignatureHealthPortfolio>('/api/v1/signature-health'),
  // Phase #4.5 admin re-verify actions
  reverifySpec: (specId: string) =>
    apiFetch<{ specId: string; state: string; previousSourceVersionId: string; newSourceVersionId: string; routineName: string }>(
      `/api/v1/specs/${specId}/re-verify`,
      { method: 'POST' },
    ),
  reverifyAllDrifted: () =>
    apiFetch<{ resetCount: number; specIds: string[] }>(
      '/api/v1/signature-health/re-verify-all',
      { method: 'POST' },
    ),

  // Phase #4 / value-add #2: prompt library
  listPrompts: () => apiFetch<{ data: PromptSummary[] }>('/api/v1/prompts'),
  getPrompt: (source: string, target: string, kind: string, version?: string) =>
    apiFetch<PromptDetail>(
      version
        ? `/api/v1/prompts/${source}/${target}/${kind}/${version}`
        : `/api/v1/prompts/${source}/${target}/${kind}`,
    ),

  // Phase #4.3 admin mutations (filesystem-backed)
  createPrompt: (body: {
    sourceSchema: string;
    targetStack: string;
    kind: string;
    version: string;
    markdown: string;
  }) =>
    apiFetch<PromptDetail>('/api/v1/prompts', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  updatePrompt: (source: string, target: string, kind: string, version: string, markdown: string) =>
    apiFetch<PromptDetail>(
      `/api/v1/prompts/${source}/${target}/${kind}/${version}`,
      { method: 'PUT', body: JSON.stringify({ markdown }) },
    ),
  deletePrompt: (source: string, target: string, kind: string, version: string) =>
    apiFetch<void>(
      `/api/v1/prompts/${source}/${target}/${kind}/${version}`,
      { method: 'DELETE' },
    ),

  // Phase #4 / value-add #3: scaffold archetype catalog
  listArchetypes: () => apiFetch<{ data: ArchetypeManifest[] }>('/api/v1/archetypes'),

  // Phase 6.0 — golden dataset (calibration corpus for prompt regression)
  listGoldenDataset: (params?: { schemaId?: string; status?: string }) => {
    const qs = new URLSearchParams();
    if (params?.schemaId) qs.set('schemaId', params.schemaId);
    if (params?.status) qs.set('status', params.status);
    const q = qs.toString();
    return apiFetch<{ data: GoldenDatasetSummary[] }>(
      `/api/v1/golden-dataset${q ? `?${q}` : ''}`,
    );
  },
  getGoldenDatasetEntry: (entryId: string) =>
    apiFetch<GoldenDatasetEntry>(`/api/v1/golden-dataset/${encodeURIComponent(entryId)}`),
  createGoldenDatasetEntry: (body: GoldenDatasetUpsert) =>
    apiFetch<GoldenDatasetEntry>('/api/v1/golden-dataset', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  updateGoldenDatasetEntry: (entryId: string, body: GoldenDatasetUpsert) =>
    apiFetch<GoldenDatasetEntry>(`/api/v1/golden-dataset/${encodeURIComponent(entryId)}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  deleteGoldenDatasetEntry: (entryId: string) =>
    apiFetch<void>(`/api/v1/golden-dataset/${encodeURIComponent(entryId)}`, {
      method: 'DELETE',
    }),
  scoreGoldenDatasetEntry: (entryId: string) =>
    apiFetch<GoldenDatasetRun>(
      `/api/v1/golden-dataset/${encodeURIComponent(entryId)}/score`,
      { method: 'POST' },
    ),
  scoreGoldenDatasetAll: (schemaId?: string) => {
    const q = schemaId ? `?schemaId=${encodeURIComponent(schemaId)}` : '';
    return apiFetch<GoldenDatasetScoreAll>(
      `/api/v1/golden-dataset/score-all${q}`,
      { method: 'POST' },
    );
  },
  // Phase 8.0.a — dependency graph
  getDependencyGraph: (corpusId: string) =>
    apiFetch<DependencyGraphResponse>(
      `/api/v1/corpora/${encodeURIComponent(corpusId)}/dependency-graph`,
    ),

  // Phase 8.0.c — per-routine migration context
  getBlastRadius: (subroutineId: string) =>
    apiFetch<BlastRadiusResponse>(
      `/api/v1/subroutines/${encodeURIComponent(subroutineId)}/blast-radius`,
    ),
  getMigrationReadiness: (subroutineId: string) =>
    apiFetch<MigrationReadinessResponse>(
      `/api/v1/subroutines/${encodeURIComponent(subroutineId)}/migration-readiness`,
    ),
  getWaveAssignment: (subroutineId: string) =>
    apiFetch<WaveAssignmentResponse>(
      `/api/v1/subroutines/${encodeURIComponent(subroutineId)}/wave-assignment`,
    ),

  // Phase 8.0.b/e — migration plan (strategy + per-strategy options)
  generateMigrationPlan: (
    corpusId: string,
    body?: { strategy?: string; options?: Record<string, unknown> },
  ) =>
    apiFetch<MigrationPlanSummary>(
      `/api/v1/corpora/${encodeURIComponent(corpusId)}/migration-plan/generate`,
      { method: 'POST', body: JSON.stringify(body ?? {}) },
    ),
  listMigrationPlanStrategies: () =>
    apiFetch<{ data: MigrationPlanStrategy[] }>('/api/v1/migration-plan-strategies'),
  approveMigrationPlan: (planId: string) =>
    apiFetch<MigrationPlanSummary>(
      `/api/v1/migration-plans/${encodeURIComponent(planId)}/approve`,
      { method: 'POST' },
    ),
  archiveMigrationPlan: (planId: string) =>
    apiFetch<MigrationPlanSummary>(
      `/api/v1/migration-plans/${encodeURIComponent(planId)}/archive`,
      { method: 'POST' },
    ),
  getCurrentMigrationPlan: (corpusId: string) =>
    apiFetch<MigrationPlanDetail>(
      `/api/v1/corpora/${encodeURIComponent(corpusId)}/migration-plan`,
    ),
  getMigrationPlan: (planId: string) =>
    apiFetch<MigrationPlanDetail>(
      `/api/v1/migration-plans/${encodeURIComponent(planId)}`,
    ),

  // Phase 8.0.d — portfolio dashboard (cross-corpus aggregates, admin only)
  getPortfolioSummary: () =>
    apiFetch<PortfolioSummaryResponse>(`/api/v1/platform/portfolio-summary`),

  // Phase 7.1 — cross-routine harmonisation
  runHarmonisation: (corpusId: string) =>
    apiFetch<HarmonisationRunSummary>(`/api/v1/corpora/${encodeURIComponent(corpusId)}/harmonise`, {
      method: 'POST',
    }),
  listHarmonisationRuns: (corpusId: string, limit?: number) => {
    const q = limit ? `?limit=${limit}` : '';
    return apiFetch<{ data: HarmonisationRunSummary[] }>(
      `/api/v1/corpora/${encodeURIComponent(corpusId)}/harmonisation${q}`,
    );
  },
  getHarmonisationRun: (runId: string) =>
    apiFetch<HarmonisationRunDetail>(`/api/v1/harmonisation/runs/${encodeURIComponent(runId)}`),
  updateHarmonisationFinding: (findingId: string, body: { status: string; adminNote?: string }) =>
    apiFetch<HarmonisationFinding>(
      `/api/v1/harmonisation/findings/${encodeURIComponent(findingId)}`,
      { method: 'PUT', body: JSON.stringify(body) },
    ),

  listGoldenDatasetRuns: (params?: { entryId?: string; promptId?: string; limit?: number }) => {
    const qs = new URLSearchParams();
    if (params?.entryId) qs.set('entryId', params.entryId);
    if (params?.promptId) qs.set('promptId', params.promptId);
    if (params?.limit) qs.set('limit', String(params.limit));
    const q = qs.toString();
    return apiFetch<{ data: GoldenDatasetRunSummary[] }>(
      `/api/v1/golden-dataset/runs${q ? `?${q}` : ''}`,
    );
  },

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
  // Phase 9.3.b — 4th gate (property-based testing / falsifying-input search).
  // v1 runs in "shadow mode" — sidecar exercises the spec end-to-end but the
  // callback returns agree=true without ref-vs-candidate comparison.
  validateFalsifying: (scaffoldId: string) =>
    apiFetch<ValidationRun>(`/api/v1/scaffolds/${scaffoldId}/validate/falsifying`, { method: 'POST' }),
  getValidationRunLog: async (runId: string): Promise<string> => {
    const headers = new Headers();
    headers.set('X-Dev-Persona', getPersona());
    const res = await fetch(`${API_BASE}/api/v1/validation-runs/${runId}/log`, { headers });
    if (!res.ok) throw new ApiError(res.status, 'http.error', `Could not fetch log (${res.status})`);
    return await res.text();
  },

  // Phase 11.0 — Documentation review
  getDocSummary: (corpusId: string) =>
    apiFetch<DocSummary>(`/api/v1/corpora/${corpusId}/docs/summary`),
  listDocSections: (corpusId: string, params?: { kind?: string; state?: string; page?: number; pageSize?: number }) => {
    const q = new URLSearchParams();
    if (params?.kind) q.set('kind', params.kind);
    if (params?.state) q.set('state', params.state);
    if (params?.page) q.set('page', String(params.page));
    if (params?.pageSize) q.set('pageSize', String(params.pageSize));
    const qs = q.size ? `?${q.toString()}` : '';
    return apiFetch<DocSectionListResult>(`/api/v1/corpora/${corpusId}/docs/sections${qs}`);
  },
  getDocSection: (sectionId: string) =>
    apiFetch<DocSectionDetail>(`/api/v1/docs/sections/${sectionId}`),
  acceptDocSection: (sectionId: string) =>
    apiFetch<{ id: string; state: string; updatedAt: string }>(
      `/api/v1/docs/sections/${sectionId}/accept`, { method: 'POST' }
    ),
  rejectDocSection: (sectionId: string) =>
    apiFetch<void>(`/api/v1/docs/sections/${sectionId}`, { method: 'DELETE' }),

  // Phase 11.0.g — documentation export.
  // Returns a Blob + the server-provided filename (from Content-Disposition).
  // Formats: mkdocs (zip), docx, pdf, confluence (json).
  exportDocs: async (corpusId: string, format: string): Promise<{ blob: Blob; filename: string }> => {
    const headers = new Headers();
    headers.set('X-Dev-Persona', getPersona());
    const res = await fetch(
      `${API_BASE}/api/v1/corpora/${encodeURIComponent(corpusId)}/docs/export?format=${encodeURIComponent(format)}`,
      { headers }
    );
    if (!res.ok) {
      const text = await res.text().catch(() => '');
      throw new ApiError(res.status, 'docs.export.failed', `Export failed (${res.status}): ${text}`);
    }
    const disposition = res.headers.get('Content-Disposition') ?? '';
    const match = disposition.match(/filename="([^"]+)"/);
    const filename = match?.[1] ?? `docs-export-${format}`;
    const blob = await res.blob();
    return { blob, filename };
  },
};

// ─── Validation types ─────────────────────────────────────────────────

export type ValidationStage = 'COMPILE' | 'TEST_PACK' | 'EQUIVALENCE' | 'FALSIFYING';
export type ValidationStatus = 'RUNNING' | 'PASSED' | 'FAILED' | 'ERRORED';

// Phase 9.3.b/c — per-claim metrics shape on a FALSIFYING ValidationRun.
// The drilldown panel reads this when run.metrics.perClaim is present.
export type FalsifyingPerClaim = {
  claimId: string;
  examplesTried: number;
  falsifying: Record<string, unknown> | null;
  refOutput: string | null;
  candOutput: string | null;
  elapsedMs: number;
  timedOut: boolean;
  callbackErrors: number;
  skipReason: string | null;
};

export type FalsifyingMetrics = {
  mode?: 'shadow' | 'live';  // v1 always "shadow"; v1.1 introduces "live"
  claimsExercised: number;
  falsifyingClaimIds: string[];
  totalExamplesTried: number;
  perClaim: FalsifyingPerClaim[];
  overallFalsified: boolean;
  totalElapsedMs?: number;
};

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

// ─── Phase 11.0 — Documentation types ────────────────────────────────
export type DocSectionSummaryItem = {
  id: string;
  kind: string;
  scope: string;
  state: string;
  moduleName?: string;
  subroutineId?: string;
  subroutineName?: string;
  generationRunId?: string;
  createdAt: string;
  updatedAt: string;
};

export type DocSectionDetail = DocSectionSummaryItem & {
  corpusId: string;
  sourceVersionId: string;
  renderedMarkdown?: string;
  payload: Record<string, unknown>;
  llmCallId?: string;
};

export type DocSectionListResult = {
  total: number;
  page: number;
  pageSize: number;
  data: DocSectionSummaryItem[];
};

export type DocSummaryBreakdown = { kind: string; state: string; count: number };
export type DocSummary = { corpusId: string; breakdown: DocSummaryBreakdown[] };
