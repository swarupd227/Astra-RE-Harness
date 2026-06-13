# ADR-026 — C++ template spec strategy

**Status:** Draft (proposed)
**Phase:** 9.1 (kickoff)
**Companion:** `phase-9.0-multi-source-language.md`, ADR-027 (Java target for header-only C++)

---

## Context

Phase 9.1 adds **C++** as a source language. C++ templates pose a unique
spec-extraction problem that Fortran / COBOL / Delphi do not:

- A single function template `template<typename T> T add(T a, T b)` produces
  many *instantiations* at compile time (`add<int>`, `add<double>`,
  `add<MyType>`). Each instantiation has different semantics (overflow on
  `int`, NaN on `double`, ADL hooks on `MyType`).
- `fmt::format` — our headline demo routine — is *entirely* template-driven.
- If we treat each instantiation as its own subroutine in the harness, a
  reasonable corpus blows up to tens of thousands of "subroutines" in the
  Projects dashboard.

We need a strategy that keeps the spec extraction tractable AND produces
honest, behaviourally-grounded specs.

---

## Decision

**One spec per primary template, with `parameterised invariants`** —
invariants that quantify over the template type parameters and capture the
*conditions* under which they hold.

Concretely:

- The parser sidecar emits **one subroutine row per primary template** (the
  `template<typename T> ...` declaration the developer wrote), not per
  instantiation.
- The Delphi schema's `template_parameter_constraint` claim kind captures
  SFINAE / Concepts requirements (e.g., `requires std::is_arithmetic_v<T>`).
- Each invariant claim carries an optional `quantifier` field — `"for all T
  satisfying <constraint>"` — and a `behaviour_on_violation` field that
  documents what happens for inputs outside the constraint (compile error,
  UB, soft fallback).
- Instantiations observed in the corpus are recorded as **calibrations** of
  the same spec, NOT new specs. The Migration Planner reports
  `instantiations_observed: ["int", "double", "fmt::detail::big_int"]` next
  to the spec.

---

## Alternatives considered

### A. One spec per instantiation

- **Pros:** Each spec is concrete; invariants can be type-specific.
- **Cons:** Catastrophic for the Projects dashboard. `fmt::format` alone
  would produce 50+ instantiations across the fmt corpus. The signed-spec
  audit trail loses cohesion — the SME signs `add<int>` and `add<double>`
  separately even though the developer wrote ONE primary template.
- **Rejected** as un-scalable and incoherent.

### B. Sample one instantiation, fall back to "generic" prose

- **Pros:** Simple to implement.
- **Cons:** The spec lies. If we pick `add<int>` and write "this routine
  adds two integers", the `add<double>` translation gets miscoded
  (overflow semantics differ).
- **Rejected** as factually wrong.

### C. One spec per primary template, parameterised invariants **(chosen)**

- **Pros:** Matches how C++ developers actually reason about templates.
  Spec-by-primary keeps the corpus dashboard sane. Parameterised invariants
  let the SME approve `add` once and have the constraint apply to all
  instantiations the corpus uses. The instantiation list provides honest
  coverage data without polluting the spec count.
- **Cons:** The extraction prompt has to be careful about *which* invariants
  are parameterisable vs which depend on a specific `T`. Edge cases
  (`add<bool>` is weird because `bool + bool` integral-promotes to `int`)
  get surfaced as `edge_cases` claims tagged with the offending `T`.
- **Effort:** ~1 day of prompt engineering, no extra parser work.

---

## Consequences

### Positive

- Corpus dashboard stays human-readable — one row per template, even when
  the corpus instantiates it dozens of times.
- The signed-spec audit trail is per-primary, which matches the
  developer's mental model.
- Migration Planner gets a `instantiations_observed` field for free.

### Negative

- Spec extraction has to handle the "for all T satisfying X" quantifier
  honestly. The mock LLM doesn't generate quantifiers; only the real
  Anthropic prompt does. Calibration corpus has 2 explicit quantifier
  examples (`fmt::detail::compile_string_check<S>` and
  `fmt::join<Range>`).
- Java target archetype (per ADR-027) has to produce generic methods
  (`<T> String format(T arg)`) even when the source is type-erased; that
  doesn't always type-check cleanly. Mitigated by `Object` fallback when
  the constraint doesn't translate.

### Neutral

- Spec-schema field `instantiations_observed` is informational, not
  signed. The Migration Planner reads it for prioritisation but the
  validation gates don't look at it.

---

## Open questions

- **OQ-026-1:** How do we surface a SFINAE rejection in the corpus?
  E.g., the user's code instantiates `add<MyType>` where `MyType` has no
  `operator+` — does that show up as an Open Question on the parent
  template, or as a corpus-level diagnostic? Spike during 9.1.a kickoff.
- **OQ-026-2:** Explicit template specialisations (e.g., `template<> int
  add<bool>(...)`). Treat as separate specs, or as overrides on the
  primary? Lean toward separate-spec because the developer wrote them
  separately. Confirm before 9.1.b.
