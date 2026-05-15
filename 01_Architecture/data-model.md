# Data Model & Persistence

**Status:** Kickoff (v0.1) — schema sketch, not migration-ready
**Source:** Spec §5 (verbatim), with notes on indexing, retention, and migration sequencing.

---

## 1. Entity catalogue

All entities have `id` (uuid PK), `created_at`, `updated_at`, `soft_deleted_at` (nullable). `AuditEvent` is the single exception — append-only, no soft-delete column.

| # | Entity | Purpose | Mutability |
|---|---|---|---|
| 1 | `User` | Authenticated identity (engineer/sme/observer/admin) | Mutable except `idp_subject` |
| 2 | `Credential` | Service-account credentials for source access | Admin-managed only |
| 3 | `Corpus` | Top-level container for connected source | Mutable; soft-deletable |
| 4 | `SourceVersion` | Point-in-time snapshot of a corpus | **Immutable** after ingest |
| 5 | `SourceFile` | Single Fortran file within a SourceVersion | **Immutable** |
| 6 | `Subroutine` | Parsed subroutine identified by AST | **Immutable** for parsed content; mutable for state |
| 7 | `Spec` | Behavioral specification for one Subroutine in one SourceVersion | Mutable in DRAFT/IN_REVIEW; **immutable** once SIGNED |
| 8 | `SpecRevision` | Each edit produces a new revision (append-only) | **Immutable** |
| 9 | `ClaimReview` | Per-claim accept/edit/reject decisions | **Immutable** once written |
| 10 | `Signature` | Sign-off record (HSM-signed) | **Immutable** |
| 11 | `Scaffold` | Generated code package | Mutable for state; content immutable post-generation |
| 12 | `LlmCall` | Audit record of every LLM invocation | **Immutable** |
| 13 | `AuditEvent` | Append-only event log across all entities | **Immutable** |
| 14 | `Comment` | Threaded comments on Specs and Claims | Mutable for body (within edit window); soft-deletable |
| 15 | `PromptTemplate` (admin) | Routing-rule target — template metadata + version | Admin-managed |
| 16 | `PromptRouting` (admin) | `(stage, template_id) → (provider, model, params)` | Admin-managed |

Relationships, foreign keys, and indexed columns are summarized in the dependency diagram below.

---

## 2. Dependency diagram (logical)

```
           User ◀────┐                                       ┌──▶ Credential
                     │                                       │
  Comment ──┐        │                                       │
            │        │                                       │
            ▼        ▼                                       │
            Spec ◀── Subroutine ◀── SourceFile ◀── SourceVersion ◀── Corpus
            ▲ │       │                                       │
            │ │       └─▶ ParsedAst (blob)                    │
            │ ▼                                               │
   SpecRevision   Signature  ◀────────┐                       │
            │     │  (HSM-signed)     │                       │
            ▼     ▼                   │                       │
       ClaimReview                    │                       │
            │                         │                       │
            └──▶ LlmCall ◀───── Scaffold ───▶ blob: scaffolds │
                  ▲                                           │
                  │                                           │
                  └─────────── AuditEvent ◀───────────────────┘
                              (everything writes here)
```

---

## 3. Key fields per entity

### Corpus
`id`, `name`, `source_type` (enum: `git` | `upload`), `source_url`, `branch`, `source_root`, `credential_id` (fk), `state` (enum: `INGESTING`, `INGESTED`, `PARSING`, `PARSED`, `EMPTY`, `FAILED`, `ARCHIVED`), `file_count`, `total_loc`, `owner_id` (fk → User), `latest_version_id` (fk → SourceVersion), timestamps.

**Indexes:** `(state)`, `(owner_id)`, `(name)` unique within non-archived rows.

### SourceVersion
`id`, `corpus_id` (fk), `git_commit_hash` (nullable for upload), `ingested_at`, `ingested_by` (fk → User), `file_manifest_blob_uri` (immutable container path).

**Indexes:** `(corpus_id, ingested_at desc)`. Partial unique on `(corpus_id, git_commit_hash)` where not null.

### SourceFile
`id`, `source_version_id` (fk), `relative_path`, `file_hash` (sha256), `line_count`, `blob_uri`.

**Indexes:** `(source_version_id, relative_path)` unique.

### Subroutine
`id`, `source_file_id` (fk), `name`, `signature` (text — declared parameter list in canonical form), `line_start`, `line_end`, `common_block_refs` (jsonb), `called_subroutines` (jsonb), `io_patterns` (jsonb), `parsed_ast_blob_uri`, `state` (enum: `INGESTED`, `PARSED`, `EXTRACTING`, `DRAFT`, `IN_REVIEW`, `SIGNED`, `SCAFFOLDING`, `SCAFFOLDED`, `SUPERSEDED`).

**Indexes:** `(source_file_id, name)`, `(state)`, GIN on `called_subroutines`.

### Spec
`id`, `subroutine_id` (fk), `source_version_id` (fk), `state` (enum), `spec_json` (jsonb — current state, schema below), `llm_call_id` (fk → LlmCall, the extraction call), `created_by` (fk → User), `created_at`.

**Indexes:** `(subroutine_id, source_version_id)` unique on the active (non-superseded) spec, `(state)`, GIN on `spec_json` for ad-hoc queries.

### SpecRevision
`id`, `spec_id` (fk), `revision_number` (monotonic), `spec_json_diff` (jsonb — RFC 6902 JSON Patch), `edited_by` (fk → User), `edited_at`.

**Indexes:** `(spec_id, revision_number)` unique.

### ClaimReview
`id`, `spec_id` (fk), `claim_path` (text — JSONPath, e.g. `$.invariants[?(@.id=='INV-1')]`), `action` (enum: `accept` | `edit` | `reject` | `question`), `reason` (text — required for `reject`, ≥ 20 chars; required for `question` as the question body), `edited_text` (text — required for `edit`), `reviewer_id` (fk → User), `reviewed_at`.

**Indexes:** `(spec_id, claim_path)` non-unique (multiple actions over time).

### Signature
`id`, `spec_id` (fk), `signer_id` (fk → User), `signed_at`, `source_version_hash` (text — sha256 of the source-version's `file_manifest`), `spec_canonical_hash` (text — sha256 of canonicalized `spec_json`), `signature_bytes` (bytea — HSM signature), `signature_key_id` (text — Key Vault key identifier), `re_auth_time` (timestamp — used to enforce ≤5min gate).

**Indexes:** `(spec_id)` unique. There is one and only one signature per spec.

### Scaffold
`id`, `spec_id` (fk), `state` (enum: `SCAFFOLDING`, `SCAFFOLDED`, `COMMITTED`, `FAILED`), `llm_call_id` (fk → LlmCall), `git_branch`, `git_commit_hash` (nullable until committed), `package_blob_uri`, `generated_by` (fk → User), `generated_at`.

**Indexes:** `(spec_id, generated_at desc)`. Multiple scaffolds per spec are allowed (regeneration produces new rows).

### LlmCall
`id`, `provider` (enum: `anthropic` | `azure_openai`), `model`, `prompt_template_id`, `prompt_template_version`, `provider_config_version` (text — captures the ZDR/no-training config snapshot for residency evidence), `input_tokens`, `output_tokens`, `latency_ms`, `cost_usd` (numeric(10,4)), `request_blob_uri` (sanitized — no source code), `response_blob_uri` (sanitized), `restricted_request_blob_uri` (nullable — full payload in restricted container, 7-day retention), `restricted_response_blob_uri` (nullable), `status` (enum: `success` | `failure` | `cancelled`), `error_code` (text, nullable), `called_by` (fk → User), `called_at`.

**Indexes:** `(called_at desc)`, `(provider, called_at desc)`, `(prompt_template_id, prompt_template_version)`.

**Retention:** `request_blob_uri` / `response_blob_uri` blobs retained 30 days. `restricted_*` blobs retained 7 days. `LlmCall` rows retained 7 years.

### AuditEvent
`id`, `event_type` (text — enumerated set documented in `audit-events.md` next phase), `actor_id` (fk → User, nullable for system events), `target_type` (text), `target_id` (uuid, nullable), `payload` (jsonb), `occurred_at`, `ip_address` (inet), `user_agent` (text).

**Indexes:** `(target_type, target_id, occurred_at desc)`, `(actor_id, occurred_at desc)`, `(event_type, occurred_at desc)`. Time-partitioned by month.

**Retention:** 7 years (Advantive policy). Rows are *never* deleted; partitions older than 7 years are archived to cold storage.

### Comment
`id`, `target_type` (enum: `spec` | `claim`), `target_id` (uuid), `parent_comment_id` (fk → Comment, nullable for thread root), `body` (text), `author_id` (fk → User), `created_at`, `edited_at` (nullable), `soft_deleted_at`.

**Indexes:** `(target_type, target_id, created_at)`, `(parent_comment_id)`.

### User, Credential, PromptTemplate, PromptRouting
Standard fields per spec §5.1. `Credential.encrypted_secret` is wrapped by Key Vault — the column stores a Key Vault reference, not the secret itself.

---

## 4. Spec JSON schema (canonical, v1)

The `Spec.spec_json` column holds a document conforming to `spec/v1.json` (spec §5.2). Reproducing the structure here so the data model and the schema stay in sync:

- `routine`, `source_path`, `source_lines`, `source_hash`, `summary` — string scalars.
- `inputs[]`, `outputs[]` — `{ id, name, type, semantic, citations[] }`.
- `invariants[]` — `{ id, claim, citations[], confidence: low|medium|high }`.
- `side_effects[]` — `{ description, citations[] }`.
- `edge_cases[]` — `{ description, citations[], behavior, confidence }`.
- `open_questions[]` — `{ id, question, status: unresolved|resolved|deferred|n_a, resolution? }`.
- `metadata` — `{ extracted_by_llm_call_id, extracted_at, prompt_template }`.

**Claim path discipline.** Every claim has an `id` (e.g. `INV-1`, `EC-2`, `Q-1`); the JSONPath `$.invariants[?(@.id=='INV-1')]` is the stable target for `ClaimReview.claim_path`. Frontend uses these paths as React keys; the server uses them as the addressable target for review actions and audit diffs.

---

## 5. Signing & immutability mechanics

The signing pipeline (spec §5.3, expanded with operational detail):

1. **Pre-flight.** Server verifies: every `Invariant`/`Side effect`/`Edge case` claim has a final `ClaimReview` (accept/edit/reject); every open question has a resolution; the SME's JWT carries `auth_time` ≤ 5 minutes from now.
2. **Canonicalize.** `spec_json` is canonicalized per RFC 8785 (JSON Canonicalization Scheme).
3. **Hash.** SHA-256 over the canonical bytes → `spec_canonical_hash`.
4. **Sign.** Hash is signed with the HSM-backed signing key for this environment via Azure Key Vault `Sign` operation (algorithm: `RS256` — RSA-SHA256, 4096-bit key). Result: `signature_bytes`, `signature_key_id`.
5. **Persist.** `Signature` row inserted; `Spec.state` transitions to `SIGNED`; signed JSON written to the **immutable** `signed-specs` blob container with a write-once-read-many policy.
6. **Verify-on-read.** Any consumer (Project 3 CI, Observer export, audit reviewer) can fetch the blob, recompute the hash, and verify against the public key for that environment. The Harness publishes the public-key JWKS at `/api/v1/signing/jwks` per environment.

**No un-sign path exists in the data model.** A signed spec is terminal. If the underlying source changes, the spec is marked SUPERSEDED and a new spec is extracted, reviewed, and signed.

---

## 6. Migration sequencing

EF Core migrations are applied via Helm pre-install / pre-upgrade hooks. Forward-only.

| Migration # | Phase | Adds |
|---|---|---|
| 0001 | A | Users, AuditEvent, Credentials, base extensions (uuid-ossp, pg_trgm) |
| 0002 | A | PromptTemplate, PromptRouting (admin tables) |
| 0003 | B | Corpus, SourceVersion, SourceFile, Subroutine (with seeded `CONSUME_ROLL`) |
| 0004 | B | Spec, SpecRevision, ClaimReview, Signature, LlmCall, Comment |
| 0005 | B | Scaffold |
| 0006 | C | Multi-corpus indexes, AuditEvent monthly partitioning, full-text search on Comment.body |
| 0007 | D | Hot-standby logical replication slots + retention partition tooling |

Migrations 0001–0005 are committed by end of week 6 (demo gate). 0006–0007 are Phase C/D work.

---

## 7. Retention summary

| Class | Retention | Storage |
|---|---|---|
| Signed specs (canonical JSON + signature) | Indefinite | Immutable blob container |
| Source files, AST artifacts | Indefinite (within 7-year audit horizon) | Versioned blob container |
| LLM request/response (sanitized) | 30 days | Standard blob container |
| LLM request/response (full, restricted) | 7 days | Restricted blob container, admin-only |
| Audit events | 7 years | Postgres (partitioned), then cold archive |
| Operational data (corpus, spec, etc.) | 2 years rolling, plus all data linked to a signed spec for 7 years | Postgres |
| Soft-deleted rows | 90 days then purged unless under hold | Postgres |

Hold flags are stored on `Corpus` and `Spec` (boolean `under_legal_hold`); when set, the purge job skips related rows.

---

## 8. Open data-model questions

Tracked alongside `00_DevelopmentPlan.md` §12:

1. **JSONPath flavor.** Use the SQL/JSON spec (Postgres native) or JSONPath-Plus (Node-flavor, what the frontend uses)? They diverge on filter syntax. *Decision due week 4.*
2. **Comment edit window.** How long can an author edit a comment before the body becomes immutable? *Default: 5 minutes; confirm with PM.*
3. **`spec_json_diff` strategy.** Store diffs as RFC 6902 JSON Patch (compact, replayable) or as full snapshots (simpler, larger). *Initial choice: JSON Patch; revisit if storage cost matters.*
