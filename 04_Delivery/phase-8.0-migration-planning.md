# Phase 8.0 — Portfolio migration planning

**Status:** Scope (v0.1)
**Owner:** TBD (proposed: Eng lead + 1 frontend)
**Companion:** `demo-build-plan.md`, `phase-5.7-demo-build-plan.md`, enterprise-readiness gap register
**Target landing:** 5 PRs over ~4–5 working weeks
**Companion to:** PRs #5/#7 (Golden Dataset), #8 (Neighbourhood), #9 (Harmonisation)

---

## 1. The single claim Phase 8.0 must prove

> "Given a freshly ingested corpus of N legacy routines, the platform produces a **dependency-aware migration plan** — grouped into waves, with per-routine blast-radius and migration-readiness classification — that a program manager can take to the steering committee, an SME can prioritise reviews from, and an engineer can pull work from."

The current platform processes per-routine well. It does not answer **"in what order should we migrate, and what's the blast-radius of getting any one wrong?"** Phase 8.0 closes that gap end-to-end.

---

## 2. Why this is one phase (in five sub-phases)

The five pieces below depend on each other but each lands as its own PR. Sequencing matters because **8.0.a's graph builder is the substrate every other sub-phase reads from**:

| Sub-phase | What lands | Depends on |
|---|---|---|
| **8.0.a — Dependency graph** | `DependencyGraphBuilder` service + read-only graph API + interactive graph UI | parser metadata already on `Subroutine` rows |
| **8.0.b — Wave planner** | Topological sort + `MigrationPlan` / `MigrationWave` entities + plan-management API + wave-list UI | 8.0.a |
| **8.0.c — Blast radius + readiness** | Downstream impact + per-routine "safe / coordinated / blocked" classification + subroutine-page sidebar additions | 8.0.a |
| **8.0.d — Portfolio dashboard** | Cross-corpus burn-down, wave progress, signed-vs-pending counts | 8.0.b |
| **8.0.e — Strategy plugins** | Extensible planner strategies beyond pure topological (business-priority CSV, risk-first, pilot-then-scale) | 8.0.b |

Effort estimate per sub-phase below; total **3.5–5 working weeks** depending on UI polish + how much customisation customers want on plan strategies.

---

## 3. Sub-phase scope

### 3.1 Phase 8.0.a — Dependency graph (~1 week)

**Goal:** turn the per-routine `CalledSubroutines` + `CommonBlockRefs` JSON blobs into a queryable graph + viewable UI.

**Backend**
- `Llm/Dependency/DependencyGraphBuilder.cs` (mirrors `NeighbourhoodBuilder` pattern):
  - `BuildAsync(corpusId, sourceVersionId)` → in-memory `DependencyGraph` record
  - One round-trip to pull all subroutines + their JSON blobs for the version (same pattern as `NeighbourhoodBuilder`)
  - Resolves callee names against in-corpus subroutines; unresolved callees flagged as `external`
  - Detects strongly-connected components (Tarjan) — mutual recursion in legacy F77 is real; the SCC is the unit, not the individual routine
- Two edge types:
  - **`call`** — from `Subroutine.CalledSubroutines`
  - **`shared-storage`** — derived from `CommonBlockRefs` (Fortran) / copybook refs (COBOL): X writes to `/CFG/`, Y reads from `/CFG/` → edge from X to Y
- Cached per `(corpusId, sourceVersionId)` in-memory; invalidated on reingest

**Data shape** (returned by API):
```json
{
  "corpusId": "...",
  "sourceVersionId": "...",
  "nodes": [
    {
      "id": "<subroutineId>",
      "name": "CONSUME_ROLL",
      "sourcePath": "src/CONSUME_ROLL.FOR",
      "state": "PARSED|SIGNED|SCAFFOLDED|COMMITTED",
      "specId": "<id>|null",
      "isLeaf": false,
      "isRoot": true,
      "sccId": "<scc-N>|null",
      "calleeCount": 3,
      "callerCount": 0
    }
  ],
  "externalCallees": ["INV_READ", "EMIT_EVENT"],
  "edges": [
    { "from": "<id>", "to": "<id>", "type": "call" },
    { "from": "<id>", "to": "<id>", "type": "shared-storage", "viaBlock": "/CFG/" }
  ],
  "sccs": [ { "id": "scc-1", "members": ["<id1>", "<id2>"] } ],
  "stats": { "nodeCount": 47, "callEdgeCount": 132, "sharedStorageEdgeCount": 18, "sccCount": 2, "externalCalleeCount": 14 }
}
```

**API**
- `GET /api/v1/corpora/{id}/dependency-graph` — open to engineer + admin; returns the shape above

**Frontend**
- New page: `/corpora/{id}/dependency-graph`
- Library: **React Flow** (`reactflow` npm) — pragmatic choice; supports auto-layout via `elkjs`, handles ~500 nodes interactively, good React story. Cytoscape.js considered; rejected because its React story is weaker.
- Layout: hierarchical (top-down) for ≤200 nodes; force-directed fallback for larger
- Node colouring: by state (DRAFT grey / SIGNED green / COMMITTED dark-green / external dashed border)
- Click node → drill into `/subroutines/{id}`
- Filter sidebar: by source file, by state, by SCC membership, hide external

**Tests**
- Unit: SCC detection on a hand-crafted mini-corpus with known cycles
- Unit: external-callee classification
- E2E (`dependency-graph.spec.ts`): seed corpus → API returns 7 nodes (CONSUME_ROLL + 6 stubs); 9 call edges; 0 SCCs; 0 external

### 3.2 Phase 8.0.b — Wave planner (~1 week)

**Goal:** turn the graph into a wave-by-wave migration plan that the team can work from.

**Backend**
- New entities:
  - `MigrationPlan` (id, corpusId, sourceVersionId, status [draft/approved/archived], strategyName, totalRoutines, totalWaves, summaryJson, createdAt, approvedAt, approvedBy)
  - `MigrationWave` (id, migrationPlanId, waveNumber, name, plannedRoutineIds[] jsonb, targetStartDate, targetEndDate, status, summary, completedRoutineCount)
- `Llm/Dependency/MigrationPlanner.cs`:
  - `GenerateDraftAsync(corpusId, strategyName)` → returns proposed plan (does NOT persist)
  - `ApproveAsync(draftId, actor)` → marks draft as approved; archives any prior approved plan for the same `(corpusId, sourceVersionId)` tuple
  - `ListAsync(corpusId)`, `GetAsync(planId)` for reads
- **Topological wave assignment algorithm** (`topological-leaves-first`, the v1 strategy):
  - Treat each SCC as one super-node (the whole cycle migrates together)
  - Kahn's algorithm: Wave 1 = all super-nodes with no callee dependencies in-corpus; Wave 2 = super-nodes whose callees are all in Wave 1 or external; …
  - External callees don't block (they're either system / OS / library — out of scope for migration)
  - `shared-storage` edges create a soft constraint: prefer co-locating routines that share a COMMON block in the same wave, but don't enforce
  - Output: ordered list of waves, each with an ordered list of routine IDs (within a wave, alphabetic by name for predictability)

**Schema additions** (additive DDL pattern, like `golden_dataset_*`):
- `migration_plans` table
- `migration_waves` table with FK to `migration_plans` (ON DELETE CASCADE)
- Indexes: `(corpus_id, status)`, `(source_version_id, status)`

**API**
- `POST /api/v1/corpora/{id}/migration-plan/generate` (admin) → returns the draft plan
- `POST /api/v1/migration-plans/{id}/approve` (admin) → marks approved, audit-logged
- `GET /api/v1/corpora/{id}/migration-plan` → returns the current approved plan (404 if none)
- `GET /api/v1/migration-plans/{id}` → plan + waves + per-wave routine list

**Frontend**
- New page: `/corpora/{id}/migration-plan`
  - Header: plan status, strategy, total routines, total waves, generated/approved timestamps
  - "Recompute plan" button (admin only) — generates a new draft alongside the approved plan
  - "Approve plan" button on draft (admin only)
  - Wave-by-wave list — each wave is a card:
    - Wave number + name (auto-generated like "Wave 1 — 23 leaf routines, 4 source files")
    - Progress bar: parsed → signed → scaffolded → validated → committed
    - Routine list with state badges; click-through to subroutine page
  - "Diff vs approved" view when a draft differs from approved (added routines, moved routines, reordered waves)

**Tests**
- Unit: topological sort on a known dependency graph; verify wave assignments match expected
- Unit: SCC treated as super-node (mutual recursion → both routines in same wave)
- E2E: seed corpus → generate plan → approve → fetch → wave 1 has CONSUME_ROLL

### 3.3 Phase 8.0.c — Blast radius + readiness (~4–5 days)

**Goal:** per-routine answers to "what breaks if I change this?" and "is this safe to migrate now?"

**Backend**
- `Llm/Dependency/BlastRadius.cs`:
  - `ComputeAsync(subroutineId)` → returns transitive set of routines that depend on this one
  - Direct callers (1 hop) + indirect (N hops) + shared-storage consumers
  - Includes the per-affected-routine state so the UI can show "of 17 affected, 3 are signed, 14 are draft"
- `Llm/Dependency/MigrationReadiness.cs`:
  - `ClassifyAsync(subroutineId)` → one of:
    - `safe-leaf` — no callees in this corpus + no shared-storage writes
    - `safe-with-deps-done` — all callees already SIGNED + no shared-storage writes
    - `coordinated-only` — writes to a COMMON block / copybook other routines read → migration must co-locate
    - `blocked-on-deps` — has callees that aren't yet signed; migrating now would extract against unsigned-spec dependencies
    - `external-caller-unknown` — has callers outside the corpus we don't have source for → external integration risk

**API**
- `GET /api/v1/subroutines/{id}/blast-radius` → `{ direct: [...], transitive: [...], sharedStorage: [...], stateBreakdown: {...} }`
- `GET /api/v1/subroutines/{id}/migration-readiness` → `{ classification: "safe-leaf", reasons: [...], blockingRoutineIds: [...] }`

**Frontend**
- Subroutine detail page sidebar additions:
  - **"Migration readiness"** badge with click-through to a popover showing the reasons
  - **"Blast radius"** card: "17 downstream routines (3 signed, 14 draft)" — click to expand
  - **"Wave assignment"** badge if a plan is approved: "Wave 3 of 12"
- Dependency graph node click → side-panel shows blast radius

**Tests**
- Unit: blast radius on a graph with known dependency chain; assert correct counts
- Unit: readiness classification covers each category
- E2E: subroutine page surfaces both badges

### 3.4 Phase 8.0.d — Portfolio dashboard (~4–5 days)

**Goal:** the program manager's view — across corpora, across waves.

**Backend**
- `Llm/Dependency/PortfolioMetrics.cs`:
  - `GetSummaryAsync()` → aggregates across all approved plans:
    - Total routines, total signed, total scaffolded, total validated, total committed
    - Per-corpus rollup
    - Per-wave rollup (which waves are 100% / partial / not-started)
    - Token + LLM-cost rollup (already in `LlmCall` table)
    - Recent activity feed (last N signed/scaffolded events)

**API**
- `GET /api/v1/platform/portfolio-summary` (admin) → aggregated dashboard payload

**Frontend**
- New page: `/platform/portfolio`
- Header tile row: total routines / signed / scaffolded / committed (with sparkline showing last 30 days)
- Per-corpus table: name | total routines | current wave | % signed | % validated | last activity
- Wave burn-down chart: x = days since plan approved, y = routines remaining in each wave (stacked area chart, Recharts library)
- LLM cost subsection: cost by corpus, cost by month, cost projection ("at this rate the corpus will cost $X to complete")

**Tests**
- Unit: portfolio aggregation correctness (signed/scaffolded/etc. counts)
- E2E: dashboard page loads + shows seed corpus rollup

### 3.5 Phase 8.0.e — Strategy plugins (~3–4 days)

**Goal:** v1 ships with `topological-leaves-first` only. This sub-phase adds three more strategies + the plugin shape.

**Backend**
- Refactor `MigrationPlanner` to accept an `IPlanStrategy` interface:
  - `string Name { get; }`
  - `string Description { get; }`
  - `IReadOnlyList<WaveDraft> AssignWaves(DependencyGraph graph, IReadOnlyDictionary<Guid, Spec?> existingSpecs, StrategyOptions options)`
- Built-in strategies:
  - `topological-leaves-first` (default, ships in 8.0.b)
  - `business-priority` — accepts a CSV upload of `routine_name,priority`; plans waves by priority bucket, with topological order within bucket
  - `risk-first` — orders by blast-radius (largest first) on the theory that high-impact routines need SME attention earliest
  - `pilot-then-scale` — Wave 1 is a configurable 5-routine pilot; Waves 2-N are pure topological from there

**API**
- `GET /api/v1/migration-plan-strategies` → list available strategies with name + description + accepted options
- `POST /api/v1/corpora/{id}/migration-plan/generate?strategy=...` → already exists from 8.0.b; gains strategy options support

**Frontend**
- Strategy picker dropdown on the plan page
- Strategy-specific options panel (e.g. CSV upload for `business-priority`)

**Tests**
- Unit: each strategy produces a valid wave assignment on a known graph
- E2E: switch strategies on a corpus; observe wave reassignments

---

## 4. Data model summary

### New entities

```
MigrationPlan
  id              uuid PK
  corpus_id       uuid NOT NULL
  source_version_id uuid NOT NULL
  status          varchar(16)   -- 'draft' | 'approved' | 'archived'
  strategy_name   varchar(64)
  strategy_options jsonb
  total_routines  int
  total_waves     int
  summary         text          -- human-readable one-liner
  generated_by    varchar(160)
  approved_by     varchar(160)  -- null until approved
  created_at      timestamptz
  approved_at     timestamptz   -- null until approved
  archived_at     timestamptz

MigrationWave
  id              uuid PK
  migration_plan_id uuid FK ON DELETE CASCADE
  wave_number     int
  name            varchar(256)
  planned_routine_ids jsonb     -- array of uuid strings
  status          varchar(16)   -- 'planned' | 'in_progress' | 'completed'
  target_start_date date
  target_end_date date
  actual_completed_at timestamptz
  routine_count   int
  signed_count    int           -- denormalised for cheap listing
  scaffolded_count int
  validated_count int
  committed_count int
```

### No changes to existing entities

`Subroutine`, `Spec`, `Scaffold`, `ValidationRun` are read-only inputs to the planner. **Critically: no `wave_id` column is added to `Subroutine`** — wave membership lives in `MigrationWave.planned_routine_ids` so that re-planning doesn't require touching every routine row.

---

## 5. Algorithms

### 5.1 Topological wave assignment (the default strategy)

Input: `DependencyGraph` with N nodes, M call edges, K SCCs.

1. Run **Tarjan's SCC algorithm** to collapse cycles. Output: condensation DAG of super-nodes.
2. Compute **in-degree** of each super-node (number of incoming call edges from other super-nodes; ignore external callees and self-loops).
3. **Kahn's BFS** on the condensation:
   - Wave 1 = super-nodes with in-degree 0
   - For each wave, remove its super-nodes; recompute in-degrees; next wave = those that now have in-degree 0
4. Expand each super-node back into its member routine IDs (sorted alphabetically within an SCC for predictability).

Complexity: `O(N + M)` for Tarjan + Kahn — trivially fast for any realistic corpus (<10k routines).

**Shared-storage edges** are not used in the wave assignment in v1 — they're surfaced as `coordinated-only` readiness flags in 8.0.c instead. Customer feedback from the first deployment will tell us whether to fold them into the wave order in a future version.

### 5.2 Blast radius

Input: subroutine ID `S`.

1. Run **reverse BFS** from `S` on the call graph, treating each call edge as `caller → callee`. Visit set = `S`'s callers transitively.
2. Augment with shared-storage consumers: for each COMMON block `S` writes to, find all routines that read from that block (and their transitive callers).
3. Return the union, with each affected routine's current state + spec status.

### 5.3 Migration readiness classification

Decision tree (first match wins):

```
if subroutine has callers outside the corpus we don't have source for:
    return "external-caller-unknown"
if subroutine writes to a COMMON block / copybook other routines read:
    return "coordinated-only"
if subroutine has callees in this corpus that are NOT yet SIGNED:
    return "blocked-on-deps" (with list of blocking routine IDs)
if subroutine has callees in this corpus AND all are SIGNED:
    return "safe-with-deps-done"
return "safe-leaf"
```

---

## 6. API surface (consolidated)

```
GET    /api/v1/corpora/{id}/dependency-graph
GET    /api/v1/corpora/{id}/migration-plan
POST   /api/v1/corpora/{id}/migration-plan/generate          (admin; ?strategy=&options=)
GET    /api/v1/migration-plans/{id}
POST   /api/v1/migration-plans/{id}/approve                  (admin)
POST   /api/v1/migration-plans/{id}/archive                  (admin)
GET    /api/v1/migration-plan-strategies                     (lists available strategies)
GET    /api/v1/subroutines/{id}/blast-radius
GET    /api/v1/subroutines/{id}/migration-readiness
GET    /api/v1/platform/portfolio-summary                    (admin)
```

All write paths audit-logged via the existing `IAuditLogger`. Reads open to engineer + admin; admin-only on the portfolio summary because it crosses corpora.

---

## 7. UI surfaces

| Surface | Status today | Phase 8.0 add |
|---|---|---|
| `/corpora/{id}` | Exists, shows file + subroutine list | Add "Migration plan: 12 waves, Wave 3 in progress" header card |
| `/corpora/{id}/dependency-graph` | **NEW** | Interactive graph, filter sidebar (8.0.a) |
| `/corpora/{id}/migration-plan` | **NEW** | Wave-by-wave list, recompute/approve, diff view (8.0.b) |
| `/subroutines/{id}` | Exists | Add 3 sidebar cards: Wave Assignment, Blast Radius, Migration Readiness (8.0.c) |
| `/platform/portfolio` | **NEW** | Cross-corpus dashboard with burn-down (8.0.d) |
| `/platform` tile index | Exists | Add "Migration Planning" tile linking to portfolio dashboard |

---

## 8. Audit + governance hooks

Every write goes through `IAuditLogger` (same pattern as `harmonisation.completed`, `validation.completed`):

- `migration_plan.generated` — strategy + total routines + total waves + generated by
- `migration_plan.approved` — plan id + approver + supersedes-plan-id (if any)
- `migration_plan.archived` — plan id + actor + reason
- `migration_wave.status_changed` — wave id + old state → new state + actor

These show up in the existing compliance feed exporter (Phase #3d) automatically because that exporter reads the `audit_events` table.

---

## 9. Test plan

| Test type | Coverage |
|---|---|
| **Unit (algorithms)** | SCC detection, topological sort, blast-radius BFS, readiness classification decision tree, strategy plugin output |
| **Unit (services)** | `DependencyGraphBuilder` against the CONSUME_ROLL seed (known 7-node graph); `MigrationPlanner` against the same |
| **Integration** | Generate plan → approve → fetch → assert wave 1 = CONSUME_ROLL (no in-corpus callees), all stub callees are `external` |
| **E2E (Playwright)** | `dependency-graph.spec.ts`, `migration-plan.spec.ts`, `blast-radius.spec.ts`, `portfolio-dashboard.spec.ts` |
| **Performance** | Graph build + topological sort on a 1000-routine synthetic corpus completes in <3s |

---

## 10. Risks + open questions

| Risk | Mitigation |
|---|---|
| **Mutual recursion produces oversized SCCs** — real Fortran has e.g. 15-routine cycles via `PERFORM`/CALL spaghetti | Surface SCC member counts on the graph; let SME split into smaller waves by editing the plan (Phase 8.0.b draft-modification path) |
| **Unresolved callees inflate "external"** when the parser misses a CALL (especially in COBOL with copybook macros) | Phase 8.0.a returns the unresolved callee list explicitly; engineer can re-ingest with copybook resolution if missing data |
| **React Flow performance at >500 nodes** | Auto-fallback to force-directed + node clustering for large corpora; documented limit |
| **Wave plan goes stale on reingest** | New ingest → plan moves to `stale` status; UI shows "regenerate" prompt |
| **No customer feedback yet** on whether shared-storage should drive wave order vs just readiness flags | Ship 8.0.b without shared-storage as wave driver; revisit after first customer engagement |

**Open questions for the user / steering committee:**
1. Should Phase 8.0 also include the **inter-corpus** call graph (some legacy systems span multiple programs)? Likely defer to Phase 8.1.
2. Should approving a plan **lock** routine signatures (no spec edits while in-progress wave)? Suggested: no — locks are heavy; rely on audit + harmonisation pass instead.
3. Should the planner have a **dry-run cost projection** ("plan will consume ~$2,400 in LLM calls based on average per-routine cost")? Nice-to-have; lands naturally with Portfolio Dashboard's cost rollup.

---

## 11. Effort + sequencing

| Sub-phase | Estimate | Cumulative |
|---|---|---|
| 8.0.a Dependency graph | 5 days | 5 days |
| 8.0.b Wave planner | 5 days | 10 days |
| 8.0.c Blast radius + readiness | 4 days | 14 days |
| 8.0.d Portfolio dashboard | 4 days | 18 days |
| 8.0.e Strategy plugins | 3 days | 21 days |

**~3.5 weeks of focused work** for a single full-stack engineer, or **~2 weeks with two engineers** working a/b in parallel + d/e in parallel (8.0.c depends on a, so it stays serial).

Each sub-phase ships as its own PR; main is releasable after every merge.

---

## 12. Success criteria

Phase 8.0 is complete when:

1. A fresh ingest of a 50-routine COBOL program produces a wave plan in <5 seconds.
2. The dependency graph UI renders interactively (60fps pan/zoom) for that 50-routine corpus.
3. Every subroutine page shows a wave assignment + blast-radius + readiness classification.
4. The portfolio dashboard loads in <2 seconds and shows correct per-corpus signed-count rollups.
5. The audit trail captures every plan generation + approval, surfaced in the compliance feed.
6. The Phase 5.7 demo can credibly add a beat: *"Here's the migration plan the platform produced for this corpus — 12 waves, 47 routines, 3 high-blast-radius critical paths."*

---

## 13. Out of scope (deliberately)

For Phase 8.0:
- **Inter-corpus dependencies.** Multi-program coupling (e.g. PROGRAM_A calls a routine in PROGRAM_B's source corpus) — Phase 8.1.
- **File-handle dependency edges.** "X opens `EMPL.DAT`, Y reads `EMPL.DAT`" — surfaced via blast-radius in 8.0.c but not as a graph edge type.
- **Cost-projection automation.** Per-wave LLM-cost estimate based on routine sizes — covered partially by Portfolio Dashboard but not built into the planner's recommendations.
- **Auto-pause on validation failure.** "Wave 3 has 4 routines that failed equivalence; auto-block Wave 4 dependency" — Phase 8.1.
- **Multi-tenant wave plans.** Each customer org gets its own plan — falls under the broader multi-tenancy story (enterprise-readiness Tier 1, separate roadmap item).
- **Plan templating / cloning across corpora.** "Use the wave structure from CORPUS_A as the starting point for CORPUS_B" — defer.

---

## 14. What this unlocks for the LinkedIn / sales story

Once 8.0 ships, the platform can credibly claim:

- *"For any legacy codebase you upload, we produce a dependency-aware migration plan with wave ordering, blast-radius analysis per routine, and a readiness classifier that tells you which routines are safe to migrate independently vs which need coordinated migration."*
- *"The plan is recomputable any time the source changes. Approvals are audit-logged. The portfolio dashboard shows where every team's migration is, what's blocked, and what the LLM spend is."*

This is the single biggest gap between **a per-routine AI tool** (what we have today) and **an enterprise migration platform** (what customers buy). Phase 8.0 closes it.
