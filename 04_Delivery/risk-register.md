# Risk Register

**Status:** Kickoff (v0.1) — to be reviewed weekly.
**Owner:** Eng lead.
**Companion:** `phase-plan-and-gates.md`, `00_DevelopmentPlan.md` §8.

Risks are scored on **likelihood** (L = Low / M = Medium / H = High) × **impact** (L / M / H / Critical).

---

## 1. Active risks

### R-01 · LLM provider outage during defense-call demo

- **Score:** L · Critical
- **Phase:** B
- **Trigger:** Anthropic or Azure OpenAI returns 5xx, 429, or timeouts during the live demo.
- **Mitigation (primary):** Pre-recorded backup of the full demo flow per Appendix A.4. Two pre-warmed sessions: production (live) and recorded. Network independence — primary connection plus a tethered fallback.
- **Mitigation (in-app):** 60-second timeout on the demo extraction call; on timeout, cached response from the most recent rehearsal renders with a "loaded from cache" indicator.
- **Trigger threshold:** If live LLM call exceeds 35 seconds or fails, presenter switches to recording mid-flow with controlled handoff line.
- **Owner:** Eng lead.
- **Review:** Weekly during Phase B; rehearsals in week 5 and 6.

### R-02 · fparser2 cannot parse Kiwiplan dialect cleanly

- **Score:** M · H
- **Phase:** A (spike) → C (full integration)
- **Trigger:** fparser2 fails on representative Kiwiplan source samples (COMMON blocks, ISAM I/O patterns, vendor extensions).
- **Mitigation:** Week-1 spike with a small, deterministic corpus tested against fparser2. ADR due week 7 confirms or pivots. Fallback parsers identified: `flang` Python bindings, hand-written tokenizer for the worst-affected constructs. We will not write a Fortran parser from scratch.
- **Owner:** BE lead + Platform.
- **Review:** Weekly until ADR-002 is merged.

### R-03 · HSM provisioning slips

- **Score:** L · H
- **Phase:** A (DEV) → B (STAGING) → D (PROD)
- **Trigger:** Azure Key Vault Managed HSM provisioning takes longer than expected; cross-team approval needed.
- **Mitigation:** Test HSM provisioned in week 1 (story 1-04) — if this slips, the entire Phase B gate is at risk. STAGING HSM provisioned in week 5; PROD HSM provisioned by end of week 14 (three weeks of runway before Phase E).
- **Backup:** Software-only signing using local keys is acceptable in DEV only; staging and PROD must use HSM. CI gate prevents staging/PROD deploys without HSM.
- **Owner:** Platform.
- **Review:** Weekly; escalation to Nous platform leadership if week-1 provisioning is not in flight by 2026-05-08.

### R-04 · Spec-extraction quality below 70% on real corpus

- **Score:** M · M
- **Phase:** C (early signal) → E (measured)
- **Trigger:** Pilot run shows accept-without-edit rate well below the 70% target.
- **Mitigation:** Prompt template iteration each week of Phase C with telemetry on accept-rate per template version. Per-corpus prompt overrides supported. Dual-route capability (admin-set fraction) to compare versions side-by-side.
- **Backup:** Lower the success-criteria target with stakeholder agreement, or extend Phase E pilot to gather more data.
- **Owner:** BE lead + Designer (prompt iteration is partly a design problem).
- **Review:** Biweekly during Phase C; weekly during Phase E.

### R-05 · SME availability is the bottleneck for Phase E

- **Score:** H · M
- **Phase:** E
- **Trigger:** Pilot SMEs cannot allocate the working blocks needed to walk 10 subroutines.
- **Mitigation:** Working blocks defined in 4-hour units (per spec §2 persona velocity). Review queue UX optimized for that pattern. Pilot scope sized for two SMEs over two weeks (10 subroutines × 2 hours each = 20 hours, comfortably within scope).
- **Backup:** Scope can drop to 6 real subroutines for the cutover gate.
- **Owner:** PM + Kiwiplan stakeholder.
- **Review:** Weekly from week 8 (when SME engagement is locked).

### R-06 · Source residency violation

- **Score:** L · Critical
- **Phase:** All
- **Trigger:** Provider configuration drifts; customer source persists at the provider; audit-letter expires; manifest divergence.
- **Mitigation (defense in depth):** (a) Adapter-level config snapshot recorded on every `LlmCall`; (b) Admin dashboard surfaces active config + audit-letter timestamp (amber >12 mo, red >18 mo); (c) CI gate compares running adapter `ConfigVersion` to per-environment expected manifest, fails deploy on mismatch.
- **Owner:** Platform + Security.
- **Review:** Weekly during Phase D (security hardening).

### R-07 · Sign-off irreversibility misunderstood by SMEs

- **Score:** M · H
- **Phase:** E
- **Trigger:** A SME signs a spec they did not intend to sign and asks for un-sign.
- **Mitigation:** Re-auth gate (≤5 min); explicit canonical-sentence checkbox; signed-name display; 30-minute training session with a guided walkthrough of the sign modal; in-product education the first time a SME approaches sign-off.
- **Backup:** Supersession path is documented and walked through in training. The SME knows the correct response is "extract a new spec," not "un-sign."
- **Owner:** Designer + PM.
- **Review:** Once before pilot kickoff.

### R-08 · CI/CD integration with Project 3 fails

- **Score:** L · M
- **Phase:** C (first integration) → E (sustained)
- **Trigger:** Project 3 CI does not pick up scaffold branches, or rejects them due to spec verification failures.
- **Mitigation:** Scaffold-output repo configured in week 1 of Phase C with a Project 3 engineer in the loop. Public verification key published per environment from week 1. Joint verification test in week 9.
- **Owner:** PM + BE lead.
- **Review:** Once at Phase C kickoff; once at Phase E kickoff.

### R-09 · Citation accuracy below 95%

- **Score:** M · M
- **Phase:** C → E
- **Trigger:** LLM citations frequently reference non-existent or incorrect line ranges.
- **Mitigation:** Citation post-validation surfaces unresolved citations as warning chips in the draft view (engineer + SME both see). Prompt template reinforces line-range citation discipline. Telemetry tracks unresolved-citation rate per template version; rate >5% triggers prompt iteration.
- **Backup:** Stakeholder agreement to re-define "accuracy" — e.g., overlap with the *correct* range counts even if not exact.
- **Owner:** BE lead + Designer.
- **Review:** Weekly during Phase C.

### R-10 · Demo prompt-engineering fragility

- **Score:** M · M
- **Phase:** B
- **Trigger:** Different LLM responses across rehearsals make the demo unpredictable.
- **Mitigation:** Synthetic Fortran sample (`CONSUME_ROLL`) is deterministic — same input across rehearsals. Prompt parameters tuned for low temperature (0.1–0.2). Per-corpus prompt-template version pinned for the demo.
- **Backup:** If even tuned prompts vary too much, fall back to cached response per Appendix A.4.
- **Owner:** Eng lead.
- **Review:** During every demo rehearsal.

### R-11 · WCAG 2.1 AA gaps surface late

- **Score:** M · M
- **Phase:** D
- **Trigger:** A11y audit in week 12 surfaces dozens of violations.
- **Mitigation:** Build to AA from week 1. Storybook contrast checks per token. Keyboard walkthrough of every screen as a Demo Friday item. Reduced-motion check on every motion-using component.
- **Backup:** Phase D capacity allocates explicit a11y remediation time. Findings beyond capacity are tracked into a Phase F.
- **Owner:** FE lead + Designer.
- **Review:** Demo Friday weekly.

### R-12 · Cost overruns from runaway extractions

- **Score:** L · M
- **Phase:** C (early signal) onwards
- **Trigger:** A misconfigured corpus or template triggers many high-cost LLM calls.
- **Mitigation:** Per-template token budgets enforced pre-flight. Per-corpus daily quota. Per-environment daily hard cap (auto-reject when hit; admin-page). Cost rollup hourly; daily-cost-vs-7-day-average alert at 120%.
- **Owner:** Platform + Eng lead.
- **Review:** Weekly during Phase C; daily during Phase E.

### R-13 · Scope creep from "just one more" requests

- **Score:** H · M
- **Phase:** All
- **Trigger:** Stakeholders request UI flourishes, additional dashboards, or extended persona capabilities mid-phase.
- **Mitigation:** D1 (D1: "Adopt the spec verbatim as scope; any deviation requires change control") in master plan §11. Change-control template in `04_Delivery/change-control-template.md`. PM is the gatekeeper.
- **Owner:** PM.
- **Review:** Weekly stakeholder sync.

### R-14 · Stream UX feels janky on slow connections

- **Score:** M · M
- **Phase:** B
- **Trigger:** SSE on weak connections produces choppy citation pulses or visible token gaps.
- **Mitigation:** Token batching at the API to smooth out provider bursts. Buffered streaming on reconnect (Last-Event-ID handling). Telemetry on stream gaps; threshold triggers a backend tweak.
- **Backup:** Offer a "Show all at once" toggle that disables streaming animation in low-bandwidth conditions; default on for ≤3 Mbps detected.
- **Owner:** FE lead + BE lead.
- **Review:** Demo Friday.

### R-15 · Magic-link SME flow rejected by Kiwiplan IT

- **Score:** M · M
- **Phase:** C
- **Trigger:** Kiwiplan IT does not allow non-federated email-based access.
- **Mitigation:** Federated identity is the preferred path; magic-link is the fallback. Conversation with Kiwiplan IT scheduled in Phase A (open question 2 in master plan §12).
- **Backup:** All SMEs federate from Auckland tenancy; magic-link is shelved.
- **Owner:** Identity lead + PM.
- **Review:** Once at week 2; revisit at week 7.

---

## 2. Closed risks

(Empty at kickoff. Closed risks are moved here for the historical record.)

---

## 3. Risk review process

- **Weekly:** Eng lead reviews top-3 active risks at the Monday standup. Score updates committed to this file.
- **At gate:** Phase gate sign-off requires the active risk register to be reviewed and any score changes documented.
- **At incident:** Any production incident triggers a risk-register review within 24 hours.

The register is a living document. New risks added with a fresh ID; closed risks moved to §2 with a closure note.
