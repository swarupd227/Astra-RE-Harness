# Teller Lite — planted-truth sheet (dataset bible)

Everything in the synthetic dataset is authored to agree with this sheet; `answers.json` is its machine-readable form and
is what the test harness scores against. **Review this before any file is authored.** All names, numbers, URLs and
text are invented. Nothing here describes any real organisation, system or person.

## 1. The story

Tellers at a fictional association, **Blue Ridge Farm Credit (synthetic)**, take loan payments and cash advances at the
branch using **Teller Lite**. Each transaction is journalled to `TLR_TXN`. A nightly UC4 chain (`TLR_EOD`) locks drawers,
takes control totals, stages and posts the day's transactions, extracts to the data warehouse and prints the EOD report.
A morning job (`TLR_AM_RECON`) reconciles cash drawers against a file from the cash-vault system. A support contractor,
**Dale Henry (synthetic)**, has run it for 9 years and is leaving. The interviewer is **Morgan Lee (synthetic)**, the
transition lead. Application tier: **1**. Transition wave: "Wave 1", ends 2027-03-31.

## 2. Source inventory (reduced volume: ~2.7k lines of code-like files, not the spec's ~7k)

| Path | Kind | Contents | ≈ Lines |
|---|---|---|---|
| `src/web/Teller.oml.xml` | ui xml (generic XML chunks) | 4 screens: Payment, CashAdvance, DrawerOpenClose, ReceiptReprint; server actions PostPayment, PostCashAdvance, OpenDrawer, CloseDrawer, ReprintReceipt | 400 |
| `src/web/Extensions/ReceiptPrinterExtension.cs` | code | outbound REST to the local receipt printer; URL read from config key `ReceiptPrinter:Url` (no literal) | 120 |
| `src/web/Extensions/SessionExtension.cs` | code | teller sign-in check against AD group from config key `Auth:TellerAdGroup` | 100 |
| `src/services/TellerPostingService.cs` | code | posting, OFAC screen, loan lookup, GL message publish, **retry loop (KE-03)** | 450 |
| `src/services/DrawerService.cs` | code | open/close drawer; **float variance comparison (KE-02)** | 300 |
| `src/services/LoanLookupClient.cs` | code | REST GET to loan accounting; literal fallback base URL in code | 200 |
| `src/services/OfacScreeningClient.cs` | code | REST POST to OFAC screening; **hard-coded 5 s timeout, ignores `Ofac:TimeoutSeconds` (KE-05)**; base URL only in config | 150 |
| `src/services/` (Program, controller, models) | code | wiring, 1 controller, DTOs | 500 |
| `db/ddl.sql` | sql | 14 tables, 2 views, `UQ_TLR_STAGE_TXN_REF` unique key on `TLR_STAGE(TXN_REF)` | 500 |
| `db/procs/*.sql` | sql | 9 stored procedures (one file each) | 450 |
| `batch/uc4/*.xml` | job | 6 job exports + `TLR_EOD.xml` job plan (JOBP) | 7 files |
| `batch/ssis/DrawerRecon.dtsx` | etl | flat-file import from the cash-vault drop; Script Task reads `Reconciliation:VaultFilePath` | 250 |
| `config/appsettings.{DEV,UAT,PROD}.json` | config | secrets replaced by `<<SECRET>>` | 3 × 40 |
| `kt/session1.vtt` | transcript | 30:00, ~4,500 words, speakers Dale Henry and Morgan Lee | — |
| `kt/session1.mp3` | audio | TTS of the transcript, two voices, timed to the VTT | ≤ 8 MiB |
| `tickets/export.csv` | ticket | ServiceNow-style, 140 rows, 12 months | — |
| `docs/TellerOpsNotes2019.docx` | document | 6 pages, partly outdated | — |

## 3. Inventory that must be found

**Tables (14):** TLR_BRANCH, TLR_TELLER, TLR_DRAWER, TLR_LIMIT, TLR_TXN, TLR_STAGE, TLR_POSTED, TLR_RECON,
TLR_VAULT_FILE, TLR_DRAWER_CLOSE, TLR_OFAC_LOG, TLR_AUDIT, TLR_JOB_LOG, TLR_CONFIG.
**Procedures (9):** usp_OpenDrawer, usp_CloseDrawer, usp_PostStage, usp_PurgeStage, usp_ReconcileDrawers, usp_LogOfac,
usp_RebuildDrawerTotals, usp_GetTellerLimits, usp_ArchiveAudit. **Views (2):** vw_DrawerLimits (shared with Branch Ops),
vw_DailyPosted.

### UC4 jobs (6) and the plan
| Job | Runs | Schedule | After | Notes |
|---|---|---|---|---|
| `TLR_EOD` (JOBP) | tasks 1-5 below | Mon-Fri 20:30, host UC4AGENT-TLR01 | — | **restart point = Task[3]**; failure notifies teller-ops@brfc.example and pages on-call |
| `TLR_EOD_LOCK` (Task 1) | `usp_CloseDrawer @Mode='EOD_LOCK'` | in plan | — | not re-runnable on locked drawers |
| `TLR_EOD_COUNT` (Task 2) | `usp_ReconcileDrawers @Mode='CONTROL_TOTALS'` | in plan | LOCK | re-running **double-counts** control totals |
| `TLR_EOD_POST` (Task 3) | `usp_PostStage` | in plan | COUNT | the 2627 job; ~25 min; restart point |
| `TLR_DW_EXTRACT` (Task 4) | inline `bcp ... queryout \\dw-stage\teller\` | in plan | POST | **the only place the data-warehouse extract appears** (no proc, no code, no config) |
| `TLR_EOD_REPORT` (Task 5) | `powershell D:\Scripts\EodReport.ps1` | in plan | POST | emails branch managers |
| `TLR_AM_RECON` | `dtexec /F D:\SSIS\DrawerRecon.dtsx` | daily 06:15 | file event on the vault drop | runs the SSIS package |

### Interfaces (8)
| # | Interface | Protocol / direction | Frequency | External party | Discoverable in |
|---|---|---|---|---|---|
| I1 | Loan lookup | REST outbound (GET `/api/loans/{ref}`) | per payment | LoanServ (loan accounting, synthetic) | code (literal fallback URL), config `LoanLookup:BaseUrl`, 2019 notes |
| I2 | GL posting | RabbitMQ message outbound (`tlr.gl` → `gl.posting.in`) | per transaction | General Ledger team | code, config `RabbitMq:*` |
| I3 | OFAC screening | REST outbound (POST `/screen`) | per payment/advance | Compliance IT screening service | code, config `Ofac:BaseUrl` (**URL only in config**) |
| I4 | Cash vault file | CSV flat file inbound | daily ~05:30 | VaultTrack (cash-vault vendor, synthetic) | SSIS package, config `Reconciliation:VaultFilePath`, UC4 file event |
| I5 | Drawer limits | shared SQL view `vw_DrawerLimits` outbound | on demand | Branch Ops app | SQL view, DrawerService |
| I6 | Teller identity | Active Directory group `BRFC-Tellers` inbound | per sign-in | Identity team | config `Auth:TellerAdGroup`, SessionExtension.cs |
| I7 | Receipt printer service | local REST outbound (POST `/print`) | per receipt | branch hardware service | ReceiptPrinterExtension.cs, config `ReceiptPrinter:Url` (**URL only in config**) |
| I8 | Data-warehouse extract | SQL export (bcp) outbound | nightly | enterprise data warehouse | **UC4 job only** |

## 4. The five known errors

| ID | Symptom | Root cause (planted in) | Fix (planted in) | Tickets |
|---|---|---|---|---|
| KE-01 | `TLR_EOD_POST` fails with SQL error 2627 | `usp_PostStage` stages rows one at a time (not atomic); a re-submitted payment leaves two `TLR_TXN` rows with the same `TXN_REF`; `UQ_TLR_STAGE_TXN_REF` rejects the second (DDL + 9 tickets) | transcript **14:32**: delete the staged duplicate (`EXEC usp_PurgeStage @TxnRef = ...`) and rerun from step 3 | 9, incl. **INC-T-0412** (P2) |
| KE-02 | drawer will not close; variance shown as $0.00 but flagged | `DrawerService` compares a `double` counted total to a `decimal` ledger total; float error ≈ 1e-14 ≠ 0 | transcript **19:05**: supervisor override on the close screen (the one manual workaround) + 4 tickets | 4 |
| KE-03 | payment posts twice after a timeout | retry loop in `TellerPostingService` re-posts after a post that succeeded but timed out | **none documented** — tickets only; becomes open question Q1 | 4, incl. one P1 (**INC-T-0377**) |
| KE-04 | morning reconciliation is empty | `appsettings.PROD.json` `Reconciliation:VaultFilePath` = `\\vault-prd\export\` (should be `\exports\`); package tolerates a missing file and reconciles nothing | transcript **22:40**: correct the PROD path, rerun `TLR_AM_RECON` | 4 |
| KE-05 | OFAC check times out and blocks the teller | `OfacScreeningClient` hard-codes 5 s and ignores `Ofac:TimeoutSeconds` (PROD sets 15, unused) | 2019 notes (outdated advice) + 3 tickets (workaround: compliance manual-check form with supervisor approval) | 3, incl. **INC-T-0455** (P2) |

## 5. The planted conflicts

- **C-01 (required).** Transcript **11:20**, Dale: the EOD chain "restarts from the top". `batch/uc4/TLR_EOD.xml` defines the
  restart point at `Task[3]` (`TLR_EOD_POST`). Restarting from the top would re-run LOCK and COUNT (double-counting control
  totals). The engine must mark the claim **Conflict**, naming both sources, and must not choose.
- **C-02 (optional, tuning decision).** 2019 notes (section "Known issues") say OFAC timeouts are fixed by raising
  `Ofac:TimeoutSeconds`; the code ignores that key. Flagging it is correct behaviour, but a second open conflict adds a click
  to the demo's review beat — K13 decides whether to keep it visible or resolve it in the seed.

## 6. The six open questions the gap finder must raise (answers appear nowhere in the dataset)

Q1 After a payment times out, how does an operator confirm whether the loan system already applied it before reversing?
(KE-03.) Q2 Who approves deleting a staged duplicate in PROD? (KE-01.) Q3 Who is the after-hours escalation for OFAC outages?
Q4 Which Branch Ops team consumes `vw_DrawerLimits`, and how are changes to it announced? (I5.) Q5 What consumes the nightly
warehouse extract, and what is its deadline? (I8.) Q6 Who owns the `tlr.gl` exchange and how are the RabbitMQ credentials
rotated? (I2.)

## 7. Tickets (140 rows)

Columns: number, opened_at, priority, state, short_description, description, assignment_group, cmdb_ci, close_notes,
resolved_by. Numbers `INC-T-xxxx` (incidents) and `REQ-T-xxxx` (requests). Window 2025-10-01 to 2026-09-30.
**84 routine rows** (60%: password resets 30, printer swaps 24, access requests 20, peripherals 10) and **56 incidents**:
KE-01 ×9, KE-02 ×4, KE-03 ×4, KE-04 ×4, KE-05 ×3 (24 clustered) plus 32 noise incidents in ≥ 16 categories of at most two
each (so nothing else clusters at 3+). Priorities: exactly three P1/P2 — INC-T-0377 (P1, KE-03), INC-T-0412 (P2, KE-01),
INC-T-0455 (P2, KE-05); everything else P3/P4. `INC-T-0412` description quotes the SQL error text, including
`UQ_TLR_STAGE_TXN_REF` and the duplicate key value; its close notes say the duplicate staged row was deleted and the chain
restarted from step 3.

## 8. Transcript and audio

`kt/session1.vtt` (WebVTT, `<v Name>` speaker tags). Topics and rough clock: intro 0:00-2:00; system overview 2:00-6:00;
nightly chain 6:00-12:00 (**11:20 the conflicting restart claim**); duplicates and 2627 12:00-16:00 (**14:32 the KE-01 fix**);
GL posting and the queue 16:00-18:00; drawer close 18:00-20:00 (**19:05 the KE-02 override**); OFAC 20:00-22:00;
reconciliation 22:00-25:00 (**22:40 the KE-04 fix**); printer, AD and limits 25:00-27:00; contacts and escalation
27:00-29:00; close 29:00-30:00. KE-03 and KE-05 symptoms are mentioned **without a fix**. Contacts named: LoanServ Support
Desk 555-0101, VaultTrack Support 555-0102, BRFC Service Desk 555-0100, DBA on-call Sam Okafor (synthetic).
**Audio first:** the TTS audio is synthesised from the script with silence padded so the four planted utterances start
exactly at their stated times; the VTT timestamps are generated from the final audio, then copied into `answers.json`.

## 9. The old notes (`TellerOpsNotes2019.docx`, headings used as locators)

Overview, Users, Interfaces (loan lookup only), Daily operations (start of day), Batch (TLR_EOD_LOCK, TLR_AM_RECON),
Environments, Restart (restart from the failed step), Known issues (drawer rounding; the outdated OFAC advice, C-02).
These seed the **legacy approved items**: overview {purpose, users}; interface {loan-lookup}; batch {TLR_EOD_LOCK,
TLR_AM_RECON}; runbook {start-of-day}; environments {environments} — giving the 18.2% "before".

## 10. The wow moment

The runbook step "If TLR_EOD_POST fails with error 2627, clear the duplicate in staging and rerun from step 3" must cite,
side by side: `kt/session1.vtt#t=00:14:32-…`, `tickets/export.csv#INC-T-0412` and the `UQ_TLR_STAGE_TXN_REF` line in `db/ddl.sql`.

## 11. Scoring (pack `knowledge-transfer`, weights from the spec)

Before = **18.2%** (legacy items only, all credit 1.0). After a scripted review, worked example **≈ 76.5%** (range 70-80):
overview 4.5/5 (owners Unverified), context 1/1, interfaces 7.5/8 (DW extract Unverified), batch 5.5/6 (POST sheet
Unverified), runbook 3.5/5 (failure-handling Unverified, escalation missing), known errors 3.0/5 (KE-02, KE-04 Unverified;
KE-03 missing), environments 2/3 (contacts missing), open questions 0. Slot credit is the best over approved versions, so
legacy items never lower the score. The spec's on-screen "74%" is within run-to-run variance of this.

## 12. Placeholder apps (portfolio only; scores pre-computed, shown with a tooltip)

| App | Tier | Score | Knowledge holder (synthetic) |
|---|---|---|---|
| Teller Lite | 1 | 18.2 → ≈ 76 | Dale Henry |
| Loan Docs Lite | 2 | 45 | Priya Nair |
| Statement Batch Lite | 2 | 62 | Marcus Webb |
| Member Portal Lite | 3 | 30 | Elena Ruiz |

Tiers for the three placeholders are **my assumption** (the spec takes tiers from a workbook we don't have). Portfolio
dependency before ≈ 62.7%, after ≈ 40.8% (tier weights 3/2/1) — under the 50% day-90 target.

## 13. Synthetic-data rules (enforced by a test)

No real organisation, person, address or account. Emails only `@brfc.example`; phones only `555-01xx`; account refs
`ACCT-` plus six digits; a denylist rejects "AgFirst", "AgriBank" and other real names. The dataset carries the banner
"Synthetic data for demonstration".
