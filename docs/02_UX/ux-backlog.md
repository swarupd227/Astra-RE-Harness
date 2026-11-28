# UX refresh backlog

Page-by-page review of the live app (1440×900, Engineer persona), started 2026-11-26. Each page lists the approved
changes; **Status** says where it is. Rules for every item: routes, `data-testid`s and demo-spec button labels stay
(CLAUDE.md rule 7); Artizent tokens only; anything an e2e spec clicks stays reachable or the spec changes in the same
commit.

| # | Page | Route | Status |
|---|---|---|---|
| 1 | Home | `/` | Live |
| 2 | Projects list | `/projects` | Live |
| 3 | Project detail | `/projects/:id` | Live (tabs → section nav; routines paged — page 8 200 px / 2 200 nodes, so no virtualisation needed; API JSON + bundle now compressed) |
| 4 | Routines | `/subroutines` | Live, incl. search over spec summaries |
| 5 | Routine + spec review | `/subroutines/:id`, `/review` | Live (parts 1–2); stepper unification live (78f1a93) |
| 6 | Generated code | `/scaffolds` | Live (gates per package, live previews) |
| 7 | My reviews | `/my-reviews` | Live; real assignment = separate feature |
| 8 | Comments | `/comments` | Live; per-user read state + following open (API) |
| 9 | Compliance | `/compliance` | Live |
| 10 | System health | `/system` | Live (deployed builds, services, model, recent problems) |
| 11 | Platform group | `/platform/*` | Live (dead ends, Golden Dataset, Signature Health) |
| 12 | Dashboard | `/home` | Live (counts, what's next, activity words) |
| 13 | Docs | `/corpora/:id/docs` | Live (citation chips, quality chip, persona hint) |
| 14 | Migration plan | `/corpora/:id/migration-plan` | Live (Approve / Recompute exist for Admin; others told who acts) |
| 15 | Routine board | `/corpora/:id/board` | Live |
| 16 | Pattern analysis | `/corpora/:id/pattern-analysis` | Live |
| 17 | Dependency graph | `/corpora/:id/dependency-graph` | Live |
| 18 | New project | `/projects/new` | Live (name check; Git branch/folder fields already present) |
| 19 | Assessment | `/corpora/:id/assessment` | Live (UI + prompt v1.2) |
| 20 | Evidence | `/corpora/:id/evidence` | Live (rows, file headings, workbook sub-filters) |
| 21 | Model provider | `/platform/llm` | Live: models in use (needs API b5bcf76), one name, same shape for non-Admins |
| 22 | Project settings | `/corpora/:id/settings` | Live (sticky save, unsaved-changes guard, rename) |
| 23 | Prompt Catalog | `/platform/prompts` | Live (grouped by purpose) |
| 24 | Languages | `/platform/languages` | Live (targets from archetypes) |
| 25 | Roles & Permissions | `/platform/roles` | Live (Can-do list from the matrix) |
| 26 | Validation policy | `/platform/validation` | Live (UI + API text) |
| 27 | Harmonisation | `/platform/harmonisation` | Live |
| 28 | Generated package | `/scaffolds/:id` | Live (gate strip) |
| 29 | Extract spec (live) | `/subroutines/:id/extract` | Live: finished heading + next-step button, plain stages, raw stream folded; readable claim rows while streaming (done) |
| 30 | Live code generation | `/specs/:id/scaffold` | Not reviewed — needs a signed spec, and signing is an SME's decision |

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
4. Target stack: chosen + eligible stacks; ineligible ones folded; consistent names. **Done:** compact pickers (routine page, regenerate) show eligible + chosen, the rest folded with reasons.
5. Claim outline chips cut at word boundaries, distinguishing near-duplicates.
6. One stepper and "Next" logic shared by routine page and spec review; shorter breadcrumb; one status pill.
   **Done (78f1a93):** `WorkflowStatus` — rail + the page's blocker or "Next: action · owner" (switch-persona hint) on both pages.

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
3. (Correction: Generate/Regenerate, Requirements pack, Export (Word, PDF, MkDocs, Confluence) and "Sign all" exist —
   they are shown only to the personas allowed to use them, so an Engineer sees none.) Remaining: tell an Engineer
   who can sign and export ("An SME signs sections; an Admin regenerates and exports").
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

## 25 · Roles & Permissions (`/platform/roles`)
1. **A raw error on the page for non-admins**: "Could not load users · ApiError: Request failed (403)" — the users
   list is Admin-only; show "Only an Admin can see and change who has which role" instead of an error.
2. Role cards list internal build stages ("Stage 1 · Ingest", "Stage 3 · Extract", "Phase #2 · Validation") — use
   the product's step names (Ingest, Extract specs, Generate code, Validate) with no stage/phase numbers.
3. Admin "owns" Signature Health, but re-verifying drift is an SME-and-Admin action elsewhere — make the cards match
   the capability matrix (generate the "owns" list from the matrix instead of a hand-written list).
4. "Every user has one role" + persona switcher in the top bar: say that the switcher is a development aid until
   single sign-on assigns roles, so a reviewer is not confused that anyone can become Admin.

## 26 · Validation policy (`/platform/validation`)
1. **The policy describes a Fortran demo, not the product**: "Drive the original Fortran (via gfortran sidecar) and the
   generated C# … one canonical input per CONSUME_ROLL family … as that lands per Phase #5". The equivalence gate
   should describe what it does for every source language (or say which languages it covers today), with no seed-data
   names or phase numbers.
2. It lists 3 gates; the platform now also has a quality gate (security, coding standard, maintainability — commit
   3b304e7) and React + Python gates (pytest, jest) — derive the gate list from what actually runs per target stack.
3. Raw values: "PT15M" (ISO duration) → "15 minutes"; "regenerate-then-run" → "Regenerate the tests, then run";
   "SCOPE global", "POLICY VERSION v1.0", "OWNED BY Nous · Validation engineering" (third brand name again).
4. "Zero warnings required at project-level error treatment" — unclear; say "The build must succeed with no errors;
   warnings are reported."
5. Read-only for everyone today with no hint who changes it — say "Set by an Admin" and, for Admins, show an Edit.

## 27 · Harmonisation (`/platform/harmonisation`)
1. **Defaults to an e2e test project** ("php-facts-e2e-1795692229075-7738") — the first corpus in the list; the picker
   lists every QA/test project too. Default to the project with the most signed specs, hide test projects (same rule
   as Projects), label it "Project", not "Corpus".
2. **The empty state names a button that isn't there**: "Click Run harmonisation pass" — there is no such button for
   an Engineer (Admin-only?). Show the button disabled with who can run it, or say "An Admin runs a pass".
3. Explain when it is worth running: "Needs at least two signed specs in the project — Travel has 31" and show that
   count per project in the picker.

## 28 · Generated package (`/scaffolds/:id`)
1. **No title says what this is**: the page heads "Scaffolded package" with no routine or project name — add
   "CalculateApiPremium · Travel insurance demo — React + Python package" and a breadcrumb back to the routine.
2. **"Commit to Git · STUB"** is an enabled-looking button labelled with an internal word; either hide it until Git
   commit is wired, or disable it with "Git commit isn't connected for this project yet".
3. The gate results (build / tests / quality) are not on this page — only a "Validation report" link. Show the three
   results as a strip under the title (same data as Generated code's Build/Tests columns), with "Fix until green" there.
4. "SCAFFOLDED" raw state pill and "Scaffolded package" → "Generated package · Ready" / "Tests failing" from the gates.
5. The preview ("It closes itself at 9:27:48 PM") is good — say how long is left ("closes in 23 min") and offer
   "Keep it running".

## 29 · Extract spec, live (`/subroutines/:id/extract`)
Reviewed on one user-approved run: `StartAgain` (27 lines, Actuarial sample - auto insurance premium), 77 s,
5 464 tokens in / 5 121 out.
1. **The heading still says "Extraction in progress" after it finished** ("Extraction complete." is a line below it) —
   the heading should change to "Spec extracted" with the next step as the primary button ("View draft spec").
2. **Raw JSON streams on screen** while Claude writes ("{\"$schema\": \"https://nous.dev/schemas/…\", …") — a third-party
   brand URL and machine field names in front of the user. Show the claims appearing as readable rows (summary first,
   then each invariant / edge case as it completes) and keep the raw stream behind "Show raw output".
3. Internal provider string on screen: "config anthropic:zdr=true:no-train:no-retention:enterprise-endpoint" — say
   "Zero data retention · not used for training" in words, or put it in a details popover.
4. No sense of time: give an expected duration from past runs of similar size ("usually about a minute") and a
   progress line based on the stages; "latency 76526 ms" → "took 1 min 17 s".
5. The stage list (Priming context → Loading source → Streaming response → Validating → Persisting) is engine
   vocabulary — "Reading the routine → Writing the spec → Checking it → Saving".
6. Routine page, seen on the way in: the target-stack panel still lists 7 stacks, 5 of them "No VB6 archetype for this
   stack yet" (page 5, item 4 — still open).

## 30 · Live code generation (`/specs/:id/scaffold`)
Not reviewed: it needs a signed spec, and signing is a human SME decision — the agent does not sign specs to reach a
screen. Review it on the next spec an SME signs.

## Second pass (2026-11-28, live, 1440×900)
1. **Home** — e2e / QA projects (`php-facts-e2e-…`) showed on Home and in the rail; "Continue where you left off" was
   ordered by the last pattern-analysis run and carried that run's "Succeeded" pill, which read as the project's status
   (even on "Nothing ingested yet"); names and next steps were cut off. **Done:** the overview flags `isTest` (e2e names
   count as test, API and frontend share the rule) and leaves test projects out of the totals; Home lists only real
   projects, ordered by last change, "updated N ago", a pill only for a run in flight, names on two lines, next step wraps.
2. **Projects / Code units** — language names raw ("php", "unibasic", "vbnet"); "1 files". **Done:** PHP, UniBasic,
   VB.NET, VBA, OpenEdge ABL … in the shared formatter; singular file.
3. **Generated code** — a dozen rows read "__construct" with nothing to tell them apart. **Done:** file name under the name.
4. **Model provider** — Test connection beside the key status (page 21 item 2). **Done.**
5. My reviews — fine as is.
6. **Project detail** — the funnel said "Parsed / Scaffolded / Committed" beside chips saying "Not started / With code".
   **Done:** the shared funnel uses Not started · … · With code · Committed to Git everywhere.
7. **Compliance** — the "On" column read "scaffold" / "spec" with no name. **Done:** `/api/v1/audit` names each target
   (spec / generated package → its code unit, project → its name, code unit) in four set queries; the column shows the
   kind in words and the name, linked to the code unit.
8. **System health** — "Recent problems" was a flat list of 50 runs named "get", "index", "__construct", mostly one
   project's repair loop. **Done:** stuck runs listed first; failed runs grouped by project with counts per check
   (Build 6 · Tests 32), folded; the API returns the project and up to 300 rows.
9. Comments — fine ("Mark all read" already disabled when nothing is unread).
10. **Dashboard** — counted test projects (48 projects vs 32 on Home), raw numbers ("62323 parsed", "30997 calls"),
    "anthropic · sonnet 4.5", a "Parsed" badge on every changed project, activity rows saying "scaffold" / "spec".
    **Done:** `/system/stats` leaves test projects out (same rule as Home); numbers formatted, state words shared
    ("not started", "with code"); "Claude (Anthropic) · Sonnet 4.5"; badge only for a state that needs a look; activity
    rows name the code unit (audit `targetName`).
11. **Home** — workbook projects said "VB6" (the grammar) where Projects says "VBA". **Done:** the overview says "vba"
    for VB6-grammar code inside an Office document (display only; stats and assessment targets unchanged).
12. **Rail** — programme avatars took punctuation ("H(", "S(", "O("). **Done:** initials from words starting with a
    letter or digit ("HUGE (plain PHP …)" → "HP").
13. **Signature Health** — "By corpus" chart squeezed ~20 projects onto a 130 px vertical bar chart (two clipped
    labels). **Done:** "By project", horizontal bars with readable names, drifted first, on its own row under the tiles.
14. **Golden Dataset** — "Schema" filter listed raw ids (fortran-f77, vbnet). **Done:** "Language" with names (values
    unchanged).
15. **Prompt Catalog** — target filter and cards showed raw ids (dotnet10-blazor, office-vba, quality, code-review;
    "cobol → dotnet10 · extract @ v0.1"). **Done:** names for every .NET 10 variant and prompt folder, "Code review";
    cards read "COBOL → .NET 10 · Extract specs · v0.1" (raw ids in the tooltip).
16. **Languages / target cards** — "preview" lower-case beside "Production". **Done:** "Preview".
17. **Roles** — "Generate a scaffold", "Commit a scaffold"; Admin's list missed what only an Admin can do. **Done:**
    "Generate code", "Commit generated code to Git"; Admin rows for documents & migration plans, the model key and
    hiding messages (each Admin-only on the server). Display only; ids unchanged.
18. **Validation policy** — "Three independent gates" above four; the quality gate's retry read "Regenerate the tests".
    **Done:** count from the policy, "Regenerate, then run again", "generated code" instead of "scaffold".
19. **Programme thread** — a workbook project's header and agent messages said "VB6"; the ingest note said "5 parse
    warning(s) are recorded on the corpus". **Done:** `ConversationService.DisplayLanguageAsync` (one rule: VB6
    grammar inside an Office document reads VBA) used by the thread header, the welcome, the ingest narration and Home;
    "5 parse warnings are recorded on the project".
20. **Code unit page** — a Java method showed Fortran/Pick panels ("COMMON blocks", "ISAM reads/writes" with "—"), a raw
    readiness id ("coordinated-only"), "shared storage block(s) /BookController/ … co-locate or replicate the shared-state
    contract", "No migration plan for this corpus", and a locked .NET 10 card titled with a C++ sample under "Generated code
    … will be .NET 10". **Done:** shared state named per language (COMMON blocks only for Fortran), I/O groups only when
    present, readiness in words ("Moves with others"; ids kept in the API), a plain reason, "project", a neutral target
    description, and no archetype sample on a locked compact card.
21. **Spec review** — outline counters read "0 accept · 0 q · 10 untouched"; claim ids shouted in capitals.
    **Done:** "accepted · edited · rejected · questioned · not reviewed"; ids in their own case.
22. **Generated package** — tab titled "Scaffold artifact"; opened on a 3-line schema.sql. **Done:** "Generated package";
    opens the service / controller / handler that cites the spec (config, SQL and tests ranked after).
23. **Validation report** — "Back to scaffold"; raw provider config string under the trust chips; quality gate "passed"
    lower-case. **Done:** "Back to package", config string on hover, "Passed". ("Run 4th gate" and "Four independent
    gates" stay: demo specs click and read them.)
24. **Project pages, missing project** — "Could not load project · Request failed (404)" with a Retry. **Done:**
    `ProjectLoadError`: a 404 says "Project not found — it may have been removed, or this link points at something
    else" with "All projects →"; other errors keep the message and Retry (project, docs, pattern analysis).
25. **Docs / Migration plan / Portfolio** — "1 SUPERSEDED", "In Review"; "for this corpus", "what it CALLs",
    "Per-corpus". **Done:** shared casing, "project" throughout.
26. **Evidence** — every row repeated the workbook's long file name under its own file heading; "D5 D6 … D38".
    **Done:** the second line says where inside the file (file name kept for screen readers); runs of three or more
    consecutive cells read as a range ("D5:D38").
