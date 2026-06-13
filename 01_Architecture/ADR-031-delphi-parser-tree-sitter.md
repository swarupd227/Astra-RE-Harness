# ADR-031 — Delphi parser strategy revision (tree-sitter over fpc shell-out)

**Status:** Draft (proposed) · **Supersedes:** ADR-024
**Phase:** 9.4 (kickoff)
**Companion:** `phase-9.4-parser-hardening.md`

---

## Context

ADR-024 picked **Free Pascal Compiler shell-out** (`fpc -al -dPARSE_ONLY`)
as the Delphi parser. Six months later — with Phase 9.0 shipped and the
v0 tokenizer in production — three things look different than they did
at ADR-024 time:

1. **fpc's "parse-only" mode doesn't exist as a single flag.** ADR-024
   referenced `-dPARSE_ONLY`, but fpc's actual parse-without-link
   surface is `-s` (skip assembler/linker) + `-Cn` (don't call linker)
   + `-al` (output assembly). The output is `.s` files we'd walk to
   reconstruct an AST. That walker is ~800 LoC of brittle assembly
   parsing — fpc's assembly output format is stable across releases
   but not formally versioned, and any reformatting breaks us.

2. **tree-sitter-pascal landed and is maintained.** The
   `Isopod/tree-sitter-pascal` grammar (75 stars, last pushed
   Dec 2025) handles real-world Delphi including preprocessor
   directives, generics, and anonymous methods. Python bindings are
   first-class via the `tree-sitter-languages` PyPI bundle.

3. **The harness ate the v0 tokenizer fine for 4 months.** Phase 9.0
   shipped, Indy was ingested (34 files / 21k LOC parsed), and the
   spec extraction pipeline produced real signed specs against the
   v0 output. Production accuracy is the bar — not theoretical
   maximum fidelity.

These three shifts together change the trade-off. tree-sitter delivers
the practical accuracy we need at a fraction of the dependency footprint
and implementation complexity. ADR-024's fpc shell-out is over-engineered
for the production accuracy bar we've calibrated against.

---

## Decision

**Adopt `tree-sitter-pascal` (via the `tree-sitter-languages` bundle) as
the production Delphi parser.** ADR-024 is marked **Superseded**.

Concretely:

- Add `tree-sitter==0.25.2` + `tree-sitter-languages==1.10.2` to the
  `parser-sidecar` Dockerfile's pip install. No system-level apt
  dependencies; no fpc binary; no Lazarus tooling.
- New module `parser_sidecar/delphi_parser_tree_sitter.py` parses each
  file via `tree_sitter_languages.get_language("pascal")` and produces
  the existing `ParseOutcome` shape.
- The v0 tokenizer-based `delphi_parser.py` stays as an automatic
  fallback: if the tree-sitter walker raises (grammar mismatch, parser
  panic), dispatch surfaces a warning on `ParseOutcome.warnings` and
  re-runs through v0.

---

## Alternatives considered

### A. fpc shell-out (ADR-024's choice)

- **Pros:** Reference compiler — handles every Delphi construct that
  fpc itself accepts. Battle-tested 20+ years.
- **Cons:** Output is assembly, not an AST. We'd write + maintain an
  assembly-listing walker (~800 LoC). Container footprint is ~90 MB
  (fpc + lazarus tooling). Fork-per-file latency (~50 ms cold, ~5 ms
  warm) bounds throughput.
- **Rejected** because the implementation complexity is no longer
  justified given how well v0 has held up in production.

### B. Hand-rolled grammar in Python (pyparsing / lark)

- **Pros:** Full control over AST shape.
- **Cons:** Real-world Delphi is hostile to grammar-based parsing.
  Same reasoning as ADR-024 §A — rejected then, rejected now.

### C. DelphiAST

- **Pros:** Mature, real-world-tested.
- **Cons:** Written *in* Delphi — shipping a Delphi runtime in the
  sidecar adds the same license + footprint problems ADR-024 §B
  called out. Rejected then, rejected now.

### D. tree-sitter-pascal **(chosen)**

- **Pros:** Real CST (concrete syntax tree) emitted by a generated
  parser. Walks in Python without compiler shell-out. Container
  footprint adds ~25 MB (tree-sitter native + grammar). Sub-millisecond
  parse times per file. Handles preprocessor branches, generics, and
  anonymous methods natively. Grammar is actively maintained.
- **Cons:** Less battle-tested than fpc — the grammar may misparse
  exotic constructs (mORMot's heavy RTTI macros, some Embarcadero
  XE-era extensions). Mitigated by the v0 fallback.
- **Effort:** ~2 days to land the walker + tests (vs ~1 week for the
  fpc assembly walker).

### E. fpc API binding (libfpc)

- **Pros:** Programmatic AST access without shelling out.
- **Cons:** Community port lags fpc releases; no stable C ABI.
  Rejected at ADR-024; same reasoning applies.

---

## Consequences

### Positive

- **Simpler dependency footprint** — no fpc binary, no Lazarus, no
  apt-managed Pascal toolchain. Container build time drops from ~45s
  to ~12s.
- **Faster parse times** — sub-millisecond per file vs ~50 ms cold for
  fpc shell-out. Indy's 34 files now parse in well under a second.
- **Native Python AST handling** — the CST walker reads like the v0
  tokenizer (familiar shape) but with semantic accuracy.
- **Automatic v0 fallback** preserves the "never block ingest" guarantee
  that v0 already gave us.

### Negative

- **Grammar coverage is less complete than fpc.** Edge cases mORMot,
  some Embarcadero-specific compiler-magic constructs, may misparse.
  Mitigated by the v0 fallback + by the tree-sitter-pascal grammar
  being actively maintained (we can upstream fixes).
- **One more PyPI dependency** to track for security advisories
  (`tree-sitter` itself plus the `tree-sitter-languages` bundle).
  Both are small, well-maintained packages.

### Neutral

- The `ParseOutcome` contract is unchanged. All schema, prompt,
  archetype, and validator code stays the same.

---

## Operational notes

- **Image footprint:** parser-sidecar container grows ~25 MB
  (tree-sitter native + grammar). Vs ~90 MB for ADR-024's fpc image.
- **No new compose service.** The Delphi production parser lives in
  the existing `parser-sidecar` container alongside fortran (fparser2),
  cobol (hand-rolled), and cpp (libclang per ADR-028).
- **Failure mode:** when tree-sitter-pascal panics on a file, the
  parse RPC returns a `ParseOutcome` with the v0 routine list +
  `warnings = ["tree-sitter-pascal panic on <file>; fell through to v0"]`.
  The Migration Planner treats it as best-effort.
- **Versioning:** pin the tree-sitter + tree-sitter-languages versions
  in the Dockerfile; bump-after-test pattern as for fparser2.

---

## Open questions

- **OQ-031-1:** Does tree-sitter-pascal handle `mORMot`'s heavy
  RTTI macros correctly? Spike during 9.4.a kickoff against the JCL
  corpus.
- **OQ-031-2:** When the v0 fallback fires, do we still emit a
  `language: "delphi"` tag on the parsed file? Yes — the language
  identification is from the file extension, independent of which
  parser ran. The `warnings` array carries the fallback signal.
- **OQ-031-3:** Should we let SMEs override the production-parser
  output via the existing harmonisation flow? Out of scope for v0;
  if a parse is wrong the engineer re-ingests after fixing the source
  or upstreaming a grammar fix.
