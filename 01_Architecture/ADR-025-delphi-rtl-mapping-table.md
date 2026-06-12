# ADR-025 — Delphi RTL → .NET / Java mapping table

**Status:** Draft (proposed)
**Phase:** 9.0.b (parallel to schema work)
**Companion:** `phase-9.0-multi-source-language.md`, ADR-024 (parser)

---

## Context

The Indy corpus (and almost every real Delphi codebase) uses the Delphi RTL heavily — `TStream`, `TStringList`, `TBytes`, `TThread`, `TComponent`, `IInterface`, `TBuffer`, the entire `Classes` unit. The extraction LLM **cannot** reliably translate uses of these types without knowing their .NET and Java equivalents, because:

- The RTL is large (~2,500 types), older than .NET itself, and not documented in the LLM's pre-training data the way .NET BCL or Java's `java.util` is.
- Many RTL types have **non-obvious** counterparts: `TStringList` is closer to `Dictionary<string, string>` than `List<string>` because of its `Names` / `Values` view; `IInterface` ref-counting needs `IDisposable` in .NET but doesn't map cleanly to Java at all.
- Letting the model guess produces high-confidence-wrong claims that flow downstream into the scaffold.

We need a way to teach the extractor "when you see `TStringList`, here's what to claim about its semantics — and here's the .NET / Java translation target".

---

## Decision

Ship a **hand-curated mapping table** of the ~50 most-used Delphi RTL types. The table is JSON, lives at `Llm/Prompts/delphi/rtl-mapping.json`, and gets included as **in-prompt context** for every Delphi extraction call (per-routine, alongside the existing callee-signatures packet).

Each entry has the shape:

```jsonc
{
  "delphi_type": "TStringList",
  "rtl_unit": "Classes",
  "dotnet_equivalent": "System.Collections.Specialized.NameValueCollection",
  "java_equivalent": "java.util.LinkedHashMap<String, String>",
  "semantics_notes": [
    "Ordered insertion (preserve order in both targets).",
    "Names/Values view: NameValueCollection supports it natively; for Java the spec must add a sister Map for the values."
  ],
  "ownership_model": "Owned",
  "thread_safety": "Not thread-safe (Delphi caller responsible)."
}
```

The table is **versioned alongside the prompt** (prompt v1 references mapping v1; bumping the mapping bumps the prompt's calibration baseline).

---

## Alternatives considered

### A. RAG over the Delphi RTL source

Free Pascal ships its RTL source; we could index it and retrieve the relevant unit per extraction.

- **Pros:** Always current; no manual maintenance.
- **Cons:** Adds embedding + vector-store infra. The retrieved chunks would be Pascal source — the LLM still has to read it and infer semantics. Per-extraction latency + cost spike. **Rejected** as too heavy for a marginal accuracy gain.

### B. "Let the LLM figure it out"

Trust the model's pre-training.

- **Pros:** Zero engineering work.
- **Cons:** We tested this on a 5-routine Indy sample with Claude Sonnet 4. Result: 60% of RTL-touching claims had errors that an SME caught at review. Unacceptable for a portfolio of 300k LoC. **Rejected.**

### C. Compile-time auto-extraction

Walk the Delphi RTL with fpc and emit the mapping table programmatically.

- **Pros:** Comprehensive.
- **Cons:** Maps **structure**, not **semantics**. The hard part is the "what does this *mean* in .NET" judgment, which no compiler can produce. **Rejected** — wrong tool for the problem.

### D. Hand-curated table **(chosen)**

A Delphi-fluent SME (initially one of us; long-term, a named owner) maintains the table.

- **Pros:** Direct expression of semantic intent. Inspectable, versionable, reviewable in PRs.
- **Cons:** Maintenance burden. **Mitigated** by: 50 entries is Pareto-coverage for Indy; updates are quarterly or on-Open-Question; new types added when an extraction surfaces an unmapped one.

---

## Scope of the v1 table (~50 types)

Bucketed by RTL unit. Indy-coverage targets are listed first.

- **`System`** (built-ins): `string`, `AnsiString`, `WideString`, `UnicodeString`, `Byte`, `Word`, `Integer`, `Cardinal`, `Int64`, `Boolean`, `TBytes`, `Variant`, `Pointer`. *(13)*
- **`SysUtils`**: `Exception`, `TFileStream`, `TFormatSettings`, `TGUID`, `TArray<T>`. *(5)*
- **`Classes`**: `TObject`, `TPersistent`, `TComponent`, `TStream`, `TMemoryStream`, `TStringStream`, `TStrings`, `TStringList`, `TThread`, `TNotifyEvent`, `IInterface`, `TInterfacedObject`. *(12)*
- **`Generics.Collections`**: `TList<T>`, `TDictionary<K,V>`, `TQueue<T>`, `TStack<T>`, `TObjectList<T>`. *(5)*
- **`Generics.Defaults`**: `IComparer<T>`, `IEqualityComparer<T>`. *(2)*
- **`SyncObjs`**: `TCriticalSection`, `TEvent`, `TMutex`. *(3)*
- **`Variants`**: `TVarType`, `VarType()`, `VarIsClear()`. *(3)*
- **Indy-specific RTL extensions** (`IdGlobal`, `IdBuffer`): `TIdBytes`, `TIdBuffer`, `TIdSocketHandle`, `TIdComponent`. *(4)*
- **Buffer slack** for v1 expansion: ~3 entries reserved.

---

## Ownership

- **Author:** Delphi-fluent SME (named in the team roster; long-term we'd like to recruit one externally; for v1, Phase 9.0 lead).
- **Reviewers on every PR:** at least one Astra platform engineer + one external Delphi SME (if any are on-roster). Internal-only reviews accepted for v1 with a sunset date for getting an external SME in the chain.
- **Update cadence:** quarterly review meeting; emergency PR when an Open Question surfaces an unmapped type.

---

## Consequences

### Positive

- Sharp accuracy lift on Indy and other RTL-heavy Delphi corpora (target: ≥80% claim coverage on Indy calibration entries vs the ~60% baseline without the table).
- Inspectable: a customer asking "how do you translate `TStringList` to Java?" gets a one-line answer with semantics notes.
- Cheap to extend — adding a type is a one-row JSON PR.

### Negative

- Maintenance dependency. If the team loses the named owner, the table will drift. **Mitigated** by quarterly review checkpoints + a "Last reviewed" timestamp per entry.
- Risk of bias: the SME's translation preferences become the platform's defaults. **Mitigated** by `semantics_notes` calling out trade-offs (e.g. "`TStringList` to Java can be a `LinkedHashMap` or a sister `List<String>` + `Map<String,String>` pair — depends on whether callers use the Names/Values view").

### Neutral

- Token cost per extraction: ~3,000 input tokens (50 entries × ~60 tokens). Acceptable; current Fortran extractions use ~5,000 input tokens for the neighbourhood packet.

---

## Open questions

- **OQ-025-1:** Does the table need a Per-target column for "Java implementation library" (e.g. for `TStream`, do we use `java.io.InputStream` or `java.nio.channels.ReadableByteChannel`)? Decide at first Indy extraction review.
- **OQ-025-2:** Versioning interaction with the prompt. If prompt v1 references mapping v1 and someone PRs mapping v2, does the prompt need a bump? Proposal: mapping is a *minor* version of the prompt (`extract.v1.2.md` = prompt v1 + mapping v2). Decide at ADR finalisation.
