# ADR-027 — Java target for header-only C++

**Status:** Draft (proposed)
**Phase:** 9.1.c
**Companion:** ADR-026 (C++ template spec strategy), `phase-9.0-multi-source-language.md`

---

## Context

Phase 9.1 ships **two** C++ target archetypes: `canonical-cpp-net8` and
`canonical-cpp-java-spring`. The .NET side is straightforward — modern C#
has direct C-interop (P/Invoke / `SafeHandle`) and decent value-type
ergonomics for the OO surface.

The Java side is harder. Java has no direct C++ interop story for
header-only libraries (which is most of the canonical C++ corpus —
`fmt`, `range-v3`, `Eigen`, `nlohmann/json`, etc.). The two real options are:

1. **Pure-Java rewrite** — translate the C++ source to idiomatic Java.
   No native binary in the deployed artifact.
2. **JNA boundary** — keep the C++ as a native library, expose it through
   Java Native Access. Java target is a thin facade.

Both are legitimate; both have failure modes. We need a default position
that the archetype emits, with an explicit escape hatch for the cases
where the default is wrong.

---

## Decision

**Pure-Java rewrite is the default** for header-only C++ libraries. The
`canonical-cpp-java-spring` archetype emits a pure-Java project with no
native dependencies.

The archetype includes a **JNA-boundary fallback path** as a parallel
project layout (under `archetype/jna-variant/`) that the user can opt into
on a per-routine basis when the rewrite isn't tractable (heavy template
metaprogramming, intrinsics, vendor-specific SIMD, etc.).

Selection happens at scaffold-generation time:

- Default: pure-Java rewrite.
- Opt-in: `?targetVariant=jna` query parameter on the scaffold-generate
  endpoint switches the archetype to the JNA layout.
- Open Question in the signed spec auto-suggests JNA when the routine has
  `template_parameter_constraint` claims that don't translate to Java
  generics (e.g., `requires std::is_trivially_copyable_v<T>`).

---

## Alternatives considered

### A. JNA boundary as the default

- **Pros:** No translation work — the native binary preserves behaviour
  byte-for-byte. Equivalence harness becomes trivial.
- **Cons:** Defeats the purpose of "migrate to Java" — the deployed
  artifact still bundles a native binary, the user still has to support
  C++ tooling, security review still has to audit C++ code. Defeats the
  Phase-9 goal of *leaving* legacy C++.
- **Rejected** as a default; kept as the opt-in fallback.

### B. JNI (raw) instead of JNA

- **Pros:** Lower overhead than JNA.
- **Cons:** Requires custom C glue per routine. Much more brittle than
  JNA. JNA's overhead is acceptable for the migration-staging use case
  (this isn't a hot inner loop, it's a one-shot translation crutch).
- **Rejected** for ergonomics.

### C. Pure-Java rewrite default + JNA fallback **(chosen)**

- **Pros:** Aligns with the platform's stated mission (leave C++ behind).
  When the rewrite is tractable (most fmt routines, all of nlohmann/json's
  parsing surface), the user gets a clean Java artifact. When it isn't,
  the JNA fallback keeps the migration moving without forcing the
  team to abandon Phase 9 mid-flight.
- **Cons:** Two archetype variants double the maintenance surface.
  Mitigated by sharing the test-fixture authoring code; only the
  implementation files differ between the two layouts.

---

## Consequences

### Positive

- The default scaffold reads like Java; the artifact is one `mvn package`
  away from production-ready.
- The escape hatch is structured, not ad-hoc — the JNA variant lives in
  the archetype, not in a comment that says "TODO: integrate with JNA
  somehow."
- The signed spec's Open Questions naturally surface routines where the
  default is likely wrong (template-constraint claims that don't map to
  Java generics).

### Negative

- Two parallel archetype layouts. The scaffold-generate endpoint has to
  switch on the `targetVariant` query parameter.
- The JNA variant ships a sample CMakeLists.txt for building the native
  shim — adds a build dependency the pure-Java variant doesn't have.

### Neutral

- The decision is per-routine, not per-project. A project may have some
  routines via pure-Java and others via JNA. The Migration Plan UI
  surfaces the variant choice as a per-routine column.

---

## Open questions

- **OQ-027-1:** What's the right default when a routine uses `std::variant`
  (no clean Java equivalent — `Object`, sealed-interface, or pattern
  match)? Default to sealed interface (Java 17+); Open Question if the
  signed spec calls out a third arm not representable.
- **OQ-027-2:** For SIMD-heavy code (Eigen, fmt's `compile_format_string`),
  is the JNA fallback acceptable or do we need a third "performance-
  oriented native module" archetype? Out of scope for 9.1; revisit if a
  Eigen demo lands later.
