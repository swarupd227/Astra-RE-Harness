# ADR-035 — VB6 parser choice

**Status:** Revised on implementation (2026-06-14)
**Phase:** 10.0.a
**Companion:** `phase-10.0-vb6-source-language.md`, ADR-036 (target paradigm), ADR-037 (equivalence runtime)

> **2026-06-14 implementation note.** On contact with the existing
> `parser-sidecar` codebase, the original "separate .NET 10 sidecar with
> Rubberduck NuGet" decision was revised. The actual parser-sidecar is
> a unified Python service that already hosts the Fortran (fparser2),
> COBOL, Delphi (v0 + tree-sitter-pascal), and C++ (v0 + libclang)
> parsers via a single gRPC contract. Adding VB6 as a new Python module
> (`vb6_parser.py` + `vb6_parser_antlr.py`) inside that service is the
> cleaner fit — one container to operate, one gRPC contract, one set of
> deployment patterns. The Rubberduck **grammar** is still the upstream
> source; we vendor `VisualBasic6.g4` at container build time and
> generate Python lexer/parser stubs via `antlr4 -Dlanguage=Python3`.
> The Decision section below has been updated to reflect this. The
> Alternatives section preserves the original .NET-host analysis since
> it remains the canonical "why not a .NET sidecar" reference.

---

## Context

Phase 10 adds VB6 as a source language. We need a VB6 parser that produces:

1. A **typed AST** at routine granularity covering `.bas` modules, `.cls` class modules, and the code-block portion of `.frm` form files.
2. A **call graph** at routine granularity (who calls whom across modules).
3. **Reference edges** — VB6's `Tools → References` (COM type-library bindings) and `Tools → References → uses`-equivalent module references — so the planner can compute blast radius.
4. The ability to handle **real-world VB6**, not textbook samples: `On Error Resume Next` blocks, late-bound `CreateObject` calls, `Variant` arithmetic, default-property access (`rs!Field`), implicit type coercion, and the `.frm` property-bag header that precedes the code block.
5. Separately, a **`.frm` property-bag parser** for the binary form-layout block (controls, properties, event bindings). This is a sibling deliverable in 10.0.a, not a separate sidecar.

The existing pattern is parser-sidecar per language (Fortran → fparser2, Delphi → tree-sitter-pascal, C++ → libclang). We want the same shape for VB6.

---

## Decision (revised on implementation)

**ANTLR4 grammar vendored from Rubberduck VBA's `Rubberduck.Parsing/Grammar/VisualBasic6.g4`, hosted as a Python module inside the existing unified `parser-sidecar`.** No new sidecar container is introduced; VB6 plugs into the same gRPC contract used by Fortran, COBOL, Delphi, and C++, dispatched by file extension in `server.py`.

Two implementation files land in 10.0.a:

- `parser_sidecar/vb6_parser.py` — v0 tokenizer-based parser (regex-driven, ~400 LOC). Mirrors `delphi_parser.py` in shape and posture. Handles `.bas`, `.cls`, `.frm` (code block), `On Error` advisories, and COM call-site detection. Also exports `parse_frm_layout` for the `.frm` property-bag header (hand-rolled, ~120 LOC).
- `parser_sidecar/vb6_parser_antlr.py` — production ANTLR4 parser skeleton (lazy-imports the generated lexer/parser, falls back to v0 on grammar absence). Mirrors `delphi_parser_tree_sitter.py` and `cpp_parser_libclang.py` in shape. The grammar vendoring and walker implementation land in 10.0.a.2.

The dispatcher in `server.py` (`_vb6_parse`) tries the ANTLR4 path first, catches grammar absence or parser panics, and falls back to v0 with a warning surfaced in `ParseOutcome.warnings`. Same pattern as the Delphi tree-sitter dispatcher.

Rubberduck VBA is an open-source (GPL-3.0) Visual Studio Tools-for-Applications-style add-in that has maintained an ANTLR4 grammar for VB6 / VBA since 2014. Its grammar covers the full language including the long tail of preprocessor directives (`#If`, `#Const`), `Declare` statements for Win32 P/Invoke, all four variants of `On Error`, and the implicit defaults. We vendor only the `.g4` grammar file, not the C# parsing services on top of it — the grammar file is the smallest reusable unit and avoids both the licence surface of Rubberduck's higher-level parser and the host-language commitment of a .NET sidecar.

---

## Alternatives considered

### A. ANTLR4 + Rubberduck VBA in a .NET sidecar **(chosen)**

- **Pros:** Battle-tested on hundreds of real VB6 codebases. Maintainers are responsive. Grammar covers the long-tail quirks (`#If` preprocessor, `Declare` Win32 P/Invoke, `On Error Goto -1`, default properties). Open-source (GPL-3.0 for the parser module — see consequences below). Same `.NET` host as the API, so the sidecar reuses our existing container/observability pipeline.
- **Cons:** GPL-3.0 licence on Rubberduck's parser is more restrictive than the LGPL/MIT we've used for other languages. Mitigated below.
- **Effort:** ~3 weeks to wrap as a sidecar + add gRPC contract + map Rubberduck's AST to our canonical `AstPacket`. Production-ready in 4 weeks.

### B. Hand-rolled lark / pyparsing grammar

- **Pros:** Full control over AST shape; permissive licence on the host code.
- **Cons:** Real VB6 is hostile to grammar-based parsing for the same reasons real Delphi is — conditional compilation, line-continuations, implicit defaults, and the cult of `Variant`. Open-source VB6 grammars routinely break on COM-heavy code. Building a Rubberduck-equivalent from scratch is a 6-month engagement, not a 4-week sub-phase.
- **Effort:** ~12–16 weeks to reach seed-corpus coverage; another 8 weeks to reach customer-codebase robustness. **Rejected** as speculative for a Phase-10.0 deliverable.

### C. Fork Roslyn-VB.NET and back-port the VB6 grammar

- **Pros:** Roslyn is Microsoft-maintained; great tooling.
- **Cons:** VB.NET grammar diverges significantly from VB6 — different declaration syntax (`Dim x As Object` vs `Dim x As Object`, but `Set x = ...` vs `x = ...`), no `On Error Resume Next`, no default properties via parenthesised access, different event semantics. Back-porting is essentially writing a new grammar from a Roslyn skeleton. Plus the ongoing pain of pulling upstream Roslyn changes into a divergent fork.
- **Effort:** ~10–14 weeks for first cut + permanent fork-maintenance debt. **Rejected.**

### D. Drive `vb6.exe` via VBIDE COM interface from a Windows host

- **Pros:** Authoritative — uses Microsoft's actual VB6 IDE to parse.
- **Cons:** Windows-only at parse time (breaks the Linux-first dev workflow). Requires Visual Studio 6 installed in the parse container (~250 MB, licensed only via legacy MSDN). Brittle: the VBIDE COM interface was deprecated; running `vb6.exe /make` to drive the parser is a documented but unsupported workflow.
- **Effort:** ~2 weeks for the spike + ongoing brittleness. **Rejected** — non-starter for CI/CD.

---

## Consequences

### Positive

- Single sidecar covers `.bas`, `.cls`, and `.frm` code-blocks. The `.frm` property-bag parser is a hand-rolled siblings (~200 LOC) using the same gRPC contract.
- Reuses our existing .NET sidecar container pattern + observability hooks.
- Rubberduck's grammar handles the long-tail quirks we can enumerate in 10.0.e Golden Dataset — every entry there has a Rubberduck issue/discussion thread we can reference.
- Maps cleanly onto the canonical `AstPacket` shape — extraction prompts and Migration Planner stay language-agnostic, same as Delphi and C++.

### Negative

- **GPL-3.0 licence on Rubberduck's parser** is more restrictive than the LGPL / MIT licences on our other parser dependencies. We mitigate by isolating the GPL'd code in the sidecar process — the sidecar exposes only a gRPC contract; the API and frontend never link against Rubberduck. Per the GPL FAQ and the FSF's interpretation of "mere aggregation" via process boundaries, this isolates the GPL obligation to the sidecar container. We will publish the sidecar Dockerfile and any modifications to Rubberduck's source under GPL-3.0 in a separate repo per the compliance requirement.
- Sidecar container footprint ≈ 130 MB (Rubberduck + ANTLR4 runtime + .NET 10 base). Larger than the Fortran or Delphi sidecars (~70–90 MB) but well within budget.

### Neutral

- Sidecar latency: ~30 ms cold start, ~3 ms warm per file. Comparable to Delphi.

---

## Operational notes

- **Container image:** `astra/vb6-parser:0.1.0` based on `mcr.microsoft.com/dotnet/aspnet:10.0-windowsservercore-ltsc2022` is NOT needed at parse time — the parser is pure code, not the VB6 runtime. Use `mcr.microsoft.com/dotnet/aspnet:10.0` (Linux). The equivalence sidecar (ADR-037) is the only component that needs Windows.
- **gRPC contract:** identical to `delphi-parser-sidecar` — `Parser.ExtractAst(corpusVersionId, fileBlob) → AstPacket`. Files with `.frm` extension trigger the dual-pass: ANTLR4 parses the code-block; hand-rolled parser handles the property-bag header.
- **Failure mode:** when Rubberduck rejects a file (syntax error or unresolvable preprocessor), the sidecar returns the diagnostic verbatim. The IngestPipeline surfaces it as a parse warning, not a fatal error.
- **Versioning:** Rubberduck NuGet version pinned in the sidecar `.csproj`. Bump-after-test pattern.

---

## Open questions

- **OQ-035-1:** Does Rubberduck's grammar emit a usable representation of `Declare Sub` Win32 P/Invoke statements? Verify against a real VB6 codebase that uses `kernel32.dll` calls (~5 LOC test). Spike during 10.0.a kickoff.
- **OQ-035-2:** Implicit type coercion (`Dim x As Variant; x = "5" + 3`) — does the AST preserve the source-level operator (`+`) or normalise it to a runtime call? The extraction prompt needs to see the source operator to surface the `late_binding_call` claim correctly.
- **OQ-035-3:** Conditional compilation (`#If VBA7 Then`). Rubberduck honours the conditional flags at parse time; do we pre-set flags per the corpus's target VB6 version, or surface both branches as alternatives? Resolve before 10.0.b schema work.
