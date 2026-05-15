# Epics & Milestones

**Status:** Kickoff (v0.1)
**Companion:** `00_DevelopmentPlan.md`, `prioritized-stories.md`

This file maps work into **epics** (cohesive bodies of work) and **milestones** (gate-bound deliverables tied to phases). Stories live in `prioritized-stories.md`; each story rolls up to one epic.

---

## 1. Milestones (one per phase)

| # | Milestone | Phase | Target | Hard gate |
|---|---|---|---|---|
| M1 | Foundations green | A | End of week 2 | Hello-world of every layer; design tokens locked; CI deploys to DEV. |
| M2 | Demo-ready slice on STAGING | B | End of week 6 | The 4-minute Appendix A flow runs end-to-end with real LLMs and HSM. Three rehearsals recorded. |
| M3 | Full pipeline E2E | C | End of week 10 | A new corpus → ingest → parse → extract → review → sign → scaffold → commit, with no manual seeding, on STAGING. |
| M4 | Hardened & accessible | D | End of week 14 | SOC 2/ISO evidence package; pen test remediated; WCAG 2.1 AA pass; perf budgets met; DR drill executed. |
| M5 | Phase 1 production cutover | E | End of week 16 | PROD live; ≥10 real subroutines through full cycle; success criteria measured. |

---

## 2. Epic catalogue

Twelve epics. Each is sized to span 2–6 weeks of effort across the team. Each rolls up to one or more milestones.

### EPIC-1 · Platform foundations

Sets up the substrate every other epic builds on. **Milestone:** M1.

- Bicep/Terraform IaC for AKS, Postgres, Blob, Key Vault, ACR, App Gateway.
- GitHub Actions: build, test, lint, container build, Helm deploy to DEV on `main`.
- OpenTelemetry collector + Azure Monitor wiring.
- Helm charts (one per service: api, worker, frontend, parser-sidecar).
- `health` endpoint on API; baseline integration tests.

### EPIC-2 · Identity & RBAC

Authentication and authorization across personas. **Milestones:** M1 (engineer + admin), M3 (SME magic-link).

- Entra ID app registrations.
- Auth.js wiring on the frontend with silent refresh.
- API JWT validation, persona policies, MediatR auth pipeline.
- Sign-off re-auth gate (`auth_time` ≤ 5 min).
- Magic-link issuance + exchange (Phase C).
- Audit-trail entries for every auth event.

### EPIC-3 · Design system & app shell

Tokens, primitives, layout, navigation. **Milestone:** M1.

- Token set committed (`design-system.md` §1–6).
- Primitive components in Storybook (`design-system.md` §7).
- App shell: top bar, left nav, breadcrumb, persona menu.
- Routing skeleton, persona-keyed entry points.
- A11y baseline: focus, ARIA roles, reduced-motion handling.

### EPIC-4 · Stage 3 — Extract

Live extraction, draft spec view, prompt template loader. **Milestone:** M2.

- `ILlmProvider` abstraction + Anthropic adapter.
- `fortran-extract-v3.2` template loader.
- `POST /subroutines/{id}/extract` + Hangfire job.
- SSE endpoint and frontend consumer.
- Citation pulse on Monaco source pane (custom decoration).
- Schema validation against `spec/v1.json`.
- Citation post-validation.
- Screen 3.1 (live extraction overlay).
- Screen 3.2 (draft spec view) — engineer edit mode.

### EPIC-5 · Stage 4 — Review & Sign

The keystone workflow. **Milestone:** M2.

- Routing to SME (side sheet, notification, persona handoff).
- Screen 4.1 (My reviews).
- Screen 4.2 (Spec review with claim cards, outline pane, source binding, keyboard shortcuts).
- Claim-level review API (`POST /specs/{id}/claims/{path}/review`).
- Open-questions resolution flow.
- Screen 4.3 (Audit trail timeline — basic).
- Sign-off pipeline: canonicalize, hash, HSM sign, immutable blob write.
- Sign-off modal with citation integrity check + re-auth + canonical-sentence checkbox.
- `signed-specs` immutable container provisioned via IaC.

### EPIC-6 · Stage 5 — Scaffold

Generation, viewer, commit. **Milestone:** M2 (viewer + stub commit), M3 (real commit).

- Azure OpenAI adapter on `ILlmProvider`.
- `dotnet-scaffold-v2.0` template loader.
- `POST /specs/{id}/scaffold` + Hangfire job.
- Streaming + multi-file emission.
- Schema validation against `scaffold/v1.json`.
- Screen 5.1 (live scaffold generation).
- Screen 5.2 (scaffold artifact view).
- Octokit commit-to-Git flow (real in M3).

### EPIC-7 · Stage 1 — Ingest

Connect Git or upload Fortran. **Milestone:** M3.

- `POST /corpora` (Git + Upload).
- Octokit-based source pull with admin-managed credentials.
- Multipart upload with 100 MB cap and progress events.
- Hash, version-tag, persist.
- Screen 1.1 (corpus list).
- Screen 1.2 (connect source side sheet).
- Screen 1.3 (corpus detail).
- Re-sync flow with SUPERSEDED transitions.

### EPIC-8 · Stage 2 — Parse

AST + structural index. **Milestone:** M3.

- fparser2 sidecar in Python with gRPC interface.
- ParseCorpusJob in Hangfire.
- Structural index: subs, COMMON, call graph, ISAM I/O, magic numbers.
- Screen 2.2 (subroutine list with filters).
- Subroutine detail tabs: Structure, Call graph (ReactFlow).

### EPIC-9 · State machine & supersession

Cross-cutting. **Milestone:** M3.

- Server-side state-transition enforcement in MediatR pipeline behaviors.
- SUPERSEDED transitions on source re-sync.
- State badge consistency across screens.
- Comprehensive E2E coverage for valid + invalid transitions.

### EPIC-10 · Observability & cost

Metrics, dashboards, alerts. **Milestone:** M2 (basic), M3 (dashboards live), M4 (alerts polished).

- OpenTelemetry attributes per spec §6.4 on every LLM call.
- Cost rollup job + admin dashboard.
- Daily hard-cap enforcement with admin override (audited).
- Three Azure Monitor dashboards: operational health, LLM cost & latency, pipeline throughput.
- PagerDuty alert integration with thresholds from the spec.

### EPIC-11 · Security & compliance hardening

Threat model, pen test, residency proof, accessibility audit. **Milestone:** M4.

- Threat model document.
- Third-party pen test + remediation.
- Provider audit-letter dashboard surface.
- Source-residency CI gate.
- Credential rotation runbook executed in staging.
- HSM emergency rotation runbook executed in staging.
- WCAG 2.1 AA audit + remediation.
- DR drill (RPO ≤15 min, RTO ≤4 hr).

### EPIC-12 · Phase 1 production cutover

The release. **Milestone:** M5.

- PROD environment provisioned (with PROD HSM key).
- Scaffold-output Git repo created and CI pickup verified by Project 3.
- SME training session and reviewer guide.
- Pilot run of 10 real subroutines.
- Success-criteria dashboard live.
- Operational handoff to platform team.

---

## 3. Epic ↔ Milestone matrix

|  | M1 (A) | M2 (B) | M3 (C) | M4 (D) | M5 (E) |
|---|---|---|---|---|---|
| EPIC-1 Platform foundations | ● | | | | |
| EPIC-2 Identity & RBAC | ◐ | | ● | | |
| EPIC-3 Design system & app shell | ● | | | | |
| EPIC-4 Stage 3 Extract | | ● | | | |
| EPIC-5 Stage 4 Review & Sign | | ● | | | |
| EPIC-6 Stage 5 Scaffold | | ◐ | ● | | |
| EPIC-7 Stage 1 Ingest | | | ● | | |
| EPIC-8 Stage 2 Parse | | | ● | | |
| EPIC-9 State machine & supersession | | | ● | | |
| EPIC-10 Observability & cost | | ◐ | ● | ◐ | |
| EPIC-11 Security & compliance hardening | | | | ● | |
| EPIC-12 Phase 1 production cutover | | | | | ● |

`●` = primarily delivered in this milestone. `◐` = partially delivered.

---

## 4. Cadence

- **Weekly review** every Monday: progress against the current milestone, gate-criteria status, top-three risks.
- **Demo Friday** every Friday in Phase B: the team walks the demo flow end-to-end on STAGING. Recorded.
- **ADR review** weekly: any new ADR is opened during the week and merged on Monday.
- **Stakeholder sync** every two weeks: PM + Eng lead + Kiwiplan stakeholder.

---

## 5. Sign-off rights

- **Story DoD met** → engineer can mark complete (per `definition-of-done.md`).
- **Epic complete** → Eng lead reviews and sign-off.
- **Milestone gate** → Eng lead + Designer + PM jointly sign-off; recorded in `04_Delivery/phase-plan-and-gates.md`.
