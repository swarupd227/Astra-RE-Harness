# Information Architecture

**Status:** Kickoff (v0.1)
**Companion:** `ux-vision-and-principles.md`, `screen-blueprints.md`

This document covers navigation, persona-keyed entry points, URL structure, and search/filter patterns.

---

## 1. Persona-keyed entry points

The landing screen on login depends on the persona:

| Persona | Default landing | Why |
|---|---|---|
| Engineer | `/corpora` (Source corpus list, 1.1) | Engineers operate the pipeline end-to-end. Their first action is almost always "what corpus am I working on today?" |
| SME | `/my-reviews` (My reviews, 4.1) | SMEs are deliberate; they want to see their queue, not the entire codebase. |
| Observer | `/audit` (Audit overview) | Observers are episodic; they want to see what happened, not what is in flight. |
| Admin | `/admin/providers` | Admin is narrow; provider configuration is the most-used surface. |

The user can override their default landing by pinning a screen, persisted per-user in localStorage.

---

## 2. Primary navigation

**Left nav** (collapsible, 240px expanded / 64px collapsed):

```
[ Logo ]
─────────
 Corpora               Engineer, Observer
 My reviews            SME (default)
 All reviews           Engineer, Observer
 Subroutines           Engineer, Observer (cross-corpus search)
 Audit                 Engineer, SME, Observer (read-only)
 Comments              All (mentions inbox)
─────────
 Admin
   Providers           Admin only
   Routing             Admin only
   Credentials         Admin only
   Cost                Admin only
─────────
 [ Help ]
 [ Persona menu ]
```

Nav items are persona-keyed: an item is *not rendered* if the persona cannot use it (rather than greyed). This is per the design principle "users never see a button they cannot use" (`ux-vision-and-principles.md` §4).

**Top bar** (56px tall, sticky):

```
[ Breadcrumb ........................ ]      [Env DEV] [Persona] [Identity menu]
```

The breadcrumb is the single most important navigation element. It always shows the full hierarchy: `Corpora › {corpus} › {file} › {subroutine}` and supports clicking any segment.

---

## 3. URL structure

Stable, shareable URLs. Filter and tab state is in the query string for bookmarking.

| Path | Screen |
|---|---|
| `/corpora` | Source corpus list (1.1) |
| `/corpora/{corpusId}` | Source detail (1.3) — `?file=<path>` selects a file |
| `/corpora/{corpusId}/subroutines` | Subroutine list (2.2) |
| `/subroutines/{subId}` | Subroutine detail (2.1) — `?tab=source\|structure\|callgraph\|spec\|scaffold` |
| `/subroutines/{subId}/extract/{extractionId}` | Live extraction overlay (3.1) |
| `/subroutines/{subId}/spec` | Draft spec / SME review (3.2 / 4.2) — same URL, different rendering by state |
| `/subroutines/{subId}/scaffold/{scaffoldId}` | Scaffold artifact view (5.2) — `?file=<path>` |
| `/specs/{specId}/audit` | Audit trail (4.3) |
| `/my-reviews` | My reviews (4.1) |
| `/all-reviews` | All reviews (engineer/observer view) |
| `/subroutines` | Cross-corpus subroutine search |
| `/audit` | Audit overview (across all specs visible to user) |
| `/admin/providers` | Admin: provider configuration |
| `/admin/routing` | Admin: prompt routing table |
| `/admin/credentials` | Admin: credential management |
| `/admin/cost` | Admin: cost dashboard |

Side sheets do not get their own URL — they are modal-context, opened from a parent screen. Modals likewise.

The URL is the source of truth for filter state. `/corpora?state=parsed,signed&q=roll&type=git` is shareable.

---

## 4. Search

Three tiers:

### 4.1 Per-screen search

Inputs at the top of list views. Debounced (300ms). Substring match on the obvious column (corpus name, subroutine name, etc.). State-bound to URL `?q=`.

### 4.2 Cross-corpus subroutine search

Dedicated screen at `/subroutines`. Indexed search across all subroutines visible to the user. Filters: state, language dialect, has-COMMON, has-ISAM, LOC range, file type. Results render in the same DataTable shape as Screen 2.2.

### 4.3 Global command bar (Phase C)

`Cmd-K` opens a global command bar (kbar pattern). Three categories: *Navigate to* (corpus, subroutine, spec, scaffold), *Action* (route to SME, extract, scaffold, sign), *Setting* (theme, persona switch for users who hold multiple personas).

The global command bar is a Phase C deliverable (week 8); not in the demo slice.

---

## 5. Filter & sort patterns

- **Filter chips at the top of list views.** Multi-select within a chip group. State persists to URL.
- **Sort.** All DataTable columns are sortable. Sort state persists to URL `?sort=<col>:asc|desc`.
- **Reset.** Every filter row has a "Clear filters" affordance when one or more filters are active.
- **Empty filtered state.** Distinct from generic empty state. Message: "No subroutines match these filters." with a "Clear filters" CTA.

---

## 6. Notifications

Three channels, in order of priority:

1. **In-app toast.** Success states only. 4s auto-dismiss, dismissible immediately. Stacked top-right.
2. **In-app banner.** Persistent on a screen (e.g., "Source corpus is being re-synced — viewing previous version"). Dismissible per session.
3. **Email.** For routing-to-SME, sign-off-completed, hard-cap-hit, credential-rotation-needed. Templates in `frontend/src/copy/email-templates/`.

@-mentions in comments dispatch in-app + email notifications. The user can disable email per channel in their profile.

---

## 7. State badges, in one place

For consistency, every screen uses the same badge for the same state:

| State | Badge |
|---|---|
| INGESTING / PARSING / EXTRACTING / SCAFFOLDING | Animated dot, neutral |
| INGESTED | "Ingested", neutral grey |
| PARSED | "Parsed", neutral grey |
| DRAFT | "Draft", `status.draft` (orange) |
| IN_REVIEW | "In review", `status.review` (green) |
| SIGNED | "Signed", `status.signed` (navy) |
| SCAFFOLDED | "Scaffolded", `status.scaffolded` (amber) |
| FAILED | "Failed", `status.failed` (red) |
| SUPERSEDED | "Superseded", `status.superseded` (grey, struck-through) |

Badge composition is always icon + text + color. Hover reveals the timestamp of last transition.

---

## 8. Confirmation patterns

| Action | Pattern |
|---|---|
| Sign spec | Modal with citation-integrity check + "I have reviewed every claim" checkbox + signed-name display + re-auth gate |
| Re-sync corpus | Modal: "This will create a new source version. Existing signed specs against the previous version will be marked SUPERSEDED. Continue?" |
| Archive corpus | Modal with "I understand" checkbox |
| Reject claim | Inline reason field (≥20 chars) replaces the action buttons |
| Cancel extraction | Inline confirmation chip ("Cancel extraction? [Yes] [No]") |
| Hard-cap admin override | Modal with "I understand this is audited" checkbox + reason field |

Toast confirmations *after* the action complete, not before.

---

## 9. Empty states

Every list and panel has a designed empty state. The illustration set commissioned in week 2 (`design-system.md` §10) covers the six canonical empty states. Each empty state has a primary CTA where one is appropriate (e.g., *No corpora yet → "+ Connect your first source"*).

---

## 10. Error states

- **Inline.** For form errors, validation, partial-load failures within a screen.
- **Block.** For full-screen failures (list fetch fails). Uses `ErrorBlock` primitive with retry CTA.
- **Modal.** Only when an in-flight critical operation fails (e.g., sign-off operation rejected by HSM). Modal includes the structured error model and a "Copy error details" affordance for support.

Generic 500 errors render the *Permission denied* illustration plus a "Something went wrong" heading and a retry CTA. The trace ID is shown in monospace for support tickets.

---

## 11. Print

Two screens are designed for print:

- **Audit trail (4.3)** — PDF export uses the print stylesheet. Header with spec name, source version, signature block. Page numbers in the footer.
- **Spec view (3.2 / 4.2)** — PDF export of the spec content for offline reference. Includes citations as page-internal links.

No other screen is print-optimized.

---

## 12. Internationalization stance (v1)

English-only UI. Locale-formatted timestamps and dates per browser. Dates in audit and metadata always display in the user's local time zone, with the UTC value visible on hover. Source code rendering preserves any non-ASCII characters in Fortran source verbatim.

i18n infrastructure (string extraction, message catalog) is *not* introduced in v1. Strings are inline. Adding i18n in v2 is a deliberate refactor, not a slow boil.
