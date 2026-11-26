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
| 6 | Generated code | `/scaffolds` | Backlog |
| 7 | My reviews | `/my-reviews` | Backlog |
| 8 | Comments | `/comments` | Backlog |
| 9 | Compliance | `/compliance` | Backlog |
| 10 | System health | `/system` | Backlog |
| 11 | Platform group | `/platform/*` | Backlog |
| 12 | Dashboard | `/home` | Backlog |
| 13 | Docs | `/corpora/:id/docs` | Backlog |
| 14+ | Migration plan, Routine board, Pattern analysis, Dependency graph, settings… | | To review |

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
