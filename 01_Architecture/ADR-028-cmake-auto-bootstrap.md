# ADR-028 — CMake auto-bootstrap on C++ ingest

**Status:** Draft (proposed)
**Phase:** 9.1.a (parser sidecar work)
**Companion:** ADR-026 (C++ template spec strategy), `phase-9.0-multi-source-language.md`

---

## Context

The C++ parser sidecar (Phase 9.1.a) is built on libclang. libclang's
real value over hand-rolled parsing comes from `compile_commands.json` —
the compilation database that records the exact flags (include paths,
preprocessor defines, language standard) each translation unit needs.
Without it, libclang parses every TU in a degraded mode (no headers
resolved, no macros expanded), and the extracted AST is roughly
"tokens + class names" — useless for spec extraction.

Most real C++ corpora produce `compile_commands.json` from a CMake build:

```
cmake -B build -DCMAKE_EXPORT_COMPILE_COMMANDS=ON
```

But the file isn't checked in — it's a build artifact. When the user
runs `POST /api/v1/ingest/git` against a CMake-based repo (`fmtlib/fmt`,
`nlohmann/json`, `range-v3`, etc.), the freshly cloned tree has no
`compile_commands.json`.

We have two options: (a) require the user to ingest a pre-built tree,
(b) have the ingest pipeline run CMake on the user's behalf.

---

## Decision

**The ingest pipeline runs CMake on the user's behalf when the corpus
ships a `CMakeLists.txt` and no `compile_commands.json` is present.**

Concretely:

- After cloning the corpus into the parser sidecar's workdir, the
  pipeline checks for `compile_commands.json` at the repo root, in
  `build/`, and in a configurable list of subdirectories.
- If absent and a top-level `CMakeLists.txt` exists, the sidecar runs
  `cmake -S . -B build -DCMAKE_EXPORT_COMPILE_COMMANDS=ON
  -DCMAKE_BUILD_TYPE=Debug` (no `--build`; we only need the database, not
  the binaries).
- The generated `build/compile_commands.json` is passed to libclang.
- If CMake fails (missing dependencies, configuration errors), the
  pipeline falls back to **degraded parse mode** — libclang runs with a
  best-effort flag set (`-std=c++20 -I<repo>/include -I<repo>/src`) and
  the parse outcome surfaces a warning that semantic resolution is
  partial.

A new compose service `cmake-bootstrap-sidecar` is **NOT** added — CMake
runs *inside* the cpp-parser-sidecar container (the container ships cmake
+ libclang together). Single image, single failure surface.

---

## Alternatives considered

### A. Require the user to ingest a pre-built tree

- **Pros:** Zero work for us. Predictable parse quality.
- **Cons:** Friction. The user has to clone, configure, build, *then*
  ingest. Demo recordings can't show "paste a GitHub URL, get a spec."
  Defeats the platform's one-click ingest narrative.
- **Rejected.**

### B. Run CMake as a separate sidecar

- **Pros:** Sidecar separation of concerns. The cpp-parser-sidecar stays
  focused on libclang.
- **Cons:** Two-hop network call (ingest → cmake-sidecar → cpp-parser-
  sidecar) adds latency without isolating risk — both sidecars would
  share the same Linux container failure modes anyway. More compose
  wiring.
- **Rejected** as over-engineering.

### C. CMake inside the cpp-parser-sidecar **(chosen)**

- **Pros:** One image, one failure surface, one log stream. Latency is
  whatever CMake's configure step takes (~5–30s for fmt-sized corpora).
  The sidecar can cache the `compile_commands.json` per `(repo, commit
  sha)` so subsequent ingests of the same commit skip the CMake hop.
- **Cons:** The cpp-parser-sidecar image is ~30 MB bigger (cmake +
  dependencies). Acceptable.

---

## Consequences

### Positive

- Maintains the one-click ingest UX for C++ corpora — same flow as
  Fortran / COBOL / Delphi.
- Caching `compile_commands.json` per commit sha is straightforward
  (mount a sidecar-private volume keyed by `<sha>.json`).
- Degraded-parse-mode fallback means the pipeline never hard-fails on
  an unfamiliar build system; the engineer gets a parsed-but-warning
  corpus and can iterate.

### Negative

- CMake configure step can be slow on large corpora (range-v3
  configures in ~45s cold). Mitigated by per-commit cache + a sidecar
  worker pool.
- The fpc-sidecar / gfortran-sidecar contracts don't have a build-system
  bootstrap step; cpp-parser-sidecar is asymmetric. Acceptable — C++
  genuinely needs it, the others don't.

### Neutral

- The sidecar's `parse` endpoint gains an optional `extraCmakeFlags`
  parameter for corpora that need a specific platform define or
  feature flag.

---

## Operational notes

- **Container image:** `astra/cpp-parser-sidecar:0.1.0` based on
  `debian:bookworm-slim` with `libclang-15`, `cmake-3.25`, `python3`.
- **Cache mount:** `/var/cache/cpp-parser/commit-shas/` mounted from a
  named volume `cpp-parser-cache`. Per-commit `compile_commands.json`
  lives here so repeat ingests are fast.
- **Timeout:** CMake configure is bounded to 5 minutes. Beyond that, the
  pipeline falls back to degraded mode and emits a warning.
- **Diagnostic surface:** `GET /api/v1/corpora/{id}/parse-warnings`
  exposes "degraded parse: CMake bootstrap timed out" so the user knows
  why their spec extraction may be missing fields.

---

## Open questions

- **OQ-028-1:** Does fmt's CMake configure step need `-DFMT_TEST=OFF`
  to skip downloading gtest? Verify during 9.1.a spike.
- **OQ-028-2:** For corpora using Bazel / Meson instead of CMake — out of
  scope for 9.1; Bazel→compile-commands has an `bazel-compile-commands-
  extractor` we could call later. Track as a 9.2 follow-up if a Bazel
  corpus is requested.
