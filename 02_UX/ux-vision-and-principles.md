# UX Vision & Principles

**Status:** Kickoff (v0.1)
**Audience:** Product designer, frontend engineers, engineering lead
**Companion:** `design-system.md`, `information-architecture.md`, `screen-blueprints.md`

---

## 1. The product, in one sentence

A precision instrument that lets a small team turn legacy Fortran into signed, traceable, AI-drafted specifications — and emits scaffolded modern code from each signature.

The word that does the most work in that sentence is *instrument*. Internal tools that move billions of dollars of legacy logic should not feel like SaaS dashboards. They should feel like a workbench an engineer reaches for the same way they reach for a debugger.

---

## 2. The three principles

### 2.1 Trace

> "Every claim, every generated line, every audit event traces to its source. Nothing in this product floats."

The single highest-value interaction is *click a claim, watch the source highlight*. A spec without that affordance is a wall of text. A spec with it is a magnifier on the source.

**Concrete commitments**

- Every claim, input, output, side effect, edge case, and open question is rendered with an inline citation chip (`L34-37`). Clicking the chip pulses the source pane.
- The source pane is sticky — it stays in view while the SME scrolls the spec. Code never disappears.
- Scaffold view binds the same way: hover a `TODO` marker → see the spec invariant; click a method → scroll the spec to the derived element.
- Audit events show before/after diffs inline. No "view diff" detour.

### 2.2 Pace

> "SMEs spend hours per spec. The working surface is engineered for sustained attention, not for the first thirty seconds."

Most products optimize for the first session. This one optimizes for the fifth hour of the third session. The SME persona is the most demanding user in the system, and Screen 4.2 is where they live.

**Concrete commitments**

- Outline pane on the left of Screen 4.2 always shows progress at a glance — section status dots tell the SME how far they have to go without forcing them to scroll.
- Claim cards collapse to a compact accepted state once acted on, reducing cognitive load as work progresses.
- Keyboard-first: `J/K` next/previous claim, `A` accept, `E` edit, `R` reject, `?` raise question, `/` focus search, `Cmd-Enter` to commit an edit. Every keyboard shortcut is discoverable via `?` (help overlay).
- No hidden state. The progress strip ("X of Y claims processed") is visible at all times. The "Sign spec" CTA is always present but disabled until preconditions are met — and the disabled tooltip explains *exactly* what is missing.

### 2.3 Trust

> "The product asks humans to vouch for LLM output. The UI never overstates what the LLM knows."

Trust is built by being more honest than you have to be. The audience for this product includes auditors and Advantive observers — people who will inspect every claim about the system. The interface needs to *look* honest because the system *is* honest.

**Concrete commitments**

- Every LLM call surfaces provider, model, prompt template ID + version, token counts. Subtle, monospace, low-emphasis — but never absent.
- Confidence indicators are honest: the LLM emits low/medium/high; the UI displays it without massaging. The SME can override.
- Streaming is real, not faked. The typing-cursor lag is not a flourish — it shows the user that they are watching the actual provider response.
- Sign-off is ceremonial. A confirmation modal shows the spec summary, the citation integrity check (does every citation still resolve?), an explicit "I have reviewed every claim" checkbox, and the signed-name display. The button copy is *Sign spec* — not *Submit*, not *Confirm*, not *OK*.
- "Cached" indicators are accurate. If the demo falls back to a cached response on timeout (per Appendix A.4), the UI shows "loaded from cache" — never silent.

---

## 3. Aesthetic point of view

The aesthetic is **calm, precise, and a little austere**. The visual model is closer to a CAD tool, a debugger, or a financial-trading terminal than to a typical SaaS dashboard.

| Choice | Why |
|---|---|
| Off-white surface (`#F8F8F6`), navy ink (`#101728`), accent orange (`#E5732C`) on primary actions and irrevocable warnings only | A muted palette lets code (always color-rich) take the visual lead. |
| Inter for UI text, JetBrains Mono for code and numerical fields | Inter at 14/20 base reads densely; JetBrains Mono at 13/20 sits beside it without size mismatch. |
| Borders rather than shadows for component separation | Shadows imply float; borders imply structure. This product is structural. |
| State badges always combine icon + text + color | Color is never the only signal. WCAG AA mandate. |
| Motion is informational, never decorative | Citation pulse signals an event. Skeleton fades reserve space. Hover transitions are 100ms. Reduced-motion users get instant transitions, no degraded experience. |
| Icons are line-style at 16px and 20px, monoline weight | Icons should not compete with code. |

A two-page mood-board PDF will accompany this document by week 1. Until then, this written description is the canon.

---

## 4. Persona-driven design choices

### Engineer

The Engineer is the operator. They are *fast*. Their landing screen (Source corpus list) prioritizes throughput: filter, search, scan. Their pivot screen is Subroutine detail (2.1) — every action they take starts there.

**Engineer-specific commitments**

- Bulk operations on the Subroutine list (extract many at once, export selected, route to SME).
- Visible cost and provider context on every LLM call. Engineers are accountable for spend; they should always be able to see it.
- Re-extract and re-generate with revised prompt — quick iteration loops surfaced in the artifact views.

### SME

The SME is the reviewer. They are *deliberate*. Their landing screen (My reviews) is workload-aware: how many reviews, how long each takes. Their working surface (Spec review, 4.2) optimizes for endurance.

**SME-specific commitments**

- Estimated review time on each card based on claim count + complexity heuristic.
- Routing note from the engineer surfaced at the top of the review surface — context before content.
- Sign-off is a ritual. The interaction friction of the confirmation modal is *deliberate*. We are not optimizing for clicks-to-sign.

### Observer

The Observer is the watcher. They are *episodic*. They drop in for weekly reviews, audits, deep-dives. They see everything and edit nothing.

**Observer-specific commitments**

- All write affordances disappear from the UI for the Observer persona (not greyed — gone). They never see a button they cannot use.
- Export is the most-used affordance for Observers. PDF and JSON exports are one-click from any artifact.
- Audit trail is the home page. We do not assume Observers want to start with corpora.

### Admin

Admin is a separate persona, gated by a separate set of pages. Admin operates with full transparency — every action is audited, including the act of viewing the audit trail. Admin is *not* "engineer with extra buttons"; it is a deliberate, narrow surface for provider configuration, credential management, and cost monitoring.

---

## 5. The five UX bets that distinguish this product

These are the bets that, if we get them right, make the product feel "world class." Each is named, scoped, and assigned a phase.

### Bet 1 · Citation pulse (Phase B)

The hero interaction. Real-time during streaming, navigable in static views. The pulse is 1.5s ease-out, the persistent highlight is a 4px accent border on the cited line range, the source pane scrolls to the line range with a 200ms ease-in-out scroll. We will tune this for the demo and protect it from feature creep.

### Bet 2 · The streaming-extraction surface (Phase B)

Screen 3.1 is the demo's centerpiece. It must feel like watching a senior engineer think, not a chatbot type. The provider context strip (subtle, monospace) makes the audience trust what they're seeing. The two-pane binding (response on the left, source on the right, citation pulses connecting them) is the visual proof that the product is grounded in real source.

### Bet 3 · The claim-card interaction model (Phase B)

Each claim is a small card with one-click `Accept`, an inline-edit affordance, a one-click `Reject` (gated by a reason field), and a `Question` escalation. The state machine of a claim is visible (untouched / accepted / edited / rejected / pending question). Bulk-accept is *not* offered — every claim is individual; that is the spec's principle.

### Bet 4 · The sign-off ritual (Phase B)

Sign-off has friction by design. The confirmation modal shows a spec summary, the citation integrity check, an explicit checkbox with the canonical sentence ("I have reviewed every claim and confirm this spec is accurate to the source as of version {version}"), and the SME's signed name. After signing, the spec view enters SIGNED display mode — read-only, with the signature block visible at the top and the audit trail link prominent.

### Bet 5 · The scaffold artifact view (Phase B)

Screen 5.2 is where the engineer sees the LLM-drafted code package alongside its provenance. The traceability panel on the right ("Derived from: INV-1, INV-3, IN.ROLL_ID") is what makes this view valuable. Without it, the panel is a code editor; with it, it is a contract viewer.

---

## 6. What we are *not* designing for

- **Mobile or sub-1280px viewports.** Spec §9.4 is explicit. The app shows a blocking message below 1280px wide. We do not pretend.
- **First-time-user delight.** This is a tool for trained operators. The onboarding is a written guide and a 30-minute training session, not an in-app tour.
- **Chat-first interfaces.** The product's value is not "chat with your codebase." It is "operate a controlled pipeline." Chat would undermine the trust principle.
- **Customer-facing polish.** The audience for the UI is internal: Nous engineers, Kiwiplan SMEs, Advantive observers. Polish belongs everywhere, but we are not designing as if a paying customer will see this UI tomorrow.

---

## 7. Process notes

- **Design lead** drives the screen blueprints in `screen-blueprints.md`. Every screen has loading, empty, and error states designed before "happy path" is signed off.
- **Frontend lead + design lead** co-own the design-system tokens. Tokens land in week 1 and freeze. Component primitives ship in week 1–2.
- **Demo dress rehearsal** in week 5 is the first pixel-perfect review. Anything that does not look final by then is a risk.
- **Accessibility audit** in Phase D (week 12). We do not "add accessibility" then; we build to AA from week 1 and audit then.
- **Copy is design.** Every error message, every empty-state line, every button label is committed to `frontend/src/copy/` and reviewed alongside design.

---

## 8. The smell test

Before any screen ships, ask:

1. **Does it trace?** Can the user click a claim, an event, or a generated line and see its source?
2. **Does it pace?** Could an SME work through this for four hours without losing flow?
3. **Does it tell the truth?** Are confidence, provenance, cost, and risk surfaced — not buried?

Any "no" is a blocker. Three yeses is the bar.
