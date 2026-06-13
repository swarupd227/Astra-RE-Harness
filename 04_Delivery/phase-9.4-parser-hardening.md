# Phase 9.4 — Parser hardening

**Status:** Plan (v0.1)
**Companion:** `phase-9.0-multi-source-language.md`, `phase-9.3-4th-validation-gate.md`, ADR-024 (Delphi parser choice — superseded), ADR-028 (CMake auto-bootstrap), ADR-031 (Delphi parser strategy revision)
**Owner:** Platform team
**Target window:** ~1.5–2 weeks once kicked off

Phase 9.0 and 9.1 shipped v0 tokenizer-based parsers for Delphi and C++
respectively, deliberately scoped narrowly so the rest of the schema /
prompt / archetype / sidecar / e2e pipeline could land in parallel.
Both v0 parsers leave real signal on the table:

- **Delphi v0** misses `{$IF Defined(...)}` preprocessor branches,
  generic type parameters, and anonymous methods. Call detection is
  heuristic and over-counts on identifiers shared with variables.
- **C++ v0** doesn't expand macros, can't follow `#include` semantics,
  and treats templates structurally rather than semantically. SFINAE
  constraints aren't visible.

Phase 9.4 promotes both to **production-grade parsers** behind the same
`ParseOutcome` contract — the rest of the harness stays unchanged.

---

## Choices locked in (2026-06-13)

| Decision | Choice | Implication |
|---|---|---|
| Delphi parser | **tree-sitter-pascal** (Python-native AST via `tree-sitter-languages` bundle) | Supersedes ADR-024's fpc shell-out. See ADR-031 for rationale. |
| C++ parser | **libclang + CMake auto-bootstrap** | Per ADR-028 unchanged. Canonical C++ AST source. |
| Container layout | Keep both parsers inside the existing `parser-sidecar` container | Single image, single failure surface; no new compose wiring. |
| v0 fallback | **Keep v0 modules side-by-side**; production parser is the new default but the v0 tokenizer stays callable for emergency fallback | If tree-sitter-pascal / libclang misparse a file, the dispatch surfaces a warning and falls through to v0 rather than failing the ingest. |
| Re-ingest strategy | **One-shot re-ingest** of Indy + fmt corpora after the parser swap | The new parsers will likely detect more routines, drop false positives, and tighten line ranges. Compare counts via the existing `/api/v1/corpora/{id}` endpoint. |

---

## Sub-phases

- **9.4 — Scope + ADR-031.** Master plan (this doc) + ADR-031 (Delphi
  parser strategy revision). ADR-024 marked Superseded.

- **9.4.a — Delphi production parser.**
  - Add `tree-sitter==0.25.2` + `tree-sitter-languages==1.10.2` to
    `parser-sidecar/Dockerfile`'s pip install.
  - New module `delphi_parser_tree_sitter.py` walks the CST emitted by
    `tree_sitter_languages.get_language("pascal")` and produces the
    existing `ParseOutcome` shape (`SubroutineSummary` list with
    `name`, `signature`, `line_start`, `line_end`, `common_block_refs`,
    `called_subroutines`).
  - The CST gives us:
    - Exact routine boundaries (no more heuristic dedup)
    - Real call detection from the body's call expressions
    - Generic type parameters (`TFoo<T> = class`) extracted as part
      of the signature
    - Preprocessor branches (`{$IF Defined(...)}`) — tree-sitter-pascal
      emits them as conditional-block nodes; we walk both arms but
      tag routines with the branch they live in
  - Dispatch in `server.py`: prefer the tree-sitter parser, fall
    through to v0 if it raises.
  - Extend `tests/test_delphi_parser.py` with 3 new fixtures that the
    v0 parser misses: a `TFoo<T>` generic class, a `{$IFDEF MSWINDOWS}`
    branch, an anonymous method passed to `TThread.Synchronize`.

- **9.4.b — C++ production parser.**
  - Add `libclang==18.1.1` + `cmake` (apt) to
    `parser-sidecar/Dockerfile`. (CMake adds ~30 MB; libclang's pip
    package bundles its native deps so no `libclang-dev` apt install
    is needed.)
  - New module `cpp_parser_libclang.py` parses each TU via
    `clang.cindex.TranslationUnit.from_source` with the flags
    extracted from `compile_commands.json` if present, falling back
    to a `-std=c++20 -I<repo>/include -I<repo>/src` best-effort flag
    set.
  - CMake auto-bootstrap (per ADR-028): when ingesting a CMake-based
    corpus that has no `compile_commands.json`, the parser sidecar
    runs `cmake -S <repo> -B <repo>/build
    -DCMAKE_EXPORT_COMPILE_COMMANDS=ON` (with a 5-minute timeout)
    before invoking libclang. The result is cached per `(repo,
    commit-sha)` at `/var/cache/parser-sidecar/cmake-shas/<sha>.json`.
  - Per-commit cache mount in compose so subsequent ingests of the
    same commit skip the CMake configure.
  - libclang gives us:
    - Full macro expansion and `#include` semantics
    - Real call resolution (caller → callee, not just identifier-in-
      call-position heuristic)
    - Template parameter constraints (`requires` clauses, SFINAE
      sigs) emitted on the templateInstantiation claim
    - Source line numbers that account for `#line` directives
  - Dispatch + v0 fallback shape mirrors the Delphi branch.
  - Extend `tests/test_cpp_parser.py` with 3 fixtures the v0 misses:
    a routine with `#ifdef`-conditional body, a function template
    using `std::enable_if_t`, an inline `namespace fmt::detail`.

- **9.4.c — Validation + bring-up + commit.**
  - Stop docker compose; pin sidecar image tag at v0.2.0.
  - Re-build parser-sidecar with the new dependencies.
  - Re-ingest **Indy** (clears + re-clones) — diff routine counts
    against v0 baseline. Expected: ≥ 5% more routines detected
    (preprocessor branches surface units v0 skipped); ≤ 2% false
    positives.
  - Re-ingest **fmt** — diff vs v0 baseline. Expected: ≥ 20% more
    routines (macro expansion unlocks template-instantiation
    bodies); call detection should now show non-zero counts (v0
    reported `calls 0` everywhere because it had no semantic
    resolution).
  - Run existing e2e suite green (no regressions on
    `indy-delphi-demo.spec.ts`, `fmt-cpp-demo.spec.ts`,
    `minpack-demo.spec.ts`, `falsifying-gate.spec.ts`).
  - Commit Phase 9.4.

---

## Hard gate (Δ-9.4)

- [ ] `tree-sitter` + `libclang` + `cmake` all installable in the
      parser-sidecar Dockerfile; container builds without manual
      intervention.
- [ ] `delphi_parser_tree_sitter.py` passes all 7 existing
      `test_delphi_parser` cases + 3 new cases (generics,
      preprocessor branch, anonymous method).
- [ ] `cpp_parser_libclang.py` passes all 9 existing
      `test_cpp_parser` cases + 3 new cases (#ifdef body,
      SFINAE template, namespace nesting).
- [ ] Re-ingesting the Indy corpus produces ≥ 5% more routine rows vs
      the v0 baseline.
- [ ] Re-ingesting the fmt corpus produces routines whose
      `called_subroutines` is non-empty for at least 10 sample
      routines (v0 reported zero).
- [ ] All 4 e2e suites (indy-delphi-demo, fmt-cpp-demo, minpack-demo,
      falsifying-gate) stay green.

## Soft gate

- [ ] ADR-024 marked Superseded by ADR-031.
- [ ] ADR-031 (Delphi parser strategy revision) merged.
- [ ] Per-commit cache for `compile_commands.json` lands behind a
      named docker-compose volume.
- [ ] CMake configure timeout surfaces as a warning, not a hard error
      (degraded-parse-mode fallback per ADR-028).

---

## Risk register (additions for Phase 9.4)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-9.4-1 | tree-sitter-pascal's grammar misses real-world Indy constructs the v0 tokenizer accidentally handled. | M | M | Keep v0 as automatic fallback; dispatch surfaces a warning on the parse outcome that the production parser raised. |
| R-9.4-2 | libclang Python bindings can't find the system libclang at runtime in the slim Debian container. | L | H | Use the PyPI `libclang` package (bundles native deps) instead of relying on apt's `libclang-15-dev`. |
| R-9.4-3 | CMake configure on fmt's tree downloads gtest, doubling the ingest time. | M | L | Pass `-DFMT_TEST=OFF` (and equivalent flags for other corpora) via a per-corpus `extraCmakeFlags` config option (already specified in ADR-028 §OQ-028-1). |
| R-9.4-4 | The production parsers detect MANY more routines, inflating Migration Planner waves into the hundreds. | M | M | Acceptable — that's the truth of the corpus. Cap dashboard rendering at 200 routines/page (already implemented); planner already handles large graphs via SCC condensation (Phase 8.0.b). |

---

## ADRs to land before / during the phase

| ADR | Title | Phase |
|---|---|---|
| ADR-024 | Delphi parser choice — fpc-AST shell-out | **Superseded by ADR-031** (kept in history) |
| ADR-028 | CMake auto-bootstrap on C++ ingest | 9.4.b (already merged) |
| ADR-031 | Delphi parser strategy revision — tree-sitter-pascal over fpc shell-out | 9.4 kickoff |

---

## Out of scope

- **Per-language production parsers for Fortran / COBOL.** v0
  (`fparser2` for Fortran, hand-rolled for COBOL) is already
  acceptably accurate for those corpora; promote later if a real
  pain point surfaces.
- **Tree-sitter for C++.** libclang is the canonical AST source;
  tree-sitter-cpp would be a downgrade.
- **Cross-translation-unit analysis.** A single TU's AST is enough
  for the harness's per-routine spec extraction; whole-program
  inlining / link-time optimisation visibility is out of scope.
- **Real-time incremental re-parsing.** Re-ingests are one-shot
  events; we don't watch the source tree.
