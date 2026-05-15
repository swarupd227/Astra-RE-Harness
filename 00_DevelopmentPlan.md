# Nous Astra RE Harness — Development Plan

**Document owner:** Engineering lead (TBA)
**Source of truth:** `Nous_RE_Harness_Product_Specification_v1.docx` (extract at `05_Reference/source-spec-extract.md`)
**Plan version:** v0.1 (kickoff draft)
**Plan date:** 2026-05-05
**Target release:** Phase 1 production cutover by 2026-08-28 (16-week plan)

---

## 1. Purpose of this plan

The Product Specification is the *what* and *why*. This document is the *how* and *when*: the team, the phasing, the engineering practices, the UX direction, and the gates we cross before each milestone. Anything in scope here that contradicts the spec defers to the spec; anything not in the spec is explicitly out of scope.

The plan is organized around two delivery commitments:

| Commitment | Date target | What "done" means |
|---|---|---|
| **Defense-call demo** | End of week 6 (≈ 2026-06-15) | The 4-minute Appendix A flow runs end-to-end on staging with a real Anthropic call, real HSM signature against an Azure Key Vault test instance, real Azure OpenAI scaffold, and a recorded backup. |
| **Phase 1 production cutover** | End of week 16 (≈ 2026-08-28) | All five stages operational against real Kiwiplan Classic source. Success criteria in §1.4 of the spec are measured for ≥10 subroutines. SOC 2 / ISO 27001 evidence captured. SME training complete. |

Everything below is in service of those two commitments.

---

## 2. Product summary (for the team)

The Harness is a **controlled human-in-the-loop pipeline** that takes Fortran 77/90/95 source from Kiwiplan Classic and produces *signed behavioural specifications* plus *.NET scaffolds* that flow into Project 3's main build pipeline.

Five stages, persisted at every transition:

```
INGEST → PARSE → EXTRACT → REVIEW → SCAFFOLD
(Git/upload) (fparser2) (Claude Sonnet) (SME sign) (Azure OpenAI GPT-4o)
```

Three personas: **Engineer** (drives the pipeline), **SME** (reviews & signs), **Observer** (read-only). A separate **Admin** persona — held by 1–2 platform engineers — manages providers, keys, and credentials via a separate ops console.

The product is **not** a Fortran-to-runtime translator. The signed spec — *not* the LLM output — is the authoritative contract.

---

## 3. Architectural posture

Full architecture is in `01_Architecture/architecture-overview.md`. Headline choices, with the rationale that drives our delivery sequence:

| Layer | Choice | Why it shapes the plan |
|---|---|---|
| Frontend | React 18 + TypeScript + Vite, TanStack Query, Monaco, Tailwind + shadcn/ui | We invest week 1 in a design system + component primitives; every screen reuses them. |
| API | .NET 8 minimal APIs, EF Core, MediatR, FluentValidation | One backend repo, one container, one Helm chart. Aligns with broader Project 3 stack — we inherit their templates. |
| Workers | Hangfire on .NET 8 | All long-running jobs (parse, extract, scaffold) are Hangfire jobs from day one. No separate queue infra. |
| Operational DB | PostgreSQL 16 + EF Core migrations | Migrations are part of the deployment artifact. Treat schema as source-controlled product. |
| Object store | Azure Blob Storage with immutable containers for signed specs | The immutability policy is a *gate* — it must be applied in IaC before any sign-off code ships. |
| LLM | Anthropic Claude Sonnet 4 (extract), Azure OpenAI GPT-4o (scaffold) | Both via a single `ILlmProvider` abstraction. Failover is contract-level, not application-level. |
| Identity | Microsoft Entra ID (OIDC) + magic-link fallback for SMEs | Magic-link path is week-3 work — it's the only thing that lets external SMEs in. |
| HSM signing | Azure Key Vault Managed HSM | Key provisioning is environment-specific. We provision the *test* HSM in week 1, the *staging* HSM in week 5, and the *production* HSM in week 14. |
| Observability | OpenTelemetry → Azure Monitor | Every LLM call is a span with provider, model, prompt-template-id, token counts, cost. Dashboards exist before the demo. |
| CI/CD | GitHub Actions → ACR → AKS via Helm | Inherited from Project 3. We do not build a bespoke pipeline. |

---

## 4. Delivery phasing

Sixteen calendar weeks, five phases. Each phase has a single hard deliverable that gates the next phase. We do not start phase N+1 until phase N's gate is green.

### Phase A · Foundations (weeks 1–2)

**Hard gate:** "Hello world" of every layer. A signed-in Engineer can hit a `/api/v1/health` endpoint, an empty React page renders inside our design-system shell, a Hangfire worker runs a no-op job, a Postgres migration applies, a blob is read and written, an OpenTelemetry trace lands in Azure Monitor, and a no-op Helm deploy succeeds in DEV.

| Workstream | Deliverable |
|---|---|
| Infra (IaC) | Bicep / Terraform for: AKS cluster (DEV), Postgres flexible-server, Blob Storage with versioning, Key Vault with test HSM key, ACR, App Gateway with WAF. Three environments planned (DEV, STAGING, PROD); only DEV is live. |
| Identity | Entra ID app registrations for API + frontend. Engineer & Admin personas exist; SME magic-link is parked for Phase B. |
| Backend skeleton | .NET 8 minimal API with `/health`, OpenTelemetry, Serilog → JSON logs, MediatR + FluentValidation wired, EF Core with one migration, Hangfire dashboard secured. |
| Frontend skeleton | Vite + TS + Tailwind + shadcn/ui. App shell: top bar (env badge, persona, identity menu), left nav, breadcrumb. Auth.js with Entra ID. **Design tokens locked in this phase.** |
| Design system | Token set (color, type, spacing, motion, elevation), component primitives (Button, Badge, Card, DataTable, SideSheet, Modal, Toast, EmptyState, ErrorBlock, Skeleton). Storybook deployed to a static site. |
| CI/CD | GitHub Actions: build, test, lint, container build, Helm deploy to DEV on merge to `main`. Branch protection on `main` (required reviews + green CI). |
| Engineering ground rules | ADR template, RFC template, code-style configs, DoD (`03_Backlog/definition-of-done.md`). |

### Phase B · Demo slice (weeks 3–6)

**Hard gate:** End-to-end run of the Appendix A 4-minute flow on STAGING for the `CONSUME_ROLL` subroutine, with a green dress-rehearsal recorded. Demo build is a *vertical slice* — we only build the screens & APIs that the 4-minute path touches, plus everything immutable about a signed spec.

| Workstream | Deliverable |
|---|---|
| Pre-loaded corpus | Seed migration + blob seeder that injects the synthetic `CONSUME_ROLL` corpus, file, AST artifact, and `Subroutine` row directly. Stage 1 & 2 UIs are **stubs** in this phase. |
| Stage 3 (Extract) — UI | Screen 2.1 (Subroutine detail, Source tab) + Screen 3.1 (Live extraction overlay) + Screen 3.2 (Draft spec). Streaming with citation pulse on the source pane is the demo's wow moment — we budget time for it. |
| Stage 3 — backend | `POST /api/v1/subroutines/{id}/extract` + Hangfire job + SSE endpoint. `ILlmProvider` with Anthropic adapter only (Azure OpenAI added later). Prompt-template loader; `fortran-extract-v3.2.md` checked into the repo. Schema validation against `spec/v1.json`. |
| Stage 4 (Review) — UI | Screen 4.1 (My reviews) + Screen 4.2 (Spec review with claim cards, accept/edit/reject/question, open-question resolution). Outline pane with progress dots. SME persona switching wired to Entra ID groups. |
| Stage 4 — backend | `PATCH /specs/{id}` (engineer DRAFT only), `POST /specs/{id}/route`, `POST /specs/{id}/claims/{path}/review`, `POST /specs/{id}/sign`. Sign-off canonicalizes (RFC 8785), hashes, signs with **test** HSM key, writes to immutable blob container. |
| Stage 5 (Scaffold) — UI | Screen 5.1 (Live generation) + Screen 5.2 (Scaffold artifact view). Commit-to-Git flow can be *stubbed* (records a pretend commit hash) for the demo if real commit is risky on demo day. |
| Stage 5 — backend | Azure OpenAI adapter on `ILlmProvider`. `dotnet-scaffold-v2.0` template. Generated package written to blob; commit-to-Git flow may be the stub above. |
| Audit trail | Append-only `AuditEvent` table, with at minimum these events visible by demo: extract, edit, route, claim-review, sign, scaffold. Screen 4.3 (audit trail timeline). |
| Demo rehearsal | Three rehearsals on staging in week 6, each video-recorded. Final rehearsal is the "recorded backup" of A.4. |

**Out of scope for Phase B:** Stage 1 (ingest) UI and APIs, Stage 2 (parse) — fparser2 wiring, multi-corpus, multi-user concurrent edit, magic-link SME auth, real provider failover, prod HSM, scaffold-output Git repo. All present as architectural seams; none implemented.

### Phase C · Full pipeline (weeks 7–10)

**Hard gate:** A new corpus can be connected from a real Git URL, walked through all five stages, and emit a committed scaffold to a real scaffold-output Git repo, with no manual seeding.

| Workstream | Deliverable |
|---|---|
| Stage 1 (Ingest) | Screens 1.1 (Source corpus list), 1.2 (Connect new source — Git + Upload tabs), 1.3 (Source detail). `POST /corpora`, `POST /corpora/{id}/sync`. Octokit-based service-account credential flow; admin-managed credentials. Upload path with 100 MB cap, multipart progress. |
| Stage 2 (Parse) | fparser2 worker (Python sidecar invoked via gRPC, or a .NET wrapper if a stable port exists — RFC required in week 7). Structural index (subs, COMMON, call graph, ISAM I/O). Screen 2.2 (Subroutine list with filters & bulk actions). Call-graph view (ReactFlow). |
| State machine | All transitions implemented and enforced server-side, including SUPERSEDED on source re-sync. Re-sync confirmation modal lands here. |
| Multi-corpus | Tested with three corpora in DEV. Pagination, filters, query-param state for shareable URLs. |
| Magic-link SME | Federated identity preferred; magic-link as a fallback for SMEs who cannot federate. Flow tested with a non-Nous tester. |
| Provider failover | Per-template primary/fallback. Trigger on 5xx, rate-limit, >120s timeout. Admin-toggleable. |
| Cost telemetry | Daily/weekly cost rollups by stage, provider, corpus, engineer. Hard-cap-per-day enforcement. |
| Comments | Threaded comments on specs and claims. Notifications dispatched on @-mentions. |

### Phase D · Hardening (weeks 11–14)

**Hard gate:** SOC 2 / ISO 27001 evidence package complete, accessibility audit passed at WCAG 2.1 AA, DR rehearsal completed within stated RTO, performance budgets met at p99.

| Workstream | Deliverable |
|---|---|
| Security | Threat model document. Pen test (third-party). Source data residency verification: provider audit-letter timestamps surfaced on admin dashboard. Credential rotation runbook tested. |
| Compliance | EU AI Act Article 14 evidence: documented oversight control = sign-off. ISO 27001 controls A.5/A.8/A.12/A.13/A.14/A.16 mapped. SOC 2 Type II evidence captured via audit trail and provider config logs. Audit-trail retention set to 7 years. |
| Performance | Load test against §9.1 budgets. Bottleneck remediation. p99 budgets enforced as alerts. |
| Observability | LLM cost & latency dashboards in Azure Monitor. Alert thresholds configured: error rate >5% over 15 min, p99 latency >60s, daily cost >120% of 7-day average. |
| Availability | Hot-standby Postgres with logical replication. Continuous blob replication. Quarterly DR drill scheduled and runbook authored. RPO ≤15 min, RTO ≤4 hr proven once. |
| Accessibility | Keyboard-only walkthrough of every screen. Screen-reader labels on every interactive element. Color-contrast audit against AA. Focus management on side sheets and modals. Reduced-motion support. |
| Browser matrix | Latest two stable majors of Chrome, Edge, Firefox, Safari verified. Sub-1280px viewport blocking message verified. |

### Phase E · Phase 1 cutover (weeks 15–16)

**Hard gate:** ≥10 real subroutines have completed the full extract → review → sign → scaffold cycle. Success criteria from §1.4 measured.

| Workstream | Deliverable |
|---|---|
| Production cutover | PROD environment provisioned. PROD HSM key created and rotated into config. Scaffold-output Git repo created and wired. Project 3 CI/CD pickup verified. |
| SME onboarding | Hands-on training session for Kiwiplan SMEs (Auckland). Reviewer guide (concise, scenario-driven — not a manual) shipped. |
| Pilot run | 10 subroutines walked through. Success-criteria dashboard live: ≥70% accept-without-edit, ≤2h SME review velocity, ≥95% citation accuracy, 100% scaffold compilability, 100% audit completeness, 100% data residency. |
| Operational handoff | On-call rotation defined. Runbook hand-over to platform team. Provider cost & alert dashboards reviewed in a joint session. |

---

## 5. UX strategy — how we hit "world class"

Full UX brief, design system, and screen blueprints live in `02_UX/`. The strategy in one page:

**The three things every screen earns the right to do:**

1. **Trace.** Every claim, every generated line, every audit event traces to its source. The single most valuable interaction in the product is *clicking a claim and watching the source highlight.* This is the demo's wow moment and the SME's daily working pattern. We invest disproportionately in citation binding, scroll-anchoring, and the pulse animation.

2. **Pace.** SMEs spend hours per spec. Their working surface (Screen 4.2) is engineered for sustained attention: outline pane that always shows progress, claim cards that collapse to compact accepted state to reduce visual noise, sticky source pane so code never disappears, keyboard shortcuts (`A` accept, `E` edit, `R` reject, `?` question, `J/K` next/previous claim).

3. **Trust.** The product asks humans to vouch for LLM output. The UI never overstates LLM confidence. Every LLM call surfaces provider, model, prompt template ID + version, token counts. Streaming is real, not faked. Confidence indicators are honest. Sign-off is a deliberate, ceremonial moment with a re-auth prompt and an explicit checkbox — never a casual click.

**Design-system principles** (full token set in `02_UX/design-system.md`):

- **Calm, not loud.** Off-white surfaces, navy text, accent orange only on primary actions and irrevocable warnings. The product must feel like a precision instrument, not a SaaS dashboard.
- **Code is a first-class citizen.** Monaco everywhere; we ship a custom Fortran language definition so syntax highlighting, line gutters, and citation highlights look the same in the source viewer, the live extraction stream, and the scaffold view.
- **Motion is informational.** Skeletons reserve space (no layout shift). Citation pulse is 1.5s ease-out. Stream tokens render with a one-token-lag cursor. Reduced-motion users get instant transitions.
- **Inline errors over modals.** Toast for success states; inline error blocks for failure states. Modals only for irrevocable actions (sign, archive, re-sync) and for explicit confirmation.
- **Empty and error states are products too.** Every list view has a designed empty state and a designed error state. Loading is never a generic spinner — it's a content-shaped skeleton.

**Accessibility (WCAG 2.1 AA, non-negotiable):** keyboard-first navigation, focus-visible everywhere, ARIA roles on the outline + progress strip in Screen 4.2, screen-reader announcements on stream events, color is never the only signal (state badges have icons + text + color).

---

## 6. Engineering practices

| Area | Standard |
|---|---|
| Branching | Trunk-based with short-lived feature branches. Required PR review. |
| Testing | Unit + integration + Playwright E2E. Coverage gates: ≥75% on backend domain, ≥60% on frontend. **Sign-off path has dedicated E2E coverage from week 5.** |
| Migrations | EF Core migrations in PR. Forward-only; rollback by forward-fix. Reviewed by two engineers. |
| Prompt templates | Versioned files under `prompts/`. Changes go through code review like any other code. Template ID + version recorded on every LLM call. |
| Secrets | Azure Key Vault. No secrets in environment files. Local dev uses a shared dev-only key. |
| Logging | Structured JSON, OpenTelemetry attributes. Source code never logged in plaintext. PII tagged and scrubbed by logging middleware. |
| ADRs | One per non-trivial decision (parser choice, Hangfire vs Azure Service Bus, structured-output mode strategy, etc.). `04_Delivery/adr/`. |
| Definition of Done | See `03_Backlog/definition-of-done.md`. Every story has tests, telemetry, and an audit-trail entry where applicable. |

---

## 7. Team & roles (proposed)

| Role | Headcount | Coverage |
|---|---|---|
| Engineering lead | 1 | Architecture, sequencing, gate sign-off |
| Backend engineers (.NET) | 3 | API, workers, LLM provider abstraction, persistence |
| Frontend engineers (React/TS) | 2 | Design system, screens, streaming UX, Monaco integration |
| Platform / DevOps | 1 | IaC, AKS, observability, HSM provisioning, CI/CD |
| Product designer | 1 | UX strategy, screen blueprints, design system, accessibility |
| Quality engineer | 1 | E2E scenarios, performance budgets, security testing coordination |
| Product / delivery | 0.5 | Stakeholder cadence, scope arbitration, release notes |

Total: 9.5 FTE. Embedded SME (Kiwiplan) at 1 day per week from week 5; full-time during Phase E.

---

## 8. Risk posture

Top risks (full register: `04_Delivery/risk-register.md`):

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| LLM provider outage during demo | Medium | High | Pre-recorded backup at 35s timeout; failover disabled for the demo (recorded fallback is more controllable). |
| fparser2 cannot parse Kiwiplan dialect cleanly | Medium | High | RFC due week 7; small, deterministic corpus tested against fparser2 in week 1 spike; fallback parser sketch retained. |
| HSM provisioning slips | Low | High | Test HSM provisioned week 1; staging week 5; prod by end of week 14 — three weeks of runway. |
| Spec-extraction quality below 70% on real corpus | Medium | Medium | Prompt template iteration each week of Phase C. Per-corpus prompt overrides supported. Telemetry shows accept-rate per template version. |
| SME availability is the bottleneck for Phase E | High | Medium | Defined working blocks (4-hour units, per spec); review queue UX optimised for that pattern; pilot scope is 10 subroutines — sized for two SMEs over two weeks. |
| Source residency violation | Low | Critical | Provider config logged on every call; admin dashboard surfaces audit-letter timestamps; gate test ("data residency") in CI rejects deploy if config differs. |
| Sign-off irreversibility misunderstood by SMEs | Medium | High | Re-auth gate; signed copy display before confirm; in-product training video on first SME login; explicit checkbox copy. |

---

## 9. Out of scope (v1) — repeated explicitly

These are *not* delivered by this plan. Any of them moves into a later version through change control:

- Multi-tenancy / per-customer data isolation
- Languages other than Fortran 77/90/95
- Automatic Fortran-to-runtime business-logic implementation
- Direct deployment to customer or production systems
- Mobile / sub-1280px viewport
- Locales other than English
- UI editing of prompt templates (templates ship through code review)

---

## 10. How this set of documents fits together

```
C:\Astra RE Harness\
├── 00_DevelopmentPlan.md                  (this document — master)
├── 01_Architecture\
│   ├── architecture-overview.md           (system, data flow, state machine)
│   ├── data-model.md                       (entities, schemas, signing)
│   ├── api-surface.md                      (endpoints, SSE protocol)
│   ├── llm-integration.md                  (providers, prompts, observability)
│   └── security-and-residency.md           (controls, audit, compliance)
├── 02_UX\
│   ├── ux-vision-and-principles.md         (the three principles, in depth)
│   ├── design-system.md                    (tokens, primitives, motion)
│   ├── information-architecture.md         (nav, persona-keyed entry points)
│   └── screen-blueprints.md                (per-screen interaction spec)
├── 03_Backlog\
│   ├── epics-and-milestones.md             (epic ↔ milestone ↔ phase map)
│   ├── definition-of-done.md
│   └── prioritized-stories.md              (Phase B + Phase C story list)
├── 04_Delivery\
│   ├── phase-plan-and-gates.md             (week-by-week, gate criteria)
│   ├── risk-register.md
│   ├── ci-cd-and-environments.md
│   └── demo-build-plan.md                  (Appendix A demo, expanded)
└── 05_Reference\
    └── source-spec-extract.md              (text extract of the source spec)
```

The master plan (this document) is the entry point. Each supporting artifact takes one slice and goes deep without contradicting the master. If the master and a supporting artifact disagree, the supporting artifact is right *for its domain* — and the master needs an update.

---

## 11. Decision log (kickoff)

| # | Decision | Date | Owner | Rationale |
|---|---|---|---|---|
| D1 | Adopt the spec verbatim as scope; any deviation requires change control. | 2026-05-05 | Eng lead | Reduces ambiguity; the spec is unusually thorough. |
| D2 | Demo slice ships first (week 6), full pipeline second. | 2026-05-05 | Eng lead | The 4-minute Appendix A flow is the most important risk to retire — we prove the architecture under demo conditions before broadening. |
| D3 | Single component library (shadcn/ui) + custom Monaco language, no other component vendors. | 2026-05-05 | Designer + FE lead | Visual consistency is part of the trust story. |
| D4 | Hangfire on .NET 8 for jobs; defer Azure Service Bus until we have a concrete need. | 2026-05-05 | BE lead | One fewer dependency; matches Project 3 stack; Hangfire's persistence model satisfies §9.3 ("In-flight LLM calls survive worker restarts"). |
| D5 | fparser2 as a Python sidecar via gRPC. | 2026-05-05 | BE lead + Platform | fparser2 is mature in Python; no stable .NET port. Sidecar isolates the GIL-bound runtime. RFC due week 7 to confirm. |
| D6 | Test HSM key in DEV from day one. | 2026-05-05 | Platform | Removes a class of late-stage surprises; the sign-off path is exercised against a real HSM from the first sign in DEV. |

Decisions D1–D6 are committed. D7 onwards captured as ADRs in `04_Delivery/adr/`.

---

## 12. Open questions for stakeholders

These are the gating questions that, left unanswered, will slow the plan. Owner & due-by listed.

1. **Scaffold-output Git repository ownership** — does Project 3 own it, or do we provision it? *Owner: Eng lead. Due: end of week 1.*
2. **Kiwiplan SME identity strategy** — federated from Auckland tenancy, or magic-link only? *Owner: Identity lead + Kiwiplan stakeholder. Due: end of week 2.*
3. **Production HSM region** — North Europe vs Australia East (data residency vs latency). *Owner: Platform. Due: end of week 6.*
4. **Embedded SME availability for Phase E pilot** — names + working blocks. *Owner: PM + Kiwiplan. Due: end of week 8.*
5. **Audit-trail export format approval from Advantive auditors** — sample export to be sent for review. *Owner: PM. Due: end of week 10.*

---

*End of master plan. Continue with `01_Architecture/architecture-overview.md` for the technical view, or `02_UX/ux-vision-and-principles.md` for the experience view.*
