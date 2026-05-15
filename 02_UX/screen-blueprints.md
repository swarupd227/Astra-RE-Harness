# Screen Blueprints

**Status:** Kickoff (v0.1) — content-level blueprints; pixel specs in Figma (Phase A).
**Companion:** `ux-vision-and-principles.md`, `design-system.md`, `information-architecture.md`
**Source:** Spec §4 (verbatim) — extended with state inventory, keyboard shortcuts, and a11y notes per screen.

The spec already provides a thorough screen-by-screen specification. This document is a designer/engineer working sheet, layered on top of that text. For each screen we record:

- Owner persona(s) and primary task
- Layout outline (panes, regions)
- All interactive states (loading / empty / error / success / partial)
- Keyboard shortcuts
- Accessibility commitments
- Phase that ships the screen

For each screen, *the spec is canon*. This file fills the gaps the spec leaves to design.

---

## 1.1 Source Corpus list

**Owner persona.** Engineer (default landing). Observer (read-only). SME does not have access.
**Primary task.** Engineer scans corpora, opens one, or connects a new source.
**Layout.** Header strip + filter row + responsive card grid.
**States.** Loading (six skeleton cards), empty (illustration + "+ Connect your first source" CTA), error (full-page error block with retry), populated (card grid).
**Keyboard.**
- `/` focus search input
- `N` open Connect new source side sheet (Engineer)
- `Tab` cycles through cards
- `Enter` opens focused card
**A11y.**
- Cards are `role="link"` with descriptive `aria-label` ("Open corpus {name}, state: {state}")
- Filter chips are `role="group"` with `aria-pressed` per chip
- Loading skeletons have `aria-busy="true"` on the grid container
**Phase.** C.

---

## 1.2 Connect new source (side sheet)

**Owner persona.** Engineer.
**Primary task.** Connect a Git repo or upload Fortran files.
**Layout.** Right-anchored 600px side sheet. Tab bar (Git default, Upload). Form. Footer with Cancel / Connect.
**States.** Default (form clean), validating (inline errors per field), submitting (Connect button shows spinner), error (banner at top of sheet), success (sheet closes, toast "Source connected").
**Keyboard.**
- `Esc` closes (with unsaved-changes confirm if form is dirty)
- `Tab` cycles through fields
- `Cmd-Enter` submits when valid
**A11y.**
- Sheet traps focus; restores on close
- Tab bar is `role="tablist"` with `aria-controls` per panel
- Inline errors are `role="alert"`
**Phase.** C.

---

## 1.3 Source detail (corpus view)

**Owner persona.** Engineer (full), Observer (read), SME (read of files containing routed specs only).
**Primary task.** Browse files, view source, trigger re-sync.
**Layout.** Three-pane (300 / flex / 340).
**States.** Loading (tree + editor skeletons), corpus failed (banner across center pane), empty corpus (guidance message in center).
**Keyboard.**
- `[`, `]` collapse / expand left tree
- Arrow keys navigate file tree
- `R` re-sync (Engineer, Git-connected)
**A11y.**
- File tree is `role="tree"` with proper `aria-expanded` and `aria-level` per node
- Monaco editor reads as `aria-label="Source viewer for {file}, read-only"`
**Phase.** C. (Stage 1 stub view in Phase B is sufficient for the demo.)

---

## 2.1 Subroutine detail (the pivot)

**Owner persona.** Engineer (full), SME (read + comment + tab gating per state), Observer (read).
**Primary task.** Inspect a subroutine; trigger Stage 3 extract; navigate the pipeline tabs.
**Layout.** Top bar with breadcrumb + state badge + action button group. Tabs row (Source / Structure / Call graph / Spec / Scaffold) — disabled tabs greyed with tooltip explaining unlock condition. Tab content area.
**States.** Per tab:
- Source — Monaco read-only with subroutine highlighted, surrounding context dimmed
- Structure — table of inputs/outputs/COMMON refs/calls/IO patterns/magic numbers
- Call graph — ReactFlow with current routine as center node
- Spec — Screen 3.2 / 4.2 content, gated by state
- Scaffold — Screen 5.2 content, gated by state
**Keyboard.**
- `1`–`5` switch tabs
- `X` Extract spec (Engineer, when state = PARSED)
- `R` Route to SME (Engineer, when state = DRAFT)
- `S` Sign spec (SME, when state = IN_REVIEW; routes through re-auth)
- `G` Generate scaffold (Engineer, when state = SIGNED)
**A11y.**
- Tabs are `role="tablist"` with `aria-disabled` on locked tabs
- The action button group's primary CTA is the document's `autoFocus` target
**Phase.** B (Source tab + Spec tab + Scaffold tab); C (Structure + Call graph).

---

## 2.2 Subroutine list (within corpus)

**Owner persona.** Engineer (full + bulk), Observer (read), SME (read).
**Primary task.** Triage subroutines, trigger batch extractions.
**Layout.** Header + counts strip + filter row + DataTable with multi-select. Sticky bulk-action bar appears when ≥1 row selected.
**States.** Loading (table skeletons), empty (illustration + CTA to connect a corpus), populated.
**Keyboard.**
- `Shift-Click` range select
- `Cmd-A` select all (within current filter)
- `B` open bulk-action menu (when selection > 0)
**A11y.**
- DataTable is `role="grid"` with `aria-rowcount` reflecting filtered totals
- Bulk-action bar is `role="region"` with `aria-label="Bulk actions for selected subroutines"`
**Phase.** C.

---

## 3.1 Live extraction view

**Owner persona.** Engineer.
**Primary task.** Watch the LLM stream the spec; cancel if needed.
**Layout.** Modal-style overlay. Header with stage indicator + cancel. Provider context strip (low-emphasis monospace). Streaming response panel (~60%) on left. Source reference panel (~40%) on right with citation pulse. Footer with View draft spec / Re-extract / View raw response.
**States.** Pre-stream (priming, loading source — empty panels), streaming (tokens flowing), validating (panels static, validation indicator), persisting (validating done), success (footer enabled), failure (error block replaces footer).
**Keyboard.**
- `Esc` cancel (with confirm if past stage 2)
- `R` re-extract with revised prompt (after success)
- `V` view raw response (after failure)
**A11y.**
- Stream panel is `aria-live="polite"` but tokens are NOT individually announced — the live region is silenced for tokens; on stage transitions, the stage label is announced
- On `done`, screen-reader announces "Extraction complete. View draft spec, button"
- Source pane scroll on citation includes a hidden `aria-live="polite"` announcement: "Citing lines 34 to 37"
**Phase.** B. **Hero screen for the demo.**

---

## 3.2 Draft spec (engineer view, pre-review)

**Owner persona.** Engineer (full edit in DRAFT), SME (read while routed), Observer (read).
**Primary task.** Engineer reviews LLM draft, edits if needed, routes to SME.
**Layout.** Top bar with state badge + action buttons. Two-pane (~55 / ~45). Left: spec rendered from JSON, sectioned. Right: source code with two-way binding.
**States.**
- DRAFT (editable), IN_REVIEW (read-only for engineer), SIGNED (read-only with signature panel pinned)
- Per claim: untouched, edited (with diff toggle)
- Loading, error
**Keyboard.**
- `J/K` next/previous claim
- `E` enter edit on focused claim (Engineer, DRAFT)
- `R` route to SME (Engineer)
- `Cmd-Enter` save edit
**A11y.**
- Each claim card is `role="region"` with `aria-labelledby={claimId}`
- Citation chips are `role="link"` with descriptive labels
- "Open questions" section is announced as "Open questions, requires SME resolution before sign-off"
**Phase.** B.

---

## 4.1 My reviews (SME landing)

**Owner persona.** SME (default landing).
**Primary task.** SME triages their queue.
**Layout.** Header with counts. Three groups: awaiting review (top), in progress (middle), signed (collapsed at bottom). Each group is a card stack.
**States.** Loading (card skeletons), empty (friendly message + last-sign-off timestamp), populated.
**Keyboard.**
- `J/K` cycle through cards
- `Enter` opens focused review
**A11y.**
- Group headings are `role="region"` with `aria-label` and an item count
- Cards announce: "Review for {subroutine}, {claim breakdown}, estimated {time}, routed by {engineer}"
**Phase.** B.

---

## 4.2 Spec review (SME working surface — the keystone)

**Owner persona.** SME (full action), Engineer (comment-only), Observer (read).
**Primary task.** SME walks every claim, accept/edit/reject/question, resolves open questions, signs.
**Layout.** Top bar with breadcrumb + state badge + progress strip + Sign spec CTA. Routing-note callout strip (dismissible). Three-pane: outline (320px) / claim cards (flex) / source (380px sticky).
**States.** Loading, populated, every claim has its own card-state matrix (untouched / accepted / edited / rejected / pending-question).
**Keyboard (the most important keyboard surface in the product).**
- `J / K` next / previous claim — even from the outline pane
- `A` accept focused claim
- `E` edit (focus enters textarea)
- `R` reject (focus enters reason input — required ≥20 chars)
- `?` raise question (focus enters question input)
- `O` open outline pane jump menu
- `Cmd-Enter` commit edit / submit reason / submit question
- `Esc` cancel current edit
- `S` open Sign spec modal (only when preconditions met)
- `Shift-?` open keyboard help overlay
**A11y.**
- Outline pane is `role="navigation"` with `aria-current="true"` on the active claim
- Status dots have visible labels in the outline ("Accepted", "Edited", etc.) plus the color
- Progress strip is `role="status"` and `aria-live="polite"`
- Sign spec CTA's disabled tooltip explains which preconditions are unmet
**Phase.** B.

---

## 4.3 Audit trail

**Owner persona.** All.
**Primary task.** Inspect the chronological event log for a spec.
**Layout.** Header with subroutine name + date filter + Export. Vertical timeline with date dividers; event cards.
**States.** Loading (skeletons), empty (rare; usually means newly-created spec — show "no events yet"), populated.
**Keyboard.**
- `F` focus filter row
- `J/K` cycle through event cards
- `Cmd-E` export PDF
**A11y.**
- Timeline is `role="list"`; events are `role="listitem"`
- Diff blocks are `role="region"` with `aria-label="Before/after diff for {field}"`
**Phase.** B (basic timeline; full filtering and export in C).

---

## 5.1 Live scaffold generation

**Owner persona.** Engineer.
**Primary task.** Watch the LLM stream the .NET scaffold.
**Layout.** Same shape as 3.1 but content panes differ: streaming code panel (left) with file tabs as files emit; spec reference panel (right) with bidirectional binding.
**States.** Same matrix as 3.1.
**Keyboard.** Same as 3.1.
**A11y.** Same as 3.1; in addition, file tabs as they appear are announced via `aria-live`.
**Phase.** B.

---

## 5.2 Scaffold artifact view

**Owner persona.** Engineer (full + commit), Observer (read), SME (read).
**Primary task.** Review generated package; trigger commit-to-Git; download.
**Layout.** Top bar with state badge + action buttons. Three-pane (220 / flex / 340). Left: file tree of generated package. Center: Monaco read-only with C# syntax highlighting and TODO/throw markers. Right: traceability panel ("Derived from: ...").
**States.** SCAFFOLDED (commit available), COMMITTED (commit URL shown, "View commit" available), FAILED (error block with retry).
**Keyboard.**
- Arrow keys navigate file tree
- `C` open commit-to-Git modal (Engineer)
- `D` download .zip
- `R` re-generate with revised prompt (Engineer)
**A11y.**
- TODO markers in code have a hover popover that is also keyboard-reachable: each marker is a focusable affordance (`tabindex=0`) that announces "TODO marker, derived from invariant INV-1, lines 39 to 42"
**Phase.** B.

---

## Admin screens (Phase D for full polish; minimal in Phase A)

Admin pages render with the same shell but a slimmer left nav. Four screens:

- `/admin/providers` — list providers, model defaults, ZDR config version, audit-letter timestamp.
- `/admin/routing` — table of `(stage, prompt_template_id) → (provider, model, params, fallback)`. Inline-editable.
- `/admin/credentials` — list of admin-managed credentials. Add/rotate/revoke.
- `/admin/cost` — daily/weekly cost rollups, per-stage and per-engineer breakdowns, hard-cap status.

All admin actions are audited; the audit trail surfaces every admin change with full context.

---

## Cross-screen interaction patterns

- **Sign-off modal.** Used from 4.2. Two-step: (a) preconditions check + citation integrity status + canonical-sentence checkbox; (b) re-auth interstitial (if `auth_time` > 5 min); (c) signing in progress; (d) success state with signature block. The modal is *interruptible* up to step (b); step (c) onwards is committed.
- **Confirmation modals.** Generic shape: heading, body explaining the change and its consequences, "I understand" checkbox where action is destructive or irreversible, primary action button (often `accent.primary` for irrevocable, `semantic.danger` for destructive). All modals trap focus.
- **Side sheets.** Right-anchored, 600px. Form patterns. Cancel / Submit footer. Esc closes with confirmation if dirty.
- **Toasts.** Top-right. Stack of up to 3. Auto-dismiss 4s; survive page navigations.
- **Banners.** Sticky to top of page content (below top bar). Dismissible per session via `aria-pressed` close button.

---

## A11y audit plan (Phase D, week 12)

The audit covers:

- Keyboard-only walkthrough of every screen above
- Screen-reader pass with VoiceOver and NVDA on the same screens
- Color-contrast verification per token
- Reduced-motion mode walkthrough
- Focus trap verification on modals and side sheets

Findings are logged in `04_Delivery/risk-register.md` and remediated before Phase E.
