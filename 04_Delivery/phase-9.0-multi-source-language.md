# Phase 9 — Multi-source-language expansion

**Status:** Plan (v0.1)
**Companion:** `phase-plan-and-gates.md`, `risk-register.md`
**Owner:** Platform team
**Target window:** ~9–11 weeks once kicked off

The platform today supports **Fortran-77 → .NET 8** and **COBOL → Java Spring**. Phase 9 adds **Delphi** and **C++** as source languages, each targeting **both** .NET 8 and Java Spring (4 new prompts + 4 new archetypes), with a heavier equivalence story (cross-runtime byte compare **plus** property-based testing).

The platform was deliberately built around a pluggable language model. Each new source plugs into the **same six slots** — parser sidecar, spec schema, extraction prompt, scaffold archetype, golden-dataset entries, equivalence sidecar — so this is additive work, not a rewrite.

---

## Choices locked in (2026-05-26)

| Decision | Choice | Implication |
|---|---|---|
| Source-language order | **Delphi first, then C++** | 9.0 ships Delphi end-to-end; 9.1 adds C++; 9.2 polishes. |
| Target stacks per source | **Both .NET 8 and Java Spring for each source** | 4 extraction prompts + 4 scaffold archetypes (delphi/cpp × dotnet8/java-spring). |
| Public corpora | **Indy (Delphi) + fmt (C++)** | Both MIT-style permissive. Recognisable. Manageable size. |
| Equivalence depth | **Cross-runtime byte-compare + property-based testing** | Adds a property-testing harness (Hypothesis-style); equivalence sidecars per source language. |

---

## Phase 9.0 — Delphi → .NET 8 + Java Spring · weeks 1–5

### Sub-phases

- **9.0.a — Parser sidecar.** Shell out to `fpc -al -fpc-asmsyntax` for AST dump. Wrap in `delphi-parser-sidecar` container, expose gRPC the same way `parser-sidecar` does for Fortran. Output: typed AST + call-graph + unit-uses edges (Delphi's analogue of COMMON blocks).
- **9.0.b — Spec schema `delphi.schema.json`.** New claim kinds beyond the Fortran set:
  - `object_lifetime` (Owned / Borrowed / RefCounted via interface)
  - `interface_implementation` (which IInterface descendant)
  - `property_accessor` (read/write/both, with side-effect annotation)
  - `event_handler_contract` (which event type, what payload)
  - `RTTI_usage` (any reflection-based dispatch)
- **9.0.c — Extraction prompts** `delphi/dotnet8/extract.v1.md` and `delphi/java-spring/extract.v1.md`. Calibrated against the new Golden Dataset entries (see 9.0.e).
- **9.0.d — Scaffold archetypes** `canonical-delphi-net8` and `canonical-delphi-java-spring`.
  - .NET archetype maps units → namespaces, classes → classes, `property` → C# property, `event` → C# `event` keyword.
  - Java archetype maps units → packages, classes → classes, property accessors → getter/setter pairs, events → listener interfaces.
- **9.0.e — Golden Dataset entries (Delphi traps).** Hand-curated calibration corpus:
  1. Ref-counted interface aliasing (two `IInterface` refs to one object — premature free)
  2. `with`-statement scope confusion (which `Caption` are we setting?)
  3. `Variant` type coercion in arithmetic
  4. RTTI-based dispatch (TPersistent property streaming)
  5. Weak interface references (avoiding ref cycles)
  6. Class-method shadowing across descendant classes
- **9.0.f — Equivalence sidecar `fpc-sidecar`.** Free Pascal Compiler container; given an original `.pas` and a translated `.cs`/`.java`, runs both on a shared input set and byte-compares stdout.
- **9.0.g — Property-based testing harness.** New `property-test-sidecar` service. Takes a signed-spec invariant and generates Hypothesis-style input sets that try to falsify it. Runs against both original and translated; failure = invariant disagreement.
- **9.0.h — Public corpus seed.** `IndyDemoSeed` background seeder. Clones `github.com/IndySockets/Indy`, ingests via the existing Git-URL pipeline.
- **9.0.i — Demo: migrate `TIdSMTP.Connect` end-to-end.** Recognisable routine, network-protocol-heavy, ~200 LoC. Pre-sign + scaffold off-camera; live walkthrough on `TIdSMTP.SendCmd`.

### Hard gate (Δ-9.0)

- [ ] `delphi-parser-sidecar` returns AST + call-graph for a representative Indy file in < 5s.
- [ ] `delphi.schema.json` validates against ≥ 6 hand-authored example specs covering each new claim kind.
- [ ] Mock LLM provider extracts a 6-invariant spec for `TIdSMTP.Connect` matching the canonical reference.
- [ ] Real Anthropic provider extracts the same shape (no missing claim kinds; ≥ 80% claim coverage on the calibration entries).
- [ ] Scaffold artifact (.NET) compiles and `dotnet test` runs the auto-generated spec-tests green.
- [ ] Scaffold artifact (Java) builds with Maven and `mvn test` runs green.
- [ ] Equivalence harness runs both translated targets vs original `TIdSMTP.Connect` on a fixed input set; outputs match byte-for-byte.
- [ ] Property-based test generates ≥ 100 inputs per invariant; ≥ 95% of generated inputs pass on the translated code.
- [ ] Indy corpus seeded in DEV; `/projects` shows it alongside MINPACK + LAPACK BLAS.

### Soft gate

- [ ] ADR-024 (Delphi parser choice: fpc-AST vs hand-rolled) merged.
- [ ] ADR-025 (Delphi RTL mapping table — which Delphi RTL types map to which .NET / Java types) merged.
- [ ] Golden Dataset entries above 60% claim coverage on real Anthropic baseline.
- [ ] Demo video (~2 min) recorded.

### Fallback

If parser sidecar slips: fall back to a regex-based Delphi tokenizer + structural-only AST (no semantic resolution). Equivalence gate degrades to compile-only.

---

## Phase 9.1 — C++ → .NET 8 + Java Spring · weeks 5–10

### Sub-phases

- **9.1.a — Parser sidecar using libclang.** Python sidecar wrapping `libclang`'s AST. Consumes a `compile_commands.json`; if the corpus doesn't ship one, the sidecar runs `cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON` automatically at ingest time.
- **9.1.b — Spec schema `cpp.schema.json`.** New claim kinds:
  - `ownership_model` (unique / shared / borrowed / raw — with note on whether the spec resolves it or flags it as an Open Question)
  - `exception_safety` (basic / strong / nothrow guarantee)
  - `template_parameter_constraint` (the SFINAE / Concepts requirement, if any)
  - `ODR_risk` (does this header definition risk ODR violation across translation units?)
  - `move_semantics` (is move-after-use observed in callers?)
- **9.1.c — Extraction prompts** `cpp/dotnet8/extract.v1.md` and `cpp/java-spring/extract.v1.md`.
- **9.1.d — Scaffold archetypes.**
  - `canonical-cpp-net8` — P/Invoke for low-level interop + safe C# wrappers for the public surface. Memory ownership claims become `SafeHandle` patterns.
  - `canonical-cpp-java-spring` — pure-Java translation where possible (for template-heavy / header-only libraries like fmt); JNA boundaries where unavoidable. Ownership claims become `AutoCloseable`.
- **9.1.e — Golden Dataset entries (C++ traps).** Hand-curated:
  1. Signed-integer overflow as UB (a routine that *relies* on wraparound, technically UB)
  2. Dangling reference returned from a stack-local
  3. Multiple-inheritance diamond (virtual base resolution)
  4. Move-after-use bug (touching `x` after `std::move(x)`)
  5. Template SFINAE edge: a routine that compiles for some `T` and not others
  6. `std::vector<bool>` proxy-reference gotcha
- **9.1.f — Equivalence sidecars `gcc-sidecar` and `clang-sidecar`.** Both, because some routines compile under one and not the other (real-world finding).
- **9.1.g — Property-based testing extension.** Same harness as Phase 9.0; new generators for C++ value types (templates, smart pointers).
- **9.1.h — Public corpus seed.** `FmtDemoSeed` clones `github.com/fmtlib/fmt`. Runs CMake to produce compile_commands.json.
- **9.1.i — Demo: migrate `fmt::format` end-to-end** to both .NET (`string.Format`-like API) and Java (returning `String`). Famous routine, focused enough to walk through claim-by-claim.

### Hard gate (Δ-9.1)

- [ ] `cpp-parser-sidecar` builds + returns AST for an fmt header file in < 10s on cold start.
- [ ] `cpp.schema.json` validates against ≥ 6 hand-authored specs covering each new claim kind.
- [ ] Mock provider extracts a spec for `fmt::format` covering format-string parsing + the type-erased argument pack.
- [ ] Real Anthropic provider produces a spec with ≥ 70% claim coverage on the calibration entries (lower bar than Delphi — C++ is harder).
- [ ] .NET scaffold compiles + tests green.
- [ ] Java scaffold compiles + tests green.
- [ ] Equivalence harness runs gcc and clang variants of the original next to the translated; outputs match.
- [ ] Property-based tests find at least one real disagreement in the calibration corpus that an SME has to resolve as an Open Question.
- [ ] fmt corpus seeded in DEV.

### Soft gate

- [ ] ADR-026 (C++ template strategy: one spec per primary template, parameterised invariants) merged.
- [ ] ADR-027 (Java target for header-only C++: pure-Java rewrite or JNA shim) merged.
- [ ] ADR-028 (CMake auto-bootstrap: whether the ingest pipeline runs CMake on the user's behalf) merged.
- [ ] Demo video (~2 min) recorded.

### Fallback

If libclang integration slips: fall back to a sub-set of C++ (C-like routines only — no templates, no overloading). Demo target becomes a single fmt utility (e.g. `fmt::detail::utf8_to_utf16`) instead of `fmt::format` proper.

---

## Phase 9.2 — Multi-language UX polish · week 11

### Sub-phases

- **9.2.a — Language picker on ingest.** Currently inferred from file extensions; surface explicitly with the target-stack choice in one combined picker.
- **9.2.b — Per-language colour accent.** Sidebar lockup colours by current corpus's source language: Fortran indigo, COBOL teal, Delphi emerald, C++ amber.
- **9.2.c — Language-aware help text in the extraction beat.** Shorter for Delphi (cleaner mapping), longer + more cautionary for C++ (more open questions expected).
- **9.2.d — Updated 3-min demo.** Three sample migrations back-to-back: Fortran (MINPACK HYBRD1), Delphi (Indy `TIdSMTP.Connect`), C++ (`fmt::format`). One pass through the pipeline per language.

### Hard gate (Δ-9.2)

- [ ] Language picker on ingest works for all four source languages; the resulting corpus reports the right `sourceSchemaId`.
- [ ] Sidebar accent reads the active corpus's language and re-tints on navigation.
- [ ] 3-min demo MP4 produced, under 4 MB, native 1600×1000.

### Soft gate

- [ ] Demo recorded with captions, posted to the share folder.

---

## Cross-cutting work (lands once, used by all languages)

- **Parser-sidecar contract refactor.** Today each parser exposes a slightly different gRPC schema. Generalise to one `Parser.ExtractAst(corpusVersionId, fileBlob) → AstPacket` contract. The AstPacket shape is language-agnostic (typed nodes, edges).
- **Schema family registry.** `SpecSchemaRegistry` becomes the single source of truth for *which* language a corpus is + *which* prompt to use for extraction. Loaded from a folder tree at startup (same pattern as ArchetypeRegistry).
- **Property-test harness service.** New `property-test-sidecar` container. Hypothesis (Python) for the v1 implementation; can swap to QuickCheck-style native testing later if needed.
- **Equivalence-harness multi-runtime support.** Today `CrossRuntimeValidator` is hard-coded for gfortran + gnucobol. Refactor to read the corpus's source language and dispatch to the right sidecar (`fpc-sidecar`, `gcc-sidecar`, `clang-sidecar`).

---

## Risk register (additions for Phase 9)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-9-1 | libclang integration on Windows is finicky (DLL hell, ABI mismatches). | M | H | Sidecar runs in Linux container; the platform doesn't host libclang in the API process. |
| R-9-2 | C++ template specialisation produces dozens of specs per primary template, polluting the project view. | M | M | One spec per primary template (per ADR-026); instantiations become "calibrations" of the same spec. |
| R-9-3 | Indy uses Delphi RTL heavily; spec extraction can't follow into the RTL without ingesting it too. | H | M | Curated RTL mapping table (per ADR-025) ships as part of the prompt context. ~50 most-used types. |
| R-9-4 | Property-based testing flakes if the generator hits an undefined-behaviour input. | M | H | Generators per claim type are bounded; UB-prone generators (signed overflow, OOB index) are flagged and require SME opt-in. |
| R-9-5 | `fmt::format` is heavily template-meta-programmed — the spec may not converge. | M | H | Fallback to `fmt::detail::*` utility routines for the demo if the headline routine doesn't extract cleanly. |
| R-9-6 | Java target for header-only C++ (rewrite vs JNA) has no obviously-right answer per ADR-027. | M | M | Ship both archetypes; the user picks per project. |

---

## ADRs to land before / during the phase

| ADR | Title | Phase |
|---|---|---|
| ADR-024 | Delphi parser choice — fpc-AST shell-out vs hand-rolled grammar | 9.0 kickoff |
| ADR-025 | Delphi RTL → .NET / Java mapping table — scope + maintenance | 9.0.b |
| ADR-026 | C++ template spec strategy — one spec per primary, parameterised invariants | 9.1 kickoff |
| ADR-027 | Java target for header-only C++ — pure-Java rewrite vs JNA boundary | 9.1.c |
| ADR-028 | CMake auto-bootstrap — does the ingest pipeline run CMake on the user's behalf? | 9.1.a |
| ADR-029 | Property-based testing harness — Hypothesis sidecar vs native per-language tool | 9.0.g |

---

## Effort estimate

| Phase | Effort (assuming 1 engineer) |
|---|---|
| 9.0 (Delphi + both targets + property-based) | 4–5 weeks |
| 9.1 (C++ + both targets) | 4–5 weeks |
| 9.2 (Multi-language polish + demo) | 1 week |
| **Total** | **9–11 weeks** |

Parallelisable: 9.0.a (parser sidecar) and 9.0.b/c (schema + prompts) can run in parallel; same in 9.1. Two engineers shave ~3 weeks off the total.

---

## Public corpora reference

### Indy — `github.com/IndySockets/Indy`
- **Size:** ~300k LoC of Delphi
- **License:** Dual MPL / LGPL-style (verify before commit; both permissive enough for demo)
- **Why:** Recognisable in the Delphi world. TCP/IP / HTTP / SMTP / POP3 / FTP. Real protocol code, not toy.
- **Demo target routine:** `TIdSMTP.Connect` (~80 LoC) and `TIdSMTP.SendCmd` (~120 LoC) — both heavily-used, both well-tested, both touch the RTL.

### fmt — `github.com/fmtlib/fmt`
- **Size:** ~40k LoC of modern C++
- **License:** MIT
- **Why:** Famous, focused, well-tested. Heavy template use stresses the spec-extraction pipeline. Real performance work.
- **Demo target routine:** `fmt::format` if it converges; fall back to `fmt::detail::utf8_to_utf16` (smaller, less template-meta) if not.

---

## What this phase does NOT include

- A general-purpose language onboarding wizard. New source languages still require six platform-level changes per the list above.
- VB6, PL/I, RPG, or other legacy languages — those are Phase 10+.
- An LLM-driven scaffold provider for the existing Fortran/COBOL pairs (still archetype + claim-mapped TODOs). The mock provider continues to power demos; switching to LLM scaffold is Phase 10.
- AOT-compiled Delphi-style components for .NET (e.g. recreating Delphi's `TForm` in a WPF analog). Out of scope.
