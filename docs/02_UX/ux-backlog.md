# UX refresh backlog

Page-by-page review of the live app (1440×900, Engineer persona), started 2026-11-26. Each page lists the approved
changes; **Status** says where it is. Rules for every item: routes, `data-testid`s and demo-spec button labels stay
(CLAUDE.md rule 7); Artizent tokens only; anything an e2e spec clicks stays reachable or the spec changes in the same
commit.

| # | Page | Route | Status |
|---|---|---|---|
| 1 | Home | `/` | Built — shipping with page 2 |
| 2 | Projects list | `/projects` | Built — shipping with page 1 |
| 3 | Project detail | `/projects/:id` | Backlog |
| 4 | Routines | `/subroutines` | Backlog |
| 5 | Routine + spec review | `/subroutines/:id`, `/review` | Backlog |
| 6 | Generated code | `/scaffolds` | Backlog |
| 7 | My reviews | `/my-reviews` | Backlog |
| 8 | Comments | `/comments` | Backlog |
| 9 | Compliance | `/compliance` | Backlog |
| 10 | System health | `/system` | Backlog |
| 11+ | Platform pages, Dashboard, settings… | | To review |

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
