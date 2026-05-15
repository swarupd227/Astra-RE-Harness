# API Surface

**Status:** Kickoff (v0.1)
**Companion:** `architecture-overview.md`, `data-model.md`
**Source:** Spec §7 (endpoint table verbatim), expanded with request/response shape, error model, and SSE protocol.

---

## 1. Conventions

- **Base URL:** `/api/v1`. The version is part of the path. Breaking changes go to `/api/v2`.
- **Auth:** Bearer JWT in `Authorization` header. Issued by Microsoft Entra ID (or a magic-link exchange for SMEs in Phase C).
- **Persona policies:** Each endpoint declares allowed personas; API rejects with `403` if the JWT subject's persona does not match.
- **Content type:** `application/json` for requests and responses. `text/event-stream` for streaming endpoints.
- **Pagination:** `?cursor=<opaque>&limit=<n>` (max 100). Response: `{ data: [...], next_cursor: "..." | null }`.
- **Validation:** FluentValidation on every command DTO. Errors return `400` with the structured error model below.
- **Idempotency:** Endpoints that trigger jobs accept an optional `Idempotency-Key` header. Repeated calls with the same key return the original 202 result for 24 hours.
- **Tracing:** Every response carries `traceparent` for cross-system correlation.

---

## 2. Error model

```json
{
  "error": {
    "code": "spec.sign.preconditions_unmet",
    "message": "Sign-off requires every claim to be processed and every open question resolved.",
    "details": [
      { "claim_path": "$.invariants[?(@.id=='INV-3')]", "issue": "untouched" },
      { "claim_path": "$.open_questions[?(@.id=='Q-1')]", "issue": "unresolved" }
    ],
    "trace_id": "..."
  }
}
```

`code` is hierarchical and stable. Examples: `corpus.create.duplicate_name`, `extract.provider.rate_limited`, `spec.sign.reauth_required`, `auth.persona_denied`.

---

## 3. Endpoint reference

### 3.1 Corpora — Stage 1

| Method | Path | Body / Query | Returns | Persona |
|---|---|---|---|---|
| POST | `/corpora` | `{ name, source_type: "git"\|"upload", source_url?, branch?, source_root?, credential_id? }` (Git); for upload, multipart body | `202 { id, state }` | Engineer |
| GET | `/corpora` | `?state=&type=&q=&cursor=&limit=` | `200 { data: Corpus[], next_cursor }` | All |
| GET | `/corpora/{id}` | — | `200 Corpus` | All |
| GET | `/corpora/{id}/state` | — | `200 { state, progress?, last_event_at }` (lightweight poll) | All |
| POST | `/corpora/{id}/sync` | `{}` | `202 { source_version_id, state }` | Engineer |
| POST | `/corpora/{id}/archive` | `{}` | `204` | Engineer |

### 3.2 Subroutines — Stage 2

| Method | Path | Body / Query | Returns | Persona |
|---|---|---|---|---|
| GET | `/corpora/{id}/subroutines` | filters per spec §4.2 (state, file, has-COMMON, has-ISAM, etc.) | `200 { data: Subroutine[], next_cursor }` | All |
| GET | `/subroutines/{id}` | — | `200 Subroutine` (with AST data, current state) | All |
| GET | `/subroutines/{id}/call-graph` | — | `200 { nodes: [...], edges: [...] }` | All |

### 3.3 Extraction — Stage 3

| Method | Path | Body | Returns | Persona |
|---|---|---|---|---|
| POST | `/subroutines/{id}/extract` | `{ prompt_template_id?: string, prompt_template_version?: string }` | `202 { extraction_id, sse_url }` | Engineer |
| GET | `/extractions/{id}/stream` | — | `text/event-stream` (see §4) | Engineer |
| POST | `/extractions/{id}/cancel` | `{}` | `204` | Engineer |
| GET | `/extractions/{id}` | — | `200 { id, status, llm_call_id, spec_id?, error? }` | All |

### 3.4 Specs — Stage 4

| Method | Path | Body | Returns | Persona |
|---|---|---|---|---|
| GET | `/specs/{id}` | — | `200 Spec` (with `spec_json`, state, metadata) | All |
| PATCH | `/specs/{id}` | RFC 6902 JSON Patch | `200 Spec` (new revision created) | Engineer (DRAFT) / SME (IN_REVIEW) |
| POST | `/specs/{id}/route` | `{ reviewer_ids: uuid[], routing_note: string }` | `200 { state: "IN_REVIEW" }` | Engineer |
| POST | `/specs/{id}/claims/{path}/review` | `{ action: "accept"\|"edit"\|"reject"\|"question", reason?, edited_text?, edited_citation? }` | `200 ClaimReview` | SME |
| POST | `/specs/{id}/sign` | `{ confirmation: "I have reviewed every claim and confirm this spec is accurate to the source as of version {version}" }` | `200 { signature, state: "SIGNED" }` | SME (re-auth ≤ 5 min) |
| GET | `/specs/{id}/audit` | `?from=&to=&type=&actor=&q=&cursor=&limit=` | `200 { data: AuditEvent[], next_cursor }` | All |
| GET | `/specs/{id}/comments` | — | `200 { threads: Comment[] }` | All |
| POST | `/specs/{id}/comments` | `{ parent_id?, body, target?: { type: "claim", path } }` | `201 Comment` | All |

### 3.5 Scaffolds — Stage 5

| Method | Path | Body | Returns | Persona |
|---|---|---|---|---|
| POST | `/specs/{id}/scaffold` | `{ target: { dotnet_version?: "8" }, prompt_template_id? }` | `202 { scaffold_id, sse_url }` | Engineer |
| GET | `/scaffolds/{id}/stream` | — | `text/event-stream` (see §4) | Engineer |
| GET | `/scaffolds/{id}` | — | `200 Scaffold` (metadata + file list) | All |
| GET | `/scaffolds/{id}/files/{path}` | — | `200 { path, content }` | All |
| POST | `/scaffolds/{id}/commit` | `{ branch?: string, commit_message?: string }` | `200 { commit_url, commit_hash }` | Engineer |
| GET | `/scaffolds/{id}/download` | — | `200 application/zip` | All |

### 3.6 Admin / observability

| Method | Path | Body | Returns | Persona |
|---|---|---|---|---|
| GET | `/admin/providers` | — | `200 [{ name, model_default, zdr_config_version, audit_letter_at }]` | Admin |
| PATCH | `/admin/routing` | `{ stage, prompt_template_id, provider, model, parameters }` | `200 PromptRouting` | Admin |
| GET | `/admin/cost/rollup` | `?bucket=daily\|weekly&from=&to=` | `200 [{ bucket, provider, stage, cost_usd, calls }]` | Admin |
| GET | `/signing/jwks` | — | `200 JWKS` (public verification key for this env) | Public (no auth) |
| GET | `/health` | — | `200 { status, dependencies: {...} }` | Public (no auth) |

---

## 4. SSE streaming protocol

Two streaming endpoints share the same protocol shape: `/extractions/{id}/stream` and `/scaffolds/{id}/stream`.

Connection is `text/event-stream` over HTTP/1.1 (no WebSocket). Server keeps the connection open until `event: done` or `event: error`. Heartbeat: `event: ping` every 15 seconds.

**Event types** (from spec §7.2, expanded):

| Event | Payload | When emitted |
|---|---|---|
| `stage` | `{ stage: "priming"\|"loading_source"\|"streaming"\|"validating"\|"persisting", step, of }` | At each pipeline-stage transition |
| `token` | `{ text }` | Per LLM token (or short batch) |
| `citation` | `{ claim_path, lines }` | When the LLM emits a `{cite: {lines: ...}}` block in extraction; per file/method binding in scaffold |
| `warning` | `{ code, message }` | E.g. `citation_unresolved`, `confidence_downgraded` |
| `done` | `{ spec_id?, scaffold_id?, call_id, input_tokens, output_tokens, cost_usd }` | On success |
| `error` | `{ code, message, retryable }` | On failure |
| `ping` | `{}` | Heartbeat every 15s |

The frontend's job during a stream:

1. Buffer `token` events into a Markdown-rendered streaming panel.
2. On `citation`, scroll the source pane to the cited range and trigger the 1.5s pulse decoration.
3. On `warning`, surface a non-blocking inline warning chip.
4. On `done`, navigate to the artifact view (Screen 3.2 or 5.2).
5. On `error`, render the error block with retry/raw-response/provider-status links.

**Cancellation.** A `POST /extractions/{id}/cancel` (or `/scaffolds/{id}/cancel`) closes the upstream provider call where supported and emits `event: error` with `code: cancelled` to any open SSE consumer.

---

## 5. Auth flow

```
Browser ──▶ Entra ID OIDC (auth code + PKCE) ──▶ id_token + access_token
                                                       │
                                                       ▼
              Frontend stores access token in memory; refreshes silently.
                                                       │
                                                       ▼
              API verifies bearer JWT (issuer, audience, signature, exp).
                                                       │
                                                       ▼
              Persona resolved from `User.persona` keyed by `idp_subject` claim.
                                                       │
                                                       ▼
              Per-endpoint `[RequirePersona(...)]` policy enforcement.
```

**Sign-off re-auth.** Sign-off endpoints additionally check `auth_time` claim ≤ 5 min from now. If older, response is `401` with `code: auth.reauth_required`. The frontend triggers an interactive re-auth, then retries the sign request.

**SME magic-link (Phase C fallback).** `POST /auth/magic-link/request` with `{ email }` — accepted only if email is in the SME-invitation table. Server sends a one-time URL. `GET /auth/magic-link/exchange?token=...` exchanges the OTT for a short-lived JWT (8 hours idle, 24 hours absolute, refresh rotated every use).

---

## 6. Rate limits & quotas

| Limit | Default | Override |
|---|---|---|
| Authenticated requests / user | 600 / minute | Admin can raise per user |
| Unauthenticated `/health` | 60 / minute / IP | — |
| LLM calls / corpus / day | 200 | Admin can raise per corpus |
| Total LLM calls / environment / day | Hard cap derived from cost ceiling | Admin emergency raise (audited) |
| Concurrent in-flight extractions / corpus | 5 | Admin override |

Limits enforced at the API gateway and in-app (defense in depth).

---

## 7. OpenAPI

The OpenAPI spec is generated from controllers via `Swashbuckle.AspNetCore` and committed to `docs/openapi.yaml`. Frontend types are generated from the OpenAPI doc via `openapi-typescript`. CI fails if the generated types drift from the committed types.

---

## 8. Versioning policy

- `/api/v1` is committed for the lifetime of v1.
- Adding fields to responses or optional fields to requests is non-breaking.
- Removing or renaming fields requires `/api/v2`.
- Deprecations announced with a `Deprecation` response header per RFC 8594, six-month minimum notice.
