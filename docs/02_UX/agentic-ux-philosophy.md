# Astra — the agentic UX philosophy

*How the product is meant to feel, why, and the rules that keep every screen honest to it.
This is the reference; `CLAUDE.md` at the repo root carries the short version every session loads.*

## The one-sentence model

**You talk to a team of agents, and they talk back.** Every programme is a conversation; every action is an
intent expressed in natural language (typed, or a chip phrased as one); every run narrates itself in plain
language; every result arrives as an agent message with a rich card attached. Pages are not the navigation
model — they are *artifact views* the agents open for you.

The product before this model was a workflow app: steppers, "Extract → Review → Sign → Generate" buttons,
admin tables, JSON-shaped claim cards. It was correct and it was not demoable. Nothing about the underlying
pipelines changed; what changed is that the *conversation is the primary surface* and the pipelines became
the hands of named agents.

## The named agents

| Agent | Speaks for | Code |
|---|---|---|
| **Astra** (orchestrator) | routes intents, keeps the thread honest | `Copilot/CopilotOrchestrator.cs` |
| Discovery | parse, survey, cluster patterns | `Llm/PatternAnalysis/*`, `IngestPipeline` |
| Spec | draft and review behavioural specs | `Llm/ExtractionPipeline.cs`, `Specs/SpecReviewService.cs` |
| Migration | generate target code from signed specs | `Llm/ScaffoldPipeline.cs` |
| Validation | compile, test-pack, equivalence, falsifying | `Validation/*` |
| Planning | dependency graph → waves | `Llm/Dependency/*` |
| Architecture | assessment, blueprint, consolidation | `Assessment/AssessmentService.cs` (blueprint: WS3) |
| Data | schemas, CRUD matrix, target model | WS4 |
| Release | commit, cutover, sign-offs | commit endpoints (cutover: later) |
| Programme | cross-programme status, cost, telemetry | `/api/v1/copilot/overview`, `/agents` |

Humans keep the gates: **Spec sign-off**, **Archetype approval**, **Commit gate**, and (WS3) **Blueprint
sign-off** and **Cutover sign-off**. Agents never cross a gate on their own.

## Ten rules (apply to every screen, every card, every message)

1. **Natural language everywhere.** The primary way to do anything is to say it. Buttons that remain
   inside artifact views are the *same* actions, never different ones, and their labels are stable.
2. **Every fact comes from a tool.** The orchestrator never asserts a status, count, claim or run result it
   did not read from a tool in the same turn. Tool calls stay on the message as **Sources** chips; tool
   cards become the message's **artifacts**. If a tool fails, the reply says so and proposes the next step.
3. **State-changing actions pause for confirmation.** A mutating tool (survey, extract, route, review,
   sign, generate, gate, docs, plan, assessment) never runs on a first pass. The turn stops on a
   **Confirm / Not now** card that names the tool and its input. Persona rules are enforced server-side
   (Engineer: extract/route/generate/gates · SME: review/sign · Admin: survey/docs/plan/assessment ·
   Observer: read-only); the UI only *hides* what a persona cannot do.
4. **Agents narrate.** Long runs post into the thread themselves (the `Narrator` follows the
   `RunEventBus`): stage boundaries, a halfway line with a real figure, and a final summary with the right
   card and 2–4 **suggestion chips phrased as the next thing the user would type**. Nobody polls a page.
5. **Results are explained, then shown.** A cluster grid, a gate report, a wave plan, a CRUD matrix — each
   is introduced in one or two plain sentences with the numbers, then rendered as a card. Cards open in the
   artifact pane; "Open full view" leads to the page.
6. **Plain language in the content, not just the chrome.** A claim is "This routine frees `Conn` twice if
   `Open` throws — lines 120–134", with Accept / Reject-because / Edit-to / Why? inline. The structured
   claim stays underneath as the record.
7. **Honesty over polish.** Failures are reported with the provider's reason. Estimates show their
   drivers. Anything not measured says "not measured". A narration must be reproducible from the run row.
8. **One accent.** Dark-first Artizent palette; `#FFDD00` (volt) is the *only* accent and it means exactly
   three things: *an agent is working*, *the primary action*, *focus*. Status uses its own four colours.
   Persona colours are fixed (Engineer volt, SME sand-200, Observer sand-400, Admin status-warn).
9. **Motion tells the truth.** Motion signals live work (the volt pulsing ring, the run card ticking) and
   arrival (a message entering) — nothing decorative. `prefers-reduced-motion` collapses it all.
10. **Nothing breaks what exists.** Routes stay, `data-testid`s stay, demo-spec button labels stay
    verbatim inside artifact views. New surfaces add test ids; they do not rename old ones.

## The surfaces (three, not thirty)

- **Workspace** (`/`, `/w/:conversationId`): left rail — *Ask Astra*, programme threads, the agents (live),
  then the old views; centre — the thread (`ProgrammeThread`: agent/user bubbles, working row, cards,
  Sources chips, suggestion chips, confirmation card, composer); right — the **artifact pane** showing the
  card the last message opened. **Mission Control** is the global thread's first screen (cross-programme
  funnel, per-programme rows, telemetry).
- **Artifact views** = the pages, restyled to the tokens and reached from cards. A page may embed a thread
  (`ThreadPanel`) — Spec review does — but it never replaces the conversation.
- **⌘K** is natural language first: type anything → routed to Astra in the current context; programmes,
  navigation and theme are the fallbacks.

## The card registry (`src/workspace/artifacts/`)

`funnel · runProgress · routineList · clusterGrid · specSummary · programmeList · routine · gateResults ·
scaffoldTree · planWaves · assessment · docSection · text`. A card renders from `{kind, refId, props}` in the
message's `artifacts`; the same component powers the card size, the pane size, and (where one exists) the
page. Props contracts live in `ArtifactBuilders.cs`; the frontend must be defensive (`props.items ?? []`).

## Design system v2 (tokens are the only vocabulary)

`src/theme/palette.json` is the single source → generated RGB-triplet CSS variables (`theme.css`) → Tailwind
names. Surfaces `canvas / raised / sunken / codebg`; lines `line-subtle / line / line-strong`; ink
`ink-primary / secondary / tertiary`; accent `volt` (+ `volt-ink` on light, `on-volt` for text on volt);
`sand-100…500`; `status-ok / warn / fail / info`; `persona-*`; `wave-1…5`. Fonts self-hosted (Inter Variable,
JetBrains Mono). Never `bg-white`, `slate-*`, `indigo-*`, hex literals in components. Charts, cytoscape,
Mermaid and Monaco take their colours from `themeHex()` / `chartTheme()` and follow the theme toggle.

## How to extend it (the recipe)

- **New capability → a tool, not a page.** Add a `CopilotTool` to `CopilotToolRegistry` that calls the
  existing service directly, declares `AllowedPersonas` and `Mutating`, gives a `Describe` sentence for the
  confirmation card, and returns a compact `ModelPayload` plus an artifact. Then, if the result deserves
  it, a card kind; then, if the card deserves it, a page.
- **New long run → narrate it.** Publish `stage / progress / state / item` on the `RunEventBus` under a run
  id, `Narrator.Track(...)` it with the agent that owns it, and add its terminal summary + chips to
  `Narrator.OnTerminalAsync`.
- **New page → an artifact view.** Tokens only, compact header, a thread panel if the page is a place where
  decisions are made, stable test ids, "Open full view" from its card.
- **Golden demo is typed, not clicked.** Anything added should be demonstrable as a sentence in a thread.
