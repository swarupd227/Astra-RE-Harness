PRODUCT SPECIFICATION  ·  ENGINEERING REFERENCE  ·  v1.0
Nous Astra RE Harness
AI-assisted reverse engineering for Fortran/Classic source
A production-grade workflow tool that ingests legacy Fortran source, extracts behavioral specifications via LLM-assisted analysis, routes them through SME review with full audit trail, and emits signed contracts plus scaffolded target-platform code. Built to operate on Kiwiplan Classic source during Project 3 Phase 0 and Phase 1.
5
Pipeline stages
Ingest · Parse · Extract · Review · Scaffold
3
User personas
Nous engineer · Kiwiplan SME · Architect/observer
2-tier
LLM strategy
Anthropic Claude (extraction) + Azure OpenAI (scaffold)

DOCUMENT FOR
Engineering — product build team
This specification is the contract for build. Every screen, data structure, API surface, prompt template, and security control listed in this document is in scope. Anything not in this document is out of scope and requires change-controlled addition.

SECTION 01
## Product scope
### 1.1  What this product is
The Nous Astra RE Harness ("the Harness") is an internal workflow tool used during Phase 0 and Phase 1 of Project 3 (Kiwiplan RSS Modernization). It is operated by Nous engineers and Kiwiplan domain SMEs to systematically extract behavioral specifications from Fortran source and produce signed-off contracts for the modern build.
The Harness is not a Fortran-to-modern-code translator. It is a controlled pipeline in which LLMs draft specifications and target-platform scaffolds, and humans review, edit, and sign every artifact before it is treated as authoritative. The signed specification — not LLM output — is the contract for the modern build.
### 1.2  In scope
STAGE 1 — INGEST
Source ingestion & versioning
Pull Fortran source from a connected Git repository or upload via UI. Tag with version, commit hash, ingestion timestamp. Multi-file modules supported. Read-only; no source modification.
STAGE 2 — PARSE
AST + structural index
Generate a Fortran AST. Build a structural map: subroutines, COMMON blocks, call graph, ISAM/file I/O patterns. Persisted as queryable JSON. Foundation for every subsequent stage.
STAGE 3 — EXTRACT
LLM-assisted spec drafting
Per subroutine: structured prompts to Anthropic Claude with the source as input. Output: behavioral spec in defined JSON schema with inline source citations. Streaming, retryable, cached.
STAGE 4 — REVIEW
Human review & sign-off
SMEs review extracted specs claim-by-claim with accept/edit/reject. Full edit history retained. Sign-off creates immutable signed-spec artifact. Signature ties to source version + reviewer identity.
STAGE 5 — SCAFFOLD
Target code + test fixtures
.NET service skeleton, DTOs, repository interfaces, integration adapters, unit-test fixtures generated from signed spec. Method bodies for business logic stubbed with explicit TODOs. Emitted to Git.
CROSS-CUTTING
Audit trail · observability · export
Every action logged. Per-spec audit trail viewable. Specs exportable as PDF, Markdown, JSON. Integration hooks for Project 3 CI/CD pipeline. Telemetry to OpenTelemetry collector.
### 1.3  Out of scope
× NOT IN PRODUCT
Automatic Fortran-to-runtime code conversion
The Harness produces scaffolds and contracts, not running implementations. Method bodies for business logic are stubs annotated with TODOs and references to spec invariants. Engineer implementation completes the work.
× NOT IN PRODUCT
Direct customer or production deployment
The Harness operates on source in a Nous-controlled environment. It does not deploy code, does not push to customer infrastructure, does not write to production systems.
× NOT IN PRODUCT
Multi-customer / multi-tenant operation
v1 is a single-tenant tool for the Advantive engagement. Multi-tenancy, per-customer data isolation, and tenant-scoped RBAC are deferred to a future version after first-customer validation.
× NOT IN PRODUCT
Languages other than Fortran
v1 supports Fortran 77/90/95 dialects representative of Kiwiplan Classic. C, COBOL, RPG, and other legacy languages are out of scope and would require parser and prompt-library extension.
### 1.4  Success criteria
Criterion
Measurement
Target
Spec extraction quality
% of LLM-drafted spec sections accepted by SME without material edit
≥ 70% in v1
SME review velocity
Average time from spec extraction to SME sign-off, per subroutine
≤ 2 hours
Source citation accuracy
% of LLM citations that resolve to correct source line range
≥ 95%
Scaffold compilability
% of generated .NET scaffolds that compile clean (excluding TODO method bodies)
100%
Audit completeness
Every spec must trace to source version, reviewer, and timestamp
100% — gating
Source data residency
No customer Fortran source transmitted to LLM provider with retention rights
100% — gating

SECTION 02
## Users & personas
Three primary personas. RBAC, screen access, and feature gating are derived from this model.

PERSONA 01
Nous Engineer
Role: operates the pipeline end-to-end. Triggers ingestion, parsing, extraction. Routes specs to SMEs. Reviews scaffold output and feeds it into Project 3 build.
Daily volume: 5–10 subroutines through extraction; 2–3 through full review-to-scaffold cycle.
Permissions: full read, full write, can trigger LLM calls, can edit specs in pre-signed state, cannot sign on SME's behalf.
PERSONA 02
Kiwiplan SME
Role: domain reviewer. Examines extracted specs against operational knowledge of RSS. Accepts, edits, or rejects each claim. Provides the sign-off that makes a spec authoritative.
Daily volume: 1–4 subroutines reviewed end-to-end per allocated 4-hour block.
Permissions: read all source and specs, edit specs in review state, sign specs (irrevocable), comment on any artifact, cannot trigger ingestion or scaffold generation.
PERSONA 03
Architect / Observer
Role: read-only oversight. Nous architects, Advantive observers, audit reviewers. Can view all artifacts, history, audit trail; cannot edit anything.
Daily volume: variable. Usually periodic deep-dives during weekly reviews.
Permissions: read everything, comment, export. No write access of any kind.
### 2.1  Authentication & identity
OIDC integration with Nous corporate identity (Microsoft Entra ID) for Nous personas. SMEs invited via federated identity from Kiwiplan's Auckland tenancy where possible; email magic-link for SMEs who cannot federate in v1.
Sessions: 8-hour idle timeout, 24-hour absolute timeout. Refresh tokens rotated on every use.
Sign-off operations require recent (≤5 min) re-authentication via the IdP — a critical-action prompt, not a password re-entry.
All authentication events logged to the audit trail with persona, IP, user-agent, and outcome.
### 2.2  Authorization model
Capability
Engineer
SME
Observer
Trigger source ingestion
✓
—
—
Trigger AST parse
✓
—
—
Trigger spec extraction (LLM call)
✓
—
—
Edit spec in DRAFT state
✓
—
—
Edit spec in REVIEW state
✓
✓
—
Sign spec (irrevocable)
—
✓
—
Trigger scaffold generation
✓
—
—
View specs & history
✓
✓
✓
Comment
✓
✓
✓
Export artifacts
✓
✓
✓
View audit trail
✓
✓
✓
Configure LLM providers / keys
—
—
—

LLM provider configuration is a privileged admin function — not exposed to Engineer, SME, or Observer personas. Admin role is held by 1–2 Nous platform engineers and managed via separate operations console.

SECTION 03
## System architecture
### 3.1  High-level architecture
The Harness is a web application backed by a stateless API service, a relational database, an object store, and a fan-out worker pool that handles long-running operations (parsing, LLM calls, scaffold generation). LLM access is mediated through a provider abstraction with per-stage routing rules.

Layer
Technology
Notes
Frontend
React 18 + TypeScript + Vite. TanStack Query for server state. Monaco Editor for source/code views. Tailwind + shadcn/ui for components.
Single-page app. No SSR required (internal tool).
API
.NET 8 minimal APIs. C#. EF Core for persistence. MediatR for command/query separation. FluentValidation.
Stateless. Behind Azure App Gateway with WAF. Aligns with broader Project 3 stack.
Background workers
Hangfire on .NET 8 for job orchestration. Horizontally scalable workers. Retry policies per job type.
Parsing, LLM calls, scaffold generation are all jobs.
Operational DB
PostgreSQL 16. Logical replication ready. Schema versioned via EF Core migrations.
Sources, ASTs, specs, reviews, sign-offs, audit log.
Object store
Azure Blob Storage with versioning. Immutable containers for signed specs.
Raw Fortran source files, AST artifacts, generated code bundles.
LLM providers
Anthropic API (Claude Sonnet 4) for extraction. Azure OpenAI (GPT-4o) for scaffold. Both configured for zero data retention.
Provider abstraction layer; per-stage routing; deterministic config.
Identity
Microsoft Entra ID (OIDC). Auth.js on the frontend. JWT bearer tokens to the API.
SME federated where possible; magic-link fallback.
Git integration
Octokit (.NET SDK). Service-account credentials. Read-only on source repos; write to scaffold output repos.
Scaffold outputs commit to dedicated repos with tagged releases.
Observability
OpenTelemetry traces, metrics, logs. Azure Monitor as backend. Structured logs (JSON). PII-safe by default.
Per-LLM-call tracing including latency, token usage, cost.
CI/CD
GitHub Actions. Container image to Azure Container Registry. Deploy to AKS via Helm.
Same pipeline pattern as the broader Project 3 build.
### 3.2  Data flow — pipeline overview
Each Fortran subroutine flows through five stages, persisted at every transition. State machine enforces forward-only progression with explicit rollback paths.

Stage
Input
Process
Output
Persistence
1 · Ingest
Git repo URL or file upload
Pull source, validate, hash, version-tag
Source corpus
Blob store + DB metadata
2 · Parse
Raw Fortran files
AST gen via fparser2; structural map; call graph
AST artifact + index
Blob store + DB index
3 · Extract
Source + AST node for one subroutine
LLM call with structured prompt; streamed response; JSON validation
Draft spec (JSON)
DB rows; LLM call audit
4 · Review
Draft spec
SME claim-by-claim review; edits tracked; signature applied
Signed spec
DB rows; sign-off in immutable container
5 · Scaffold
Signed spec
LLM call to generate .NET scaffold; static validation; commit to Git
Scaffold + test fixtures
Git repo + DB linkage
### 3.3  State machine
A subroutine artifact moves through these states. Transitions are auditable; backward transitions are explicitly modeled.

State
Set by
Allowed transitions
UI access
INGESTED
System (post-pull)
→ PARSED (auto)
Read-only view of source
PARSED
System (post-parse)
→ EXTRACTING (engineer-triggered)
Read-only AST view; engineer can trigger extract
EXTRACTING
Engineer
→ DRAFT (success) | → PARSED (failed)
Streaming progress view; cancellable
DRAFT
System (post-extract)
→ IN_REVIEW (engineer routes to SME) | → EXTRACTING (re-extract with revised prompt)
Engineer-editable; not signable
IN_REVIEW
Engineer
→ SIGNED (SME signs) | → DRAFT (SME requests re-extraction) | → IN_REVIEW (SME edits)
SME-editable; engineer comment-only
SIGNED
SME (irrevocable for that source version)
→ SCAFFOLDING (engineer triggers)
Read-only; sign-off bundle viewable
SCAFFOLDING
Engineer
→ SCAFFOLDED (success) | → SIGNED (failed; retry)
Streaming progress view; cancellable
SCAFFOLDED
System (post-scaffold)
→ SCAFFOLDING (re-generate with revised prompt; new artifact)
Scaffold view; download/export
SUPERSEDED
System (when source version updates)
Terminal — historical reference only
Read-only; clearly marked superseded

ARCHITECTURAL PRINCIPLE
Sign-off is irrevocable; supersession is the only forward path
When a spec is signed, that signature is permanent and bound to the exact source version it covers. If the underlying Fortran is later modified, the existing signed spec is marked SUPERSEDED and the engineer must trigger a new extraction → review → sign cycle against the new source version. We never "un-sign" — the audit trail is immutable.

SECTION 04
## Screen-by-screen specification
Each screen is specified with: layout, every interactive element, every state (loading, empty, error, success), data dependencies, and required API calls. Wireframe sketches describe spatial arrangement; component library is shadcn/ui unless noted. All screens are responsive down to 1280px wide; mobile is out of scope (this is an internal engineering tool).

GLOBAL UI CONVENTIONS
Apply to every screen unless explicitly overridden
Top bar with product name, environment badge (DEV/STAGING/PROD), current persona, and identity menu. Left sidebar with primary nav. Breadcrumb under top bar showing source → module → subroutine context. All destructive or irrevocable actions require a confirmation modal with an explicit "I understand" checkbox. Toast notifications for success states; inline error blocks for failure states; never alert() or browser-native dialogs.
### 4.1  Stage 1 — Ingest
Stage 1 screens: source repository configuration, source corpus list, source detail view. The engineer connects a Git repository or uploads files; the Harness pulls, hashes, and versions the source.
#### Screen 1.1  Source Corpus list (landing screen for engineers)
First screen after login for the Engineer persona. Lists all ingested source corpora with their state, size, and last activity. Primary action: connect a new source.

WIREFRAME — SOURCE CORPUS LIST
Header strip  Page title "Source Corpora" + primary CTA button "+ Connect new source" right-aligned.
Filter row  Search input (by corpus name), state filter (chip group: All · Ingesting · Parsed · Indexed · Failed), repository-type filter (Git · Upload).
Corpus card grid  Responsive grid (3 cols at ≥1600px, 2 at ≥1280px). Each card: corpus name, repository URL or upload tag, file count, total LOC, state badge with color, last-activity timestamp, owner avatar.
Empty state  When zero corpora: a centered illustration block with "No source connected yet" heading, one-paragraph explanation, and "+ Connect your first source" CTA.
Loading state  Card-shaped skeleton placeholders. Six skeletons rendered until first data arrives.
Error state  If list fetch fails: full-page error block with retry button, error code, and link to "View status" admin page.
Interactive elements
Element
Type
Behavior
+ Connect new source
Primary button
Opens Screen 1.2 (Connect Source dialog) as a side sheet (right-anchored 600px panel)
Corpus card
Clickable card
Navigates to Screen 1.3 (Source detail) for that corpus. Hover state: 2px navy border, shadow-md elevation.
Search input
Debounced text
300ms debounce; case-insensitive substring match on corpus name; updates list in place. URL query param ?q= for shareable filter state.
State filter chips
Toggle group
Multi-select within chip group. URL query param ?state=. Combines AND with search and repository-type filter.
Repository-type filter
Toggle group
Two values (git, upload). URL query param ?type=.
Card 'kebab' menu (⋮)
Dropdown
Three options: 'View detail' (= card click), 'Re-sync from source' (engineer only; visible if connected via Git), 'Archive corpus' (engineer only; soft-delete with confirmation modal).
Data dependencies
GET /api/v1/corpora — paginated list of corpora visible to the calling persona; returns {id, name, source_type, source_url, file_count, total_loc, state, owner, created_at, updated_at}.
GET /api/v1/corpora/{id}/state — lightweight poll endpoint for corpora in transitional states (Ingesting, Parsing); polls every 2 seconds while card is in transitional state, stops when terminal.
#### Screen 1.2  Connect new source (side sheet)
Side-sheet dialog (right-anchored 600px). Triggered from "+ Connect new source" button. Two tabs: Git (default) and Upload.

WIREFRAME — CONNECT NEW SOURCE (Git tab)
Tab bar  Two tabs: 'Git repository' (default), 'Upload files'. Tab state persisted to URL hash for shareable links.
Form (Git tab)  Repository URL (text, required, validated as Git HTTPS or SSH URI), Branch or tag (text, defaults to 'main'), Display name (text, required, defaults to repo name parsed from URL), Source root path (text, optional, defaults to repository root), Credential selector (dropdown — admin-managed credentials list; engineer cannot enter raw credentials).
Form (Upload tab)  Drag-and-drop zone for .for / .f / .f90 / .inc files or .zip archives up to 100 MB; Display name (text, required); after upload, file list with per-file size and remove (×) action.
Footer  'Cancel' (secondary) closes sheet without action. 'Connect' (primary) submits — disabled until form is valid.
Validation states  Per-field inline error text. Submission errors render as a banner at top of sheet with retry link. Successful connection: sheet closes, toast 'Source connected — ingestion started', new corpus card appears at top of grid in 'Ingesting' state.
Behavior — Git tab submission
Frontend POSTs to POST /api/v1/corpora with {name, source_type: 'git', source_url, branch, source_root, credential_id}.
API enqueues an ingestion job, returns 202 with the new corpus ID and initial state INGESTING. The side sheet closes; the engineer is returned to the corpus list with the new card visible.
Worker pulls the repo via service-account credential, walks the source root, hashes each Fortran file, persists files to blob store with version tag, populates corpus DB rows. On success: state → PARSING (Stage 2 auto-triggered). On failure: state → FAILED with a structured error and retry guidance.
Behavior — Upload tab submission
Files uploaded via multipart POST with progress events. Total upload capped at 100 MB; over-limit rejected client-side before transmission.
On upload completion, same back-end pipeline as Git: hash, persist, version, transition to PARSING.
Edge cases
Repository URL not reachable: error banner "Cannot reach repository — check URL and credentials."
Repository reached but no Fortran files found at source_root: corpus is created but immediately enters EMPTY state with a clear UI message; no parse stage triggered.
File upload exceeds 100 MB: rejected client-side with "Total size exceeds 100 MB; split into multiple corpora."
Duplicate corpus name: server returns 409; UI shows inline error on Display Name field.
#### Screen 1.3  Source detail (corpus view)
Full-screen view of a single corpus. Three-pane layout: left tree of files, center source viewer, right metadata + actions panel.

WIREFRAME — SOURCE DETAIL
Top bar  Breadcrumb 'Source corpora › {corpus name}'. Right side: state badge, repository link icon, '⟳ Re-sync' button (engineer, Git-connected corpora only).
Left pane (300px)  File tree. Folders collapsible. Each file shows icon (.for/.f90/.inc differentiated) and a small badge indicating count of subroutines parsed (after Stage 2). Search box at top filters tree.
Center pane (flexible)  Monaco Editor in read-only mode rendering selected file with Fortran syntax highlighting (custom Monaco language). Line numbers visible. Subroutine boundaries highlighted with subtle background tint. Click on a subroutine line jumps to Screen 2.1 for that subroutine (after Stage 2).
Right pane (340px)  Metadata: file path, size, line count, hash, ingested timestamp, source version. Below: 'Subroutines in this file' list (after Stage 2 — see Screen 2.1). At bottom: action buttons appropriate to corpus state.
Loading state  Tree and editor render skeletons while content loads. Editor reserves space (no layout shift).
Failed state  If parse failed for the corpus, banner across center pane: 'Parse failed for this corpus — view error' with link to error detail and retry CTA.
Interactive elements
Element
Behavior
File tree node click
Loads file content into center editor; updates URL with ?file= param.
Subroutine highlight click (in editor)
Navigates to Screen 2.1 — Subroutine detail. Available only when corpus state ≥ PARSED.
Re-sync button
Engineer-only. Triggers a re-pull from Git; opens confirmation modal: "This will create a new source version. Existing signed specs against the previous version will be marked SUPERSEDED. Continue?"
Right-pane action buttons
Vary by corpus state: INGESTING shows progress; PARSING shows progress with cancel; PARSED shows "Trigger AST parse" (if not already); EMPTY shows guidance to add files.

### 4.2  Stage 2 — Parse & browse
Stage 2 screens surface the parsed AST and structural index: subroutine list, subroutine detail, call graph view. The engineer drills from corpus to subroutine and triggers Stage 3 extraction.
#### Screen 2.1  Subroutine detail
The pivotal navigation screen. Shows one subroutine in isolation with everything the engineer needs to decide whether to extract its spec, and to launch extraction.

WIREFRAME — SUBROUTINE DETAIL
Top bar  Breadcrumb 'Source corpora › {corpus} › {file} › {subroutine}'. Right side: state badge for this subroutine (INGESTED/PARSED/DRAFT/IN_REVIEW/SIGNED/...), action button group.
Tabs row  Source · Structure · Call graph · Spec · Scaffold. Tabs become enabled as the subroutine progresses through the pipeline. Disabled tabs render greyed with a tooltip: "Available after Stage 3 extraction."
Source tab (default)  Monaco read-only with the subroutine source highlighted. Lines outside the subroutine are still visible but dimmed, so the engineer can see surrounding context (INCLUDE files, COMMON block declarations). Subroutine header pinned at top of viewport when scrolling within long subroutines.
Structure tab  Auto-generated tabular view from AST: Inputs (parameter list with INTENT and TYPE), Outputs (parameters with INTENT(OUT/INOUT)), COMMON block references, Called subroutines (list with click-through), File I/O patterns detected (READ/WRITE/REWRITE on which logical units), Magic numbers detected (PARAMETER constants and inline literals). All clickable to source location.
Call graph tab  Interactive graph (uses ReactFlow): the current subroutine as the center node. Edges out to subroutines it calls; edges in from subroutines that call it (within this corpus). Click any node to navigate to its detail screen. Useful for SMEs to assess context before review.
Spec tab  Visible only when state ≥ DRAFT. Renders Screen 3.x — see Stage 3 section.
Scaffold tab  Visible only when state ≥ SCAFFOLDED. Renders Screen 5.x — see Stage 5 section.
Action button group (top right)  Primary CTA varies by state: PARSED → 'Extract spec' (orange, primary); DRAFT → 'Route to SME for review' (engineer); IN_REVIEW → 'View review' (links to Screen 4.x); SIGNED → 'Generate scaffold' (engineer); SCAFFOLDED → 'Open scaffold' (links to Screen 5.x). Secondary kebab menu always available with: 'Re-extract', 'View audit trail', 'Export', 'Add comment'.
Worked example — the running subroutine throughout this spec
All subsequent screen examples reference one synthetic Fortran subroutine — CONSUME_ROLL — included as the canonical example. It is representative of Kiwiplan Classic style: COMMON block dependencies, ISAM I/O via callouts, magic-number constants, multi-branch control flow with side effects. Source listed below.

      SUBROUTINE CONSUME_ROLL(ROLL_ID, USED_LF, OPER_ID, RESULT_CD)
C     ------------------------------------------------------------------
C     CONSUME_ROLL — Posts a roll consumption event from the wet end.
C     Decrements on-hand linear footage, updates roll status,
C     and emits the CSC inventory-changed notification.
C
C     PARAMS:
C       ROLL_ID    Unique roll identifier (CHAR*12)
C       USED_LF    Linear feet consumed in this event (REAL)
C       OPER_ID    Operator ID for audit (CHAR*8)
C       RESULT_CD  Out: 0=ok, 1=not_found, 2=insufficient, 3=locked
C     ------------------------------------------------------------------
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      CHARACTER*8  OPER_ID
      REAL         USED_LF
      INTEGER      RESULT_CD

      INCLUDE 'RSCOMMN.INC'
      INCLUDE 'CSCMSG.INC'

      REAL         ON_HAND_LF, NEW_LF, MIN_REMAIN
      INTEGER      ROLL_STATUS, IO_STAT
      CHARACTER*4  GRADE_CD
      LOGICAL      LOCKED

      PARAMETER (MIN_REMAIN = 12.0)

C     Read the current roll record from RSMASTR (ISAM keyed on ROLL_ID)
      CALL RS_READ(ROLL_ID, ON_HAND_LF, ROLL_STATUS, GRADE_CD,
     &             LOCKED, IO_STAT)
      IF (IO_STAT .NE. 0) THEN
         RESULT_CD = 1
         RETURN
      END IF

      IF (LOCKED) THEN
         RESULT_CD = 3
         RETURN
      END IF

      IF (USED_LF .GT. ON_HAND_LF) THEN
         RESULT_CD = 2
         RETURN
      END IF

      NEW_LF = ON_HAND_LF - USED_LF

C     If remaining stock is below threshold, mark roll as DEPLETED
      IF (NEW_LF .LT. MIN_REMAIN) THEN
         ROLL_STATUS = 9
      END IF

C     Persist update via ISAM rewrite
      CALL RS_WRITE(ROLL_ID, NEW_LF, ROLL_STATUS, OPER_ID, IO_STAT)
      IF (IO_STAT .NE. 0) THEN
         RESULT_CD = 1
         RETURN
      END IF

C     Notify the corrugator scheduling channel (CSC)
      CALL CSC_NOTIFY('INV_CHG', ROLL_ID, GRADE_CD, NEW_LF)

      RESULT_CD = 0
      RETURN
      END

#### Screen 2.2  Subroutine list (within corpus)
A table view of every subroutine in a corpus. Engineers use this to triage which subroutines to extract first based on size, dependency depth, and current pipeline state.

WIREFRAME — SUBROUTINE LIST
Header  Page title 'Subroutines in {corpus}'. Right side: counts strip — Total · Pending extract · In review · Signed · Scaffolded — each clickable as a state filter.
Filter row  Search by subroutine name. Filters: state, file, has-COMMON-block, has-ISAM-I/O, has-callouts, LOC range slider.
Data table  Columns: Name · File · Lines · State (badge) · Dependencies (count of called subs) · Has I/O · Last updated · Owner · Actions (kebab). Sortable by every column. Multi-select with bulk actions: 'Extract specs (batch)' for engineer; 'Assign reviewer' for engineer; 'Export selected' for any persona.
Bulk-action behavior  When ≥1 row selected, a sticky action bar appears at the bottom of the screen with selected count and applicable bulk operations. Bulk extraction submits each subroutine as an individual Stage 3 job; the engineer is shown a summary toast and the table updates as jobs complete.

### 4.3  Stage 3 — Extract (LLM-assisted spec drafting)
The most consequential stage. The engineer triggers extraction; an LLM produces a structured behavioral spec for the subroutine; the spec is persisted in DRAFT state. Two screens: the live extraction view (during the LLM call) and the draft spec view (after).
#### Screen 3.1  Live extraction view
Modal-style overlay (or full screen on first extraction) shown from the moment the engineer clicks 'Extract spec' until the LLM response completes or fails. Streams response in real time.

WIREFRAME — LIVE EXTRACTION
Header  Subroutine name + 'Extracting spec…' label with animated stage indicator (1 of 5: priming context · 2: loading source · 3: streaming response · 4: validating · 5: persisting). Right side: 'Cancel extraction' button (allowed during stages 1-3, disabled during 4-5).
Provider context strip  Shows: provider (Anthropic), model (claude-sonnet-4-...), prompt template ID and version (e.g. 'fortran-extract-v3.2'), token budget (input + output ceiling). Subtle, monospace, low-emphasis — for engineer transparency, not center-stage.
Streaming response panel (left, ~60% width)  Renders LLM tokens as they arrive. Markdown-aware so headings appear immediately. Below the panel: live token-count and elapsed-time meter.
Source reference panel (right, ~40% width)  Source code with auto-scroll to lines being cited as the LLM streams. Cited line ranges highlight in the source as their citations appear in the response. This visual binding is the centerpiece of the screen — the audience sees the LLM's citations land on the actual source lines.
Footer  On success: 'View draft spec' (primary, navigates to Screen 3.2) and 'Extract again with revised prompt' (secondary). On failure: error block with retry, 'View raw response', and a link to provider-status page.
Streaming behavior
Server-Sent Events (SSE) endpoint streams tokens from the LLM provider through the API to the frontend. Each token rendered with a faint typing-cursor animation that lags one token behind.
As the LLM emits a citation block (specific schema: {"cite": {"lines": "34-37"}}), the frontend captures it, scrolls the source panel to that line range, and applies a 1.5-second highlight pulse before settling to a persistent highlight.
If the streamed JSON parses as malformed at any point, the validating phase will report it. The engineer can choose 'View raw response' to see what the LLM actually emitted before validation.
Cancellation
Cancel button is enabled until the API begins persistence. Clicking it sends an abort signal to the SSE connection and a backend command that cancels the upstream provider call (where supported) or marks the call as cancelled in the audit log.
A cancelled extraction does not produce a spec; the subroutine returns to PARSED state. The cancellation is logged with timestamp and engineer ID.
Failure handling
Failure mode
User-facing message
Backend action
Provider rate-limited
"Provider returned rate-limit error. Retry in {retry-after} seconds."
Job marked retryable; engineer can retry; auto-retry once after retry-after suggestion.
Provider 5xx
"Provider returned a server error — the call did not complete. Retry, or switch to fallback provider."
Job marked retryable; engineer can retry; admin can route the prompt template to the fallback provider for this corpus.
Provider returned malformed JSON
"Response did not match expected schema. View raw response or retry."
Spec is not persisted; raw response stored in audit log; retry available.
Token budget exceeded
"Subroutine source exceeds the prompt token budget. Try splitting at COMMON-block boundaries, or contact admin to raise the budget for this corpus."
No call made; subroutine remains in PARSED.
Network/SSE drop
"Connection dropped mid-extraction. The call may still have completed — refresh to check status."
Backend continues processing; spec persists if backend completes; engineer reload checks final state.

#### Screen 3.2  Draft spec (engineer view, pre-review)
After successful extraction. The engineer reviews the LLM-drafted spec, can make corrections, and routes it to an SME for review. This screen is also visible to SMEs once the engineer routes to review (read-only for them) and to Observers (read-only).

WIREFRAME — DRAFT SPEC
Top bar  Breadcrumb. State badge: DRAFT (orange). Action buttons: 'Edit' (engineer, in DRAFT only), 'Re-extract', 'Route to SME for review' (engineer, primary), kebab menu with Export / Audit trail / Comment.
Two-pane layout  Left pane (~55%): the spec, rendered from the structured JSON. Right pane (~45%): source code, with two-way binding — clicking on a citation in the spec scrolls the source pane; clicking on a source line opens a 'Cited in:' tooltip listing every spec section that references it.
Spec sections in left pane  Rendered in this fixed order: Summary · Inputs · Outputs · Invariants · Side effects · Edge cases · Open questions. Each section is collapsible. Each individual claim within Invariants/Side effects/Edge cases has its own card with the claim text, cited source line range, and confidence indicator.
Citation widget  Every claim has an inline 'L34-37' chip linking to source. Click highlights those lines in the right pane and pulses for 1.5 sec. Hover shows a popover with the cited code excerpt.
Confidence indicator  Each claim has a low/medium/high confidence pill. LLM emits this; engineer can override during DRAFT edit. SME confirms or changes during REVIEW.
Open questions section  Distinct visual treatment — yellow background, slightly indented. Each question is a callout for SME attention; the SME is expected to either resolve ("yes/no/clarification") or edit the spec to make the question moot before signing.
Footer (engineer DRAFT mode)  'Save changes' (visible while editing). 'Route to SME for review' opens a side sheet to select reviewer(s) and add a routing note.
Edit interactions (engineer, DRAFT state)
Each claim card has an inline edit affordance (small pencil icon). Clicking makes the claim text editable in place; the citation chip remains and can be edited (engineer can update the line range to a more accurate citation).
Engineer can add or remove claims within Invariants, Side effects, Edge cases, and Open questions sections. Add control appears as a dashed-border 'New claim' card at the bottom of each section.
Engineer cannot change the subroutine name, source citation, or hash — these are immutable identifiers.
Every edit produces a delta in the spec audit log: timestamp, engineer ID, before/after diff. Visible in the audit trail view.
Routing to SME
Side sheet on 'Route to SME for review'. Lists available SMEs (with their current load: count of in-flight reviews). Engineer selects one or more SMEs; if multiple, signing requires unanimous sign-off.
Routing note: a free-text field for the engineer to highlight specific concerns or open questions for the SME's attention. Note is preserved in the spec record and surfaced at the top of the SME review view.
On submit: state transitions DRAFT → IN_REVIEW. Engineer can no longer edit; SME(s) can. Engineer can comment but not edit. Email and in-app notification dispatched to selected SME(s).

### 4.4  Stage 4 — Review (SME workflow)
The most critical workflow in the product. SMEs review every claim in the LLM-drafted spec, accept/edit/reject each one, resolve open questions, and ultimately sign — making the spec the authoritative contract for the modern build.
#### Screen 4.1  My reviews (SME landing screen)
Default screen for the SME persona on login. Lists every spec routed to them, grouped by status. Optimized for the SME's working pattern: "What do I need to look at, and how long will each take?"

WIREFRAME — MY REVIEWS
Header  Page title 'My reviews' + counts badge: '3 awaiting review · 1 in progress · 17 signed'.
Awaiting review group (top)  Cards stacked vertically. Each card: subroutine name, source corpus and file, routing note from engineer, claim count breakdown (e.g. '7 invariants · 3 edge cases · 2 open questions'), estimated review time (calculated from claim count + complexity heuristic), routed-by engineer, routed-on date, primary CTA 'Begin review'.
In progress group (middle)  Reviews the SME has started but not signed. Same card layout, plus a progress bar showing % of claims processed. CTA: 'Resume'.
Signed group (bottom, collapsed by default)  Click to expand. Recent sign-offs by this SME, most recent first. Read-only. Useful as a personal record.
Empty state (no awaiting reviews)  Friendly message: 'No reviews awaiting your attention. Last sign-off: {time ago}.' With a link to view all signed specs.
#### Screen 4.2  Spec review (the SME working surface)
This is where the SME spends most of their time in the product. The screen is engineered to make claim-by-claim review fast, contextual, and traceable. It is a deliberate adaptation of the Draft Spec view (Screen 3.2) with claim-level interaction primitives added.

WIREFRAME — SPEC REVIEW (SME)
Top bar  Breadcrumb. State badge: IN_REVIEW (green). Right side: progress strip showing 'X of Y claims processed', and primary CTA 'Sign spec' (disabled until every claim is processed AND every open question is resolved).
Routing note callout  If engineer left a routing note, it appears as a callout strip across the top of the content area, dismissible. Shows engineer name, timestamp, note text.
Three-pane layout  Left (320px): outline + jump nav. Center (flexible): claim cards. Right (380px): source code with citation binding.
Outline pane (left)  Section list (Summary · Inputs · Outputs · Invariants · Side effects · Edge cases · Open questions). Each section shows a small status dot per claim: grey (untouched), green (accepted), amber (edited), red (rejected), purple (open question pending). Click any claim to scroll-jump to it. Allows the SME to see overall progress at a glance.
Claim card (center, repeated for each claim)  Header: claim ID (INV-1, EC-2, etc.), one-line title, action chips. Body: full claim text. Footer: citation chip, confidence indicator, comment count, action buttons: '✓ Accept' / '✎ Edit' / '✗ Reject' / '? Question'.
Source pane (right)  Same as Screen 3.2 but with two-way navigation and citation pulse on hover. Sticky to viewport so the SME never loses code context.
Claim-card interaction model
Accept — marks the claim as accepted as drafted. One-click; no further input. Card collapses to a compact accepted state showing claim title and accepted-by stamp.
Edit — makes claim text and citation editable inline. Save commits the edit; cancel discards. Edited claims display the edit history (original text + each edit) when expanded. The diff is preserved in the audit trail.
Reject — marks the claim as not part of the spec. Requires a rejection reason (free-text, required, ≥ 20 chars). Rejected claims remain visible (struck through) so reviewers can see what the LLM proposed and why it was excluded.
Question — escalates the claim back to the engineer or another SME for clarification, without committing accept/edit/reject. Adds a comment thread. The claim is marked PENDING-QUESTION and does not block sign-off only after the question is resolved (answered + claim re-processed).
Open questions handling
The 'Open questions' section is special. Unlike Invariants, Side effects, and Edge cases — which represent claims about behavior — open questions represent the LLM's flagged uncertainties. The SME must resolve every open question before signing.
Resolve options per question: 'Answer in spec' (SME edits an existing claim or adds a new claim that addresses the question), 'Mark not applicable' (with required justification), 'Defer' (with required follow-up ticket reference).
Open questions also become a deliverable artifact in the export — engineering teams downstream see them as explicit known-unknowns.
Sign-off

CRITICAL ACTION — IRREVOCABLE
Signing makes the spec the authoritative contract for the modern build
Sign-off is permanent and bound to the source version. The Harness will not allow un-signing. If new information surfaces, the path is supersession (extract a new spec against the same or updated source, sign that one) — never reversal of an existing signature.

Sign-off CTA enables only when: (a) every Invariant, Side effect, and Edge case claim is in Accepted/Edited/Rejected state — none Untouched; (b) every Open question is resolved; (c) SME has performed re-authentication within the last 5 minutes.
Clicking 'Sign spec' opens a confirmation modal: shows full spec summary, citation-source binding integrity check (system verifies every citation still resolves to valid source line ranges), and explicit "I have reviewed every claim and confirm this spec is accurate to the source as of version {version}" checkbox + signed-name display.
On confirm: backend creates an immutable signed-spec record in a versioned blob container, generates a cryptographic signature (SHA-256 hash of canonicalized spec JSON, signed with a per-environment HSM-backed key), persists the signature, transitions state IN_REVIEW → SIGNED, dispatches notification to the engineer who routed it.
After sign-off: spec view enters SIGNED display mode — all interaction is read-only, signature block is visible at top, audit trail link is prominent. Engineer's next available action is 'Generate scaffold' (Stage 5).
#### Screen 4.3  Audit trail (any persona)
Reachable from any spec via 'View audit trail' menu item. Read-only chronological log of every event affecting the spec.

WIREFRAME — AUDIT TRAIL
Header  Subroutine name + 'Audit trail'. Timeline range filter (last 7 days · last 30 days · all). Export button (PDF or JSON).
Timeline (vertical)  Each event is a card on a vertical timeline. Cards grouped by date with date dividers. Each card: timestamp, actor (persona + name), action verb, before/after diff (if applicable), structured payload (collapsible).
Event types rendered  Source ingested · AST parsed · Spec extracted (with LLM call metadata: provider, model, prompt template ID and version, input tokens, output tokens, latency, cost) · Spec edited · Routed to review · Claim accepted/edited/rejected · Question opened · Question resolved · Spec signed · Scaffold generated · Comment added.
Filter row  Filter by event type (multi-select), actor (multi-select), and free-text search across event payload.
Export  PDF export produces a print-ready audit trail document with a header containing spec name, source version, signature block, and the full event log. JSON export produces the raw event stream for offline analysis.

### 4.5  Stage 5 — Scaffold (target-platform code generation)
Once a spec is signed, the engineer can trigger scaffold generation. A second LLM call produces a target-platform code package: .NET service skeleton, DTOs, repository interfaces, integration adapters, and unit-test fixtures derived from the signed invariants. Method bodies for business logic are explicitly stubbed; this is not running implementation.

WHAT THE SCAFFOLD IS, AND IS NOT
AI-drafted skeleton + test fixtures. Engineer-completed business logic.
Generated code includes: class structure, public method signatures, DTOs from inputs/outputs, repository interface definitions, integration adapter stubs (e.g., RS_READ becomes IRollRepository.GetById), and one unit-test fixture per invariant. What it does NOT include: working implementation of business logic. Method bodies that contain real logic are stubbed with `throw new NotImplementedException("See spec invariant INV-X");` and an annotated TODO referencing the relevant invariants. The engineer completes implementation; the scaffold ensures the public surface area matches the signed spec.
#### Screen 5.1  Scaffold generation (live)
Same pattern as Screen 3.1 (live extraction): a streaming overlay during the LLM call. Different content panes.

WIREFRAME — LIVE SCAFFOLD GENERATION
Header  Subroutine name + 'Generating scaffold…'. Stage indicator (1: priming · 2: streaming code · 3: validating · 4: committing to Git). Cancel button enabled during stages 1-2.
Provider context strip  Provider (Azure OpenAI), model (gpt-4o-...), prompt template ID, target platform config (.NET 8 default; visible to engineer).
Streaming code panel (left)  Files appear as tabs as the LLM emits each one (Service.cs, DTOs.cs, IRollRepository.cs, RollRepositoryAdapter.cs, ConsumeRollTests.cs). Code streamed with C# syntax highlighting. Each file's TODO/throw-stubs are visually distinct (yellow gutter strip).
Spec reference panel (right)  The signed spec, with two-way binding to the streaming code: as a method or test is being generated, the spec invariant or input it derives from is highlighted.
Footer  On success: 'View scaffold' (primary, Screen 5.2) and 'Re-generate with revised prompt' (secondary). On failure: same error pattern as Stage 3.
#### Screen 5.2  Scaffold artifact view
After successful generation. Shows the generated code package with full traceability back to the signed spec. Engineer can review, request regeneration, and trigger commit-to-Git. Read-only for SMEs and Observers.

WIREFRAME — SCAFFOLD ARTIFACT
Top bar  Breadcrumb. State badge: SCAFFOLDED (amber). Action buttons: 'Commit to Git' (engineer, if not yet committed), 'Re-generate' (engineer), 'Download .zip', 'View commit' (engineer, if committed).
Three-pane layout  Left (220px): file tree of generated package. Center (flexible): selected file in Monaco editor with C# syntax highlighting. Right (340px): traceability panel.
Generated file tree (left)  Default structure: /src — ConsumeRollService.cs, ConsumeRollDtos.cs, IRollRepository.cs, RollRepositoryAdapter.cs, ICscNotifier.cs; /tests — ConsumeRollServiceTests.cs (one fixture per invariant), ConsumeRollEdgeCaseTests.cs; /spec — bundled signed spec PDF + JSON for engineer reference.
Code editor (center)  Read-only Monaco. TODO comments and NotImplementedException stubs are syntax-highlighted with a distinct gutter marker (orange triangle). Hovering the marker shows the linked spec invariant text.
Traceability panel (right)  For the currently-selected file/method: 'Derived from' section listing the signed-spec elements that mapped into this code. Click an entry to scroll to that location in the spec (opens spec in a separate tab).
Commit-to-Git flow  Confirmation modal: 'Commit to {scaffold-output-repo} on branch scaffold/{subroutine-name}?' Pre-fills branch name and commit message; engineer can edit message. On submit: backend pushes the commit, returns commit URL, modal closes, banner shows 'Committed at {commit hash} → view'.
Generated code structure (canonical for the worked example)
For the CONSUME_ROLL example, the generated package looks like this. Every file is committed verbatim — engineers receive the same structure every time.

// ConsumeRollService.cs
public class ConsumeRollService : IConsumeRollService
{
    private readonly IRollRepository _rolls;
    private readonly ICscNotifier _csc;

    public ConsumeRollService(IRollRepository rolls, ICscNotifier csc)
    {
        _rolls = rolls;
        _csc = csc;
    }

    public async Task<ConsumeRollResult> ConsumeAsync(
        string rollId, decimal usedLf, string operatorId)
    {
        // TODO: implement per signed spec
        // - INV-1: USED_LF must not exceed ON_HAND_LF
        // - INV-2: locked rolls return Locked status without modification
        // - INV-3: NEW_LF below MIN_REMAIN sets status to Depleted
        // - INV-5: successful consumption emits CSC notification
        throw new NotImplementedException(
            "See spec invariants INV-1..5 — engineer implementation required");
    }
}
Generated tests (canonical for the worked example)
Test fixtures generated per invariant. xUnit + Moq pattern. Each fixture is named after its source invariant for traceability.

// ConsumeRollServiceTests.cs
public class ConsumeRollServiceTests
{
    [Fact] // INV-1: USED_LF > ON_HAND_LF returns Insufficient
    public async Task Consume_WhenUsedExceedsOnHand_ReturnsInsufficient()
    {
        var roll = new Roll { Id = "R001", OnHandLf = 50m, Locked = false };
        var repo = Mock.Of<IRollRepository>(r =>
            r.GetByIdAsync("R001") == Task.FromResult(roll));
        var sut = new ConsumeRollService(repo, Mock.Of<ICscNotifier>());

        var result = await sut.ConsumeAsync("R001", 100m, "OP01");

        Assert.Equal(ConsumeRollResult.Insufficient, result);
    }

    [Fact] // INV-2: locked rolls return Locked
    public async Task Consume_WhenRollLocked_ReturnsLocked() { /* ... */ }

    [Fact] // INV-3: NEW_LF below MIN_REMAIN sets Depleted status
    public async Task Consume_WhenRemainingBelowThreshold_SetsDepleted() { /* ... */ }
}

PROJECT 3 INTEGRATION CONTRACT
Scaffolds commit to a designated repo, tagged with spec version
Every scaffold commits to the project's scaffold-output Git repository on a branch named scaffold/{subroutine}-{specVersion}. The commit message includes the signed-spec ID, source version hash, and Harness run ID. Project 3's main build pipeline picks up these branches as upstream sources. This is the integration handoff — once committed, the scaffold lives in the broader Project 3 repository and is no longer the Harness's concern.

SECTION 05
## Data model & persistence
PostgreSQL 16 as the operational database. EF Core for the data access layer; migrations are part of the deployment artifact. Every entity below has created_at, updated_at, and a soft_deleted_at nullable column. Audit-log entries are append-only — no soft-delete on audit rows.
### 5.1  Core entities

Entity
Purpose
Key fields
Corpus
Top-level container for a connected source (Git repo or file upload).
id (uuid, pk), name, source_type (git|upload), source_url, branch, source_root, credential_id (fk), state (enum), file_count, total_loc, owner_id (fk), latest_version_id (fk)
SourceVersion
A point-in-time snapshot of a corpus. Immutable.
id, corpus_id (fk), git_commit_hash, ingested_at, ingested_by, file_manifest_blob_uri (immutable container)
SourceFile
A single Fortran file within a SourceVersion.
id, source_version_id (fk), relative_path, file_hash (sha256), line_count, blob_uri
Subroutine
Parsed subroutine identified by AST. Belongs to a SourceFile.
id, source_file_id (fk), name, signature (text), line_start, line_end, common_block_refs (jsonb), called_subroutines (jsonb), io_patterns (jsonb), parsed_ast_blob_uri
Spec
A behavioral specification for one Subroutine in one SourceVersion.
id, subroutine_id (fk), source_version_id (fk), state (enum), spec_json (jsonb), llm_call_id (fk), created_by, created_at
SpecRevision
Each edit to a Spec produces a new revision. Append-only.
id, spec_id (fk), revision_number, spec_json_diff (jsonb), edited_by, edited_at
ClaimReview
Per-claim accept/edit/reject decisions during SME review.
id, spec_id (fk), claim_path (jsonpath), action (accept|edit|reject|question), reason (text, required for reject), edited_text (text, for edit), reviewer_id, reviewed_at
Signature
Immutable sign-off record.
id, spec_id (fk), signer_id, signed_at, source_version_hash, spec_canonical_hash, signature_bytes (bytea, HSM-signed), signature_key_id
Scaffold
Generated code package for a signed Spec.
id, spec_id (fk), state (enum), llm_call_id (fk), git_branch, git_commit_hash, package_blob_uri, generated_by, generated_at
LlmCall
Audit record of every LLM invocation.
id, provider (anthropic|azure_openai), model, prompt_template_id, prompt_template_version, input_tokens, output_tokens, latency_ms, cost_usd, request_blob_uri, response_blob_uri, status, error_code, called_by, called_at
AuditEvent
Append-only event log across all entities.
id, event_type, actor_id, target_type, target_id, payload (jsonb), occurred_at, ip_address, user_agent
Comment
Threaded comments on Specs and Claims.
id, target_type (spec|claim), target_id, parent_comment_id (nullable, for threading), body, author_id, created_at
User
Authenticated identity.
id, email, persona (engineer|sme|observer|admin), display_name, idp_subject, last_login_at
Credential
Service-account credential for source access. Admin-managed only.
id, name, scope (git_read|git_write), encrypted_secret, created_at
### 5.2  Spec JSON schema (canonical)
The spec_json column on the Spec entity holds a structured document conforming to this schema. Every claim has a stable claim_path used for ClaimReview targeting and for citation/diff resolution.

{
  "$schema": "https://nous.dev/schemas/re-harness/spec/v1.json",
  "routine": "CONSUME_ROLL",
  "source_path": "RSS/SRC/CONSUME_ROLL.FOR",
  "source_lines": "1-66",
  "source_hash": "sha256:e3a2f9c1b7d4...",
  "summary": "Posts a roll consumption event...",
  "inputs": [
    {
      "id": "in.ROLL_ID",
      "name": "ROLL_ID",
      "type": "CHAR(12)",
      "semantic": "Unique roll identifier",
      "citations": [{ "lines": "1, 12" }]
    }
    /* ... */
  ],
  "outputs": [ /* same shape as inputs */ ],
  "invariants": [
    {
      "id": "INV-1",
      "claim": "Linear feet must never go negative...",
      "citations": [{ "lines": "39-42" }],
      "confidence": "high"
    }
    /* ... */
  ],
  "side_effects": [ /* { description, citations } */ ],
  "edge_cases": [ /* { description, citations, behavior, confidence } */ ],
  "open_questions": [
    {
      "id": "Q-1",
      "question": "Source does not validate USED_LF >= 0...",
      "status": "unresolved"
    }
  ],
  "metadata": {
    "extracted_by_llm_call_id": "...",
    "extracted_at": "2026-05-15T10:23:45Z",
    "prompt_template": "fortran-extract-v3.2"
  }
}
Claim paths
Each claim is addressable via JSONPath, e.g. $.invariants[?(@.id=='INV-1')]. The frontend uses these paths as React keys; the backend uses them as the target for ClaimReview rows.
When a claim is edited, the original is preserved in spec_json_diff on a new SpecRevision row. The Spec entity's spec_json always reflects the current state; the diff chain reconstructs history.
### 5.3  Signing & immutability
Signed specs require strong tamper evidence. The mechanism:
On sign-off, the spec_json is canonicalized (RFC 8785 JSON Canonicalization Scheme) and SHA-256 hashed.
The hash is signed with an HSM-backed key per environment (Azure Key Vault Managed HSM). Signature, hash, key ID, and source_version_hash are persisted on the Signature row.
The signed spec_json is also written to an immutable blob container (write-once-read-many policy via Azure Blob Storage immutability) for tamper evidence at rest.
Verification: any consumer can fetch the signed-spec blob, recompute the hash, and verify the signature against the public key for that environment. CI pipelines that consume scaffolds verify before building.

SECTION 06
## LLM integration architecture
The Harness invokes LLMs at two stages: Stage 3 (spec extraction) and Stage 5 (scaffold generation). Both are mediated by a provider abstraction with strict observability and safety controls. Per-stage routing rules select provider, model, and prompt template.
### 6.1  Provider abstraction

Concern
Decision
Provider interface
ILlmProvider with InvokeAsync(LlmRequest) → IAsyncEnumerable<LlmResponseChunk>. All callers consume the streaming interface; non-streaming is layered on top.
Stage-3 default
Anthropic Claude Sonnet 4 (or current strongest reasoning model). Configured for zero data retention. Structured-output mode where supported; otherwise schema-guided prompting + post-validation.
Stage-5 default
Azure OpenAI GPT-4o (in a tenant with no-training, no-retention configuration). Better for verbose code generation; lower latency for boilerplate.
Failover
Each prompt template declares a primary and fallback provider/model. Admin can enable failover globally or per-corpus. Failover triggers on 5xx, rate-limit, or timeout (>120s).
Per-stage routing
Routing rules expressed as: { stage, prompt_template_id, provider, model, parameters }. Persisted in DB; admin-mutable; engineer can see the resolved rule on the live extraction screen.
Token budgets
Per prompt template: max_input_tokens, max_output_tokens, max_total_cost_per_call. Calls exceeding budget at request time are rejected with a clear error. Budgets editable per template.
Cost telemetry
Every call records input_tokens, output_tokens, cost_usd. Admin dashboard shows daily/weekly cost rollups by stage, provider, corpus, and engineer. Hard cap per environment per day; on cap-hit, all LLM calls are rejected and admin notified.
### 6.2  Prompt templates
Prompts are versioned artifacts in the codebase, loaded at deploy time. Templates are not editable through the UI in v1 — changes go through code review. Each template has an ID, version, target stage, and required input variables.
#### 6.2.1  Stage-3 extraction prompt — fortran-extract-v3.2

# fortran-extract-v3.2.md
# Target stage: extract
# Inputs: subroutine_source, common_block_definitions, structural_index
# Output schema: spec/v1.json

## SYSTEM

You are a senior systems engineer with deep experience in Fortran 77/90,
ISAM-based data systems, and roll-stock manufacturing software. You are
extracting a behavioral specification from a Fortran subroutine that will
be used as the contract for a re-implementation in modern C#.

Rules:
1. Cite specific source line numbers for every claim. Use the format
   {"cite": {"lines": "<start>-<end>"}} inline.
2. Distinguish between behavior the source IMPLEMENTS and behavior the
   source ASSUMES. Mark assumptions as open questions.
3. Magic numbers and hardcoded constants must be flagged as open
   questions if their meaning is not clear from context.
4. Do not infer business intent. If you cannot ground a claim in source,
   raise it as an open question.
5. Output must conform to the spec/v1 JSON schema.

## USER

Subroutine source:
```fortran
{{ subroutine_source }}
```

Relevant COMMON block definitions (from INCLUDE files):
```fortran
{{ common_block_definitions }}
```

Structural context (auto-extracted from AST):
```json
{{ structural_index }}
```

Produce the behavioral specification as a JSON document conforming to
spec/v1. Be specific. Cite. Question what cannot be confirmed.
Output validation
Response must be valid JSON. Streaming parser surfaces parse errors as soon as the response deviates.
Validated against spec/v1 JSON Schema before persistence. Schema-invalid responses are not persisted as Specs; raw response is preserved in audit trail; engineer is offered retry.
Post-validation: every cited line range is verified against the actual source. Citations referencing non-existent line ranges produce a warning on the draft spec view but do not block routing — the SME sees the warning and resolves during review.
#### 6.2.2  Stage-5 scaffold prompt — dotnet-scaffold-v2.0
Inputs: signed spec_json (canonical form), target platform config (.NET version, project layout). Output: structured response containing one entry per output file with path and content. Same validation pattern: JSON-structured, schema-validated, persisted to blob + Git.
### 6.3  Zero data retention

DATA RESIDENCY — GATING REQUIREMENT
Customer Fortran source is never retained by LLM providers
Anthropic API: zero-retention configuration via the dedicated enterprise endpoint with the no-training, no-retention header set on every request. Azure OpenAI: tenant deployed in a configuration with abuse-monitoring disabled and no human-review path. Both providers: contractual no-training, no-fine-tuning agreements as part of MSA. Verification: admin dashboard shows the active provider configuration and the most recent provider audit-letter timestamp.
### 6.4  Observability
Every LLM call produces an OpenTelemetry span with attributes: llm.provider, llm.model, llm.prompt_template_id, llm.prompt_template_version, llm.input_tokens, llm.output_tokens, llm.latency_ms, llm.cost_usd, llm.status, llm.user_id, llm.subroutine_id.
Request and response payloads (sanitized — no raw source code) persist to blob storage with 30-day retention; full request/response (including source) persists to a separate restricted-access blob container with 7-day retention for failure-debugging only.
Latency, cost, and error-rate dashboards in Azure Monitor. Alert thresholds: error rate >5% over 15-min window; p99 latency >60s; daily cost >120% of 7-day average.

SECTION 07
## API surface
REST + SSE for streaming. JSON request/response. Bearer JWT for auth. Endpoints below are organized by resource. Full OpenAPI spec is generated from controllers and committed alongside the code.
### 7.1  Endpoint reference

Method  ·  Path
Purpose
Auth
POST /api/v1/corpora
Create new corpus from Git URL or upload. Returns 202 with corpus ID; ingestion happens async.
Engineer
GET /api/v1/corpora
List corpora visible to caller. Paginated.
All
GET /api/v1/corpora/{id}
Corpus detail including state, file tree, latest version.
All
GET /api/v1/corpora/{id}/state
Lightweight state poll for cards in transitional states.
All
POST /api/v1/corpora/{id}/sync
Re-pull source from connected Git. Creates new SourceVersion.
Engineer
GET /api/v1/subroutines/{id}
Subroutine detail with AST data and current state.
All
POST /api/v1/subroutines/{id}/extract
Trigger Stage-3 extraction. Returns SSE stream URL.
Engineer
GET /api/v1/extractions/{id}/stream
SSE endpoint streaming LLM tokens for an in-flight extraction.
Engineer
POST /api/v1/extractions/{id}/cancel
Cancel an in-flight extraction.
Engineer
GET /api/v1/specs/{id}
Spec detail with current spec_json, state, and metadata.
All
PATCH /api/v1/specs/{id}
Edit spec (DRAFT or IN_REVIEW only). Body is a JSON Patch; produces SpecRevision.
Engineer (DRAFT) | SME (IN_REVIEW)
POST /api/v1/specs/{id}/route
Route spec to one or more SMEs. Body: {reviewer_ids, routing_note}.
Engineer
POST /api/v1/specs/{id}/claims/{path}/review
SME claim-level action: accept | edit | reject | question.
SME
POST /api/v1/specs/{id}/sign
Sign spec (irrevocable). Requires recent re-auth.
SME
GET /api/v1/specs/{id}/audit
Audit-trail event stream for a spec.
All
POST /api/v1/specs/{id}/scaffold
Trigger Stage-5 scaffold generation. Returns SSE stream URL.
Engineer
GET /api/v1/scaffolds/{id}
Scaffold artifact metadata + file list.
All
GET /api/v1/scaffolds/{id}/files/{path}
Get a single scaffold file content.
All
POST /api/v1/scaffolds/{id}/commit
Commit scaffold to Git. Body: {branch, commit_message}.
Engineer
GET /api/v1/scaffolds/{id}/download
Download scaffold as .zip.
All
### 7.2  Streaming protocol
Server-Sent Events. Event types defined for type-safe client handling.

// Extraction stream events
event: stage
data: {"stage":"priming","step":1,"of":5}

event: token
data: {"text":"The subroutine"}

event: citation
data: {"claim_path":"$.invariants[0]","lines":"39-42"}

event: warning
data: {"code":"citation_unresolved","message":"Lines 80-82 do not exist in source."}

event: done
data: {"spec_id":"...","call_id":"...","input_tokens":4231,"output_tokens":1842}

event: error
data: {"code":"provider_5xx","message":"Provider returned 503; retryable."}

SECTION 08
## Security & data residency
The Harness handles confidential customer source code. Every control below is mandatory; deviations require explicit security-team sign-off documented in the audit trail.
### 8.1  Data classification

Data class
Examples
Storage requirement
Access requirement
RESTRICTED
Customer Fortran source, signed specs, scaffold output
Encrypted at rest (AES-256), Azure-managed keys; immutable container for signed artifacts
Persona-based RBAC; every read logged
CONFIDENTIAL
AST artifacts, draft specs, comments, audit log
Encrypted at rest; standard mutability
Persona-based RBAC; reads logged
INTERNAL
User profiles, configuration, prompt templates (with secrets redacted)
Encrypted at rest
Authenticated users
PUBLIC
Application metadata, schema docs, API spec
Standard
Anyone authenticated
### 8.2  Source data handling
Customer Fortran source is fetched from a Kiwiplan-controlled Git endpoint via Nous service-account credentials. Credentials are managed in Azure Key Vault; rotated quarterly; never accessible from application logs.
Source bytes traverse: Git → Harness API → blob store → worker pool. All hops are TLS-encrypted in transit. No source data persists outside the Harness's Azure subscription.
Source is sent to LLM providers only via the configured zero-retention endpoints. The Harness records the provider configuration version on every call to demonstrate at audit time which retention regime applied.
Source is never logged in plaintext. Application logs reference subroutines by ID, not by content. Failed-call diagnostic captures store source in a separate restricted container with 7-day retention; access requires admin role and produces an audit event.
### 8.3  Authentication & authorization
OIDC via Microsoft Entra ID. All API calls require a valid bearer JWT with audience and issuer matching the Harness configuration.
Sign-off operations require the JWT to contain an authentication-time claim (auth_time) within 5 minutes of the operation. Older sessions trigger a re-auth prompt.
RBAC enforced at the API layer. Every endpoint declares allowed personas; persona is read from the JWT subject claim resolved against the Users table.
LLM provider credentials are admin-managed only; never returned in API responses; rotation procedures documented in the runbook.
### 8.4  Audit completeness
Every state transition produces an AuditEvent. Append-only; no edits, no deletes.
Audit events include: source ingestion, AST parse, LLM invocation (with full call metadata), spec edits (with diffs), routing, claim reviews, sign-off, scaffold generation, scaffold commit, comment, login, logout, permission denial.
Audit data retained 7 years (matches Advantive's record-retention policy). Operational data 2 years; soft-deleted entities purged after 90 days unless under hold.
### 8.5  Compliance posture
ISO 27001 controls inherited from the parent Nous platform; the Harness maps to controls A.5 (policy), A.8 (asset management), A.12 (operations security), A.13 (communications), A.14 (acquisition/dev/maintenance), A.16 (incident management).
SOC 2 Type II evidence captured via the audit trail and provider configuration logs.
EU AI Act Article 14 (human oversight): every consequential output (extracted spec, generated scaffold) is gated by human review before it is treated as authoritative. Sign-off mechanism is the documented oversight control.

SECTION 09
## Non-functional requirements
### 9.1  Performance budgets

Operation
Target
Hard limit
Source ingestion (Git pull, ≤500 files)
p50 < 30s
p99 < 120s
AST parse for one file (≤2000 LOC)
p50 < 5s
p99 < 20s
AST parse for one corpus (≤500 files)
p50 < 5min
p99 < 20min
LLM extraction call (typical subroutine)
p50 < 25s
p99 < 90s
LLM scaffold call (typical signed spec)
p50 < 30s
p99 < 120s
Spec view render (DB → UI)
p50 < 800ms
p99 < 2s
Claim review action persistence
p50 < 200ms
p99 < 800ms
Sign-off operation (HSM signature included)
p50 < 1.5s
p99 < 4s
Scaffold commit-to-Git
p50 < 10s
p99 < 30s
### 9.2  Scalability targets (v1)
Concurrent users: 25 (Engineers + SMEs combined working simultaneously).
Concurrent in-flight LLM calls: 10 (rate-limited by provider quotas).
Subroutines in pipeline at once: 200 across all corpora.
Total corpus size: 2 GB across all connected sources.
Audit events: 1 million per environment per year.
### 9.3  Availability & recovery
Target availability: 99.5% during business hours (NZ + IN time zones combined). Off-hours availability is best-effort; planned-maintenance windows allowed.
RPO (data loss tolerance): 15 minutes. Achieved via PostgreSQL transaction-log shipping to a hot-standby and continuous blob replication.
RTO (recovery time): 4 hours. DR procedure runbook tested quarterly.
In-flight LLM calls survive worker restarts: jobs are persisted; worker pool replays from last checkpoint.
### 9.4  Browser support
Latest Chrome, Edge, Firefox, Safari (last two stable major releases).
Minimum viewport: 1280 × 800. Mobile and tablet not supported in v1 — explicit error message on load.
### 9.5  Internationalization
English UI only in v1. Locale-formatted timestamps and dates per browser. UTF-8 throughout. Source code rendering preserves any non-ASCII characters in Fortran source verbatim.

SECTION 10
## Project 3 integration
The Harness is a workflow tool, not the destination. Its outputs flow into the broader Project 3 build pipeline. This section defines the integration contract.
### 10.1  Outbound artifacts

Artifact
Destination
Format
Trigger
Signed spec (canonical JSON)
Project 3 spec repository
JSON + signature manifest
Sign-off event
Signed spec (PDF)
Project 3 spec repository
PDF, audit-ready
Sign-off event
Scaffold package
Project 3 scaffold-output Git repo
C# project on a dedicated branch
Engineer commit-to-Git action
Test fixtures
Same as scaffold package, in /tests folder
xUnit + Moq C# files
With scaffold package
Audit-trail extract (per spec)
Project 3 spec repository
JSON event log
On-demand engineer export
Open questions log (per corpus)
Project 3 issue tracker (GitHub Issues)
Issue per unresolved question, labeled 'sme-question'
On engineer-initiated dispatch
### 10.2  CI/CD pickup
Project 3's main build pipeline watches the scaffold-output repo. New scaffold/* branches trigger a CI run that: verifies signed-spec signature; compiles the scaffold; runs the auto-generated tests against the auto-generated stubs (which fail with NotImplementedException — expected); reports the scaffold as ready-for-implementation.
Engineers in Project 3 then merge the scaffold branch into a feature branch, complete the method bodies, and the same tests now pass against real implementations. The signed spec accompanies the merge as evidence-of-contract.
### 10.3  Boundary conditions
The Harness does not deploy to any environment beyond its own. It does not push to production, customer infrastructure, or any environment outside Nous's controlled subscription.
The Harness does not modify Project 3's main repository. It only commits to the dedicated scaffold-output repo.
If Project 3 wants to update a signed spec, the path is: amend the source, ingest the new SourceVersion, supersede the existing spec, extract → review → sign a new spec. The old spec is preserved in the audit trail.

APPENDIX A
## Defense-call demo subset
The product specified above is the production deliverable for Phase 0/1 of Project 3. The defense call demo will exercise a deliberate subset that proves the architectural claim end-to-end in 4 minutes. This appendix defines that subset so the team can plan the demo build separately from the full product build.
### A.1  What the demo must prove
One claim, demonstrated end-to-end: the Harness extracts behavioral specifications from real Fortran source, surfaces SME review with full traceability to source, signs an authoritative contract, and emits scaffolded target code with test fixtures derived from signed invariants.

DEMO PRINCIPLE
Demo proves the pipeline as a system — not a magic translator
Every screen the audience sees has a human in the loop or a clear handoff to one. The wow factor is integration and traceability, not autonomy.
### A.2  Demo flow (4 minutes)

Time
Screen
Action
Audience takeaway
0:00–0:30
Subroutine detail (2.1)
Open the CONSUME_ROLL subroutine in source view; briefly call out it's representative Fortran/Classic code with COMMON, ISAM I/O, magic numbers
This is real legacy code
0:30–1:30
Live extraction (3.1)
Click 'Extract spec'. Real LLM call. Audience watches the spec stream in with citations landing on source lines
Real LLM. Real source. Real citations.
1:30–2:30
Spec review (4.2)
Switch to SME persona. Show invariants with citations, accept several, edit one to refine, resolve an open question
Humans verify. Every claim cited. Every action audited.
2:30–3:00
Sign-off + audit
Sign the spec. Show the signed-state view + the audit trail entry with HSM signature info
Signed = authoritative. Tamper-evident. Traceable.
3:00–3:45
Scaffold view (5.2)
Show the generated .NET service skeleton + tests, with TODO stubs annotated to spec invariants
Engineer-completable scaffold. Not autogenerated production code.
3:45–4:00
Close + handoff
Show commit-to-Git event in the audit log. Mention this scaffold flows into the Project 3 main build pipeline
Integration with the broader programme is real, not theoretical.
### A.3  What the demo build needs (vs. full product)

Capability
Full product
Demo build
Source ingestion
Git pull + upload
Pre-loaded with one corpus
AST parsing
Real fparser2 across full corpus
Pre-computed for the one subroutine
LLM call (Stage 3)
Real, in production with full retry/fallback
Real Anthropic call, live
SME review interactions
Multi-user, real notifications
Single-user; pre-rehearsed actions
Sign-off + HSM
Real HSM signature
Real signature using Azure Key Vault test instance
Scaffold generation
Real LLM call + commit pipeline
Real LLM call against signed spec
Audit trail
Full append-only event log
Real, populated from prior demo runs + this one
Multi-corpus
Yes
No — one corpus, one subroutine
Multi-user concurrent edit
Yes
No — presenter is sole user
Provider failover
Yes
Configured but unlikely to trigger in 4 minutes
### A.4  Live-demo risk mitigations
Recorded backup of full demo flow, ready in adjacent browser tab. If the live LLM call exceeds 35 seconds or fails, presenter switches to recording mid-flow with a controlled handoff line: "Let me show you this from our recorded run so we don't lose time."
Two pre-warmed sessions: production (live) and recorded. Network independence — primary connection plus a tethered fallback.
LLM call wrapped in a 60-second timeout. On timeout, the cached response from the most recent rehearsal renders with a small "loaded from cache" indicator. Honest, not deceptive.
Synthetic Fortran sample is deterministic — same input across rehearsals — so prompt engineering can stabilize output enough that all rehearsals produce comparable specs.

APPENDIX B
## Synthetic Fortran sample — CONSUME_ROLL
This is the canonical worked example used throughout the spec. Designed to be representative of Kiwiplan Classic style without reproducing actual Kiwiplan source. It exercises COMMON block dependencies via INCLUDE files, ISAM I/O via subroutine callouts (RS_READ, RS_WRITE), magic-number constants (MIN_REMAIN = 12.0), multi-branch control flow with side effects, and an integration callout (CSC_NOTIFY).
### B.1  Source

      SUBROUTINE CONSUME_ROLL(ROLL_ID, USED_LF, OPER_ID, RESULT_CD)
C     ------------------------------------------------------------------
C     CONSUME_ROLL — Posts a roll consumption event from the wet end.
C     Decrements on-hand linear footage, updates roll status,
C     and emits the CSC inventory-changed notification.
C
C     PARAMS:
C       ROLL_ID    Unique roll identifier (CHAR*12)
C       USED_LF    Linear feet consumed in this event (REAL)
C       OPER_ID    Operator ID for audit (CHAR*8)
C       RESULT_CD  Out: 0=ok, 1=not_found, 2=insufficient, 3=locked
C     ------------------------------------------------------------------
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      CHARACTER*8  OPER_ID
      REAL         USED_LF
      INTEGER      RESULT_CD

      INCLUDE 'RSCOMMN.INC'
      INCLUDE 'CSCMSG.INC'

      REAL         ON_HAND_LF, NEW_LF, MIN_REMAIN
      INTEGER      ROLL_STATUS, IO_STAT
      CHARACTER*4  GRADE_CD
      LOGICAL      LOCKED

      PARAMETER (MIN_REMAIN = 12.0)

C     Read the current roll record from RSMASTR (ISAM keyed on ROLL_ID)
      CALL RS_READ(ROLL_ID, ON_HAND_LF, ROLL_STATUS, GRADE_CD,
     &             LOCKED, IO_STAT)
      IF (IO_STAT .NE. 0) THEN
         RESULT_CD = 1
         RETURN
      END IF

      IF (LOCKED) THEN
         RESULT_CD = 3
         RETURN
      END IF

      IF (USED_LF .GT. ON_HAND_LF) THEN
         RESULT_CD = 2
         RETURN
      END IF

      NEW_LF = ON_HAND_LF - USED_LF

C     If remaining stock is below threshold, mark roll as DEPLETED
      IF (NEW_LF .LT. MIN_REMAIN) THEN
         ROLL_STATUS = 9
      END IF

C     Persist update via ISAM rewrite
      CALL RS_WRITE(ROLL_ID, NEW_LF, ROLL_STATUS, OPER_ID, IO_STAT)
      IF (IO_STAT .NE. 0) THEN
         RESULT_CD = 1
         RETURN
      END IF

C     Notify the corrugator scheduling channel (CSC)
      CALL CSC_NOTIFY('INV_CHG', ROLL_ID, GRADE_CD, NEW_LF)

      RESULT_CD = 0
      RETURN
      END

### B.2  Why this example was chosen
Real RSS-domain semantics: roll-stock inventory consumption is a recognizable Kiwiplan operation; an SME reviewer can verify the LLM-extracted spec against operational knowledge.
Compact: 66 lines, single subroutine. Fits in a single Monaco editor viewport without scrolling, which matters for citation pulse animation during live demo.
Multiple invariants of varying difficulty: some obvious (negative LF check), some subtle (the order of locked-check vs. insufficient-check matters for the returned code), some require domain knowledge (status code 9 = DEPLETED is not in the source).
Includes deliberate ambiguity: USED_LF is not validated as non-negative. This is the planted open question the SME resolves during the demo's review stage. It demonstrates that the system surfaces what it doesn't know, not just what it does.
Includes magic numbers: 12.0 LF threshold and status code 9 should both be flagged as configuration / lookup-table candidates by the LLM. Makes for a high-value SME conversation in the demo.
### B.3  INCLUDE files (referenced but not included in source)
RSCOMMN.INC and CSCMSG.INC are referenced. For the demo build, stub versions exist with the COMMON block declarations the LLM needs to interpret the source. In production use against real Kiwiplan source, these would be the real INCLUDE files pulled from the same repository.

C     RSCOMMN.INC — Roll-stock COMMON block declarations (stub)
C     ----------------------------------------------------------
      COMMON /RSGLOBAL/ CURRENT_PLANT, CURRENT_SHIFT, AUDIT_FLAG
      INTEGER     CURRENT_PLANT
      CHARACTER*8 CURRENT_SHIFT
      LOGICAL     AUDIT_FLAG

C     RS_READ / RS_WRITE expected interfaces:
C       RS_READ(roll_id, on_hand_lf, status, grade_cd, locked, io_stat)
C       RS_WRITE(roll_id, new_lf, status, oper_id, io_stat)
