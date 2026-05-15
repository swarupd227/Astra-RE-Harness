# Design System

**Status:** Kickoff (v0.1) — token set + primitive list. Pixel specs in Figma (Phase A).
**Audience:** Frontend engineers, designer.
**Companion:** `ux-vision-and-principles.md`, `screen-blueprints.md`

The token set below is the contract between design and engineering. Tokens are committed in `frontend/src/tokens/` and consumed by Tailwind (via the Tailwind config) and by component-level CSS variables. Components do not reference raw values — only tokens.

---

## 1. Color tokens

### 1.1 Surface

| Token | Hex | Usage |
|---|---|---|
| `surface.canvas` | `#F8F8F6` | Page background |
| `surface.raised` | `#FFFFFF` | Cards, side sheets, modals |
| `surface.sunken` | `#F1F1ED` | Code editor surrounds, inert blocks |
| `surface.code` | `#0F1116` | Monaco editor background (light theme uses `#FFFFFF`; we ship dark for code) |

### 1.2 Ink

| Token | Hex | Usage |
|---|---|---|
| `ink.primary` | `#101728` | Body text, headings |
| `ink.secondary` | `#475063` | Sub-text, metadata, captions |
| `ink.tertiary` | `#7A8497` | Disabled labels, low-emphasis |
| `ink.inverse` | `#F8F8F6` | Text on dark surfaces (Monaco, code blocks) |
| `ink.link` | `#1F4FA8` | Inline links |

### 1.3 Accent (use sparingly)

| Token | Hex | Usage |
|---|---|---|
| `accent.primary` | `#E5732C` | Primary CTA fill, irrevocable-action emphasis |
| `accent.primary.hover` | `#CD5F1E` | Hover/active state for primary CTAs |
| `accent.primary.muted` | `#FBE7D6` | Tinted backgrounds (e.g., highlighted citation range) |

### 1.4 Status

| Token | Hex | Usage |
|---|---|---|
| `status.draft` | `#B9520B` | DRAFT badges (orange) |
| `status.review` | `#0E7C66` | IN_REVIEW badges (green) |
| `status.signed` | `#1F4FA8` | SIGNED badges (navy blue) |
| `status.scaffolded` | `#9A6E1A` | SCAFFOLDED badges (amber) |
| `status.failed` | `#A8201A` | FAILED, error badges (red) |
| `status.superseded` | `#7A8497` | SUPERSEDED (grey) |
| `status.untouched` | `#C9CFD8` | Outline-pane untouched dot |

Each status token has matching `.bg` (`-15`-tinted) and `.fg` variants for badge composition.

### 1.5 Semantic

| Token | Hex | Usage |
|---|---|---|
| `semantic.success` | `#0E7C66` | Success toasts, accepted-claim affordance |
| `semantic.warning` | `#B9520B` | Warning chips, at-risk states |
| `semantic.danger` | `#A8201A` | Destructive action emphasis, validation errors |
| `semantic.info` | `#1F4FA8` | Info banners, tips |

### 1.6 Citation highlight

| Token | Hex | Usage |
|---|---|---|
| `citation.range` | `#E5732C` | 4px accent left-border on cited lines |
| `citation.range.bg` | `#FBE7D6` | Tinted background on cited lines (persistent) |
| `citation.pulse` | `#E5732C` (alpha 0.4 → 0) | 1.5s ease-out flash overlay |

---

## 2. Typography

| Token | Family | Weight | Size / line | Usage |
|---|---|---|---|---|
| `type.display` | Inter | 600 | 28 / 36 | Page titles |
| `type.heading.lg` | Inter | 600 | 22 / 30 | Section headings |
| `type.heading.md` | Inter | 600 | 18 / 26 | Sub-section headings, claim card titles |
| `type.heading.sm` | Inter | 600 | 14 / 20 | Compact card titles, table headers |
| `type.body` | Inter | 400 | 14 / 20 | Default body |
| `type.body.lg` | Inter | 400 | 16 / 24 | Long-form prose (audit narratives) |
| `type.caption` | Inter | 400 | 12 / 16 | Metadata, timestamps |
| `type.mono` | JetBrains Mono | 400 | 13 / 20 | Code, IDs, hashes, token counts |
| `type.mono.sm` | JetBrains Mono | 400 | 11 / 16 | Provider context strip |

Numerals are tabular by default (Inter has tabular-num feature; JetBrains Mono is fixed). Letter-spacing follows Inter's defaults; small caps are not used.

---

## 3. Spacing & layout

`space-{n}` scale, based on a 4px grid:

```
space-0 = 0
space-1 = 4
space-2 = 8
space-3 = 12
space-4 = 16
space-5 = 20
space-6 = 24
space-8 = 32
space-10 = 40
space-12 = 48
space-16 = 64
space-20 = 80
```

**Page-level layout**

| Element | Spec |
|---|---|
| Top bar height | 56px |
| Left nav width (collapsed) | 64px |
| Left nav width (expanded) | 240px |
| Side sheet width (default) | 600px |
| Modal max-width | 720px |
| Card padding | `space-6` (24px) |
| Section vertical rhythm | `space-12` (48px) |
| Minimum viewport | 1280 × 800 |

---

## 4. Radius

| Token | Value |
|---|---|
| `radius.sm` | 4px (badges, chips) |
| `radius.md` | 6px (buttons, inputs, cards) |
| `radius.lg` | 8px (modals, side sheets) |
| `radius.full` | 9999px (avatars, dot indicators) |

---

## 5. Borders & elevation

| Token | Spec |
|---|---|
| `border.subtle` | 1px solid `#E4E6EB` |
| `border.default` | 1px solid `#CFD3DA` |
| `border.strong` | 2px solid `#101728` (focus, active card) |
| `divider` | 1px solid `#E4E6EB` |
| `elevation.0` | none |
| `elevation.1` | `0 1px 2px rgba(16,23,40,0.04), 0 1px 3px rgba(16,23,40,0.06)` (default cards) |
| `elevation.2` | `0 4px 12px rgba(16,23,40,0.08)` (hover, side sheet) |
| `elevation.3` | `0 12px 32px rgba(16,23,40,0.12)` (modal) |

Borders are preferred over elevation for component separation (per the principle "structural, not floating"). Elevation is reserved for transient surfaces (modals, side sheets, dropdowns).

---

## 6. Motion

| Token | Spec |
|---|---|
| `motion.fast` | 100ms ease-out (hover, focus) |
| `motion.medium` | 200ms ease-in-out (panel transitions, scroll-into-view) |
| `motion.slow` | 320ms ease-in-out (side sheet, modal open) |
| `motion.pulse` | 1500ms ease-out (citation pulse) |
| `motion.skeleton` | 1200ms linear (skeleton shimmer) |

**Reduced motion.** When `prefers-reduced-motion: reduce`, transitions collapse to 0ms and pulse becomes a 200ms fade-in to persistent highlight. No information is lost; only the kinetic affordance.

---

## 7. Component primitives

The list below is the inventory of components shipped in Phase A (week 1–2). Every primitive has a Storybook entry with: variants, states (default, hover, focus, disabled, loading, error), accessibility notes, and props table.

### Atoms

- `Button` — variants: `primary`, `secondary`, `ghost`, `destructive`. Sizes: `md` (default), `sm`, `lg`. Loading state. Icon-leading and icon-trailing slots.
- `IconButton` — for kebab menus, close buttons. 32px square hit target minimum.
- `Badge` — variants for each status token. Composition: icon + text + color.
- `Chip` — citation chips, tag chips, filter chips. `selected` state for filter groups.
- `Tag` — neutral metadata tag (e.g., file type, prompt template ID).
- `Avatar` — initials or image; `xs`, `sm`, `md`.
- `Tooltip` — controlled, with `delay` prop. Default delay 400ms.
- `Spinner` — for inline loading inside buttons. Avoid for page-level loading (use Skeleton).
- `Divider` — horizontal and vertical.
- `Kbd` — keyboard key indicator (`<Kbd>J</Kbd>`).

### Form

- `Input` — text, with leading/trailing slots. Inline error state.
- `Textarea` — autosizing.
- `Select` — single-select with searchable variant for long lists.
- `Combobox` — multi-select, used for SME assignment.
- `Checkbox` — including the "I understand" pattern.
- `Radio`, `RadioGroup`.
- `Switch` — for binary settings.
- `FieldError`, `FieldHint`, `FieldLabel` — composable form-field affordances.
- `FormField` — wrapper that composes label + control + hint + error consistently.

### Layout

- `Stack`, `HStack` — flex composition primitives.
- `Card` — variants: `default`, `interactive` (clickable, with hover state), `compact` (collapsed claim card).
- `Section` — semantic section with title, description, action slot.
- `EmptyState` — illustration + heading + description + CTA. One illustration set across the product (vector, two-color).
- `ErrorBlock` — inline error with icon, message, retry CTA.
- `Skeleton` — content-shaped loading placeholder.
- `Toast`, `Toaster` — success and info toasts. Errors are inline, not toasts.

### Navigation

- `TopBar` — env badge, breadcrumb, persona menu.
- `LeftNav` — primary nav with persona-keyed item visibility.
- `Breadcrumb` — controlled, with overflow truncation on long paths.
- `Tabs` — used on Subroutine detail (2.1). Disabled tabs show tooltip explaining unlock condition.
- `Pagination` — cursor-based; "Load more" pattern preferred over numbered pages.

### Disclosure

- `Modal` — for irrevocable confirmations only. Includes "I understand" checkbox primitive.
- `SideSheet` — right-anchored 600px panel for forms (Connect new source, Route to SME).
- `Popover` — for citation hover excerpt and metadata peeks.
- `Drawer` — left-anchored, used for mobile (out of scope v1) — placeholder only.

### Data

- `DataTable` — sortable, filterable, with sticky header, multi-select, sticky bulk-action bar at bottom when rows selected.
- `KeyValueList` — metadata panel rendering (file path, hash, ingested timestamp).
- `Timeline` — used on Audit trail (4.3). Vertical, date dividers, event cards.
- `ProgressStrip` — "X of Y claims processed" header strip.
- `OutlineNav` — left pane on Spec review (4.2). Section list with status dots per claim.

### Code

- `MonacoEditor` — wrapped Monaco with our Fortran language definition, citation decorations, and custom theme. Two themes: `light` (for spec view contrasts) and `dark` (default for code surface).
- `InlineCode` — JetBrains Mono inline.
- `CodeBlock` — for spec rendering and snippets, with copy-to-clipboard.

### Composite (Phase B)

- `ClaimCard` — the keystone component. Header (claim ID + title + action chips), body (claim text + citation chip), footer (confidence + comment count + action buttons). Variants: `untouched`, `accepted`, `edited`, `rejected`, `pending-question`.
- `LiveStreamPanel` — used in Screens 3.1 and 5.1. Streaming text on the left, source binding on the right, stage indicator + provider strip + cancel.
- `SignaturePanel` — read-only display of a signed signature (signer, signed-at, key ID, hash, verify-link).
- `ProviderStrip` — small monospace strip showing provider, model, prompt template ID + version, token budget.

---

## 8. Iconography

- Source: a single icon set (Lucide). 16px and 20px sizes; monoline weight.
- Custom icons (Fortran file types, Kiwiplan-specific concepts): commissioned in week 2, drawn to the same monoline grammar.
- Status badge icons: `Edit3` (DRAFT), `Search` (REVIEW), `BadgeCheck` (SIGNED), `Cog` (SCAFFOLDED), `AlertCircle` (FAILED), `Archive` (SUPERSEDED).

---

## 9. Accessibility commitments

- **Color contrast.** Body text on canvas: 14.6:1. Body text on sunken: 13.2:1. Inverse on code: 16.1:1. Status badges: ≥4.5:1 against their background. Audited per token in Storybook.
- **Focus.** `border.strong` (2px navy) on focus-visible. Never removed; outlines respected on every interactive element.
- **Keyboard.** Every interactive element reachable via Tab. Modals trap focus and restore on close. Side sheets do the same. Roving tabindex on the outline pane.
- **ARIA.** `role="status"` on toasts. `role="alert"` on inline errors. `aria-live="polite"` on the streaming panel during extraction. Outline pane is `role="navigation"` with `aria-current="true"` on the active claim.
- **Screen reader.** Status badges have a hidden text label ("Draft state, awaiting review"). Citation chips announce as "Citation: lines 34 to 37, link." Stream tokens are *not* announced (would be noise); the `done` event triggers a single announcement: "Extraction complete. View draft spec."
- **Reduced motion.** All `motion.*` tokens collapse per §6.

---

## 10. Iconographic & illustration set

A two-color vector illustration set is commissioned in week 2 for empty states. Style: editorial, line-and-fill, navy + accent orange. Six illustrations: *No corpora yet*, *No reviews assigned*, *No specs in this state*, *Cannot reach repository*, *Browser too small*, *Permission denied*.

Illustrations are SVG, no raster assets. Filed under `frontend/src/illustrations/`.

---

## 11. Sample component spec — `ClaimCard`

For reference shape; full specs live in Storybook.

```
ClaimCard
─────────
Slots:
  header.claimId          chip with claim id (e.g., "INV-1")
  header.title            string, single line, truncates with tooltip
  header.actionChips      array of chips (status, confidence, comment count)
  body.text               markdown-rendered claim body
  body.citation           CitationChip; click → pulse on source pane
  footer.actions          ActionBar: Accept / Edit / Reject / Question

States:
  untouched               default
  accepted                collapsed; shows title + accepted-by stamp
  edited                  expanded; shows current text + diff toggle
  rejected                struck-through text + rejection reason
  pending-question        outlined in purple; shows comment thread

Keyboard:
  When focused:
    A  → accept
    E  → edit (focus enters body editor)
    R  → reject (focus enters reason input)
    ?  → raise question (focus enters question input)
    J  → next claim
    K  → previous claim

A11y:
  role="region" with aria-labelledby={header.claimId}
  Action buttons announced as "Accept claim INV-1, button"
```
