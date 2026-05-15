# Phase 5 · COBOL → Java Spring end-to-end migration

## Scope

End-to-end production-quality migration of a public COBOL corpus through
the existing Astra pipeline (ingest → parse → extract → review → sign →
scaffold → validate → commit), targeting Java Spring as the modern stack,
and recording a stakeholder-facing demo at the end.

This is a real-runtime migration — every gate is wired against a real
runtime, not a stub:

- COBOL parser:   `cobol-parser-sidecar` (ProLeap-based)
- Compile gate:   `maven-sidecar` (`mvn compile` + `mvn test`)
- Equivalence:    `gnucobol-sidecar` (`cobc` compile + run with canonical
                  inputs vs the generated Java service)

## Source repo

- **URL**:   https://github.com/openmainframeproject/cobol-programming-course
- **License**: CC-BY-4.0 (suitable for derivative + commercial demo work)
- **Default branch**: main
- **Inventory**: 7 COBOL files, 1022 total LOC, all COBOL-85 fixed-form

### File inventory

| File | LOC | Surface | Target? |
|---|---|---|---|
| `Course #4 - Testing/Labs/cbl/DEPTPAY.CBL` | 36 | Pure compute, group structures, AVERAGE-SALARY/DISPLAY-DETAILS sections | ✓ migrate |
| `Course #4 - Testing/Labs/cbl/EMPPAY.CBL` | 54 | Nested IF/ELSE on hours, COMPUTE for weekly+monthly pay | ✓ migrate |
| `Course #3 - Advanced Topics/Challenges/Debugging/cbl/CBL0106.cbl` | 195 | SELECT/FD sequential file I/O (`ACCT-REC` → `PRINT-LINE`) | ✓ migrate |
| `Course #3 - Advanced Topics/Challenges/Debugging/cbl/CBL0106C.cbl` | 204 | Same surface as CBL0106 (corrected variant) | ✗ skip — duplicate surface |
| `Course #3 - Advanced Topics/Labs/cbl/CBLDB21.cbl` | 144 | EXEC SQL DB2 (DECLARE CURSOR, FETCH) | ✗ skip — Phase 7 (JDBC equivalence) |
| `Course #3 - Advanced Topics/Labs/cbl/CBLDB22.cbl` | 201 | EXEC SQL DB2 (multi-row processing) | ✗ skip — Phase 7 |
| `Course #3 - Advanced Topics/Labs/cbl/CBLDB23.cbl` | 188 | EXEC SQL DB2 (host-variable nuances) | ✗ skip — Phase 7 |

**In scope**: 3 programs · 285 LOC · COBOL → Java Spring 3
**Out of scope (Phase 7)**: 3 DB2 programs · 533 LOC

The migration targets escalate in pattern complexity: **DEPTPAY**
(compute) → **EMPPAY** (branching) → **CBL0106** (sequential file I/O).
The diversity ensures each gate (compile, test pack, equivalence) is
exercised against meaningfully different patterns.

## Target Java mapping

| COBOL source | Java service                  | Notes |
|---|---|---|
| `DEPTPAY.CBL`   | `DeptPayService` + `Department` record + `DeptPayServiceTest`   | Pure-function service; JUnit asserts AVERAGE-SALARY's COMPUTE result |
| `EMPPAY.CBL`    | `EmpPayService` + `Employee` record + `EmpPayServiceTest`       | Nested-IF semantics → boundary-condition JUnit tests around 40 / 50 / 150 hours |
| `CBL0106.cbl`   | `AccountReportService` + `Account` record + `IAccountRepository` (sequential read) + `IPrintWriter` (write) + JUnit on the read→format→write pipeline | I/O boundaries → interfaces with adapter stubs |

All three services land in a single `Demo.PayrollReports` Maven module
(group `com.example.payroll`).

## Success criteria

A buyer watching the demo should see, in order:

1. **Ingest** — the platform clones the repo, identifies 3 in-scope
   COBOL programs (skipping DB2 ones with a visible reason).
2. **Parse** — the ProLeap-based parser surfaces each PROGRAM-ID as an
   entity, plus PROCEDURE DIVISION sections and DATA DIVISION groups
   as nested structure.
3. **Extract** — real Claude call against the `cobol/java-spring/extract`
   prompt produces typed claims (invariants / section contracts / I/O
   side effects / edge cases / open questions) cited to source lines.
4. **Sign** — SME walks every claim, signs cryptographically. Signature
   binds to the exact COBOL bytes.
5. **Scaffold** — real Claude call against the `cobol/java-spring/scaffold`
   prompt streams a Maven module with claim-mapped JUnit fixtures.
6. **Compile gate** — `mvn compile` runs in the new `maven-sidecar`
   and reports `BUILD SUCCESS`.
7. **Test-pack gate** — JUnit runs every claim-mapped fixture; every
   signed claim is covered.
8. **Equivalence gate** — `gnucobol-sidecar` compiles the original
   COBOL with stubs for any file I/O / display, drives both the COBOL
   binary and the Java service with the same canonical inputs, asserts
   outputs match per-input (mirrors `ConsumeRollEquivalence.cs`).
9. **Commit** — the Maven module + validation attestation lands in a
   Git branch.
10. **Demo recording** — 3-4 minute captioned silent walkthrough,
    spliced with the platform tour appendix from Phase 3.

## Phase breakdown

| # | Deliverable | Estimated effort |
|---|---|---|
| 5.0 | This intake doc | ½ day |
| 5.1 | `cobol-parser-sidecar` (ProLeap container + HTTP/gRPC API + integration into ingest) | 2-3 days |
| 5.2 | Pipeline routing by source language (`fortran-f77` vs `cobol`); auto-detects from file extensions | 1 day |
| 5.3 | `cobol/java-spring/extract.v0.1.md` calibrated against the 3 target programs | 1 day |
| 5.4 | Real Claude-driven Java Spring scaffold provider + `cobol/java-spring/scaffold.v0.1.md` prompt | 2-3 days |
| 5.5 | `maven-sidecar` (Eclipse Temurin + Maven container, `mvn compile` + `mvn test` HTTP API) + JavaCompileValidator + JavaTestPackValidator | 2 days |
| 5.6 | `gnucobol-sidecar` (cobc + executor container) + COBOL-side equivalence harness analogous to `ConsumeRollEquivalence.cs` | 2-3 days |
| 5.7 | End-to-end demo: extend `minpack-demo.spec.ts` (or new `cobol-demo.spec.ts`), update narration timeline + captions, re-record | 1-2 days |
| **Total** | **~2-3 weeks** of focused engineering | |

## Risks & decisions

- **ProLeap COBOL parser** ships as a Maven library. The parser sidecar
  needs JVM runtime (~80 MB), unlike the existing Python sidecars. We
  accept this — COBOL is mainframe territory and Java tooling is the
  least-friction path.

- **GnuCOBOL stub strategy**. The 3 target programs use stdout DISPLAY
  (DEPTPAY, EMPPAY) and SELECT/FD file I/O (CBL0106). GnuCOBOL compiles
  DISPLAY natively. For CBL0106's sequential I/O we'll wire a
  data-file harness into `/var/tmp/cobol-runs/<run>/` — no SELECT-to-ISAM
  stub required because we're using legitimate sequential files.

- **Claude budget**. End-to-end per-program: 1 extract call + 1 scaffold
  call ≈ 6 calls total at current rates (~$0.30). Demo recording
  re-runs these — budget another $1-2 for re-takes.

- **Repo size**. The cobol-programming-course repo is small (1K LOC) by
  COBOL standards. The "mid-size" framing in the original ask is best
  read as "non-trivial real-world COBOL with multiple programs and
  multiple I/O patterns" rather than 10K+ LOC banking. If 10K+ scale
  matters, we follow up with Phase 6 against a larger repo
  (recommendations: IBM CICS bank sample, or a synthesised payroll
  module set of 20+ programs).
