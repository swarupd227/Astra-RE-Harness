# Phase 9.3 — 4th validation gate (property-based testing)

**Status:** Plan (v0.1)
**Companion:** `phase-9.0-multi-source-language.md`, ADR-029 (property-based testing harness), ADR-030 (per-claim generator-hint embedding)
**Owner:** Platform team
**Target window:** ~1.5 weeks once kicked off

The validation report already runs three gates against every scaffold:
**COMPILE** (does the translated code build?), **TEST_PACK** (do the
auto-generated spec-tests pass?), **EQUIVALENCE** (does the translated
binary produce the same output as the reference binary on canonical
inputs?). Phase 9.0/9.1 added the `property-test-sidecar` container that
implements the **4th gate** — *can a Hypothesis-driven search find an
input that makes the translated and reference binaries disagree?* — but
the sidecar has no callers in the API. This phase wires it through:
schema → spec → validator → endpoint → UI → audit.

The 4th gate is the only one that *actively searches for bugs* rather
than checking pre-authored fixtures. It is what makes the harness
defensible against "but did you really test the corner cases?" and it's
the headline differentiator vs every other migration tool on the
market. Today it ships dark.

---

## Choices locked in (2026-06-13)

| Decision | Choice | Implication |
|---|---|---|
| Where generator hints live | **Per-claim, on the spec** (per ADR-030) | The SME sees + signs the hints; deterministic input space is part of the signed contract. |
| Which claims get exercised | **`invariant` + `edge_case`** | Only claim kinds that make a falsifiable assertion. `objectLifetime` / `propertyAccessor` / `templateInstantiation` don't have falsifiable shape. |
| Callback architecture | **API hosts the equivalence callback** | Sidecar drives Hypothesis; API knows how to bridge ref-binary (fpc/gpp/gfortran/gnucobol) ↔ candidate-binary (dotnet/maven) per spec's `schemaId`. |
| Budget defaults | **100 inputs / 30s / claim, 10min / spec** | Matches ADR-029. Tunable via config. |
| Gate-failure policy | **`FAILED` only when a falsifying example is found** | A timeout returns `PASSED with timed_out=true` — the test exhausted budget without falsifying. Distinct from a failure. |

---

## Sub-phases

- **9.3.a — Schema extension.** Add optional `generatorHints` field to
  `invariant` + `edge_case` claim kinds across all four schemas
  (`fortran-f77`, `cobol`, `delphi`, `cpp`). Shape per ADR-029:
  ```
  {
    "inputs": [{ "name": "x", "type": "int", "min": 0, "max": 1000 }, …],
    "constraint": "x > 0 implies result > 0",
    "examples": [{ "x": 0 }, { "x": 1 }]
  }
  ```
  Update the four extraction prompts to teach the LLM when to emit
  hints (rule of thumb: every invariant that *quantifies* over inputs
  should carry hints; pure structural invariants don't need them).
  Backward compatible — the field is optional everywhere, so existing
  signed specs validate unchanged.

- **9.3.b — PropertyTestClient + PropertyTestValidator + endpoint.**
  - `PropertyTestClient.cs` is a thin HTTP client for `property-test-sidecar:51056/falsify`. Same shape as
    `MavenClient.cs` / `GfortranClient.cs`.
  - `PropertyTestValidator.cs` walks the signed spec, extracts every
    `invariant` + `edge_case` claim with `generatorHints`, builds the
    `/falsify` request, and persists a `ValidationRun` row.
  - `POST /api/v1/scaffolds/{id}/validate/falsifying` mirrors the three
    existing `/validate` endpoints.
  - `POST /internal/equivalence-callback` is the new
    sidecar-talks-back-to-API endpoint. Receives one generated input
    per call, runs the reference binary (via the appropriate
    `*-sidecar` per the spec's `schemaId`) and the candidate binary
    (via maven or `dotnet` against the scaffold artifact), compares
    outputs, returns `{agree, refOutput, candOutput}`. Locked down to
    the sidecar's IP via a shared-secret header (the property-test-
    sidecar passes the secret through; the API rejects calls without it).

- **9.3.c — Persistence + metrics shape.** Extend
  `ValidationRun.Stage` enum-string with `"FALSIFYING"` — no DB
  migration required (existing schema is a string field).
  `MetricsJson` shape:
  ```json
  {
    "claimsExercised": 8,
    "falsifyingClaimIds": ["INV-2"],
    "totalExamplesTried": 723,
    "perClaim": [
      { "claimId": "INV-1", "examplesTried": 100, "falsifying": null,
        "elapsedMs": 1842, "timedOut": false, "callbackErrors": 0 },
      { "claimId": "INV-2", "examplesTried": 17,
        "falsifying": { "x": -1 },
        "refOutput": "0", "candOutput": "1",
        "elapsedMs": 1100, "timedOut": false, "callbackErrors": 0 }
    ],
    "overallFalsified": true
  }
  ```
  Audit event types: `validation.falsifying.started`, `.passed`,
  `.failed`.

- **9.3.d — Frontend 4th card + drilldown.**
  - `ValidationReportPage.tsx`: append a fourth `<ValidationStageCard>`
    keyed by `FALSIFYING`. Card body shows
    `<examplesTried>/<budget>` badge + a count of falsifying claims.
  - Drilldown panel for each falsifying claim shows the minimal
    counterexample input alongside the ref-vs-candidate output diff.
    Uses the same `<Card>`/`<Badge>` primitives the existing cards use
    (no new component library work).
  - Update the "All gates green" header copy to "All 4 gates green" /
    "Blocked — fix the red gates" wording.
  - `data-testid="validation-card-falsifying"` so the e2e can drive
    its Run button the same way as `validation-card-compile` etc.

- **9.3.e — E2E + bring-up + commit.**
  - Extend `minpack-demo.spec.ts`'s `runGate` helper to drive the 4th
    gate when `RECORD_DEMO=1`.
  - New standalone `falsifying-gate.spec.ts`: clean scaffold against
    a routine with a falsifiable invariant → 4th gate runs ≥ 1 example
    per claim → returns `PASSED` (or `FAILED` with a falsifying
    example, in which case the test asserts the example shape).
  - Bring up; verify the callback path round-trips end-to-end against
    MINPACK's `HYBRD1` (the same routine the existing demo uses).
  - Commit Phase 9.3.

---

## Hard gate (Δ-9.3)

- [ ] All four schemas validate against ≥ 2 signed-spec examples carrying
      `generatorHints` on at least one invariant.
- [ ] Real Anthropic provider emits `generatorHints` on ≥ 60% of
      invariants when prompted (rule #N in the updated extraction
      prompts).
- [ ] `POST /validate/falsifying` against a scaffold whose spec has at
      least one hinted invariant returns a `ValidationRun` row with
      `metrics.claimsExercised > 0`.
- [ ] `/internal/equivalence-callback` round-trips MINPACK `HYBRD1`'s
      reference (gfortran) + candidate (.NET) binaries on ≥ 50
      Hypothesis-generated inputs in under 10 minutes.
- [ ] When the candidate scaffold is intentionally broken (e.g. invariant
      `result > 0` violated for negative inputs), the 4th gate returns
      `FAILED` with a minimal counterexample in `metrics.perClaim[*].falsifying`.
- [ ] ValidationReportPage renders the 4th card with the same visual
      hierarchy as the existing three; "All 4 gates green" copy fires
      when every stage is `PASSED`.
- [ ] E2E walks the full 4-gate flow without flake (≥ 5 consecutive
      runs green).

## Soft gate

- [ ] ADR-029 (property-based testing harness) re-reviewed — confirm
      the harness budget / cap defaults match what we shipped.
- [ ] ADR-030 (per-claim generator-hint embedding) merged.
- [ ] Demo recording stays on hold per the standing Phase-9 directive;
      the 4-gate flow is demo-able but not recorded in 9.3.

---

## Risk register (additions for Phase 9.3)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-9.3-1 | The LLM doesn't emit useful generator hints — coverage stays low. | M | H | Hand-curated example specs in the prompt context; soft-gate at 60% coverage so the gate doesn't ship broken; fallback to "no hints → claim skipped, not failed". |
| R-9.3-2 | Equivalence-callback latency dominates the per-claim budget — only ~10 inputs fit in 30s instead of the target 100. | M | M | Cache the candidate-binary compile per scaffold (one build, many runs); cache the ref-binary similarly. Bump per-claim timeout to 60s if needed (still under the 10-min spec cap). |
| R-9.3-3 | A non-deterministic routine (uses time, random, env) makes the 4th gate flake unbounded. | M | M | Per-claim `behavior_on_violation` field (from cpp.schema.json) carries forward the "expected non-determinism" signal; the prompt warns the LLM to skip generator-hint emission when the routine is non-deterministic. SME signs off on the absence of hints. |

---

## ADRs to land before / during the phase

| ADR | Title | Phase |
|---|---|---|
| ADR-029 | Property-based testing harness (Hypothesis-based sidecar) | 9.0.g (already merged) |
| ADR-030 | Per-claim generator-hint embedding — schema, prompt, signing surface | 9.3 kickoff |

---

## Out of scope

- **Shrinking-strategy tuning.** Hypothesis's defaults handle our v1
  use cases; we don't fork the shrinker.
- **Property-testing for COBOL / Delphi RTL routines that have no
  obvious input shape.** The hinted-invariant model only works for
  routines whose inputs are values, not state machines. Stateful
  routines stay on the existing 3 gates.
- **Per-claim concurrency.** v1 runs claims sequentially. Per-spec
  parallelism comes in a Phase-9.4-ish polish round if the elapsed
  time becomes a real demo pain.
