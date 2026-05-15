# Security, Data Residency, & Compliance

**Status:** Kickoff (v0.1)
**Source:** Spec §8 (verbatim), expanded with concrete controls, evidence collection, and the Phase D hardening plan.

---

## 1. Threat model (concise)

The Harness handles **confidential customer source code** and produces **authoritative signed contracts**. The threat model is dominated by two risks:

1. **Source leakage** — Fortran source bytes ending up somewhere they should not (LLM provider training, public log, screenshots, archived backup outside Nous's subscription).
2. **Tamper or counterfeit signed specs** — a downstream consumer accepting a spec as authoritative when it has been altered or never genuinely signed.

A full STRIDE walkthrough is scheduled for week 11. Initial mitigations are listed below.

---

## 2. Data classification

| Class | Examples | Storage | Access |
|---|---|---|---|
| **RESTRICTED** | Customer Fortran source, signed specs, scaffold output | AES-256 at rest, Azure-managed keys; immutable container for signed | Persona-based RBAC; every read logged |
| **CONFIDENTIAL** | AST artifacts, draft specs, comments, audit log | Encrypted at rest; standard mutability | Persona-based RBAC; reads logged |
| **INTERNAL** | User profiles, configuration, prompt templates (with secrets redacted) | Encrypted at rest | Authenticated users |
| **PUBLIC** | Application metadata, schema docs, API spec | Standard | Anyone authenticated |

**Operational rule:** logs and traces never carry RESTRICTED content. Subroutines are referenced by ID, not by source text. The logging middleware has a small allowlist of fields it will serialize; everything else is redacted by default.

---

## 3. Source data handling

The chain of custody for customer Fortran source:

```
Kiwiplan Git ──TLS──▶ Harness API ──TLS──▶ Blob Storage ──TLS──▶ Worker ──TLS──▶ ZDR LLM endpoint
       ▲                                       │                                    │
       │                                       ▼                                    ▼
       └── service-account credential          AES-256 at rest                no provider retention
           in Key Vault, rotated quarterly      versioned containers          (config snapshot logged)
```

- **In transit:** TLS 1.2+ on every hop. Internal hops use mutual TLS where the substrate supports it (AKS service mesh).
- **At rest:** AES-256 with Azure-managed keys for standard containers. The `signed-specs` container has an additional **immutability policy** (write-once-read-many) applied via IaC and verified on every deploy.
- **Provider hops:** Only via configured zero-retention endpoints. The active provider configuration version is recorded on every `LlmCall` (see `llm-integration.md` §8).
- **Log discipline:** Source is *never* logged in plaintext. The logging middleware drops fields tagged `[RestrictedData]`. A CI test verifies the tag is present on every relevant DTO.

---

## 4. Authentication & authorization

### 4.1 Authentication

- **Engineers, Architects, Admins:** OIDC via Microsoft Entra ID. Auth code + PKCE. id_token + access_token. Refresh tokens rotated on every use.
- **SMEs (Kiwiplan):** federated identity from Auckland tenancy where possible. Otherwise email magic-link as a fallback (Phase C). Magic-link OTT: 10-minute lifetime, single-use, exchanged for a short-lived JWT.
- **Sessions:** 8-hour idle timeout, 24-hour absolute timeout. Re-auth required for sign-off operations (auth_time ≤ 5 min).
- **Logging:** every authentication event — login, logout, refresh, denial — written to the audit trail with persona, IP, user-agent, outcome.

### 4.2 Authorization

- **RBAC at the API layer.** Every endpoint declares the personas it permits via `[RequirePersona]`. The persona is resolved from `User.persona` keyed by the JWT `sub` claim.
- **No broker tokens.** Each user's actions are attributed to their JWT — there is no shared service identity for user-initiated work.
- **Capability matrix** (spec §2.2, verbatim): see master plan §3 of this folder. Highlights: only SMEs can sign; only engineers trigger ingest/parse/extract/scaffold; only admin configures providers and credentials; observers are read-only.
- **Critical-action re-auth.** Sign-off rejected (`401 auth.reauth_required`) if `auth_time` > 5 min. Frontend triggers an interactive re-auth, then retries.
- **Server-side state-machine enforcement.** A SME *cannot* edit a SIGNED spec — not because the UI hides the affordance, but because the API rejects the PATCH. State transitions are checked in MediatR pipeline behaviors before any handler runs.

---

## 5. Secret management

| Secret | Storage | Rotation |
|---|---|---|
| Git service-account PAT/SSH key | Key Vault, accessed by managed identity | Quarterly |
| LLM provider API keys | Key Vault | Quarterly, or on incident |
| HSM signing key | Azure Key Vault Managed HSM (per environment) | Annual; runbook for emergency rotation |
| JWT signing keys | Managed by Entra ID | Per Microsoft schedule |
| Magic-link signing key | Key Vault | Quarterly |
| Database connection string | Key Vault, surfaced via managed identity | On infrastructure change |
| OpenTelemetry collector key | Key Vault | Annual |

Secrets never appear in logs, environment variables, or CI artifacts. Local development uses a separate dev-only Key Vault with placeholder values; the production Key Vaults are not accessible from developer laptops.

---

## 6. Sign-off integrity

The sign-off pipeline (see `data-model.md` §5) produces three pieces of tamper evidence per signed spec:

1. **Canonical hash.** SHA-256 over RFC 8785-canonicalized `spec_json`.
2. **HSM signature.** Hash signed with the per-environment HSM key (RS256, 4096-bit). Signature, hash, and key ID persisted on the `Signature` row.
3. **Immutable blob.** Canonical signed JSON written to a write-once-read-many container; the blob URI is part of the artifact and survives any subsequent operational changes.

Verification is public:

- `GET /api/v1/signing/jwks` returns the public key for the environment.
- Project 3 CI runs `verify-signed-spec.sh <blob_uri>` which fetches the blob, recomputes the hash, and verifies against the public key.
- Audit reviewers can run the same verification offline.

The system has no API to "un-sign" a spec. The data model has no column to mark a signature as void. If a signed spec is later found to be wrong, the path is **supersession** — the source is updated, a new SourceVersion is created, the prior spec is marked SUPERSEDED, and a new spec is extracted, reviewed, and signed against the new source. The original signature stays valid for what it covered (the prior source version).

---

## 7. Audit completeness

`AuditEvent` is append-only — no edits, no deletes. The trail covers (spec §8.4):

- Source ingested
- AST parsed
- LLM invocation (with full call metadata: provider, model, template ID + version, input/output tokens, latency, cost)
- Spec edited (with diffs)
- Routed to review
- Claim accepted / edited / rejected / questioned
- Question opened / resolved
- Spec signed
- Scaffold generated
- Scaffold committed
- Comment added
- Login / logout / permission denial
- Admin actions (provider routing change, credential rotation, hard-cap override)

Retention: **7 years** (Advantive policy). Operational data: 2 years rolling unless under hold. Soft-deleted entities purged after 90 days unless under hold.

The trail is exposed:

- Per spec, in Screen 4.3 (audit trail timeline).
- Per environment, via admin export (`GET /api/v1/admin/audit/export?from=&to=`).
- To Project 3, as part of every signed-spec bundle (audit extract for that spec).

---

## 8. Compliance posture

| Standard | Posture | Evidence source |
|---|---|---|
| **ISO 27001** | Inherited from parent Nous platform. Harness maps to controls A.5 (policy), A.8 (asset management), A.12 (operations security), A.13 (communications), A.14 (acquisition/dev/maintenance), A.16 (incident management). | Audit trail, deployment records, threat model. |
| **SOC 2 Type II** | Operated under parent platform's SOC 2 program. Harness contributes evidence via `AuditEvent` and provider-config logs. | Audit-trail extract; provider audit-letter timestamps. |
| **EU AI Act Article 14 (human oversight)** | Sign-off is the documented oversight control: every consequential output is gated by a human signature before becoming authoritative. | Sign-off events in audit trail; sign-off UI showing the explicit checkbox. |
| **GDPR** | Source code is not personal data; user identities are minimized (email + display name). DSAR export via admin. | User table, audit trail. |

Phase D evidence-collection plan in `04_Delivery/risk-register.md`.

---

## 9. Pen test scope (Phase D, week 11–12)

- External attacker simulation: attempts to reach the API without a valid JWT, attempts to elevate persona, attempts to access another user's spec.
- Internal attacker simulation: an engineer attempting to bypass the SME-only sign-off path; an SME attempting to access an unrouted spec.
- Storage attack: attempts to overwrite an immutable blob (must fail at the storage layer).
- Source leakage: scrape of logs, dashboards, exports, error traces for any byte of Fortran source.
- LLM provider misconfiguration: deliberate flip of the residency config; CI gate must reject deploy.

The pen test report and remediation log are part of the SOC 2 evidence package.

---

## 10. Incident response

The Harness inherits the parent platform's incident-response runbook. Harness-specific additions:

- **Suspected source leak:** rotate Git service-account credential immediately; freeze affected corpus; produce an audit-trail extract for the suspected window; engage Anthropic / Azure incident channels in parallel.
- **HSM key compromise:** rotate signing key per emergency rotation runbook; mark all specs signed with the compromised key as `signature_under_review` (a mutable flag separate from the immutable signature) and re-sign in coordination with the original SMEs; notify Project 3 to halt CI pickup until cleared.
- **Provider config drift:** the CI gate prevents deployment-time drift; runtime drift detection is via the admin dashboard's `provider_config_version` panel — operator pages on mismatch.

---

## 11. Privacy & data subject rights

- The Harness stores user identity (email, display name, IDP subject). It does not store personal data of customers (Kiwiplan personnel beyond the SME reviewers' identities).
- Data subject access and erasure requests are handled by the parent platform's privacy team. The Harness exposes admin endpoints to extract or anonymize a user's actions while preserving audit-trail integrity (the action stays; the actor identifier becomes a tombstone).

---

## 12. Hardening checklist (Phase D exit gate)

- [ ] Threat model document approved.
- [ ] Pen test executed; high/critical findings remediated; medium findings tracked.
- [ ] Source-residency CI gate passes against production manifest.
- [ ] Provider audit-letter timestamps fresh (<12 months) on admin dashboard.
- [ ] Credential-rotation runbook executed once in staging.
- [ ] HSM emergency-rotation runbook executed once in staging.
- [ ] Audit-trail retention partitioning live; cold-archive runbook authored.
- [ ] DR drill executed: RPO ≤15 min, RTO ≤4 hr proven.
- [ ] WCAG 2.1 AA audit passed.
- [ ] Browser matrix verified.
