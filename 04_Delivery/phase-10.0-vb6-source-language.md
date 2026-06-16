# Phase 10 — VB6 source language

**Status:** Plan (v0.1)
**Companion:** `phase-9.0-multi-source-language.md` (precedent for adding a source language)
**Owner:** Platform team
**Target window:** 8–10 weeks for production · 4–6 weeks for demoable MVP

The platform today supports **Fortran**, **COBOL**, **Delphi**, and **C++** as input languages, targeting **.NET 8 / 10** and **Java Spring**. Phase 10 adds **VB6** as the fifth input language, targeting **.NET 10** with three flavours of starter kit (WinForms · Blazor Server · minimal API).

VB6 is a natural next addition: massive installed base, no vendor migration path since Microsoft abandoned the Upgrade Wizard, and a long list of idioms (Variant, On Error Resume Next, default properties, COM interop) that fail under naive LLM-only translation but map cleanly onto the same six-slot model the harness already uses for the other four languages.

This is **additive work**, not a re-architecture. Same six slots: parser sidecar · spec schema · extraction prompt · scaffold archetype · golden-dataset entries · equivalence sidecar.

---

## Choices locked in (2026-06-14)

| Decision | Choice | Implication |
|---|---|---|
| Default target | **.NET 10 LTS** (support horizon Nov 2028) | All extraction prompts + archetypes default to net10; net8 retained as legacy option. |
| Target paradigms | **Three starter-kit archetypes — WinForms · Blazor Server · minimal API** | Customer picks during Discovery. WinForms is the closest paradigm match; Blazor for web modernize; minimal API for headless services. |
| Parser strategy | **ANTLR4 grammar seeded from Rubberduck VBA** | Battle-tested on real VB6 codebases; MIT-licensed; covers the long tail of VB6 quirks. Wrapped as `vb6-parser-sidecar`. |
| `.frm` form designer files | **Separate hand-rolled parser** for the binary layout block; code block goes through the main parser | `.frm` mixes code + a serialised property bag. Treat them as two artefacts. |
| Equivalence runtime | **Windows Server Core 2022 container + customer-provided VB6 runtime DLLs** | We do NOT redistribute the VB6 runtime (licensing). Container ships an install script; customer drops the DLLs in at build time. Wine-based fallback for dev. |
| Public seed corpus | **Synthesised "VB6 Inventory Sample"** (Nous-authored, MIT) | OSS VB6 is sparse + low-quality. We hand-author a 12-form / ~3k LOC sample that deliberately exercises the 5 new claim kinds + COM interop + DAO. |

---

## Phase 10.0 — VB6 → .NET 10 · weeks 1–10

### Sub-phases

- **10.0.a — Parser sidecar `vb6-parser-sidecar`.** ANTLR4 grammar (seeded from Rubberduck VBA's `Rubberduck.Parsing.VBA`), Python or C# host, gRPC interface mirroring `delphi-parser-sidecar`. Produces:
  - typed AST per `.bas` / `.cls` / `.frm` code-block
  - separate property-bag parse of `.frm` form-layout block (controls, properties, event bindings)
  - call graph at routine granularity
  - module-uses edges (Imports / References) — VB6's analogue of Delphi's uses-clause
  - COM type-library references (`Tools → References` declarations)

- **10.0.b — Spec schema `vb6.schema.json`.** Five new claim kinds tuned to VB6 traps:
  - `on_error_handler` — `On Error Goto`, `On Error Resume Next`, `Err.Number` checks. Captures swallow-vs-rethrow behaviour. Maps to typed C# exception handling.
  - `com_interop_contract` — outbound calls to COM objects (`CreateObject`, `GetObject`, `Tools → References` types). Identifies external dependencies the customer must source modern equivalents for.
  - `event_handler_contract` — `Form_Load`, `Control_Click`, `MyClass_Initialize`, `MyClass_Terminate` and friends. Captures when fires, what state is assumed.
  - `default_property_usage` — implicit `Item` / `Value` access (e.g., `rs!FieldName` against a Recordset). Hidden in source; explicit in C# port.
  - `late_binding_call` — `Set obj = ...` against a `Variant` or `Object` reference; method calls without typed interfaces. Highest-risk claims; SME attention recommended.

- **10.0.c — Extraction prompts.** Three target paradigms × one source = three prompts:
  - `vb6/dotnet10-winforms/extract.v1.md` — form-by-form translation, code-behind stays close to original event model
  - `vb6/dotnet10-blazor/extract.v1.md` — form → component split (UI as `.razor`, logic as code-behind), state held in scoped services
  - `vb6/dotnet10-minapi/extract.v1.md` — for code-only `.bas`/`.cls` modules (no `.frm`); routines become minimal-API endpoints or background services

- **10.0.d — Scaffold archetypes.** Three sibling archetypes under `Llm/Archetypes/dotnet10/`:
  - `canonical-vb6-winforms` — net10.0-windows; form layout faithfully recreated; events become `+= EventHandler`; DAO/ADO becomes EF Core or Dapper per the archetype's manifest
  - `canonical-vb6-blazor` — net10.0; Server-rendered Blazor; form → component; state in scoped services; SignalR for live updates if the original used Timer events
  - `canonical-vb6-minapi` — net10.0; routines → minimal API endpoints; background services for `Sub Main`-style entry points
  - All three emit `[SpecClaim("INV-1")]` attributes citing the signed spec, matching the Delphi/C++ pattern.

- **10.0.e — Golden Dataset (VB6 traps).** Six hand-curated entries:
  1. `On Error Resume Next` masking — a routine that silently swallows division-by-zero
  2. Variant type coercion — `"5" + 3` vs `"5" & 3` (one is 8, the other is "53")
  3. Default property ambiguity — `Set x = rs("Name")` vs `x = rs("Name")`
  4. Late-bound dispatch — `CreateObject("Excel.Application")` and subsequent calls
  5. `Form_Load` ordering — controls initialised before `Form_Load` fires; relying on this is non-portable
  6. DAO recordset position — `MoveLast` then `MoveFirst` for `RecordCount`; modern providers don't need this

- **10.0.f — Equivalence sidecar `vb6-sidecar`.** Windows Server Core 2022 base image. At build time:
  - customer drops VB6 runtime DLLs (msvbvm60.dll, oleaut32, mscomctl, etc.) into a known volume
  - sidecar exposes an HTTP `/run` endpoint that compiles the original `.bas` / `.cls` / `.frm` via `vb6.exe /make`, runs the compiled binary, captures stdout
  - parallel call runs the .NET 10 candidate binary
  - cross-runtime byte-compare for behaviour parity
  - Wine-based fallback (`vb6-sidecar-wine`) for dev environments where Windows containers aren't available; covers non-COM routines

- **10.0.g — Property-based generators (VB6).** Extends the existing `property-test-sidecar`. New generators for VB6 value types:
  - Variant generator (mixes Integer / Long / Double / String / Date / Currency / Null)
  - Recordset proxy (synthesises a small in-memory ADO recordset)
  - COM IDispatch proxy (records method calls, returns canned responses)

- **10.0.h — Public seed corpus `VB6DemoSeed`.** Background seeder for the synthesised "Inventory Sample":
  - 12 forms (Login, MainMDI, OrderEntry, CustomerLookup, ProductCatalog, InvoicePrint, ReportDispatcher, ...)
  - ~3k LOC across `.bas` / `.cls` / `.frm`
  - DAO recordset use against an Access MDB
  - At least one CreateObject call into Excel for invoice export
  - Deliberately includes all 5 new claim kinds
  - MIT-licensed; lives in `seed-data/vb6-inventory-sample/`

- **10.0.i — Demo: migrate `OrderEntry_Submit` end-to-end.** Recognisable routine: button-click event handler, DAO insert, COM call to Excel, On Error Resume Next around the COM call. ~120 LOC. Pre-sign + scaffold off-camera; live walkthrough of the 4-gate validation report.

### Hard gate (Δ-10.0)

- [ ] `vb6-parser-sidecar` returns AST + call-graph for all 12 forms of the seed corpus in < 5s.
- [ ] `.frm` parser correctly extracts both code-block + property-bag for every form in the seed corpus.
- [ ] `vb6.schema.json` validates against ≥ 6 hand-authored example specs covering each new claim kind.
- [ ] Mock provider extracts a spec for `OrderEntry_Submit` matching the canonical reference (5 invariants, 3 edge cases, ≥ 2 `com_interop_contract` claims).
- [ ] Real Anthropic provider achieves ≥ 80% claim coverage on the Golden Dataset (matching the Delphi bar).
- [ ] All three .NET 10 scaffold archetypes build with the `net10.0` SDK and run `dotnet test` green.
- [ ] `vb6-sidecar` compiles + runs the seed corpus's `OrderEntry_Submit` against the .NET 10 candidate; outputs match byte-for-byte on a fixed input set.
- [ ] Property-based test generates ≥ 100 inputs per invariant; ≥ 95% pass on the WinForms candidate.
- [ ] VB6 Inventory Sample seeded in DEV; `/projects` shows it alongside MINPACK / LAPACK / Indy / fmt.
- [ ] Language picker on the New Corpus page (Phase 9.2.a) lists VB6 as a fifth schema choice.

### Soft gate

- [ ] ADR-035 (VB6 parser choice: ANTLR4 + Rubberduck heritage vs Roslyn-VB-fork) merged.
- [ ] ADR-036 (VB6 → .NET target paradigm: WinForms vs Blazor vs minimal API — selection criteria for engagement teams) merged.
- [ ] ADR-037 (VB6 equivalence runtime: Windows Server Core container + customer-provided runtime DLLs vs Wine fallback) merged.
- [ ] Demo video (~2 min) recorded for the OrderEntry walkthrough.
- [ ] Three new Phase 9.2.c help-banner copy blocks (one per target paradigm) added to the routine page.

### Fallback

- **If Windows container slips**: ship Wine-based fallback first. Wine runs VB6 IDE platinum-rated, and `msvbvm60.dll` works for non-COM routines. The equivalence story degrades on COM-heavy code but stays green on pure-logic routines.
- **If ANTLR4 grammar has gaps**: hand-rolled regex tokenizer + structural-only AST. Loses semantic resolution but the LLM can still extract claims from line-level context.
- **If `.frm` parser proves hostile**: skip form layout for v1; cover code-block only. Customers run a one-time `.frm`-to-`.cs` form-layout converter (open-source tools exist) before ingest.

---

## Risk register (additions for Phase 10.0)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-10.0-1 | VB6 runtime DLL licensing — Microsoft's runtime is part of VS6 install media; redistributing in our image likely breaches license | H | H | Don't redistribute. `vb6-sidecar` ships an empty `/runtime` volume + install script. Customer drops DLLs at build time. Document clearly in README. |
| R-10.0-2 | Customer's VB6 calls third-party ActiveX controls we have no replacement for | H | M | `com_interop_contract` claim flags every interop boundary. Customer decides per boundary: modern equivalent, COM interop shim, or feature drop. We catch the boundary; we don't fabricate replacements. |
| R-10.0-3 | `.frm` files mix code + binary form layout; ANTLR4 grammar handles code only | H | M | Separate `.frm` parser in 10.0.a — parse the property-bag header into a typed layout descriptor, send the code-block through the main parser. |
| R-10.0-4 | DAO/ADO data access doesn't translate 1:1 to EF Core | M | M | New claim kind subtype `data_access_pattern` (under `side_effects`) flags every recordset use. Archetype's manifest declares the target data layer (EF Core for transactional, Dapper for read-heavy). Customer architects pick. |
| R-10.0-5 | Variant arithmetic + implicit coercion produces subtly different results between VB6 and .NET (numeric overflow, string concat vs add) | M | H | Gate 4 (property-based search) is built for exactly this. Variant generator in 10.0.g specifically targets the coercion boundary. |
| R-10.0-6 | Synthesised seed corpus doesn't represent real VB6 code | M | M | Validate by ingesting a customer-provided sample during the first engagement. Plan to expand seed corpus to a second public sample (microsoft/VBSamples archive material is available under MIT). |
| R-10.0-7 | Windows container infrastructure unfamiliar to Linux-first dev team | M | M | One engineer takes the spike + writes ADR-037. Wine-based fallback exists for everyone else's day-to-day work. |
| R-10.0-8 | Anthropic claim coverage on VB6 lower than Delphi due to less Pascal-like grammar in training data | M | M | Calibrate the extraction prompt with more example specs in the system prompt. Lower the soft-gate bar from 80% to 70% if needed (matching the C++ bar in 9.1). |

---

## Out of scope (explicitly)

- **VBA in Office documents** (Word / Excel macros) — different runtime entry point, different host integration. Plan as Phase 10.1 if customer demand.
- **VB.NET** — already runs on .NET; no source-language migration needed. Different problem.
- **Win API `Declare` statements** — surfaced as `com_interop_contract` claims; customer provides modern equivalents (often `Microsoft.Win32` or `System.Runtime.InteropServices`).
- **GUI pixel-parity** for WinForms targets — the archetype recreates control layout from `.frm` property bags, but does not guarantee per-pixel parity. Customer signs off the visual diff during Phase 4 cutover.

---

## Sequencing & estimate

| Sub-phase | Cal-weeks (production) | Cal-weeks (MVP demo) | Owner | Depends on |
|---|---|---|---|---|
| 10.0.a — parser sidecar | 4 | 3 | Backend engineer | ANTLR4 toolchain spike |
| 10.0.b — schema | 1 | 1 | Backend engineer | 10.0.a (claim-kind validation) |
| 10.0.c — extraction prompts (3) | 2 | 1 (one paradigm only) | Prompt engineer | 10.0.b |
| 10.0.d — archetypes (3) | 4 | 2 (one paradigm only) | Backend engineer | 10.0.b |
| 10.0.e — Golden Dataset | 2 | 1 | Senior engineer / SME | 10.0.b |
| 10.0.f — equivalence sidecar | 4 | skip for MVP | DevOps + backend | 10.0.a, ADR-037 |
| 10.0.g — property generators | 1 | skip for MVP | Backend engineer | 10.0.f or existing harness |
| 10.0.h — seed corpus | 2 | 1.5 | Senior engineer | — |
| 10.0.i — E2E demo | 1 | 1 | Frontend / e2e engineer | 10.0.a through 10.0.h |
| **Total (calendar, 2-engineer team in parallel where possible)** | **~10 weeks** | **~5 weeks** | | |

Demoable MVP path: 10.0.a → 10.0.b → 10.0.c (one paradigm: WinForms) → 10.0.d (one archetype) → 10.0.h → 10.0.i. Skip 10.0.f and 10.0.g; rely on Gates 1, 2, and shadow-mode Gate 4 only. Equivalence sidecar lands in a follow-on 10.0.j.

Production path: all sub-phases as scoped.

---

## Kickoff checklist

Before starting 10.0.a:

- [ ] ADR-035 drafted: parser strategy chosen (default: ANTLR4 + Rubberduck) and signed off
- [ ] ADR-037 drafted: equivalence-runtime strategy chosen (Windows container vs Wine) — affects 10.0.f scope
- [ ] One engineer spikes Rubberduck VBA's `Rubberduck.Parsing.VBA` against the seed corpus skeleton (2 days)
- [ ] DevOps confirms Windows Server Core 2022 container support in the target deploy environment (or commits to Wine fallback)
- [ ] Phase 10.0 entry added to `phase-plan-and-gates.md`
- [ ] First five sub-phases ticketed
