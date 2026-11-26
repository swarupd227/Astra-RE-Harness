# UX refresh backlog

Page-by-page review of the live app (1440×900, Engineer persona), started 2026-11-26. Each page lists the approved
changes; **Status** says where it is. Rules for every item: routes, `data-testid`s and demo-spec button labels stay
(CLAUDE.md rule 7); Artizent tokens only; anything an e2e spec clicks stays reachable or the spec changes in the same
commit.

| # | Page | Route | Status |
|---|---|---|---|
| 1 | Home | `/` | Live (fc5947d) |
| 2 | Projects list | `/projects` | Live (fc5947d) |
| 3 | Project detail | `/projects/:id` | Live (fc5947d) |
| 4 | Routines | `/subroutines` | Built |
| 5 | Routine + spec review | `/subroutines/:id`, `/review` | Built (part 1) |
| 6 | Generated code | `/scaffolds` | Built (ca6eea0) |
| 7 | My reviews | `/my-reviews` | Built (57f3aa8) |
| 8 | Comments | `/comments` | Built, testing |
| 9 | Compliance | `/compliance` | Live (a127a74) |
| 10 | System health | `/system` | Live (a127a74) |
| 11 | Platform group | `/platform/*` | Backlog |
| 12 | Dashboard | `/home` | Backlog |
| 13 | Docs | `/corpora/:id/docs` | Backlog |
| 14 | Migration plan | `/corpora/:id/migration-plan` | Backlog |
| 15 | Routine board | `/corpora/:id/board` | Backlog |
| 16 | Pattern analysis | `/corpora/:id/pattern-analysis` | Backlog |
| 17 | Dependency graph | `/corpora/:id/dependency-graph` | Backlog |
| 18 | New project | `/projects/new` | Backlog |
| 19 | Assessment | `/corpora/:id/assessment` | Backlog |
| 20 | Evidence | `/corpora/:id/evidence` | Backlog |
| 21 | LLM Provider | `/platform/llm` | Backlog |
| 22 | Project settings | `/corpora/:id/settings` | Backlog |
| 23 | Prompt Catalog | `/platform/prompts` | Backlog |
| 24 | Languages | `/platform/languages` | Backlog |
| 25+ | Harmonisation, Roles, Validation policy, routine extract & live scaffold screens… | | To review |

## 1 · Home (`/`)
1. Estate summary on top: 4 KPI cards (Programmes · Routines with code · Waiting for review · Runs in progress).
2. "Continue where you left off": last 3 programmes with funnel and next step; all programmes folded below.
3. Earlier conversation folds behind "Show earlier conversation (n)"; the page opens at the top.
4. Funnel readable in the artifact pane (two-column stage list); pane only shown when there is a card.
5. Rail grouped Work / Quality / Platform / Admin (collapsible, remembered); agents as a named, folded list; one-line footer.
6. Top bar: one status dot (API, model, key) with a popover instead of latency + provider pills; red banner when the
   model key is rejected (API reports `keyRejected` from the rate limiter's last 401/403).
7. One way to ask: ⌘K is "Search or jump to…"; rail "Ask Astra" focuses the composer.
8. "Mission Control" → "Home", "Global" → "All programmes"; `/home` rail link labelled "Dashboard" (test id kept).
9. Suggestion chips only under the newest reply; secondary text contrast raised.

## 2 · Projects (`/projects`)
1. Toolbar: search, language, status, sort, table/cards — all in the URL.
2. Table view by default at 12+ projects: name · language · routines · progress · next step · last activity.
3. Compact cards (3 columns) with progress and next step.
4. "Needs attention" strip: failed ingests and parses stuck > 1 day.
5. Test projects hidden by default ("Show test projects (n)"); flagged by name pattern or the new `test` project setting
   (checkbox on Project settings). Nothing deleted.
6. Language always shown ("Unknown" when none); relative times; quiet source icons; short subtitle.

## 3 · Project detail (`/projects/:id`)
1. Summary header: language, source, relative ingest time; funnel; next step as the one primary button; "Ask about this project".
2. Tabs (`?tab=`): Overview · Routines · Sources & evidence · Docs · Plan.
3. Routines tab: search, state chips, group by folder (common prefix stripped), paging/virtualisation over 200 rows, name first.
4. Views as a quiet group; Re-sync and Settings in a "⋯" menu, Re-sync confirmed.
5. First paint fast on large projects (ADV-CoreLibrary page is 123 000 px today).

## 4 · Routines (`/subroutines`)
1. Paged results table (name · project · language · state · lines · risk · last change), sortable, row links.
2. Default view "Needs action" (in review, drafts, failed gates) instead of a random 100.
3. Search also matches path and spec purpose text, showing where it matched.
4. Filters: project (searchable, test projects hidden), language, state chips, risk — in the URL.
5. One shared state vocabulary across pages ("With code", not "SCAFFOLDED").

## 5 · Routine + spec review
1. Review workspace fills the viewport; evidence trail moves into an "Evidence & signature" drawer; dev-posture note Admin-only.
2. Primary button follows state: Sign spec → Generate code → Open generated code (Regenerate behind "⋯" + confirm).
3. Re-extract in a "⋯" menu; on a signed spec the confirm names what is replaced.
4. Target stack: chosen + eligible stacks; ineligible ones folded; consistent names.
5. Claim outline chips cut at word boundaries, distinguishing near-duplicates.
6. One stepper and "Next" logic shared by routine page and spec review; shorter breadcrumb; one status pill.

## 6 · Generated code (`/scaffolds`)
1. Group by project with gate summary (build ✓ · tests ✓ · previews live); failing groups first.
2. Rows: routine · stack · build · tests · TODOs (amber > 0) · preview link · relative time; VBA labelled VBA.
3. Filters: stack chips with counts, gate status, has TODOs, project — in the URL.
4. Git column only when committed; "Generated" instead of "Scaffolded"; "Live previews" strip.

## 7 · My reviews (`/my-reviews`)
1. Honest scope ("Reviews waiting for an SME"), age per card ("Waiting 18 d", amber after 7 d).
2. Count pills filter the list; cards with word-boundary truncation, file name only, one status + progress bar.
3. "Start next review" primary; "Ask the Spec agent" per card; group by project; sort by age/effort/project.
4. Signed section most-recent-first with signer and time. (Real assignment = separate feature.)

## 8 · Comments (`/comments`)
1. Tabs: Mentions · All recent threads.
2. Empty state that points at where comments are written (My reviews, most recently discussed spec).
3. Per-user read state (group `@engineer` mentions kept, labelled).
4. Following: replies on threads you joined, comments on specs you signed.
5. "Mark all read" disabled when nothing is unread; loading text; wider layout; one name ("Comments").

## 9 · Compliance (`/compliance`)
1. Overview strip for the range: events · signed · committed · **validation bypasses** · exports; each filters the list.
2. Paged recent-events table (time · event · actor · target · severity) with filters.
3. Export defaults to all matching events with the count on the button; explicit time zone; quick ranges.
4. Compact format tiles (Production / Template badges explained); column table and rationale behind disclosures.
5. Copy: no "harness" or vendor lists; heading "Compliance & audit".

## 10 · System health (`/system`)
1. Build card shows what is really deployed: API build sha (from `/health/ready`) and frontend build sha, both linked
   to the commit; drop the stale "0.1.0 · Phase A · local-dev · 2026-05-06" constants.
2. Readiness lists every dependency with its version (parser `astra-parser 0.6.1`) and what it is ("File storage", not
   "Minio" — the API stores blobs in Azure Blob now).
3. Add the model provider row: provider · model · key working / rejected (same source as the top-bar status dot), and
   today's call count, error rate and median latency.
4. Recent problems: last failed runs / validation runs stuck in RUNNING, with links.
5. Identity card: "Signed in as Dev User · Engineer"; the "Bypass true" and X-Dev-Persona text move into an
   Admin-only "Authentication (dev mode)" note.

## 11 · Platform group (`/platform`, portfolio, golden dataset, prompts, signatures, harmonisation, languages)
1. **Dead ends for non-admins:** the rail shows Platform → Overview and Portfolio to every persona, but both answer
   with a red "Admin only" box. Hide admin-only items from non-admins in the rail, and turn the page itself into a calm
   "Ask an Admin" note naming what the page shows (not an error block).
2. **Golden Dataset** says "Aggregate score 100%" from **1 of 160** entries scored — show "1 of 160 scored" as the
   headline and the score only with its coverage ("100% on 1 entry"); page is 16 800 px: page the entry list
   (50 at a time) and fold each entry's sample code.
3. **Signature Health:** "5 drifted" is the actionable number — lead with it, list the drifted specs first with a
   "Re-verify" action per spec (and per project), healthy ones folded.
4. **Prompt Catalog** (4 800 px) and **Languages** (4 000 px): search plus filter chips already exist — add
   collapsed-by-default rows and a count line ("62 prompts · 9 languages"); open one at a time.
5. **Harmonisation:** empty-state explaining what triggers findings (signed specs in one project) and linking to the
   project with the most signed specs.
6. Consistent eyebrows: these pages say "Quality", "Governance", "Spec schemas", "Prompt asset library" — use the
   rail group name ("Quality" / "Platform").

## 12 · Dashboard (`/home`)
1. **It duplicates Home and contradicts it.** Same estate numbers in a different shape, and the numbers disagree:
   "Route **9 848** draft specs" while its own Routines card says **1 631** draft; "37 signed specs awaiting scaffold"
   while most of them already have code (Home: 33 with code). Fix the counts at the source (one overview query feeds
   both pages) — wrong numbers on a dashboard are worse than none.
2. Give it a distinct job: **Activity** — Claude spend (today / 7 d / 30 d, per project), pipeline activity as
   readable sentences ("Validation passed for CalculateApiPremium · React + Python", not `validation.completed ·
   scaffold`), and recent sign-offs. Estate progress and "what's next" live on Home only.
3. Title and crumb say "Dashboard" (the tab and breadcrumb still say "Home" — two Homes). Eyebrow "Work", not
   "Astra RE Harness".
4. "What's next" items link to the filtered list they describe (Routines → Not started / Draft / Signed) instead of
   generic copy; remove "Extract a spec · 9 703 parsed" as a to-do (it is the whole backlog, not a next step).
5. The project list duplicates Projects: replace with "Projects changed this week" (≤ 5) and a link.
6. "Add project" button text wraps onto two lines — give the card header room.

## 13 · Docs (`/corpora/:id/docs`)
1. **Citations render as raw text** — "[Ultimate_Comprehensive_Travel_Insurance_Raterv1.xlsm/TravelRaterApi.bas:L39–L89]"
   in the middle of sentences, often wrapping over two lines. CLAUDE.md: anything that cites uses `CitationChip` —
   render them as compact chips ("TravelRaterApi.bas · L39–89") that open the source.
2. **"Quality 15/100 · 3/4 checks"** in a red pill on the Overview with no explanation of what scored low; make it a
   button that opens the failed check and its reason, and say what "revised" means.
3. **No way forward on 102 drafts:** every section says "0/n signed" and there is no review/approve action in view,
   no bulk "Review this section", and no export/PDF button on the page. Add a header action row: Review next draft ·
   Export PDF · Regenerate (⋯).
4. The middle column lists one document per section and wastes ~200 px at 1440 — collapse it into the left nav when a
   section has a single document.
5. "102 Draft · 1 SUPERSEDED" pills: same casing, and make them filters.

## 14 · Migration plan (`/corpora/:id/migration-plan`)
1. Jargon up front: "topological-leaves-first", "corpus", "no in-corpus callees", "draft" pill. Say it plainly: "3
   waves. Each wave only depends on routines in earlier waves, so they can be migrated in this order." Strategy name
   in a tooltip.
2. The plan says what is done per wave: a progress bar per wave ("15 of 15 have code") and the wave a person should
   work on now highlighted; finished waves fold.
3. "Wave 2 · 1 routines" — plural bug; "signed: 0 · scaffolded: 13 · committed: 0" reads as raw counters — use the
   shared stage words and hide zeros.
4. Routine chips show their state in shouty "SCAFFOLDED" pills — use the shared labels ("With code"), and show why a
   routine sits in its wave (what it calls) on hover.
5. The plan is "draft" with no visible way to approve or regenerate it — add Approve plan (Admin) / Regenerate (⋯).
6. "← Back to corpus" → "Back to project" (breadcrumb already does this; the link can go).

## 15 · Routine board (`/corpora/:id/board`)
1. **The only column with cards is off-screen.** At 1440 px the first view is five empty columns ("Nothing here
   yet." × 4 visible); all 15 Travel routines sit in "Built", scrolled out of view to the right. Collapse empty
   columns to a thin labelled strip ("Draft · 0") so the occupied ones get the width.
2. "Built" here vs "Scaffolded" / "With code" elsewhere — use the shared stage words and the same order as the funnel.
3. Cards repeat the full workbook path ("Ultimate_Comprehensive_Travel_Insurance_Raterv1.xlsm/TravelRaterApi.bas:39"):
   file name + line, folder on hover; add the gate result (build/tests) on cards in the last column.
4. Header copy explains what the board is not ("nothing here is dragged") — replace with counts per stage.

## 16 · Pattern analysis (`/corpora/:id/pattern-analysis`)
1. "Last run" is a debug line: "0 digested (0 surveyed, 0 propagated, 0 trivial) in 1s for $0.00 → 11 cluster(s)
   across 15 routine(s) [15 from specs, 0 from digests]". Say "Grouped 15 routines into 11 patterns · 2 d ago ·
   Re-run (⋯)"; the counters go into a "Details" disclosure.
2. The three numbers don't add up for a reader: 3 core patterns + 8 singletons = 11, but "Routines analysed 15" —
   show "15 routines → 11 patterns (3 shared by 2+ routines, 8 one-offs)".
3. Machine ids under every title ("canonical-vba-json-value-serializer") and "Claim-kind signature:
   edgeCase,invariant,openQuestion" — move to a tooltip; the plain title and description are what people read.
4. (Correction: the routine chips already link to the routine.) Each pattern's next step — the archetype proposal
   below it — could be summarised on the card header.
5. Eyebrow "Pattern Analysis" + "corpus" + "archetypes a migration engagement needs" — plain words ("project",
   "how many distinct things the code does").

## 17 · Dependency graph (`/corpora/:id/dependency-graph`)
1. **The canvas is blank on open** (Travel, 15 nodes, 1440×900 after 9 s): the graph is not fitted into view — open
   with "fit to view" applied, and say "15 routines · 11 calls" over the canvas until it lays out.
2. The side panel is raw stats in mono lowercase ("SCCs (cycles): 0", "shared-storage edges: 78", "roots: 4") — turn
   into plain sentences: "No call cycles. 13 routines call nothing else; 4 are entry points. 2 outside calls (MSXML2)."
3. The wave legend lists waves 1–5 while this plan has 3 — show only the plan's waves, with their names.
4. Header copy is parser jargon ("Parser-extracted call graph + shared-storage references", "drill in") — one plain
   line plus the interaction hint once, at the bottom of the canvas (it is currently said twice).
5. "← Back to corpus" → remove (breadcrumb does it); "Hide shared-storage edges" → "Hide shared worksheet/variable links".

## 18 · New project (`/projects/new`)
1. **The language picker contradicts itself**: "C++ → .NET 8 + Java Spring + Angular" while the platform's default
   target for C++ is .NET 10; "Delphi → .NET 8 …" likewise; "Transition documentation" sits in the language list
   although it is not a language; Office VBA is not listed at all (it is detected from .xlsm). Show source
   languages only ("COBOL", "C++", "VBA / VB6", …) — the target stack is chosen later, on the routine — and move
   "Transition documentation" to the pack choice.
2. **Ten artefact kinds, each with a paragraph, push "Ingest files" 1 800 px down** for a decision the copy says can
   be changed later. Fold "Additional evidence (optional)" by default with a one-line summary of what is on.
3. Name field: "Must be unique" — check as you type and say so inline, not after submit.
4. Copy: "Ingest" / "Connect a new project" / "Ingest files" → "New project" / "Create project"; say what happens
   next ("Astra reads every file and lists its routines — usually under a minute; large repositories continue in
   the background").
5. Git URL tab: show branch and folder fields up front with defaults ("main", "/"), and whether private repos need a
   token.

## 19 · Assessment (`/corpora/:id/assessment`)
1. **Contradicts itself on the recommendation**: the card says "Faithful 1:1 on react-python", the prose says
   "like-for-like conversion to React and FastAPI" and then "the heuristic recommends replatform … but the
   modernization-flag count is not provided in the fact sheet" — internal reasoning leaking into a customer deliverable.
   One recommendation, stated once, with the reason; the generator's caveats go to an "Assumptions" note.
2. **Says VB6 for a VBA workbook** (header pill, prose "VB6/VBA", risk "vb6: scarce skills") — use the project's
   language label (VBA) as Projects does.
3. Effort reads as maths ("15 routines at ~50/person-week for vb6 → 0.3 base weeks · ×1.04 · ×1.17") — lead with
   the band in words ("Small: under a week with the platform") and fold the calculation under "How this is estimated".
4. "Dependency hotspots: 10" lists every routine with 1 caller — for a 15-routine workbook that is noise; show
   hotspots only above a threshold (e.g. reach ≥ 20 %) and say "No hotspots" otherwise.
5. Page is 6 000 px with no actions in view: add Export PDF · Share · Re-run (⋯) to the header, and a contents rail.

## 20 · Evidence (`/corpora/:id/evidence`)
1. **Each row says the long workbook name three times** — id line, label line ("Ultimate_Comprehensive_…xlsm · 1.
   Rating Dashboard!C7") and locator line ("Ultimate_Comprehensive_…xlsm · input:1. Rating Dashboard!C7"), plus two
   tags ("Spreadsheets", "Structured") that repeat the active filter. Row = what it is ("Input C7 · 1. Rating
   Dashboard"), kind icon, file name once on hover; the short id as a small copyable chip.
2. Group by file, then by sheet/routine, collapsible — 33 flat rows for one workbook becomes 3 groups.
3. Inputs / formulas / controls are different things a reader wants — sub-filter chips under "Spreadsheets"
   (Sheets · Inputs · Formulas · Controls · Forms).
4. "Show together" is disabled until rows are ticked but nothing says so — show "Tick 2 or more to read them side by
   side" beside it.
5. Eyebrow "Project" → the project name (the subtitle currently carries it after a "·").

## 21 · LLM Provider (`/platform/llm`)
1. Eyebrow "Task #178 · Provider configuration" — an internal ticket number on a customer page. → "Platform".
2. The page does not show whether the key **works**: "set from UI" is shown even while Anthropic rejects it. Show
   the same state as the top-bar status dot ("Working" / "Rejected by Anthropic 3 min ago — calls are failing"),
   with "Test connection" beside it rather than in a separate card.
3. Docs and code generation use their own models (Sonnet/Opus per pipeline, `Docs:Generator:*`) — list every model
   in use and where, so "Model: claude-sonnet-4-5" is not mistaken for the whole story.
4. Non-admins see "Only the Admin persona can change the API key" as body text — the Set key field should be shown
   disabled with that line as its hint, so the page shape is the same for everyone.
5. Title "LLM Provider" vs rail "LLM Provider" vs System health "Model" — call it "Model provider" everywhere.

## 22 · Project settings (`/corpora/:id/settings`)
1. **"Save settings" is 1 900 px down**, under ten artefact-kind paragraphs — same issue as New project: make the
   save bar sticky, and show the kind picker as a compact two-column checklist (description on hover).
2. "Programme details · Used by the portfolio and dependency views" — say what each field changes ("Tier sets the
   order on the Portfolio page"); "Knowledge holder" needs an example ("the person or vendor who knows this system").
3. No unsaved-changes guard: leaving with edits loses them silently — warn, and disable Save until something changed.
4. The project's language, source (upload / Git URL, branch) and name are not editable or even shown here; a rename
   and the Git source belong on this page (Re-sync opens from the project page today).
5. "Test project" sits under "Display" — move it to its own "Visibility" line (it changes lists, not the banner).

## 23 · Prompt Catalog (`/platform/prompts`)
1. **Mixed ownership branding in a customer UI**: cards say "owner: Nous · COBOL migration accelerator" next to
   "owner: Artizent · pre-sales assessment". One product owner name (or hide the owner field outside Admin).
2. 51 cards (6 400 px) with raw ids — "cobol → dotnet10 · extract @ v0.1", stack ids like "dotnet10-minapi" and
   "dotnet10-minimalapi" both listed. A compact table (Purpose · Source · Target · Version · Status · Model), grouped by
   purpose (Extract specs · Generate code · Assess · Patterns · Agent), with the shared target names.
3. Filters are three unlabelled native selects showing raw ids ("fortran-f77", "office-vba") — label them and use
   display names; add a search box.
4. "preview" vs "production" status is the most important fact for a reviewer — make it a filter and colour it.
5. Intro copy: "spec provenance carries forward into every audit pull" → plain: "Every model call records which
   prompt and version it used."

## 24 · Languages (`/platform/languages`)
1. **Internal engineering notes in customer copy**: "Per ADR-026 …", "Per ADR-028, the parser sidecar receives a
   compile_commands.json bootstrapped by CMake", "Calibrated against the Tier-1 portfolio (Phase 1 …". Keep one
   plain sentence per language (what source it reads, what it is good at); ADR notes go to an Admin-only "Details".
2. **Stale target lists contradict the platform**: C++ "→ dotnet8, java-spring, angular-java" and the title "C++ → .NET
   8 + Java Spring + Angular", though .NET 10 is the default .NET target; VB6 card doesn't mention Office VBA or
   React + Python. Derive the targets shown from the archetypes actually available per language (the same list the
   routine page's target picker uses), with display names.
3. 12 large cards, 5 900 px — a table (Language · Reads · Targets · Status · Claim kinds) with the description on
   expand; "preview" vs "Production" casing differs.
4. Eyebrow "Spec schemas" + jargon ("typed claim schema", "taxonomy", "mechanically derivable") → "Languages Astra
   reads, and what it can turn each into."
