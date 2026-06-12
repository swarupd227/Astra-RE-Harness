# ADR-029 — Property-based testing harness

**Status:** Draft (proposed)
**Phase:** 9.0.g (parallel to language onboarding)
**Companion:** `phase-9.0-multi-source-language.md`, ADR-024 (parser), ADR-025 (RTL mapping)

---

## Context

Phase 9 adds Delphi and C++ as source languages, with a **heavier equivalence story** chosen during scoping: cross-runtime byte-compare **plus** property-based testing.

The existing equivalence harness (CrossRuntimeValidator) compiles the original and the translated, runs both on a **fixed input set** captured during demo seeding, and byte-compares stdout. That catches deterministic translation errors well but misses:

- Behaviours that emerge only at edge inputs (extreme floats, empty strings, negative array indices, Unicode surrogates).
- Behaviours that emerge only at **scale** (a routine that handles 10 items correctly but not 10 million).
- Behaviours that are stochastic in the original (random seeds, time-based defaults).

We want to **fuzz the signed-spec invariants** — given an invariant like *"NEW_LF = ON_HAND_LF − USED_LF (no clamping)"*, generate hundreds of inputs and verify the invariant holds on both original and translated.

---

## Decision

Ship a **Hypothesis-based property-test sidecar** (`property-test-sidecar`).

- Hypothesis (Python) generates input vectors from a per-claim generator spec.
- The sidecar calls the **original** routine (in whatever sidecar that language lives in — fpc, gcc, gfortran, gnucobol) and the **translated** routine (.NET / Java) with the same input.
- It checks that **both** satisfy the spec invariant for the input — not just that they agree with each other.
- Disagreements OR invariant violations are returned as a structured **falsifying example** with the input vector and the diverging output.

The harness is **cross-language** by construction — it talks to language-specific runners over a uniform `{language, code_blob, inputs} → {outputs, runtime_ms}` contract. So we ship it once and Delphi + C++ + Java + .NET all benefit.

The harness output plugs into the existing `ValidationRun` model as a fourth gate: `compile → spec-tests → equivalence (fixed-input compare) → equivalence (property-based)`. Commit-to-Git stays disabled until **all four** are green.

---

## Alternatives considered

### A. Native per-language tools (QuickCheck / FsCheck / Jqwik / RapidCheck)

One per language: QuickCheck for Haskell-style, FsCheck for .NET, Jqwik for Java, RapidCheck for C++.

- **Pros:** Most idiomatic — properties live in the target language; outputs are familiar to engineers.
- **Cons:** 4 different harnesses to maintain. Each tool has different generator semantics. We'd build the invariant→generator bridge four times. Cross-language disagreements harder to reason about because each tool's shrinker is different.
- **Effort:** ~3x our Hypothesis-only path.
- **Rejected** as too costly for v1.

### B. "Just write more spec-tests by hand"

SME writes ~50 hand-crafted edge-case tests per spec.

- **Pros:** Zero new infrastructure.
- **Cons:** Doesn't scale. A 300k LoC corpus has thousands of routines.
- **Rejected** — defeats the platform's value proposition.

### C. LLM-generated test inputs

Ask the LLM to produce edge cases per claim.

- **Pros:** Cheap to integrate.
- **Cons:** Unreliable, unrepeatable, expensive to re-run when the spec changes. Hard to shrink to minimal falsifying examples.
- **Rejected** — wrong tool for the problem.

### D. Hypothesis sidecar **(chosen)**

A single Python sidecar wrapping Hypothesis, dispatching to language-specific runners.

- **Pros:** One codebase, four-language coverage. Hypothesis has best-in-class shrinkers (minimal falsifying examples). Mature; battle-tested on real-world projects. Generator semantics standardised across languages.
- **Cons:** Hypothesis is Python — the generator code itself isn't in the target language. Some teams find this conceptually weird; we accept it because the *spec invariants* are the source of truth, not the test code.

---

## Generator spec — how a claim becomes inputs

Each claim in the spec carries a **generator hint** the SME fills in (or the LLM proposes and the SME confirms):

```jsonc
{
  "id": "INV-4",
  "claim": "NEW_LF = ON_HAND_LF - USED_LF, no clamping",
  "citations": [{ "lines": "47" }],
  "confidence": "high",
  "generator": {
    "inputs": [
      { "name": "ON_HAND_LF", "kind": "real", "range": [-1e6, 1e6] },
      { "name": "USED_LF",    "kind": "real", "range": [-1e6, 1e6] }
    ],
    "constraint": "ON_HAND_LF >= 0",
    "examples": [
      { "ON_HAND_LF": 100, "USED_LF": 25 },
      { "ON_HAND_LF": 0,   "USED_LF": 0 }
    ]
  }
}
```

- `kind`: `real`, `int`, `string`, `bytes`, `unicode_string`, `bounded_int(min,max)`, `pointer_or_null`, `interface_ref`, …
- `constraint`: optional Python expression evaluated per generated input; failing examples are discarded (not counted toward the budget).
- `examples`: SME-seeded "known-tricky" inputs; always run in addition to Hypothesis's random ones.

Generator kinds for **Delphi-specific** types come from the RTL mapping table (ADR-025): `interface_ref` (refcount-aware), `widestring_with_surrogates`, etc.

Generator kinds for **C++-specific** types (Phase 9.1): `move_after_use`, `dangling_ref`, `unique_ptr<T>`, etc. — designed to trip exception-safety / ownership claims.

---

## Operational notes

- **Container:** `astra/property-test:0.1.0`, Python 3.12 + Hypothesis + the language runners.
- **Trigger:** the existing ValidationPolicy admin page gets a 4th gate toggle. Default off in DEV (slow); on in staging + prod.
- **Budget:** 100 inputs per claim, hard cap of 30 s per claim, hard cap of 10 min per spec. Configurable per-policy.
- **Output:** falsifying examples land in `ValidationRun.metrics.falsifying` as a structured JSON blob. The Validation Report page renders them as a collapsible table.

---

## Consequences

### Positive

- Catches edge-case translation errors the fixed-input harness can't see. Real-world expectation: 1–3 falsifying examples per 100-routine corpus on the first run.
- Cross-language by design — Phase 10 adds languages without re-doing the harness.
- Hypothesis's shrinker reduces every failure to a minimal repro, which the SME can paste into a spec-test directly.

### Negative

- Performance: ~10× slower than the fixed-input harness because it runs N inputs per claim. Acceptable as a pre-commit gate; not acceptable on every PR check.
- Mock-LLM provider doesn't model side-effecting routines well, so falsifying examples sometimes flag the *original* as wrong when running against the mock. Mitigated by running property tests only when the LLM provider is real (Anthropic), gated by `Llm:Provider == "anthropic"`.

### Neutral

- Adds Hypothesis as a transitive dependency. License: MPL 2.0 — fine for Apache-2.0 distribution.

---

## Open questions

- **OQ-029-1:** Does the falsifying example go in the audit log as a `validation.falsified` event, or stay quiet on the ValidationRun row? Proposal: log it (it's a meaningful event the auditor cares about).
- **OQ-029-2:** Should the SME be able to opt **out** of property-based testing per-spec (e.g. for routines they know are nondeterministic, like ones that emit a UUID)? Proposal: yes, via a `spec.metadata.skip_property_tests: true` opt-out, with a required justification field.
- **OQ-029-3:** How does the property-test harness handle routines that mutate global state? Proposal: process-per-input isolation, slower but correct. Tighten if it becomes a bottleneck.
