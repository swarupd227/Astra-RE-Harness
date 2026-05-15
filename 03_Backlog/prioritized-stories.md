# Prioritized Stories — Phase A & Phase B

**Status:** Kickoff (v0.1)
**Companion:** `epics-and-milestones.md`, `definition-of-done.md`

This is the working backlog for the first six weeks (Phase A + Phase B = milestones M1 + M2). Phase C–E backlog is added at the start of each phase.

Stories are listed in priority order *within each epic*. ID format: `<EPIC>-<NN>`. Effort is a t-shirt size (XS = ½ day, S = 1–2 days, M = 3–5 days, L = 6–10 days, XL = >10).

---

## Phase A · Foundations (Milestone M1, weeks 1–2)

### EPIC-1 · Platform foundations

| ID | Story | Size |
|---|---|---|
| 1-01 | As Platform, I provision a DEV AKS cluster, ACR, App Gateway+WAF via Bicep so the team has a target environment. | M |
| 1-02 | As Platform, I provision a DEV Postgres flexible-server with managed identity access from AKS. | S |
| 1-03 | As Platform, I provision Blob Storage with versioning + a `signed-specs` container with an immutability policy applied via IaC. | S |
| 1-04 | As Platform, I provision a DEV Key Vault Managed HSM with a test signing key the API can call from week 1. | M |
| 1-05 | As Platform, I configure GitHub Actions to build, test, lint, push to ACR, and Helm-deploy to DEV on every merge to `main`. | M |
| 1-06 | As Platform, I wire OpenTelemetry collector to Azure Monitor; logs and traces from API land within seconds. | S |
| 1-07 | As BE, I scaffold the .NET 8 API with a `/health` endpoint, MediatR, FluentValidation, EF Core, OpenTelemetry, and Serilog. | M |
| 1-08 | As BE, I scaffold a Hangfire worker container that runs a no-op job and persists job state in Postgres. | S |
| 1-09 | As BE, I scaffold the parser sidecar (Python + fparser2) container with a stub `Parse(SourceFile)` gRPC endpoint. | S |
| 1-10 | As Platform, I author Helm charts for api, worker, frontend, parser-sidecar — each with values for DEV/STAGING/PROD overrides. | M |

### EPIC-2 · Identity & RBAC (partial in M1)

| ID | Story | Size |
|---|---|---|
| 2-01 | As Platform, I create Entra ID app registrations for the API and the SPA, with the Engineer + Admin personas mapped to Entra groups. | S |
| 2-02 | As FE, I integrate Auth.js with the Entra ID provider; the SPA can sign in and obtain a bearer JWT. | M |
| 2-03 | As BE, I validate inbound JWTs (issuer, audience, signature, exp) and resolve persona from `User.persona` keyed by `idp_subject`. | M |
| 2-04 | As BE, I implement `[RequirePersona]` policy behaviors and apply them to `/health` (none), `/admin/*` (admin), and a stub `/whoami`. | S |
| 2-05 | As BE, I record a `login` AuditEvent on every successful auth and a `permission_denied` event on every 403. | S |

### EPIC-3 · Design system & app shell

| ID | Story | Size |
|---|---|---|
| 3-01 | As Designer + FE, I commit the token set (color, type, spacing, motion, elevation) per `design-system.md`. | S |
| 3-02 | As FE, I configure Tailwind to consume tokens; we never use raw hex/rem in components. | XS |
| 3-03 | As FE, I implement primitives: Button, IconButton, Badge, Chip, Tag, Avatar, Tooltip, Spinner, Divider, Kbd. Storybook entries with all states. | M |
| 3-04 | As FE, I implement form primitives: Input, Textarea, Select, Combobox, Checkbox, Radio, Switch, FormField, FieldError. | M |
| 3-05 | As FE, I implement layout primitives: Stack, HStack, Card, Section, EmptyState, ErrorBlock, Skeleton, Toaster. | M |
| 3-06 | As FE, I implement navigation primitives: TopBar, LeftNav, Breadcrumb, Tabs. | M |
| 3-07 | As FE, I implement disclosure primitives: Modal, SideSheet, Popover. | S |
| 3-08 | As FE, I implement DataTable with sorting, multi-select, sticky header, sticky bulk-action bar. | M |
| 3-09 | As FE, I commit the Monaco wrapper with our custom Fortran language definition and citation decorations (light + dark theme). | M |
| 3-10 | As FE, I wire the persona-keyed entry points so each persona lands on their default screen post-login. | S |
| 3-11 | As FE, I add the `?` keyboard help overlay listing every shortcut. | S |
| 3-12 | As FE, I add a 1280×800 minimum-viewport blocking message. | XS |

---

## Phase B · Demo slice (Milestone M2, weeks 3–6)

### EPIC-4 · Stage 3 — Extract

| ID | Story | Size |
|---|---|---|
| 4-01 | As BE, I implement `ILlmProvider` with an Anthropic adapter calling the ZDR enterprise endpoint and emitting streamed chunks. | M |
| 4-02 | As BE, I implement `LlmProviderConfigVersion` capture and write it on every `LlmCall` row. | S |
| 4-03 | As BE, I implement a deploy-time CI gate that compares adapter `ConfigVersion` against the per-environment expected manifest. | S |
| 4-04 | As BE, I implement the prompt template loader and load `fortran-extract-v3.2`. | S |
| 4-05 | As BE, I implement `POST /subroutines/{id}/extract` returning `202 { extraction_id, sse_url }` and enqueueing a Hangfire job. | M |
| 4-06 | As BE, I implement `GET /extractions/{id}/stream` SSE endpoint that multiplexes provider chunks to the client. | M |
| 4-07 | As BE, I implement schema validation against `spec/v1.json` and persist a `Spec` row only on validation success. | M |
| 4-08 | As BE, I implement citation post-validation against the source file and emit `warning` events for unresolved citations. | S |
| 4-09 | As BE, I implement `POST /extractions/{id}/cancel` that aborts the upstream call. | S |
| 4-10 | As FE, I build Screen 3.1 (live extraction overlay): provider strip, dual-pane streaming + source binding, citation pulse, cancel. | L |
| 4-11 | As FE, I build the citation-pulse Monaco decoration (4px accent left-border + 1.5s alpha fade). | M |
| 4-12 | As FE, I build Screen 3.2 (draft spec) with sectioned spec rendering, two-pane source binding, edit mode for engineer. | L |
| 4-13 | As BE, I implement `PATCH /specs/{id}` (RFC 6902 JSON Patch) producing a `SpecRevision` row. Engineer-only in DRAFT. | M |
| 4-14 | As BE, I implement seed migration injecting the `CONSUME_ROLL` corpus, file, AST, subroutine for the demo. | S |

### EPIC-5 · Stage 4 — Review & Sign

| ID | Story | Size |
|---|---|---|
| 5-01 | As BE, I implement `POST /specs/{id}/route` accepting `{ reviewer_ids, routing_note }` and transitioning to IN_REVIEW. | S |
| 5-02 | As FE, I build Screen 4.1 (My reviews) for the SME persona with awaiting/in-progress/signed groups. | M |
| 5-03 | As BE, I implement `POST /specs/{id}/claims/{path}/review` with action validation (reject requires reason; question opens thread). | M |
| 5-04 | As FE, I build Screen 4.2 layout: outline pane with status dots, claim cards, sticky source pane. | L |
| 5-05 | As FE, I implement claim-card states (untouched, accepted, edited, rejected, pending-question) and per-card keyboard shortcuts (A/E/R/?/J/K). | M |
| 5-06 | As FE, I implement the routing-note callout strip on Screen 4.2. | XS |
| 5-07 | As FE, I implement the open-questions resolution flow ("Answer in spec", "Mark not applicable", "Defer"). | M |
| 5-08 | As BE, I implement the sign-off pipeline: precondition check, RFC 8785 canonicalize, SHA-256 hash, HSM sign via Key Vault, immutable blob write, AuditEvent. | L |
| 5-09 | As BE, I enforce the `auth_time ≤ 5 min` gate on the sign endpoint, returning `401 auth.reauth_required` otherwise. | S |
| 5-10 | As FE, I build the sign-off modal: citation integrity check, canonical-sentence checkbox, signed-name display, re-auth interstitial, success state. | M |
| 5-11 | As FE, I build the SIGNED display mode for Screen 3.2 / 4.2: signature panel pinned, all interactive affordances removed. | S |
| 5-12 | As BE, I implement `GET /specs/{id}/audit` returning the AuditEvent stream. | S |
| 5-13 | As FE, I build Screen 4.3 (Audit trail timeline) — basic list, date dividers, event cards. | M |
| 5-14 | As QA, I author Playwright E2E covering: extract → engineer edit → route → SME accepts/edits/rejects → resolve question → sign. Runs nightly. | M |

### EPIC-6 · Stage 5 — Scaffold (M2 portion)

| ID | Story | Size |
|---|---|---|
| 6-01 | As BE, I implement the Azure OpenAI adapter on `ILlmProvider`. | M |
| 6-02 | As BE, I implement the prompt template loader entry for `dotnet-scaffold-v2.0`. | S |
| 6-03 | As BE, I implement `POST /specs/{id}/scaffold` enqueueing the GenerateScaffoldJob and returning SSE URL. | M |
| 6-04 | As BE, I implement `GET /scaffolds/{id}/stream` and the multi-file emission protocol. | M |
| 6-05 | As BE, I implement schema validation against `scaffold/v1.json` and persist a `Scaffold` row + package blob. | S |
| 6-06 | As FE, I build Screen 5.1 (live scaffold) with file-tab streaming and spec-binding. | L |
| 6-07 | As FE, I build Screen 5.2 (scaffold artifact view) with file tree, Monaco + TODO markers, traceability panel. | L |
| 6-08 | As BE, I implement a *stub* `POST /scaffolds/{id}/commit` that records a faux commit (real Octokit commit moves to M3). | XS |

### EPIC-9 · State machine (M2 portion)

| ID | Story | Size |
|---|---|---|
| 9-01 | As BE, I implement the Subroutine state machine in MediatR pipeline behaviors so any handler attempting an invalid transition returns 409 with `state.invalid_transition`. | M |
| 9-02 | As BE, I write transition unit tests for every state in `architecture-overview.md` §3 (forward + invalid backward). | S |

### EPIC-10 · Observability (M2 portion)

| ID | Story | Size |
|---|---|---|
| 10-01 | As BE, I add LLM-call OpenTelemetry attributes per spec §6.4 to every Anthropic + Azure OpenAI call. | S |
| 10-02 | As Platform, I create the basic Azure Monitor "operational health" dashboard. | S |
| 10-03 | As Platform, I create the basic LLM cost & latency dashboard. | S |
| 10-04 | As BE, I implement the daily cost-rollup Hangfire job. | S |

### Demo rehearsal (week 6)

| ID | Story | Size |
|---|---|---|
| D-01 | As Eng + Designer + PM, I conduct three rehearsals on STAGING; the third is recorded as the Appendix A.4 backup. | S |
| D-02 | As Eng, I configure a 60-second timeout on the demo extraction with cached-fallback per Appendix A.4. | S |
| D-03 | As Eng, I document the live-demo runbook (network fallback, recorded backup trigger, presenter cue cards). | S |

---

## Phase C+ stories

Phase C, D, E backlogs will be authored at the start of each phase, scoped from the gates and risk register. They follow the same shape as above, sized at the start of the phase.

---

## Velocity & sequencing notes

- Phase A: heavy parallel work across Platform / BE / FE. Daily standup discipline keeps the surface integrated.
- Phase B week 3 is the first joint integration — by end of week 3, an engineer should be able to click "Extract" on the seeded `CONSUME_ROLL` and see real Anthropic tokens stream into Screen 3.1, even if half the spec view is unstyled.
- The sign-off pipeline (5-08, 5-09, 5-10) is the riskiest single story in M2 — start it in week 4, not week 5. HSM availability from week 1 (1-04) gives this story time.
- The citation pulse (4-11) is the demo's hero interaction — it gets a dedicated polish pass in week 6 by the design lead.
