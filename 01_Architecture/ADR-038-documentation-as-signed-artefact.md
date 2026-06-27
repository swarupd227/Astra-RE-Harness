# ADR-038 — Documentation as a signed artefact

**Status:** Accepted
**Phase:** 11.0 (foundation)
**Companion:** `phase-11.0-documentation-generation.md`, ADR-024 (Spec/v1 schema-driven), ADR-025 (signing & audit)

---

## Context

Customers are starting to ask Astra to generate **transition documentation** for legacy applications that aren't ready to be modernised yet — i.e. they want the harness to take over the documentation burden during the discovery / hand-over phase, ahead of the eventual code migration.

What manual transition teams actually produce is a small, well-known set of artefacts: system overview, entry-point map, module catalogue, data dictionary, glossary, interface catalogue, business-rules catalogue, headline sequence diagrams, risk register, operational runbook. The first six can be drafted by an LLM working over the parser output we already have. The headline diagrams ride on the dependency graph we already have. The risk register and runbook need humans who ran the system in production — those stay manual.

Three architectural choices have to be made before any of that:

1. **What is "documentation" in the Astra data model?** A new entity, a JSON blob on Subroutine, a generated file in the source repo, or something else?
2. **How does review + sign-off work?** Documentation that ages without re-confirmation is worse than no documentation — engineers stop trusting it. We need a signed-state lifecycle and a drift signal when source moves under signed sections.
3. **Where does the published output live?** In Astra, in git, in a CMS — or all three?

We considered three positions:

- **Generated-file-in-repo (stateless):** Astra writes markdown straight into the source tree; the customer reviews via PR. Simple, but loses the audit + drift-detection lifecycle, and Astra has no way to know which sections are signed vs. drafts.
- **External CMS push (Confluence-first):** Skip the in-Astra review surface; auto-draft straight into Confluence. Matches enterprise workflows that already exist, but cedes the lifecycle to a tool that doesn't model claim-level audit.
- **Astra DB as system-of-record + git-mirror on sign:** Documentation is a first-class entity inside Astra with the same DRAFT → IN_REVIEW → SIGNED lifecycle as Spec. On sign, a background job mirrors the rendered markdown into a configured git path. Engineers read from git; SMEs review in Astra.

---

## Decision

**Documentation is a first-class signed artefact inside Astra.** Each documentation unit is a `DocSection` row with the same lifecycle (`DRAFT` → `IN_REVIEW` → `SIGNED` → `SUPERSEDED`), the same signing primitives (RS256 over a canonical hash, signer identity in audit), and the same drift semantics (when source-version-of-record moves, signed sections referencing changed source citations become `STALE` and require SME re-confirmation).

Generation runs are tracked separately as `DocGenerationRun` rows (one per batch invocation, mirroring `ValidationRun`'s shape) so cost, model, prompt version, and latency are auditable per run.

**Git is a downstream mirror, not the source of truth.** On sign, a background job mirrors the rendered markdown into a dedicated branch in the customer's source repository — `astra-docs/<corpus-id>` by convention, never `main`. The branch is one-way append-only from Astra's perspective; the customer's team reviews + PRs into their own trunk. This stops Astra from owning the customer's merge conflicts and keeps git history clean.

**Published-site generation is layered above the data model**, not baked into it. MkDocs-Material is the default static-site renderer; Pandoc handles PDF and Confluence-XML export. The renderer reads SIGNED `DocSection` rows from the DB and emits the configured output formats. Customers who mandate their own theme override `mkdocs.yml`; customers who mandate a specific CMS run the exporter against their target.

---

## Alternatives considered

### A. Astra DB system-of-record + git-mirror on sign **(chosen)**

- **Pros:** Reuses the signing + audit + drift plumbing already in place for Spec. Documentation lifecycle visible in the same UI engineers already use. Drift detection comes for free off the existing source-version graph. Git mirror keeps engineers reading docs in their normal workflow.
- **Cons:** Two write-targets (DB + git) to keep consistent. Git push permissions need to be configured per corpus.
- **Effort:** Foundation: 1 day (this commit). Full pipeline through 11.0.h: ~2 weeks.

### B. Generated-file-in-repo (stateless)

- **Pros:** Zero new entities. Customer reviews via their normal PR workflow.
- **Cons:** No claim-level audit. No drift signal — when the SME signed a docs PR six months ago and the source has moved, nothing tells anyone the docs are stale. Customers asked us explicitly for the audit lifecycle; this position fails that ask.
- **Effort:** ~3 days. **Rejected** for not solving the drift problem.

### C. External CMS push (Confluence-first)

- **Pros:** Customers' existing review tooling. No new UI in Astra.
- **Cons:** Confluence doesn't model claim-level sign-off; review state lives in human comments, not structured fields. We'd lose the lifecycle in the round-trip. Also customer-specific — many customers don't use Confluence; we'd have to ship a connector per CMS.
- **Effort:** Connector-per-CMS, ongoing. **Rejected** as a system-of-record but **kept** as an export target via Pandoc in 11.0.g.

---

## Rationale

Three things drove the call:

1. **The drift problem is the load-bearing requirement.** Documentation that ages without re-confirmation gets ignored. The whole point of paying for AI-drafted docs is that the AI can re-draft on source change — but only Astra knows what the source change *was* (the SourceVersion graph). Storing the lifecycle anywhere else means we can't connect "source moved" to "this signed section needs SME re-confirmation". B and C both fail this.
2. **The plumbing is mostly free.** Spec already has DRAFT → IN_REVIEW → SIGNED. ClaimReview already records per-claim accept/reject decisions. Signature already does RS256 over a canonical hash. We're shaping a new entity that reuses all of it, not building a new lifecycle from scratch.
3. **One-way git mirror is the right contract with the customer's repo.** If we tried to do bi-directional sync (customer edits docs in git, we read back), we'd own merge conflicts and have to reconcile two source-of-truth claims. Dedicated `astra-docs/<corpus-id>` branch with append-only commits keeps the boundary clean and gives the customer a clean PR target.

The cost of being wrong is moderate — if customers turn out to want git as the system-of-record, we can degrade the DocSection entity into a thin metadata row that points at a markdown file. The cost of being wrong in the other direction (no audit lifecycle from day 1) is much higher — we'd be teaching customers that Astra docs are unreviewed AI output, which is the failure mode this whole feature has to avoid.

---

## Implementation surface (Phase 11.0 foundation)

- `app/api/src/Astra.Api/Llm/Schemas/doc.schema.json` — section-type taxonomy (overview / module / routine / glossary / data-dictionary / interface / business-rule / diagram), each with field shape + display tone
- `app/api/src/Astra.Api/Persistence/Entities/DocSection.cs` — entity with `State`, `SectionKind`, JSON payload, citations, signing FK
- `app/api/src/Astra.Api/Persistence/Entities/DocGenerationRun.cs` — batch-run entity mirroring `ValidationRun`
- `app/api/src/Astra.Api/Persistence/AppDbContext.cs` — register `DocSections` and `DocGenerationRuns`
- `app/docker-compose.yml` — config keys `Docs__GitMirror__Enabled`, `Docs__GitMirror__BranchPattern`, `Docs__GitMirror__Author*`, `Docs__Generator__Provider`

Per-sub-phase deliverables (11.0.a through 11.0.h) are tracked in `04_Delivery/phase-11.0-documentation-generation.md`. This ADR locks the data model and the git-mirror contract; everything else is implementation work that hangs off them.
