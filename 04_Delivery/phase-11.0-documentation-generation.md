# Phase 11.0 — Documentation Generation (Fortran first)

**Status:** In progress (foundation landed; vertical slice next)
**Companion ADRs:** ADR-038 (doc-as-signed-artefact)
**Predecessor:** Phase 10.3 (VB6 four-gate close-out)

---

## Objective

Stand up production-grade **transition documentation** generation for legacy codebases. The first customer use case is takeover-without-immediate-modernisation: the customer is handing us a Fortran codebase that we have to operate while a modernisation backlog is sequenced, and they need real documentation now so the handover doesn't depend on one or two retiring SMEs.

This is **not** code-level documentation. Doxygen / Ford / Sphinx-fortran already solve that and we're not duplicating them. We're producing the artefacts a manual transition team produces during hand-over: system overview, entry-point map, module catalogue, data dictionary, glossary, interface catalogue, business-rules catalogue, headline sequence diagrams. Risk register and operational runbook stay manual.

Production-grade means: **signed, auditable, drift-aware, hierarchical (so it scales to 1M+ LOC), and cost-bounded by model tiering**.

---

## Architecture summary

Per ADR-038:

- **Astra DB is the system of record.** `DocSection` rows carry the DRAFT → IN_REVIEW → SIGNED → STALE → SUPERSEDED lifecycle. `DocGenerationRun` rows track batch invocations (cost, model, latency).
- **Git is a downstream mirror.** On sign, a background job pushes rendered markdown into a dedicated `astra-docs/<corpus-id>` branch. One-way, append-only, never `main`.
- **MkDocs-Material renders the published site.** Pandoc handles PDF + Confluence-XML export.
- **Hybrid rule extraction.** Conservative globally; SME flags rules-dense modules where the aggressive pass runs.
- **Hierarchical roll-up.** Per-routine summaries → module summaries → corpus overview. Each level reads its predecessor, not raw source. Cost scales sub-linearly.
- **Model tiering by blast-radius.** Headline routines get Opus; mid-tier gets Sonnet; the long tail gets batched Haiku. Migration-plan blast-radius (Phase 8.0.c) supplies the tier signal.

---

## Sub-phase ledger

| # | Title | Deliverable | State |
|---|---|---|---|
| **11.0** | Foundation | ADR-038, doc.schema.json, DocSection + DocGenerationRun entities, AppDbContext wiring, Docs__ config keys, this plan | In progress (this commit) |
| **11.0 (slice)** | Vertical slice on LAPACK BLAS | ~10 routine-summary sections end-to-end through the new entity model. Read by eye. Decide before committing 11.0.a–h. | Next |
| **11.0.a** | Per-routine summary pipeline | `doc-summary` extraction prompt for Fortran; Haiku-batched long tail; Sonnet for mid-tier; Opus for headlines auto-picked by blast-radius; `DocsExtractionService` mirroring the existing `ExtractionPipeline` shape | Pending |
| **11.0.b** | Hierarchical roll-up | Module → subsystem → overview. Walks SCC-condensed migration plan. | Pending |
| **11.0.c** | Cross-cutting catalogs | Data dictionary (COMMON-block-aware), glossary, interface catalog, business rules (hybrid mode wired) | Pending |
| **11.0.d** | Diagram generation | Mermaid sequence diagrams for headline flows; dependency diagrams reuse Phase 8.0.a graph | Pending |
| **11.0.e** | SME review UI | `DocReviewPage` modelled on `SpecReviewPage`; edit/accept/reject/sign per section | Pending |
| **11.0.f** | Drift detection | Source moves → cited sections become STALE → re-confirm flow | Pending |
| **11.0.g** | Static site + exports | MkDocs-Material site generator, Pandoc PDF + Confluence export, on-sign git mirror writer | Pending |
| **11.0.h** | E2E demo + recording | LAPACK BLAS or MINPACK walkthrough; cost + latency report | Pending |

Sub-phases ship in order — each lands a commit, the foundation gates them all.

---

## Scope decisions (locked)

| Choice | Decision | Rationale |
|---|---|---|
| Doc home | Astra DB + git-mirror on sign | Drift detection requires the lifecycle to live where the SourceVersion graph lives. Git mirror keeps engineers reading docs in their normal workflow. |
| Rule extraction | Hybrid (conservative default, aggressive on flagged modules) | Rules-dense systems (pricing, eligibility, tax) get the recall; plumbing-heavy systems don't pay the review-fatigue tax. SME flag is the knob. |
| Source format | Markdown + YAML frontmatter | Pandoc-ready, git-diffable, MkDocs-native. |
| Published site | MkDocs-Material | Battle-tested, search out of the box, enterprise customers can override theme via mkdocs.yml. |
| Diagrams | Mermaid | Already in our frontend; renders inline in MkDocs; SME-editable. |
| PDF / Confluence | Pandoc | One binary, both formats, no per-CMS connector. |
| Skipped libraries | Doxygen, Ford, Sphinx-fortran | Wrong abstraction level. |

---

## Scale budget

Rough ballpark, calibrated against the existing LAPACK Reference BLAS corpus (169 files, 48k LOC, ~500 routines) and extrapolated:

| Corpus size | First-pass full doc | Re-ingest after 10% diff |
|---|---|---|
| ~50k LOC (BLAS-scale) | $80–150 | $10–15 |
| ~250k LOC | $400–800 | $40–80 |
| ~1M LOC | $1.5k–3k | $50–200 |

Numbers depend on the long-tail Haiku batch hit-rate. The 11.0 vertical slice will refine these against real LAPACK BLAS output.

Latency budget: per-routine summary 1–3s with prompt caching warm; module roll-up 5–15s; overview 30–60s. Full corpus pass should complete inside the 30-minute "coffee break" window for any corpus up to ~250k LOC. Larger corpora run as a background job with progress reporting.

---

## Risks + mitigations

1. **Roll-up quality.** Hierarchical summarisation loses signal — module summaries become bland because the cheap-tier routine summaries dropped load-bearing nouns. *Mitigation*: 11.0.a–b ship behind a feature flag; we measure on LAPACK BLAS before committing to the cost model. If quality is poor, escalate the routine-summary tier (more Sonnet, less Haiku) which moves the cost band.
2. **Drift-cascade.** A single source change can invalidate many signed sections (an interface change ripples into every catalogue entry that cites it). *Mitigation*: drift detection groups STALE sections by root cause so SMEs re-confirm one batch, not 50 individual entries.
3. **Git push permissions.** Customer-side push permissions are a deployment-time configuration we can't validate in CI. *Mitigation*: `Docs__GitMirror__Enabled` defaults to `false`; first sign with mirror disabled produces a clear "mirror skipped — configure Docs__GitMirror__* to enable" notice in the audit log instead of failing silently or unexpectedly.
4. **SME review fatigue.** Catalogue artefacts can produce 100s of items; reviewing all is unrealistic. *Mitigation*: review UI groups by confidence — `low`-confidence items at the top of the queue, `high`-confidence items auto-accept on signing with the SME explicitly opting in via "auto-accept HIGH" toggle.
5. **Prompt-shape hallucination** (replays the lesson from VB6 prompts in Phase 10.3.e). *Mitigation*: every doc prompt inlines the literal JSON shape + a worked example. No "same shape as X" delegations.

---

## Out of scope

- Risk register, operational runbook — these need humans who ran the system in production.
- Multi-corpus / portfolio-level documentation — Phase 11.x backlog; Phase 11.0 is per-corpus only.
- Live editing UI for markdown sections — Phase 11.0.e ships claim-level edit (matching SpecReviewPage); freeform markdown editing is Phase 11.1.
- Translation / localisation of generated docs — out of scope for first ship.
- Bi-directional git sync (customer edits in /docs, we read back) — explicitly rejected in ADR-038; one-way mirror only.

---

## Definition of done (Phase 11.0 foundation)

- ADR-038 committed ✅
- doc.schema.json with the 9 section kinds ✅
- `DocSection` + `DocGenerationRun` entities + AppDbContext wiring + indexes ✅
- `Docs__*` config keys in docker-compose.yml ✅
- This delivery plan committed ✅
- API builds and starts cleanly with new entities (verified post-restart)
- 10-routine vertical slice over LAPACK BLAS reads sensibly by eye (verified next)
