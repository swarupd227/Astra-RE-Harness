# ADR-036 — VB6 → .NET target paradigm

**Status:** Draft (proposed)
**Phase:** 10.0.d (pre-kickoff)
**Companion:** `phase-10.0-vb6-source-language.md`, ADR-035 (parser choice), ADR-037 (equivalence runtime)

---

## Context

Phase 10.0.d ships scaffold archetypes for the VB6 → .NET 10 path. Unlike Fortran (one target — .NET 8/10), COBOL (one target — Java Spring), Delphi, and C++ (one paradigm each per target stack), VB6 codebases come in three structurally different shapes:

1. **Thick-client forms apps** — `.frm`-heavy GUI codebases that want to stay thick-client (legacy desktop deployment, often integrated with industrial / point-of-sale / shop-floor hardware).
2. **Web-modernisation candidates** — same `.frm`-heavy shape, but the customer's strategy is to web-ify during the migration (move to browser-delivered UI, retire the desktop install footprint).
3. **Code-only modules** — `.bas` / `.cls` libraries, COM components, background processes, scheduled batch jobs. No GUI.

A single "canonical VB6 → .NET" archetype would force one of these paradigms on customers who chose a different modernisation path. We have to pick the paradigm during Discovery, not bake it into the harness.

---

## Decision

**Three sibling archetypes under `Llm/Archetypes/dotnet10/`, with explicit picker at scaffold-generation time:**

| Archetype id | When to use | Output shape |
|---|---|---|
| `canonical-vb6-winforms` | Thick-client forms apps that stay thick-client | `.csproj` targets `net10.0-windows`; `.frm` → WinForms designer; event handlers → `+= EventHandler`; DAO/ADO → EF Core (or Dapper per manifest) |
| `canonical-vb6-blazor` | Forms apps web-modernising | `.csproj` targets `net10.0`; `.frm` → `.razor` component (code-behind partial class); state in scoped services; Timer events → SignalR push |
| `canonical-vb6-minapi` | Code-only `.bas` / `.cls` libraries, services, batch jobs | `.csproj` targets `net10.0`; routines → minimal API endpoints OR `IHostedService` background services; no UI |

Selection happens during **Discovery** (Phase 1 of the customer programme), not at the routine level. The whole corpus uses one archetype. Mixed corpora (some thick-client forms, some background services) get a single archetype per logical sub-corpus — the customer splits the ingest by logical boundary.

Selection criteria (decision tree):

```
Has .frm files?
├── No  → canonical-vb6-minapi
└── Yes
    └── Customer's modernisation strategy?
        ├── Keep thick-client  → canonical-vb6-winforms
        └── Web-modernise      → canonical-vb6-blazor
```

The archetype is recorded on the corpus at ingest time and stored as `Corpus.TargetArchetypeId`. The picker UI lives on the New Corpus page (Phase 9.2.a's language picker is extended with an archetype picker for VB6).

---

## Alternatives considered

### A. Three sibling archetypes + explicit picker **(chosen)**

- **Pros:** Customer's modernisation strategy drives the choice. No paradigm assumption baked into the harness. Each archetype is internally consistent (a WinForms scaffold doesn't try to be Blazor in places). Aligns with how customer architects already think about VB6 modernisation.
- **Cons:** Three archetypes to build + maintain (vs one). Three sets of extraction prompts. Three sets of starter-kit unit tests. Roughly 3× the effort of the Delphi or C++ archetypes.
- **Effort:** ~3 weeks per archetype = ~9 weeks total for 10.0.d if built serially; ~5 weeks if parallelised across two engineers.

### B. Single auto-detect archetype

- **Pros:** No picker UI. Customer just uploads the corpus.
- **Cons:** Auto-detect from `.frm` presence forces a thick-client paradigm on customers who want to web-modernise — they'd have to manually rewrite the WinForms scaffold into Blazor after generation, defeating the point of the harness. Auto-detect is making an irreversible architecture decision on the customer's behalf.
- **Effort:** ~3 weeks for one archetype + ongoing customer pain. **Rejected.**

### C. Single WinForms-only archetype

- **Pros:** Lowest effort. Closest paradigm match to original VB6. Lets us ship Phase 10.0 fastest.
- **Cons:** Forecloses the web-modernisation upside that's often the customer's stated reason for migrating off VB6 in the first place. Manufacturing and shop-floor customers may be happy; financial-services and SaaS customers would push back hard.
- **Effort:** ~3 weeks. **Rejected as Phase 10.0 target** but kept as the **starting archetype** for 10.0.d sequencing — see "Sequencing" below.

### D. Single Blazor-only archetype

- **Pros:** Maximises modernisation upside.
- **Cons:** Forces a UX redesign on every customer — including the ones who explicitly want a thick-client port. Industrial customers with on-prem deployments often don't have the browser-delivery infrastructure to host Blazor; for them, this is a non-starter.
- **Effort:** ~4 weeks (Blazor is more complex than WinForms). **Rejected.**

---

## Consequences

### Positive

- Customers pick the paradigm matching their modernisation strategy. No hidden assumptions.
- Each archetype is internally coherent — easier to test, easier for SMEs to review the generated code.
- Sets a clean pattern for future source languages that have similar paradigm splits (e.g., PowerBuilder, where customers also split between thick-client and web).

### Negative

- 3× the archetype-maintenance burden going forward. Each new behaviour-rule kind has to be reflected in three archetypes.
- The picker is a new surface on the New Corpus page that needs UX design.

### Neutral

- Three archetypes means three Golden-Dataset calibration runs. Extraction prompts converge on the same claim taxonomy; the prompt's `archetypePrefHint` field selects between them.

---

## Sequencing

10.0.d ships in three sub-stages so we don't gate the whole phase on building all three archetypes:

1. **10.0.d.1 — WinForms** (closest paradigm match, ships first; unlocks the demo)
2. **10.0.d.2 — minimal API** (simplest of the three; ships second)
3. **10.0.d.3 — Blazor Server** (most complex; ships last)

10.0.i E2E demo uses WinForms. Customer engagements that need Blazor or minimal API kick off after 10.0.d.2 / .3 ship respectively.

---

## Operational notes

- **Picker UI:** the New Corpus page (Phase 9.2.a) gets a second select element when VB6 is the chosen source language: "Target paradigm" with three options. Default: WinForms (closest paradigm match).
- **Selection persistence:** `Corpus.TargetArchetypeId` is set at ingest. Changing it later requires re-scaffolding (acceptable; spec stays unchanged, only the scaffold archetype changes).
- **Spec compatibility:** all three archetypes consume the same `vb6.schema.json`. The same signed spec produces three different .NET scaffolds — exactly the point.
- **Test pack:** each archetype includes its own xUnit test pack template. WinForms tests use `WinFormsTestHost`; Blazor tests use `bUnit`; minimal-API tests use `WebApplicationFactory`. All three run under `dotnet test`.

---

## Open questions

- **OQ-036-1:** For mixed corpora (some forms, some background services), should we support per-file archetype assignment? V1 says no — customer splits by logical boundary. Re-visit if customers push back during the first engagement.
- **OQ-036-2:** Does the Blazor archetype use Server or WebAssembly? Default Server (matches VB6's tightly-coupled event model and on-prem deployment). Open question whether to also support WebAssembly as a sibling — likely Phase 10.1.
- **OQ-036-3:** Authentication: VB6 thick-client apps often use Windows Integrated Auth via DCOM. Blazor archetype defaults to cookie auth; WinForms archetype keeps Windows auth. Document the trade-off in the archetype manifest's `description`.
