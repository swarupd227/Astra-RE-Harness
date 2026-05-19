# Phase 5.7 — Extended demo build plan

**Status:** v0.1 (ready to rehearse)
**Companion:** `demo-build-plan.md` (the original 4-min defense pitch), `phase-plan-and-gates.md`
**Length target:** **9–10 minutes** end-to-end (vs. the defense pitch's 4)
**Audience:** Engineering-depth — peers, technical evaluators, prospective partners. The 4-minute defense pitch stays the canonical short-form.

---

## 1. The single claim the extended demo must prove

> "The Harness handles **two source languages** (Fortran and COBOL) into **two target stacks** (.NET and Java), with **structured cross-routine context**, an **Admin-configurable calibration corpus** that scores the LLM's output, a **corpus-wide consistency pass** that catches drift the per-routine extract can't see, and a **per-routine equivalence harness** that runs the legacy binary side-by-side with the migration — every step audit-logged, every claim cited, every signature HSM-backed."

That sentence is too long to say in a demo. The visual proof is the demo. Each beat below earns one part of the claim.

---

## 2. Demo flow (10 minutes, beat-by-beat)

| Beat | Time | Surface | Action | Audience takeaway |
|---|---|---|---|---|
| **B0** | 0:00–0:20 | Landing page | Brief framing: "Two languages, two target stacks, every step audited." | Scope |
| **B1** | 0:20–1:00 | Subroutine detail (`CONSUME_ROLL`) | Open the Fortran subroutine. Call out COMMON blocks, ISAM I/O, magic numbers. | This is real legacy code. |
| **B2** | 1:00–2:00 | Live extraction (Fortran) | Click **Extract spec**. **NEW**: a `neighbourhood_attached` indicator appears showing 3 callees + 0 callers + 1 COMMON block resolved from the parser's call graph. Real Anthropic streaming. | Cross-routine context is structured, not RAG. |
| **B3** | 2:00–2:45 | Spec review + sign | Switch to SME persona. Accept 3 invariants, edit 1, resolve 1 open question. Sign. Show HSM signature info. | Humans verify. Tamper-evident contract. |
| **B4** | 2:45–3:30 | Scaffold + .NET validation | Generate the .NET scaffold. Show test pack derived from signed invariants. Validate (compile + test). | Engineer-completable, not autogen. |
| **B5** | 3:30–4:30 | COBOL ingest | Upload `DEPTPAY.CBL`. Show parser detects `sourceLanguage=cobol`, extracts 1 program + 2 paragraphs. | Same pipeline, new language. |
| **B6** | 4:30–5:30 | COBOL extract → Java scaffold | Extract DEPTPAY using the COBOL→Java extract prompt. Sign. Generate the `java-spring/cobol-canonical-payroll` archetype. | Two source languages, two targets, one pipeline. |
| **B7** | 5:30–6:30 | Maven sidecar validation | Run the compile + test-pack validators. They dispatch through the Maven sidecar (Eclipse Temurin 21 + Maven 3.9). Build log streams. | The validator is target-stack-aware. |
| **B8** | 6:30–7:30 | DEPTPAY equivalence preview | Open the equivalence preview. GnuCOBOL sidecar compiles AVG-DRIVER; runs it side-by-side with the C# `decimal` reference. **6 rows match**. Call out the `111111.11 / 19 = 5847.95` line. | Legacy binary and migration produce identical output. |
| **B9** | 7:30–8:30 | Golden dataset (we measure ourselves) | Open `/platform/golden-dataset`. Show **100+ entries** (50 Fortran + 50+ COBOL). Aggregate score banner. Click the `cobol-rounded-off-by-one` entry → show the source snippet + canonical inputs that match the 5847.95 trap. | The platform measures itself. |
| **B10** | 8:30–9:30 | Cross-routine harmonisation | Open `/platform/harmonisation`. Click **Run harmonisation pass**. ~30s LLM call. Shows N findings sorted by severity. Open one, attach an admin note, mark **Accepted**. | Corpus-wide drift catcher. |
| **B11** | 9:30–10:00 | Audit close | Full audit trail of the past 10 minutes: 40+ events, every action signed/timestamped/persona-attributed. | Auditor would have no questions. |

---

## 3. What's new vs. the original 4-min defense pitch

| Original (4 min) | Extended (10 min) — net additions |
|---|---|
| Fortran → .NET only | Fortran → .NET **and** COBOL → Java |
| Per-routine extract, no cross-routine context | **Neighbourhood-attached extract** (Phase 7.0) |
| No validation surface shown | **Compile + test-pack validators** on both .NET and Java (Phase 5.5, dispatched by `Scaffold.TargetPlatform`) |
| No equivalence harness | **Per-routine equivalence preview** (CONSUME_ROLL + DEPTPAY AVERAGE-SALARY, Phase 5.6) |
| No calibration corpus | **Golden Dataset** (100+ entries, Admin-configurable, regression-scored, Phase 6.0) |
| No corpus-wide consistency check | **Harmonisation pass** (Phase 7.1) |
| Anthropic call uncached | **Prompt caching** on stable system prefix (Phase 7.0) — visible in the token-usage badge |

---

## 4. Pre-flight checklist (run before any rehearsal)

Run `scripts/demo-readiness.sh` (see Phase 5.7 PR). Manual fallback:

- [ ] `docker compose up -d` brings every sidecar healthy
  - [ ] `curl -sf http://127.0.0.1:51052/health` → gfortran
  - [ ] `curl -sf http://127.0.0.1:51053/health` → maven
  - [ ] `curl -sf http://127.0.0.1:51054/health` → gnucobol
  - [ ] `curl -sf http://127.0.0.1:50051/health` → parser (gRPC)
  - [ ] `curl -sf http://127.0.0.1:38080/health` → API
- [ ] `curl -sf http://127.0.0.1:38080/api/v1/corpora` returns the CONSUME_ROLL seed
- [ ] DEPTPAY.CBL is staged for upload (see `app/e2e/fixtures/cobol/DEPTPAY.CBL` once added)
- [ ] Anthropic API key in `.env`; `Llm:Provider=anthropic`
- [ ] Frontend renders `/platform/golden-dataset` showing ≥ 100 entries
- [ ] Frontend renders `/platform/harmonisation` (empty runs list is OK)

If any step fails, the recording rehearsal is paused — the demo proves nothing if the underlying stack is degraded.

---

## 5. Risk mitigations (extension of original §4)

### 5.1 Pre-recorded backup

The original `demo-narrated.mp4` (5.5MB) and `demo-silent-captioned.mp4` (2.6MB) live in `app/e2e/demo-output/`. The Phase 5.7 extended recording produces:

- `phase-5.7-narrated.mp4` — full 10-minute narrated cut
- `phase-5.7-silent-captioned.mp4` — same flow, no audio, SRT captions burned in (accessibility + venue-without-audio)
- `phase-5.7-beats/beat_BN.mp4` per beat for surgical edits

The Playwright driver (Phase 5.7.4) produces deterministic raw video; narration is overlaid in post.

### 5.2 Two pre-warmed sessions

Same as original. Session B's recording is the Phase 5.7 cut, not the 4-min cut, when presenting the extended demo.

### 5.3 Determinism deltas vs. the original

- **Anthropic call variance:** prompt caching reduces token-level variance run-to-run but does NOT make outputs identical. The Playwright driver pins to mock-provider for visual-determinism rehearsals; the actual recording rehearsal switches to `anthropic` and accepts ±1 invariant of variance.
- **Maven first-build cold cache:** the maven-sidecar's pre-warmed `~/.m2` cache eliminates this concern, but the FIRST run after `docker compose up` may show a 3–5s warmup. Pre-run a throw-away compile during start-of-day setup.
- **GnuCOBOL compile time:** ~1.2s reliably on bookworm-slim. No concern.

### 5.4 Live-demo time budget

| Beat | Cushion | Action if over |
|---|---|---|
| B2 (extract) | Up to 90s, then cut to recording | Recording switchover via `Cmd-Tab` |
| B7 (Maven validation) | Up to 60s | Skip the test-pack run; just show compile log |
| B10 (harmonisation) | Up to 45s | Switch to pre-loaded historical run |

---

## 6. Cue-card index

See `phase-5.7-cue-cards.md` for the verbatim narration per beat. The cue cards are designed to be printable (one beat per page, tablet-friendly) and to leave room for presenter improvisation in the 5–10s between beat transitions.

---

## 7. Rehearsal schedule (Phase 5.7 additions)

| Date | Rehearsal | Goal |
|---|---|---|
| (T-7d) | Internal walk-through | Surface integration gaps. Tech lead + designer. |
| (T-5d) | Timed rehearsal #1 | Run the full 10-min flow; record baseline. |
| (T-3d) | Timed rehearsal #2 | Surface pacing + copy issues; record. |
| (T-1d) | Timed rehearsal #3 | Final timing pass. Recording from this rehearsal is the live-demo backup. |
| (T-0d) | Demo | Live. |

Each rehearsal has a written debrief under `04_Delivery/rehearsal-debriefs/phase-5.7-rN.md`.

---

## 8. What we still do NOT show in the extended demo

Out of scope, even at 10 minutes:

- **Multi-corpus admin** — single corpus per session is enough.
- **Provider failover** — covered in the architecture doc, not the demo.
- **CICS/SQL inter-program traps** — entries exist in the golden dataset; we cite them by name but don't drill in.
- **GitHub Actions CI gate replay** — the `golden-dataset.yml` workflow is real but a PR-time thing, not a live-demo beat. We mention it in B9 and move on.
- **The fortran-callee-side-effect-inheritance Phase 7.0 golden entry** — a strong artifact for an engineering deep dive but too subtle for a 10-minute audience. Save for a follow-up.

If audience asks about any of the above, the answer is "yes, it's in the platform — happy to drill in after the call."

---

## 9. Production notes (narration overlay)

- Voice direction: professional but conversational. The defense-call narrator continues; consistent voice ensures the extended cut feels like a longer version, not a re-recording.
- On-screen captions: enabled by default for accessibility; the WebVTT cue file lives at `app/e2e/demo-output/phase-5.7-captions.vtt` (generated alongside the recording).
- Music: none (matches the original 4-min cut).
- Background colour: black bars during transitions between beats (no fade — too leisurely for a technical demo).

---

## 10. Success criteria

The Phase 5.7 extended demo is successful when:

1. **All 11 beats** complete in ≤ 10:00 wall-clock.
2. **Zero error banners** in the UI during the recording.
3. **Every action in the audit trail** has a persona attribution + a signature where applicable.
4. The **5847.95 row** in the DEPTPAY equivalence appears on screen with both runtimes agreeing.
5. The **golden dataset aggregate score** is visible on the page (any value; the act of measuring is the proof, not the specific number).
6. The harmonisation pass surfaces **at least one finding** that the SME can interact with.

If any of these is missed in a rehearsal, the rehearsal is debriefed and rerun before the actual demo.
