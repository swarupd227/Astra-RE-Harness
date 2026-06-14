# Phase 9.2 — Multi-language UX polish

**Status:** Plan (v0.1)
**Companion:** `phase-9.0-multi-source-language.md` (Phase 9.2 stub)
**Owner:** Platform team
**Target window:** ~3 days once kicked off

Phase 9.0 / 9.1 / 9.4 shipped 4 source languages (Fortran, COBOL, Delphi,
C++) × 2 target stacks. The harness now handles them all, but the UX
treats them uniformly: same colour, same language hints, same ingest
flow regardless of the source language. Phase 9.2 closes that gap with
three small surface upgrades that make the per-language story visible
without changing any core flow.

---

## Choices locked in (2026-06-13)

| Decision | Choice | Implication |
|---|---|---|
| Language identification on ingest | **Explicit picker + extension-derived default** | Engineer can override; if they don't, the existing file-extension heuristic still picks. Manual override wins. |
| Per-language colour palette | **Fortran indigo · COBOL teal · Delphi emerald · C++ amber** | Single accent per language. Reuses existing Tailwind tokens (`ace-900`, `accent-muted`, etc.) so the sidebar's two-tone shell stays coherent. |
| Where the accent renders | **Sidebar lockup + corpus card badges** | Two highest-visibility surfaces. The TopBar / page-hero stay unchanged. |
| Help-text variant per language | **Schema-driven, not hard-coded** | The schema's `description` + `promptHints.emphasis` already capture per-language nuance; the extraction-beat help text reads from there. |

---

## Sub-phases

- **9.2.a — Language picker on ingest.**
  - Add an explicit `<Select>` on the New Corpus page that lists every
    schema registered with the platform (read from
    `GET /api/v1/spec-schemas`).
  - Default value: derived from the first file's extension via
    `SourceLanguageDetector.FromFilename` semantics on the client.
  - The override travels through `IngestPipeline.IngestRequest.SchemaIdOverride`
    (new optional field) and wins over the per-file extension
    detection.

- **9.2.b — Per-language colour accent.**
  - New `useCorpusAccent(corpusId)` React Query hook that returns the
    Tailwind colour token for the corpus's source language. Cached
    alongside the corpus query so re-renders are free.
  - Sidebar lockup tints to the accent when the active route is a
    corpus / subroutine / spec / scaffold under that corpus.
  - Corpus cards on the Projects page carry a leading colour bar
    keyed by the language.

- **9.2.c — Language-aware help text.**
  - New `<ExtractionHelpText>` component reads the spec schema's
    `description` + `promptHints.emphasis` from the catalog and
    renders a one-paragraph blurb on the extraction beat
    (SubroutineDetailPage). Shorter for Delphi (cleaner mapping),
    longer + cautionary for C++ (more open questions expected).
  - Surface the schema's `calibratedAgainst` list as a small
    "calibrated against" pill so reviewers can see what the prompt
    knows.

---

## Hard gate (Δ-9.2)

- [ ] Language picker renders all 4 schemas on the New Corpus page.
- [ ] Picking a language explicitly overrides the extension-derived
      default; the ingest pipeline tags every routine in that corpus
      with the picked `source_language`.
- [ ] Sidebar lockup re-tints when navigating between corpora of
      different languages without a hard reload.
- [ ] Corpus cards on the Projects page show the per-language colour
      bar.
- [ ] ExtractionHelpText renders the schema's description + emphasis
      copy on the subroutine page; the copy is shorter for Delphi than
      for C++.
- [ ] All 4 existing e2e suites stay green.

## Soft gate

- [ ] One small e2e (`phase-9.2-ux-polish.spec.ts`) that asserts the
      picker, the accent, and the help text are all wired.

---

## Risk register (additions for Phase 9.2)

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R-9.2-1 | Manual schema override picks a language that doesn't match the file extensions, silently mis-routing the parse. | M | M | The ingest pipeline still uses per-file detection internally; the override only sets the corpus's default. Per-file warnings surface in the parse outcome. |
| R-9.2-2 | The sidebar re-tint flashes during navigation. | L | L | Use Tailwind's `transition-colors duration-fast` so the colour change is smooth. |
| R-9.2-3 | The language-aware help text crowds the existing extraction page. | M | L | Collapse into a `<details>` block by default; expand on first hover. |

---

## Out of scope

- **Per-language target-stack auto-selection.** v1 keeps the
  explicit target-stack chooser; smart defaults come later.
- **Migration Plan strategy variants per language.** The existing
  4 strategies (Phase 8.0.e) stay language-agnostic.
- **Per-language demo recording.** That ships in Phase 9.6.
