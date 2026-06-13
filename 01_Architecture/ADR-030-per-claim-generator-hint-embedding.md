# ADR-030 — Per-claim generator-hint embedding

**Status:** Draft (proposed)
**Phase:** 9.3 (kickoff)
**Companion:** `phase-9.3-4th-validation-gate.md`, ADR-029 (property-based testing harness)

---

## Context

Phase 9.3 wires the 4th validation gate — Hypothesis-driven falsifying-
input search via the `property-test-sidecar`. The sidecar needs three
things to exercise a claim:

1. **What inputs to generate** — type, range, alphabet, length bounds.
2. **What constraint to filter inputs by** (optional) — e.g. `x > 0`.
3. **What to assert when an input is produced** — i.e. *the claim
   itself*, which already lives on the signed spec.

The claim is on the spec. The third piece is free. The question is
**where (1) and (2) live**:

- On the spec, as a per-claim `generatorHints` field that the SME signs.
- On the scaffold artifact, computed at scaffold-generation time.
- On the validation-run row, computed at gate-trigger time.
- In a separate per-spec sidecar configuration file.

Each option has consequences for *who is accountable for the input
universe* — and that turns out to matter more than the engineering
considerations.

---

## Decision

**Generator hints live on the spec, per-claim, in a `generatorHints`
field on the `invariant` and `edge_case` claim kinds. The SME sees and
signs the hints alongside the claim.**

Schema shape (added to both kinds, optional):

```json
{
  "id": "INV-1",
  "claim": "result equals the sum of inputs",
  "citations": [...],
  "confidence": "high",
  "generatorHints": {
    "inputs": [
      { "name": "a", "type": "int", "min": -1000, "max": 1000 },
      { "name": "b", "type": "int", "min": -1000, "max": 1000 }
    ],
    "constraint": null,
    "examples": [{ "a": 0, "b": 0 }, { "a": -1, "b": 1 }]
  }
}
```

The extraction prompt is taught to emit hints when the invariant
quantifies over an input vector (the common case). Pure structural
invariants ("result is non-null", "the cache is invalidated") don't
get hints; the prompt explicitly tells the LLM to omit the field.

---

## Alternatives considered

### A. Hints on the scaffold artifact

- **Pros:** No schema change; the scaffold knows the candidate
  binary's input/output types.
- **Cons:** **The SME never sees them.** A generator that explores
  inputs the SME considers out-of-scope (e.g. `INT_MIN`) silently
  finds counterexamples that the SME would reject as not-a-bug. The
  signed spec — the SME's accountability surface — has nothing to do
  with the universe being explored.
- **Rejected** because it severs the trust chain that the signed spec
  is supposed to anchor.

### B. Hints on the ValidationRun row

- **Pros:** Most engineer-friendly — tweak the hints per gate run.
- **Cons:** Re-running the gate with different hints means the
  ValidationRun's PASSED verdict isn't reproducible. Every PASSED run
  has to record its hints in the metrics, which bulks up the audit
  trail without giving the SME a clean signing surface.
- **Rejected** as ergonomics-over-correctness.

### C. Per-spec sidecar configuration file

- **Pros:** Keeps the signed spec clean.
- **Cons:** Two-source-of-truth problem. The SME signs the spec; the
  hints live elsewhere; an SME audit can't see the input space the
  4th gate explored without leaving the harness.
- **Rejected.**

### D. Per-claim hints on the signed spec **(chosen)**

- **Pros:** **One source of truth.** The SME signs the input universe
  at the same time they sign the claim. The audit trail captures the
  whole contract — claim + input universe + verdict — in one signed
  package. The harness can refuse to run the 4th gate against a
  claim without hints (instead of inventing them), which is the
  honest failure mode.
- **Cons:** Schema change across four schemas (fortran-f77, cobol,
  delphi, cpp). Each extraction prompt has to be updated to teach
  the LLM the hint shape. The SME's signing UI has to render the
  hints in a reviewable form.
- **Effort:** Schema change ≈ 1h. Prompt updates ≈ 2h × 4. SME UI
  surface ≈ 1d. Total ≈ 1.5d.

---

## Consequences

### Positive

- **Single signed contract.** Claim + input universe + (eventually)
  verdict all live on one signed-spec artifact. The compliance feed
  ships a complete picture per claim.
- **Honest failure mode.** When a claim has no hints, the 4th gate
  *skips it* with a `skipReason: no_hints` — the metrics page tells
  the engineer exactly why coverage is below 100%, and the SME knows
  to add hints if they want that claim exercised.
- **Reproducible runs.** The same signed spec produces the same input
  universe on every gate trigger; Hypothesis's `derandomize=True`
  flag completes the determinism story.

### Negative

- **Schema change across four schemas.** Each one needs the field
  added to `invariant` and `edge_case` claim definitions. Existing
  signed specs that pre-date the field validate unchanged because the
  field is optional.
- **Extraction prompt complexity.** The LLM has to decide *when* to
  emit hints. Heuristic in the prompt: "emit hints when the invariant
  quantifies over an input vector; omit when the invariant is
  purely structural or refers to non-deterministic state (clock,
  random)."
- **SME signing surface gets one extra section.** Mitigation: the
  Routing surface renders the hints inline with the claim, collapsed
  by default; opening the disclosure shows the input shape + examples.

### Neutral

- The hints' types are restricted to the property-test sidecar's
  supported set (`int`, `float`, `bool`, `string`, `bytes`). Hint
  types outside this set become an Open Question on the spec.

---

## Open questions

- **OQ-030-1:** Should the SME be able to *edit* hints during routing
  (vs accept-or-reject only)? Lean toward accept-only for v1 to keep
  the signing UI simple; an edit path can come later if SMEs ask.
- **OQ-030-2:** When the LLM emits hints with an obviously-bad bound
  (`min: 0, max: 1000000000000000` on a routine that loops over
  every value), do we warn at signing time or silently accept and
  let the gate's 30s/claim budget catch it? Lean toward accept; the
  budget IS the warning.
- **OQ-030-3:** Should hints have a `seed` field for fully-deterministic
  reruns across machines? Hypothesis's `derandomize=True` is already
  machine-independent for the same code + same spec; adding an
  explicit seed is over-engineering for v1.
