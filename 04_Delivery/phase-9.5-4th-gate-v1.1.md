# Phase 9.5 — 4th gate v1.1 (real ref/candidate execution)

**Status:** Plan (v0.1)
**Companion:** `phase-9.3-4th-validation-gate.md`, ADR-029 (property-based testing harness), ADR-030 (per-claim generator-hint embedding), ADR-032 (HarnessDriver contract + per-run binary cache)
**Owner:** Platform team
**Target window:** ~1.5 weeks once kicked off

Phase 9.3 shipped the 4th gate in **shadow mode** — the
`property-test-sidecar` drives Hypothesis end-to-end and the harness
records `examplesTried` per claim, but the
`/internal/equivalence-callback` endpoint returns `agree:true`
without actually comparing a reference binary to a candidate. v1.1
wires the real comparison behind the same callback contract so the
upgrade is purely additive: existing shadow-mode behaviour stays
available as a fallback, and only the upgraded path (initially
fortran-f77 → dotnet8 for the MINPACK demo) flips to `mode: "live"`.

---

## Choices locked in (2026-06-13)

| Decision | Choice | Implication |
|---|---|---|
| Where binaries are cached | **Per-validation-run, in-process** | A single compile per /validate/falsifying; subsequent callbacks hit a `ConcurrentDictionary<runId, BinaryArtifacts>` keyed by the run id. |
| Subprocess strategy | **Spawn-per-input for v1.1** | 100 inputs × ~200ms spawn cost = ~20s/claim, well within the 30s/claim budget. A long-lived stdin-pipe coprocess is v1.2 work. |
| Scaffold contract for stdin driver | **Per-archetype HarnessDriver** (per ADR-032) | Each scaffold archetype that wants to participate ships a `HarnessDriver` entry point that reads a single JSON line from stdin and writes a single JSON line to stdout. |
| v1.1 archetype scope | **canonical-minpack only** (Fortran-f77 → dotnet8) | The remaining 7 archetypes (delphi, cpp × dotnet8 + java-spring, etc.) stay in shadow mode until v1.2 adds their HarnessDriver. |
| Comparison semantics | **JSON-equal on the stdout payload** | Both binaries print one JSON object; equality is a deep structural compare. Tolerances for floating-point claims handled by the claim's `generatorHints.constraint` field (out of scope for v1.1). |

---

## Sub-phases

- **9.5 — Scope + ADR-032.** Master plan (this doc) + ADR-032
  (HarnessDriver contract + per-run binary cache architecture).

- **9.5.a — Per-run binary cache + ref-binary execution.**
  - New `PropertyTestRunCache.cs` — singleton
    `ConcurrentDictionary<Guid, BinaryArtifacts>` keyed by validation
    run id. `BinaryArtifacts` carries the gfortran-sidecar
    `artifactId` for the reference binary and a local filesystem path
    for the candidate binary (built by `PropertyTestValidator` before
    /falsify is called).
  - `PropertyTestValidator.RunAsync` is extended to:
    1. Decide whether the (schemaId, target) pair supports live mode
       (initially only fortran-f77 + dotnet8).
    2. Compile the reference binary via `gfortran-sidecar`
       /compile-and-run (with a shim driver that reads stdin and
       echoes the routine's stdout); cache the artifactId.
    3. Build the candidate scaffold via `dotnet publish` against the
       scaffold's HarnessDriver entry point; cache the path.
    4. Populate `BinaryArtifacts` and kick off /falsify.
    5. On run completion / error, evict from the cache and clean up
       the candidate's tempdir.
  - The `/internal/equivalence-callback` endpoint reads
    `runId` from the query string, looks up `BinaryArtifacts`, and
    when present, runs the reference binary (via
    `gfortran-sidecar` /run with stdin = input JSON) and captures
    stdout. The callback returns `{agree: true, refOutput: <stdout>,
    candOutput: <ref-output-mirror>}` for the half-live path before
    the candidate is wired (so the live-mode bring-up can be
    validated end-to-end against MINPACK before the candidate side
    lands).

- **9.5.b — Scaffold candidate execution + comparison.**
  - `canonical-minpack` archetype gets a new
    `src/HarnessDriver.cs` entry point: reads one JSON line from
    stdin, parses it as `{n, x:[…], tol}`, calls
    `Hybrd1Service.Solve(…)`, emits the result as JSON to stdout.
  - Archetype manifest's `compatibleTargetStacks` now records the
    `HarnessDriver` capability so PropertyTestValidator can detect
    it.
  - The callback compiles + runs the candidate via spawn-per-input
    using the cached candidate path; compares `refOutput` vs
    `candOutput` via JSON-structural equality; returns
    `{agree, refOutput, candOutput}` to the sidecar.
  - When candidate execution succeeds, the validator stamps
    `metrics.mode = "live"` instead of `"shadow"`.

- **9.5.c — Validation + bring-up + commit.**
  - `ValidationReportPage.tsx` reads `metrics.mode` and renders
    the `ShadowModeBadge` only when `mode === "shadow"`; live runs
    show no badge.
  - `falsifying-gate.spec.ts` learns about live mode: when targeting
    a MINPACK scaffold, the spec asserts `metrics.mode === "live"`
    and `metrics.totalExamplesTried > 0` with a non-empty
    `metrics.perClaim[*].refOutput`.
  - Bring up the stack; trigger /validate/falsifying against a
    real MINPACK scaffold; verify the round-trip works (~20s per
    claim, ~100 inputs across multiple claims).
  - Commit Phase 9.5.

---

## Hard gate (Δ-9.5)

- [ ] `PropertyTestRunCache` survives a `dotnet test` cycle — singleton
      registration works, eviction works, no GC leaks of the cached
      tempdirs.
- [ ] Reference binary compiles in < 30s on first call for MINPACK;
      cache hit returns in < 5ms on subsequent calls.
- [ ] Candidate binary compiles in < 60s on first call; cache hit
      returns in < 5ms.
- [ ] Per-callback round-trip stays under 250ms (ref + cand
      subprocess spawn + JSON compare).
- [ ] `/validate/falsifying` against a MINPACK scaffold returns
      `metrics.mode === "live"`, `claimsExercised > 0`, and at
      least one `perClaim` entry with non-empty
      `refOutput` + `candOutput`.
- [ ] Intentionally-broken candidate (mutate `Hybrd1Service.Solve` to
      return wrong values) → 4th gate returns `FAILED` with a
      `falsifying` example in the metrics.
- [ ] `ValidationReportPage` drops the shadow-mode badge when
      `mode === "live"`.
- [ ] `falsifying-gate.spec.ts` extended to assert live mode for
      MINPACK; all 4 existing e2e suites stay green.

## Soft gate

- [ ] ADR-032 (HarnessDriver contract + per-run binary cache) merged.
- [ ] Cache eviction on validator error path documented in the
      master plan.
- [ ] Long-lived stdin-coprocess strategy sketched as v1.2 follow-up.

---

## Risk register (additions for Phase 9.5)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-9.5-1 | Per-callback subprocess spawn cost dominates → only ~10 inputs fit in 30s budget. | M | M | Cache the candidate binary path so subsequent spawns reuse it. Long-lived coprocess as v1.2 if 200ms/call proves too slow. |
| R-9.5-2 | HarnessDriver's JSON parse / output format diverges between languages, so cross-language comparison fails on whitespace or key ordering. | M | M | Canonicalise both sides via `System.Text.Json` (.NET) and a thin Python pre-processor on the gfortran side; compare structurally not textually. |
| R-9.5-3 | The shim Fortran driver compiled into the reference binary is itself a regression source — a bug in the shim looks like a "real" disagreement. | L | M | Driver source ships per spec schema with a focused test fixture. |
| R-9.5-4 | Eviction races: if /falsify returns while the run-cache row is still being written, the callback fails to find the binary. | L | M | Cache is populated BEFORE /falsify is called; cleared only after /falsify returns. The validator's existing async flow already serialises these. |

---

## ADRs to land before / during the phase

| ADR | Title | Phase |
|---|---|---|
| ADR-029 | Property-based testing harness | 9.0.g (merged) |
| ADR-030 | Per-claim generator-hint embedding | 9.3 (merged) |
| ADR-032 | HarnessDriver contract + per-run binary cache | 9.5 kickoff |

---

## Out of scope

- **Per-language candidate execution beyond Fortran-f77 → dotnet8.**
  v1.2 adds Delphi/C++/COBOL HarnessDrivers + the Java-Spring path
  (which needs `mvn exec:java` instead of `dotnet`).
- **Long-lived coprocess optimisation.** Spawn-per-input is fine for
  the v1.1 budget; sub-100ms callbacks aren't a v1.1 goal.
- **Floating-point tolerance modes.** v1.1 compares JSON exactly;
  tolerance modes (epsilon, relative-error) come with the next
  schema iteration.
- **Shrinking-strategy tuning for live-mode failures.** Hypothesis's
  defaults handle minimal counterexamples adequately; tuning is a
  v1.3 concern.
