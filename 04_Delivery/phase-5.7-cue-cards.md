# Phase 5.7 — Presenter cue cards (verbatim)

Each card is one beat. Print one per A5/tablet page. Time stamps are wall-clock targets, not strict bounds.

---

## B0 · 0:00–0:20 · Framing

> "In the next ten minutes I'll show you the same Harness across two source languages — Fortran and COBOL — into two target stacks — .NET and Java. Each step is audit-logged. Each LLM claim is cited to source. Each signature is HSM-backed. Let's start."

[Click into Subroutines list. Mouse-pointer-only highlight on the persona indicator showing **Engineer**.]

---

## B1 · 0:20–1:00 · Open the Fortran subroutine

[Open `/subroutines/<CONSUME_ROLL id>`. Source pane on the left, structural metadata on the right.]

> "This is `CONSUME_ROLL` from a Kiwiplan-class roll-stock corpus. Notice the COMMON block reference on the right pane — `STUBSTATE`. ISAM I/O via `CALL INV_READ` / `INV_WRITE`. A magic number — `MIN_REMAIN = 12.0`. This is real legacy code. Nothing synthetic about the trap surface."

[Scroll the source pane once so the audience sees the depth — but no longer than 8 seconds.]

---

## B2 · 1:00–2:00 · Live extraction (with neighbourhood)

[Click **Extract spec**. Land on `/extract`.]

> "Real Claude Sonnet 4.5 call, streaming. Watch the top-right of the source pane —"

[Point at the `neighbourhood_attached` indicator the moment it appears.]

> "— that just told me the extract has structured cross-routine context. Three callees, one COMMON block declarer. None of that came from RAG. The parser sidecar extracted the call graph at ingest; we just threaded it through the prompt. Prompt-caching on the system prefix means subsequent extracts in this corpus pay ten percent of the input-token cost."

[Wait for the spec to finish streaming. Citations pulse on source lines as they land.]

> "Every invariant cites the source line it derives from."

---

## B3 · 2:00–2:45 · SME review + sign

[Switch persona via the top-right menu to **SME**. Land back on the spec page.]

> "Different persona now — the domain expert. I see five invariants. Every one cites the COBOL line it came from."

[Click **Accept** on three invariants in succession.]

> "Three of these read correctly to me."

[Click **Edit** on one, change a phrase, **Save**.]

> "One needs a tighter wording. I'll log the edit — captured in the audit trail."

[Click **Resolve** on an open question, fill in the SME's answer, save.]

> "The open question — what's `MIN_REMAIN = 12.0` mean? I know the answer. Linear-feet minimum on a roll. Resolved."

[Click **Sign spec**. Confirm dialog. Sign.]

> "HSM-backed signature. Software HSM in dev; Azure Key Vault Managed HSM in prod. The spec is now authoritative."

---

## B4 · 2:45–3:30 · .NET scaffold + validation

[Click **Generate scaffold**. Wait for the package to materialise.]

> "Scaffold derived from the signed spec. C# service skeleton, xUnit test pack with one assertion per signed invariant. Method bodies are deliberate TODOs — this is engineer-completable, not autogen-and-pray."

[Click **Validate compile**. Stream the build log. Show **PASSED · 0 warnings**.]

> "Compile gate green."

[Click **Validate test-pack**. Show some-failed-by-design output — the TODOs aren't implemented yet.]

> "Test pack fails as designed — the TODOs aren't implemented yet. The engineer's job is to fill them in until this turns green, with every invariant they tighten still signed."

---

## B5 · 3:30–4:30 · COBOL ingest

[Navigate to `/corpora` → click **+ Upload**. Drag `DEPTPAY.CBL` into the upload zone.]

> "Same platform, different language. This is `DEPTPAY.CBL` from the openmainframeproject COBOL course. Standard demo corpus."

[Wait for parse. Land on the corpus detail page.]

> "Parser detected `sourceLanguage=cobol`. Two paragraphs extracted — `100-MAIN` and `AVERAGE-SALARY`. Routing-by-language is automatic — no config flag."

[Click into `AVERAGE-SALARY`.]

---

## B6 · 4:30–5:30 · COBOL extract → Java scaffold

[Click **Extract spec**. Wait for the cobol-extract-v0.1 prompt's output.]

> "Different extract prompt — `cobol/java-spring/extract.v0.1`. The same neighbourhood mechanic — paragraph PERFORMs become callees, copybook fields become inputs."

[Wait for stream to settle. Switch to SME, sign quickly — pre-rehearsed.]

> "Spec signed."

[Click **Generate scaffold** → select target **java-spring**.]

> "Java-spring archetype. The `cobol-canonical-payroll` template — a Maven module with JUnit 5 + AssertJ + Mockito. Twelve files, including the `pom.xml`, the service skeleton, the repository interface, and a JUnit fixture per signed invariant."

[Show the scaffold artifact page — the file tree.]

---

## B7 · 5:30–6:30 · Maven sidecar validation

[Click **Validate compile**.]

> "The compile validator dispatches by target platform. For java-spring it routes through the Maven sidecar — Eclipse Temurin 21 plus Maven 3.9, pre-warmed `~/.m2` cache so we don't pay for an online resolve on every demo."

[Stream the build log. `mvn compile` succeeds.]

> "Maven compile green — about a second."

[Click **Validate test-pack**.]

> "And the same dispatch for the test-pack — `mvn test` on the surefire-instrumented module."

[Show the test pack failures — same story as the .NET side. TODOs unimplemented.]

> "Same shape as the .NET side. Tests fail until the engineer fills the TODOs. Same audit trail, same gate."

---

## B8 · 6:30–7:30 · DEPTPAY equivalence (the 5847.95 moment)

[Switch to **Admin** persona. Navigate to the per-routine equivalence preview for DEPTPAY.]

> "Now the moment. The Harness can drive the *legacy COBOL binary* side-by-side with the Java rewrite using the same canonical inputs."

[Click **Run equivalence preview**.]

> "The GnuCOBOL sidecar compiles the AVG-DRIVER wrapper around DEPTPAY's `AVERAGE-SALARY` paragraph. A C# `decimal` reference — same `HALF_UP, scale=2` shape Java BigDecimal would use — evaluates side-by-side."

[Show the 6-row diff table. Point at the `(111111.11, 19) → 5847.95` row.]

> "Six canonical inputs. Six matches. Watch this row — `111111.11 divided by 19`. The COBOL binary returns `5847.95`. The Java/decimal reference returns `5847.95`. Both correct. But the original test fixture that was hand-written in the Phase 5.4 Java scaffold asserted `5848.06`. The equivalence harness caught a bug in the engineer's hand-computed expected value before anyone shipped the test."

[Pause for two seconds.]

> "That bug — `19 times 5848 is 111112, not 111111` — would have survived code review. The harness surfaces it on the first run."

---

## B9 · 7:30–8:30 · Golden Dataset — we measure ourselves

[Navigate to `/platform/golden-dataset`.]

> "How do we know the LLM keeps surfacing traps like that one across prompt changes? We measure ourselves."

[Pan the entry list. Filter by `schemaId=cobol`. Show the entry count + the aggregate score banner.]

> "One hundred-plus hand-curated trap snippets — fifty Fortran, fifty COBOL. Each pairs a real legacy-code snippet with the claims the extract pipeline is *expected* to produce. The scorer is regex-pattern matching — robust to phrasing variation, deterministic, cheap to author."

[Click `cobol-rounded-off-by-one`. Show the entry detail.]

> "Here's the entry for the 5847.95 trap. The expected-claims patterns flag any extract output that misses the `ROUNDED` semantic or the `ON SIZE ERROR` fall-through. The CI workflow runs the scorer on every PR that touches a prompt — fails the merge if the aggregate score drops by more than 5 percentage points."

[Briefly mouse over the aggregate-score number.]

> "We measure prompt quality the same way we measure migration correctness."

---

## B10 · 8:30–9:30 · Cross-routine harmonisation

[Navigate to `/platform/harmonisation`. Pick the seeded corpus.]

> "One more layer. The per-routine extract — even with neighbourhood context — can't see corpus-wide drift. Two specs in the same program might disagree about what `INV_READ` does. Or use different names for the same field. Or assert contradictory COMMON-block layouts. We run a separate pass for that."

[Click **Run harmonisation pass**. Wait ~20 seconds for the LLM call to complete.]

> "One LLM call. Every signed spec in the corpus, inline. The model is told what to find: callee-IO drift, COMMON-layout drift, terminology drift, missing invariants, duplicate open questions."

[The run lands. Click into the findings drawer.]

> "It found two — one medium-severity terminology drift between two specs that named the same field `AMOUNT` in one and `TOTAL` in the other. I review, attach an admin note —"

[Type a short note.]

> "— and mark **Accepted**. That decision is audit-logged. The SME's reasoning lives next to the finding forever."

---

## B11 · 9:30–10:00 · Audit close

[Navigate to the audit trail. Filter by today's date.]

> "Forty-something audit events in the last ten minutes. Every action persona-attributed. Every signed-spec event tied to an HSM signature ID. Every LLM call recorded with its prompt id, version, model, input + output tokens, and — for the cached calls — the cache-read vs cache-create breakdown."

[Scroll the trail once, slowly.]

> "An auditor walking up to this trail would have no questions. Two languages, two target stacks, structured cross-routine context, an Admin-configurable calibration corpus, a harmonisation pass, a per-routine equivalence harness — all observable, all signed, all reproducible."

[End on the audit page.]

> "Thanks. Happy to drill into any of it."

---

## Filming notes

- Each card has roughly **2–4 lines** of verbatim narration. The pauses between cards are NOT scripted — the presenter improvises a 2–3 second transition or stays silent.
- Lines with `>` are read; everything in `[brackets]` is action notation, not narration.
- The `5847.95` line in B8 must land cleanly — slow down through that beat. It's the strongest single moment in the demo.
- If a beat runs over its budget, the **B7 test-pack run** is the soft cut: skip the test-pack validation, narrate "and the test-pack runs the same way for Java as it did for .NET" and move on.
