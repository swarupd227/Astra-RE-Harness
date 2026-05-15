# Architecture Overview

**Status:** Kickoff (v0.1)
**Companion:** `00_DevelopmentPlan.md`, `data-model.md`, `api-surface.md`, `llm-integration.md`, `security-and-residency.md`

This document describes the *runtime shape* of the Harness: how the pieces fit, how data flows, and what state lives where. Decisions inherit from the source spec sections 3, 5, 6, 8, 9.

---

## 1. System context

```
┌────────────────────────────────────────────────────────────────────┐
│                         NOUS AZURE SUBSCRIPTION                     │
│                                                                     │
│   ┌────────────┐                                                   │
│   │  Browser   │                                                   │
│   │ (Engineer/ │                                                   │
│   │  SME/Obs)  │                                                   │
│   └─────┬──────┘                                                   │
│         │ HTTPS + SSE                                              │
│         ▼                                                          │
│  ┌──────────────┐    ┌──────────────────┐                         │
│  │ App Gateway  │───▶│  Frontend SPA    │  React 18 + TS + Vite  │
│  │   + WAF      │    │  (static, ACR)   │  (TanStack Query,       │
│  └──────┬───────┘    └──────────────────┘   Monaco, shadcn/ui)    │
│         │                                                          │
│         ▼                                                          │
│  ┌──────────────────────────────────────────────────────────┐     │
│  │  API service (.NET 8, AKS)                               │     │
│  │  · Minimal APIs · MediatR · FluentValidation · EF Core    │     │
│  │  · OIDC bearer JWT · OpenTelemetry                        │     │
│  └──┬───────────────┬───────────────────────────────────┬───┘     │
│     │               │                                   │         │
│     ▼               ▼                                   ▼         │
│  ┌──────────┐  ┌──────────────┐                  ┌──────────┐    │
│  │ Postgres │  │ Hangfire     │                  │  Blob    │    │
│  │  16      │  │ workers      │                  │ Storage  │    │
│  │ (op DB)  │  │ (AKS pool)   │                  │ (immut.  │    │
│  └──────────┘  └──┬─────┬─────┘                  │ for      │    │
│                   │     │                         │ signed)  │    │
│                   │     │                         └──────────┘    │
│                   │     │                                         │
│      ┌────────────┘     └────────────┐                            │
│      ▼                               ▼                            │
│  ┌──────────────┐               ┌──────────────┐                 │
│  │ Fortran      │               │ LLM provider │                 │
│  │ parser       │               │ abstraction  │                 │
│  │ sidecar      │               │ (ILlmProvider)│                │
│  │ (Python +    │               └──┬────────┬──┘                 │
│  │  fparser2)   │                  │        │                    │
│  └──────────────┘                  │        │                    │
│                                    ▼        ▼                    │
│                       ┌──────────────┐ ┌──────────────┐          │
│                       │  Anthropic   │ │ Azure OpenAI │          │
│                       │  Claude (ZDR)│ │  GPT-4o (ZDR)│          │
│                       └──────────────┘ └──────────────┘          │
│                                                                  │
│   Identity: Microsoft Entra ID (OIDC) ──────► All clients/svcs   │
│   Secrets: Azure Key Vault Managed HSM (signing keys)            │
│   Observability: OpenTelemetry collector → Azure Monitor         │
│   Git: Octokit (.NET) — read source repos, write scaffold repos  │
└──────────────────────────────────────────────────────────────────┘
```

Single tenant. Single region (primary). One Azure subscription. AKS as the compute substrate. PostgreSQL as the operational database. Blob Storage as the artifact store.

---

## 2. Component responsibilities

### 2.1 Frontend SPA

- React 18, TypeScript, Vite. No SSR — internal tool, internal users.
- TanStack Query for all server state (cache, invalidation, polling).
- Monaco Editor with a custom Fortran language definition (tokenizer, comment style, subroutine fold ranges) and a custom citation-pulse decoration. Reused across Stage 1 source viewer, Stage 3 streaming view, Stage 4 review pane, and Stage 5 scaffold viewer.
- Tailwind CSS + shadcn/ui as the only component vendor. No additional UI kit.
- Auth.js with Entra ID provider for the OIDC flow. JWT held in memory, refreshed silently on the IdP-managed schedule.
- SSE consumer for streaming endpoints (extraction, scaffold). EventSource polyfill not required — modern browsers only.

### 2.2 API service

- .NET 8 minimal APIs. C#. EF Core for persistence. MediatR for command/query separation. FluentValidation on every request DTO.
- Stateless. Horizontally scalable. Behind Azure App Gateway with WAF.
- Authentication: bearer JWT, audience + issuer matched to Harness configuration.
- Authorization: persona claim → policy. Policies declared per endpoint (`[RequireEngineer]`, `[RequireSME]`, etc.).
- Telemetry: OpenTelemetry .NET, structured Serilog → JSON. Per-LLM-call span carries provider, model, prompt template ID + version, token counts, latency, cost.
- The API is the *only* external surface. Workers do not accept inbound traffic.

### 2.3 Hangfire workers

All long-running operations are Hangfire jobs:

| Job | Triggered by | Idempotent? | Notes |
|---|---|---|---|
| `IngestSourceJob` | `POST /corpora` | Yes (by corpus ID) | Pulls Git repo or processes uploaded blobs; hashes; persists. |
| `ParseCorpusJob` | Auto after ingest | Yes (by source-version ID) | Calls fparser2 sidecar per file; persists AST + structural index. |
| `ExtractSpecJob` | `POST /subroutines/{id}/extract` | No (each call is a new attempt) | Calls Anthropic via `ILlmProvider`; streams tokens to API which fans out via SSE; persists Spec on success. |
| `GenerateScaffoldJob` | `POST /specs/{id}/scaffold` | No | Calls Azure OpenAI; validates output schema; persists package. |
| `CommitScaffoldJob` | `POST /scaffolds/{id}/commit` | No | Octokit push to scaffold-output repo; tags with spec ID + source hash. |
| `ProviderConfigAuditJob` | Cron, daily | Yes | Pulls latest provider audit-letter timestamps; updates admin dashboard. |
| `CostRollupJob` | Cron, hourly | Yes | Aggregates `LlmCall` rows; updates dashboard tables. |

Hangfire's persistence (PostgreSQL extension) means in-flight jobs survive worker restarts — the persistence point is a state-machine checkpoint, not a memory variable. Jobs are designed to be **resumable from their last persisted state**, not from scratch.

### 2.4 Fortran parser sidecar

- Python service running fparser2 (mature, widely used Fortran 77/90/95 AST library — no stable .NET port).
- Exposed over gRPC (single endpoint: `Parse(SourceFile) → AstResult`). gRPC chosen over HTTP for binary AST efficiency and built-in streaming.
- Containerized; runs in the same AKS namespace as the workers; horizontally scalable.
- The sidecar is **stateless** — no source persistence, no caching. Workers feed it source bytes and receive AST artifacts.
- An ADR is due in week 7 to confirm fparser2 covers Kiwiplan dialect; a small spike in week 1 retires the worst risk.

### 2.5 LLM provider abstraction

```csharp
public interface ILlmProvider
{
    string Name { get; } // "anthropic" | "azure_openai"
    IAsyncEnumerable<LlmResponseChunk> InvokeAsync(LlmRequest req, CancellationToken ct);
}
```

Two adapters in v1: `AnthropicProvider`, `AzureOpenAIProvider`. Both **must** be configured for zero data retention; the configuration version is recorded on every `LlmCall` row.

Routing rules live in the database (`prompt_routing` table), keyed by `(stage, prompt_template_id)`. Admin can edit at runtime; engineer sees the resolved rule on the live extraction screen as transparency.

### 2.6 Persistence

- **PostgreSQL 16** for operational data: corpora, source versions, subroutines, specs, revisions, claim reviews, signatures, scaffolds, LLM calls, audit events, comments, users, credentials. Schema lives in EF Core migrations under `src/api/Migrations`. Migrations are applied as the first step of any deploy.
- **Blob Storage**:
  - `sources` container — raw Fortran files, AST artifacts. Versioned.
  - `signed-specs` container — **immutable** (write-once-read-many). Holds canonicalized signed spec JSON + signature manifest. Immutability policy applied via IaC; verified in a CI gate.
  - `scaffolds` container — generated code packages.
  - `llm-debug-restricted` container — full request/response capture for failure debugging. 7-day retention. Admin-only access. Reads produce an audit event.
- **Azure Key Vault Managed HSM** — signing keys, one per environment (DEV / STAGING / PROD). The DEV key is created in week 1 and used by the test sign-off path. Key rotation is a documented runbook, not an automated job.

---

## 3. Data flow per stage

```
INGEST                PARSE                EXTRACT              REVIEW                SIGN                  SCAFFOLD              COMMIT
──────                ─────                ───────              ──────                ────                  ────────              ──────
 Git URL  ─pull──▶  Source bytes ─AST─▶ Subroutine ─prompt─▶ DRAFT spec ─claim─▶  IN_REVIEW ─sign─▶ SIGNED + sig ─prompt─▶ Scaffold ─push─▶ Git
   │                                       source +              │                  ops              │                       │             │
   │                                       AST node              │                                   │                       │             │
   ▼                                                             ▼                                   ▼                       ▼             ▼
 Blob:                                                       Postgres + LLM      Postgres +    Immutable blob +        Postgres +     Project 3
 sources                                                     audit row            ClaimReview  HSM signature row       scaffolds      scaffold-out
```

Forward-only state machine. The only "backward" transitions modeled (per spec §3.3) are retries and rejections, never sign-reversal. SUPERSEDED is set when the underlying source version changes; the prior signed spec is kept for audit.

---

## 4. Environments

| Environment | Purpose | LLM providers | HSM | Source repo | Scaffold repo |
|---|---|---|---|---|---|
| DEV | Engineer dev loop, integration tests | Anthropic + Azure OpenAI (real, ZDR) | Test HSM key | Synthetic + opt-in real Kiwiplan corpus | Stub repo |
| STAGING | Demo rehearsals, UAT, perf testing | Real ZDR endpoints | Staging HSM key | Real Kiwiplan source (read-only) | Real scaffold-output repo |
| PROD | Phase 1 production use | Real ZDR endpoints | PROD HSM key | Real Kiwiplan source | Real scaffold-output repo |

DEV is provisioned in week 1. STAGING in week 5. PROD in week 14.

Promotion model: container image is built once, promoted across environments via Helm value overrides. Migrations run as a Helm pre-install / pre-upgrade hook.

---

## 5. Cross-cutting concerns

### 5.1 Audit trail

Every meaningful state change writes an `AuditEvent` row. Append-only. No deletes. The trail is the single source of truth for compliance and SOC 2 evidence. Rendered to the user in Screen 4.3 with timeline grouping.

### 5.2 Observability

- **Tracing.** Every API request is a span. Every Hangfire job is a span. Every LLM call is a span with the attribute schema in spec §6.4.
- **Metrics.** Custom metrics: `llm.calls`, `llm.cost_usd`, `extract.duration_ms`, `sign.duration_ms`, `claim.action_count` (tagged accept/edit/reject/question).
- **Logs.** JSON-structured. PII fields tagged for the logging middleware to scrub. Source code is *never* logged — only subroutine IDs and metadata.
- **Dashboards.** Three dashboards in Azure Monitor: *operational health*, *LLM cost & latency*, *pipeline throughput* (subroutines per state, time-in-state). All three live by end of Phase B.
- **Alerts.** Per spec §6.4 thresholds: error rate >5% over 15-min window, p99 latency >60s, daily cost >120% of 7-day average. PagerDuty integration.

### 5.3 Security

Detailed in `security-and-residency.md`. Headlines:

- TLS everywhere in transit. AES-256 at rest. WAF on App Gateway.
- Bearer JWT with audience + issuer match. Sign-off requires `auth_time` ≤ 5 min.
- Service-account credentials for Git in Key Vault; rotated quarterly.
- LLM provider credentials admin-managed; never returned in API responses.
- Threat model + pen test in Phase D.

### 5.4 Resilience

- **In-flight jobs survive worker restarts** — Hangfire persistence + state-machine checkpoints.
- **In-flight LLM calls** — if the SSE connection drops, the backend continues processing; the engineer reload checks final state. Streamed tokens are buffered server-side until consumed or until the call completes.
- **Database** — hot-standby with logical replication; RPO 15 min.
- **Blob storage** — geo-redundant (RA-GRS) for `signed-specs` and `sources`.
- **Provider failover** — primary/fallback per template; trigger on 5xx, rate-limit, >120s timeout. Admin-toggleable; off by default during the demo.

---

## 6. Integration with Project 3

- **Outbound only.** The Harness pushes scaffold artifacts to a Project-3-owned Git repository; Project 3 CI picks them up.
- **No reverse channel.** The Harness does not consume anything from Project 3 at runtime.
- **Spec verification.** Project 3 CI verifies the HSM signature on every signed spec before building. The Harness publishes the public verification key per environment.
- **Issue dispatch.** Open questions (Stage 4 artifact) can be dispatched to Project 3's GitHub Issues tracker, one issue per question, labelled `sme-question`.

---

## 7. What we are *not* building

- A separate ops console for admin (the spec mentions one but defers it to v1.x — admin functions in v1 ship as a small set of pages within the same SPA, gated by the Admin persona).
- A multi-tenant control plane.
- A non-Fortran ingestion path.
- A direct-to-customer deployment surface.
- A general-purpose LLM playground inside the product.

Each of these is captured in `00_DevelopmentPlan.md` §9.
