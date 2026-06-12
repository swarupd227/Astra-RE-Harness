# ADR-024 — Delphi parser choice

**Status:** Draft (proposed)
**Phase:** 9.0.a (pre-kickoff)
**Companion:** `phase-9.0-multi-source-language.md`, ADR-025 (Delphi RTL mapping)

---

## Context

Phase 9.0 adds Delphi as a source language. We need a Delphi parser that produces:

1. A **typed AST** good enough to extract signatures, properties, events, interface declarations, and class hierarchies.
2. A **call graph** at routine granularity (who calls whom).
3. **Unit-uses edges** — Delphi's analogue of Fortran's COMMON blocks — so the planner can compute blast radius across `uses` clauses.
4. The ability to handle **real-world Delphi**, not just textbook samples: `{$IF...}` preprocessor directives, conditional compilation, inline assembly fragments, and the RTL's heavy `interface` usage (Indy in particular).

The existing Fortran path uses a Python sidecar wrapping `fparser2`. We want the same sidecar pattern for Delphi.

---

## Decision

**Shell out to Free Pascal Compiler (`fpc`) in a Linux container** to produce the AST. The sidecar invokes `fpc -al -dPARSE_ONLY` against the input `.pas` files, captures the assembly listing + symbol table, then walks the output to construct the canonical AST packet.

The sidecar runs as `delphi-parser-sidecar`, exposes the same gRPC contract as `parser-sidecar` (Fortran), and produces the same `AstPacket` shape — keeping the schema language-agnostic.

---

## Alternatives considered

### A. Hand-rolled grammar in Python (pyparsing / lark)

- **Pros:** Full control of the AST shape; no external binary dependency.
- **Cons:** Real-world Delphi is hostile to grammar-based parsing. Conditional compilation (`{$IFDEF}`, `{$IF Defined(MSWINDOWS)}`), the `with` statement, anonymous methods, generics with constraints, and asm-block escapes all require ad-hoc lookahead. Open-source Pascal grammars routinely fail on Indy and mORMot.
- **Effort:** ~3–4 weeks to reach Indy-coverage. **Rejected** as too speculative for a Phase-9.0 deliverable.

### B. DelphiAST (github.com/RomanYankovsky/DelphiAST)

- **Pros:** Mature, maintained, knows real-world Delphi.
- **Cons:** Written *in* Delphi — we'd have to ship a Delphi runtime in the sidecar (commercial Delphi or Lazarus). License complications (commercial Delphi). Smaller community than fpc.
- **Effort:** ~1 week to integrate, but the runtime/license drag-along makes it the highest-cost option for long-term ops. **Rejected.**

### C. libclang-style — fpc plus a libfpc binding

- **Pros:** Programmatic AST access without shelling out.
- **Cons:** No stable C ABI for fpc's internal AST; the libfpc port is community-maintained and lags behind fpc releases. We'd be on our own when fpc updates.
- **Effort:** ~1 week + ongoing breakage. **Rejected** — too brittle.

### D. fpc shell-out **(chosen)**

- **Pros:** Battle-tested for 20+ years on real-world Delphi (Free Pascal Project compiles itself). Handles preprocessor directives natively. Open-source (LGPL). Runs in a Linux container with zero license drag. Maintained.
- **Cons:** Output is assembly + symbol-table, not a pre-formed AST — we have to walk it. Slightly higher latency than in-process parsing (one fork per file ~50 ms cold).
- **Effort:** ~1 week to land the sidecar + a parser for fpc's output format.

---

## Consequences

### Positive

- Zero new licensing surface; fpc's LGPL clears commercial use of the sidecar via Astra's existing Apache-2.0 distribution.
- The same AstPacket shape as the Fortran path — extraction prompts and Migration Planner stay language-agnostic.
- Linux container is reproducible across CI / dev / prod.

### Negative

- Shell-out latency (~50 ms cold start, ~5 ms warm). Mitigated by long-running sidecar process with a worker pool.
- We have to maintain a parser for fpc's output format (assembly listing + symbol table). Output format is stable across fpc releases but not formally versioned — pin the fpc image tag, upgrade behind a sidecar version bump.

### Neutral

- Sidecar binary footprint ≈ 90 MB (fpc + Lazarus tooling). Comparable to the Fortran sidecar (~70 MB).

---

## Operational notes

- **Container image:** `astra/delphi-parser:0.1.0` based on `freepascal/fpc:3.2.2-bookworm`.
- **gRPC contract:** identical to `parser-sidecar` — `Parser.ExtractAst(corpusVersionId, fileBlob) → AstPacket`.
- **Failure mode:** when fpc rejects a file (syntax error or unresolvable preprocessor), the sidecar returns the fpc diagnostic verbatim. The IngestPipeline surfaces it as a parse warning, not a fatal error — same behaviour as fparser2.
- **Versioning:** fpc image tag pinned in `docker-compose.yml`; bump-after-test pattern as for fparser2.

---

## Open questions

- **OQ-024-1:** Does fpc's `-al` output cover anonymous methods cleanly? Verify against an Indy file that uses `TThread` callbacks (~10 LoC test). Spike during 9.0.a kickoff.
- **OQ-024-2:** Generics in Delphi 2010+ — does fpc's symbol table emit type-parameter information sufficient for the spec? If not, the Generics claim becomes an Open Question per spec rather than a structured field.
