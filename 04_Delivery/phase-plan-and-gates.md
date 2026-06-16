# Phase Plan & Gates

**Status:** Kickoff (v0.1)
**Companion:** `00_DevelopmentPlan.md`, `risk-register.md`, `demo-build-plan.md`

This document is the operational schedule. Each phase has a target date, a hard gate (must-pass), a soft gate (should-pass), and a fallback if the hard gate slips.

---

## Schedule at a glance

| Phase | Weeks | Calendar (target) | Milestone |
|---|---|---|---|
| A · Foundations | 1–2 | 2026-05-04 → 2026-05-17 | M1 |
| B · Demo slice | 3–6 | 2026-05-18 → 2026-06-14 | M2 (defense-call demo) |
| C · Full pipeline | 7–10 | 2026-06-15 → 2026-07-12 | M3 |
| D · Hardening | 11–14 | 2026-07-13 → 2026-08-09 | M4 |
| E · Production cutover | 15–16 | 2026-08-10 → 2026-08-23 | M5 |

**Buffer:** 1 week (week 17, 2026-08-24 → 2026-08-30) absorbs slip from Phase E into the final cutover commitment of 2026-08-28. Buffer is *not* allocated to Phase E work.

---

## Phase A · Foundations · weeks 1–2

**Hard gate (M1):**

- [ ] AKS DEV cluster reachable; Helm deploy of API + worker + frontend + parser sidecar succeeds.
- [ ] `/health` endpoint returns 200 and reports DB, Blob, KeyVault dependencies as healthy.
- [ ] Engineer persona can sign in via Entra ID; SPA renders the app shell.
- [ ] OpenTelemetry trace from a `/health` call lands in Azure Monitor within 30 seconds.
- [ ] Test HSM key in DEV Key Vault; a no-op `Sign` operation succeeds.
- [ ] Postgres migration `0001_baseline` applied; subsequent re-deploy is a no-op.
- [ ] Design tokens committed; Storybook deployed; ≥30 primitives present with all states.
- [ ] CI pipeline runs lint, unit, integration on PRs; failing CI blocks merge.

**Soft gate:**

- [ ] ADR-001 (Hangfire vs Service Bus), ADR-002 (fparser2 sidecar), ADR-003 (Auth.js choice) merged.
- [ ] Storybook accessible at a stable URL.
- [ ] Internal stand-up cadence + Demo Friday cadence in place.

**Fallback if hard gate slips:**

- A 1-week extension is permitted by deferring some EPIC-3 primitives (DataTable, Combobox) into week 3. Tokens, Button/Card/Input/Modal are non-negotiable.
- Identity wiring is non-negotiable; without it, Phase B cannot start.

**Sign-off:** Eng lead.

---

## Phase B · Demo slice · weeks 3–6

**Hard gate (M2):**

- [ ] The 4-minute Appendix A flow runs end-to-end on STAGING with the `CONSUME_ROLL` seed corpus:
  1. Subroutine detail (2.1) renders the source.
  2. Click "Extract spec" → live extraction (3.1) streams real Anthropic tokens; citations pulse on the source pane.
  3. Draft spec (3.2) renders with full citation binding.
  4. Switch to SME persona; routing-note + claim-card review on (4.2).
  5. Accept several, edit one, resolve one open question.
  6. Sign-off modal → re-auth → real HSM signature → SIGNED state.
  7. Engineer triggers scaffold → live scaffold (5.1) → scaffold artifact view (5.2).
  8. Audit trail (4.3) shows the full event chain.
- [ ] Three rehearsals completed in week 6, each video-recorded; the third recording is the Appendix A.4 backup.
- [ ] Sign-off E2E (Playwright) green nightly for at least 5 consecutive nights.
- [ ] HSM signing in DEV + STAGING produces verifiable signatures that pass `verify-signed-spec.sh`.

**Soft gate:**

- [ ] Audit trail (4.3) includes diff payloads for spec edits.
- [ ] Citation post-validation surfaces unresolved citations as warning chips.
- [ ] Cost rollup populated with at least 7 days of demo-rehearsal data.

**Fallback if hard gate slips:**

- The defense-call demo can use a *partly recorded* version of the flow per Appendix A.4 — but the live LLM call (Stage 3) is non-negotiable; the system must demonstrate at least one real Anthropic invocation. If sign-off is still flaky, sign the spec offline (manually invoke the API) and demo the SIGNED state from a pre-sign'd spec — this is acceptable as long as the audience is told.
- The scaffold step can be replaced with a pre-generated artifact view if Stage 5 streaming is unstable.

**Sign-off:** Eng lead + Designer + PM.

---

## Phase C · Full pipeline · weeks 7–10

**Hard gate (M3):**

- [ ] A new corpus can be connected from a real Git URL via Screen 1.2; the system pulls, hashes, parses, and lists subroutines without manual seeding.
- [ ] Re-sync correctly transitions prior signed specs to SUPERSEDED.
- [ ] At least three corpora exist in DEV; the corpus list paginates and filters as expected.
- [ ] Magic-link SME flow tested with a non-Nous tester from outside the corporate network.
- [ ] Provider failover triggers correctly on simulated 5xx and rate-limit (admin-toggleable).
- [ ] Real Octokit commit-to-Git works against the scaffold-output repo; Project 3 CI sees the branch.
- [ ] Threaded comments work on specs and claims; @-mentions dispatch in-app + email.
- [ ] All three Azure Monitor dashboards live and populated.
- [ ] Hard-cap-per-day enforcement verified by a deliberate-trip test.
- [ ] State-machine invariants covered by ≥30 transition tests.

**Soft gate:**

- [ ] Global command bar (`Cmd-K`) live.
- [ ] Cross-corpus subroutine search live.

**Fallback if hard gate slips:**

- A one-week extension into week 11 is permitted. This compresses Phase D by one week.
- Magic-link can be deferred to week 11–12 if Entra federation works for the pilot SMEs.

**Sign-off:** Eng lead + PM.

---

## Phase D · Hardening · weeks 11–14

**Hard gate (M4):**

- [ ] Threat model document approved by Nous security.
- [ ] Pen test executed; high/critical findings remediated; medium findings tracked.
- [ ] Source-residency CI gate green against PROD manifest.
- [ ] Provider audit-letter timestamps fresh (<12 months) on admin dashboard.
- [ ] Credential rotation runbook executed once on STAGING.
- [ ] HSM emergency rotation runbook executed once on STAGING.
- [ ] Audit-trail retention partitioning live; cold-archive runbook authored.
- [ ] DR drill completed: RPO ≤15 min, RTO ≤4 hr, runbook updated with findings.
- [ ] WCAG 2.1 AA audit passed (≤3 minor findings open with remediation tickets).
- [ ] Browser matrix verified (Chrome, Edge, Firefox, Safari latest two majors).
- [ ] Performance budgets per spec §9.1 met at p99 in load test.

**Soft gate:**

- [ ] On-call rotation defined and rehearsed.
- [ ] Stakeholder demo to Advantive observers completed.

**Fallback if hard gate slips:**

- Phase E cutover can slip into the buffer week 17. PROD provisioning can begin in parallel during the slip without consuming Phase D's hardening capacity.

**Sign-off:** Eng lead + Security + PM.

---

## Phase E · Production cutover · weeks 15–16

**Hard gate (M5):**

- [ ] PROD environment provisioned and healthy.
- [ ] PROD HSM signing key created; CI gate verifies adapter ConfigVersion matches PROD manifest.
- [ ] Scaffold-output Git repository in production; Project 3 CI confirms successful pickup of one scaffold.
- [ ] SME training session completed (Auckland); reviewer guide shipped.
- [ ] ≥10 real Kiwiplan subroutines have completed the full extract → review → sign → scaffold cycle in PROD.
- [ ] Success criteria measured (per spec §1.4):
  - [ ] ≥70% of LLM-drafted spec sections accepted by SME without material edit
  - [ ] ≤2h average SME review velocity per subroutine
  - [ ] ≥95% citation accuracy
  - [ ] 100% scaffold compilability
  - [ ] 100% audit completeness
  - [ ] 100% data residency proof on every call
- [ ] On-call rotation handed off to platform team; runbook in their hands.

**Soft gate:**

- [ ] Reviewer guide includes 3 worked examples drawn from real corpora.
- [ ] Cost dashboard reviewed jointly with stakeholders.

**Fallback if hard gate slips:**

- Pilot scope can be reduced from 10 to 6 real subroutines, with the remaining 4 deferred to a Phase F post-cutover follow-up. Success criteria are measured on whatever subset completes.

**Sign-off:** Eng lead + PM + Kiwiplan stakeholder + Advantive observer.

---

## Phase 10.0 · VB6 source language · weeks 1–10 (post-Phase-9 expansion)

**Status:** Plan kicked off 2026-06-14. See `phase-10.0-vb6-source-language.md` for the full sub-phase breakdown and ADR-035 / 036 / 037 for the architecture decisions.

**Hard gate (Δ-10.0):**

- [ ] `vb6-parser-sidecar` returns AST + call-graph for all 12 forms of the seed corpus in < 5s.
- [ ] `.frm` parser correctly extracts both code-block + property-bag for every form.
- [ ] `vb6.schema.json` validates against ≥ 6 hand-authored example specs covering each new claim kind.
- [ ] Mock provider extracts a spec for `OrderEntry_Submit` matching the canonical reference.
- [ ] Real Anthropic provider achieves ≥ 80% claim coverage on the Golden Dataset.
- [ ] All three .NET 10 scaffold archetypes (WinForms / Blazor / minimal API) build and run `dotnet test` green.
- [ ] `vb6-sidecar` compiles + runs the seed corpus's `OrderEntry_Submit` against the .NET 10 candidate; outputs match byte-for-byte on a fixed input set.
- [ ] Property-based test generates ≥ 100 inputs per invariant; ≥ 95% pass.
- [ ] VB6 Inventory Sample seeded in DEV; `/projects` shows it alongside MINPACK / LAPACK / Indy / fmt.
- [ ] Language picker on the New Corpus page lists VB6 as a fifth schema choice.

**Soft gate:**

- [ ] ADR-035 / 036 / 037 merged.
- [ ] Demo video (~2 min) recorded for the OrderEntry walkthrough.
- [ ] Phase 9.2.c help-banner copy added for VB6 (one block per target paradigm).

**Fallback if hard gate slips:**

- Demoable MVP path: skip 10.0.f (equivalence sidecar) and 10.0.g (property generators). Rely on Gates 1, 2, and shadow-mode Gate 4 only. Cuts ~5 weeks. Equivalence sidecar lands in follow-on 10.0.j.
- If Windows container infrastructure slips: Wine-based dev-tier sidecar covers most non-COM routines. Document the production-confirmed badge on every signed spec validated against Wine only.

**Sequencing:** 10.0.a (parser) → 10.0.b (schema) → 10.0.c (prompts) → 10.0.d (archetypes, sub-staged WinForms → minimal API → Blazor) → 10.0.e (Golden Dataset) → 10.0.f (equivalence sidecar) → 10.0.g (property generators) → 10.0.h (seed corpus) → 10.0.i (E2E demo).

**Owner:** Platform team. **Sign-off:** Eng lead + PM + first customer engagement lead.

---

## Cadence rituals

| Ritual | Cadence | Format |
|---|---|---|
| Daily standup | Daily, 15 min | Async-first, in-person 2× per week |
| Demo Friday | Weekly during Phase B, biweekly thereafter | Live walkthrough on STAGING; recorded |
| Risk review | Weekly | Top 3 risks reviewed against `risk-register.md` |
| Stakeholder sync | Biweekly | Eng lead + PM + Kiwiplan + Advantive (Phase D onwards) |
| ADR triage | Weekly Monday | Open ADRs reviewed and merged |
| Retro | Phase end | Per-phase retro, written up in `04_Delivery/retros/` |

---

## Decision log entries (this phase plan)

- D-PP-1: Phase B is committed as 4 weeks; the demo cannot move. *Owner: Eng lead.*
- D-PP-2: Phase D is 4 weeks of hardening with no feature work. *Owner: Eng lead.*
- D-PP-3: Buffer week 17 is reserved for Phase E slip; not allocated to Phase E work. *Owner: PM.*
