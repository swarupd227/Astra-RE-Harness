# Narration script — MINPACK demo (CIO-tone)

**Audience:** CIO / senior IT leadership.
**Voice model:** `en_US-libritts_r-medium` (Piper TTS).
**Target pace:** ~145 wpm.

Each beat closes with a value statement, not a description of what is on screen.
When the final recording captures the full RECORD_DEMO=1 flow (HYBRD1 walk +
validation report + 5-routine montage + dashboard wide shot), this script
replaces the previous 1:51 cut.

---

## Final cut — projected duration ~3:10

> **[0:00 – 0:08]** *(MINPACK corpus list)*
> Fifty Fortran files. Sixty subroutines. Before a line of new code is written,
> the harness has already indexed every one of them.

> **[0:08 – 0:30]** *(open HYBRD1, click Extract)*
> That structure on screen is a real parser reading the source — not a language
> model guessing. Your discovery phase is already finished. We open one routine
> and ask Claude to draft its specification. This is work that normally takes
> a senior engineer two days per routine.

> **[0:30 – 0:37]** *(caption card #1 — Anthropic extract on HYBRD1)*
> That is a production Anthropic call. Forty-eight seconds of streaming,
> compressed for the recording.

> **[0:37 – 0:55]** *(draft spec → SME review)*
> What comes back is not code. It is structured, citable claims —
> invariants, side-effects, edge-cases, open questions — every fact tied
> to specific source lines. Your experts review evidence. They are not asked
> to trust the model.

> **[0:55 – 1:25]** *(SME accept loop + sign)*
> The expert, not the AI, signs off — and three properties matter at this
> scale. One: the signature is cryptographic, audit-grade by default. Two:
> it is bound to this exact source revision. If the Fortran changes, the
> signature is automatically invalidated. If it does not, the signature
> carries forward — so you never pay to re-validate code that did not
> change. Three: every state transition lands in an immutable audit log.

> **[1:25 – 1:39]** *(engineer regenerates scaffold, caption card #2 — scaffold gen)*
> From the signed specification, the harness generates target-stack code.
> Every file traces back to the signed claims by hash. Provenance is built
> in, not bolted on.

> **[1:39 – 2:10]** *(NEW — validation report; three Run buttons land green)*
> Before that scaffold gets a green light, three independent gates run.
> Compile — the package builds against a real .NET 8 toolchain. Test
> pack — every signed claim has an auto-generated xUnit fixture, and they
> all pass. Equivalence — the harness compiles the original Fortran and
> drives both runtimes with the same input vector; the outputs match.
> All three are queryable. All three are in the audit feed. Commit is
> blocked until every badge is green.

> **[2:10 – 2:20]** *(commit lands; faux hash visible)*
> The commit lands in your existing Git workflow with the validation
> attestation attached.

> **[2:20 – 2:50]** *(NEW — batch montage, 5 routines through extract → sign at speed)*
> What you have just watched for HYBRD1 repeats, deterministically, for
> the other fifty-nine routines. Five of them, here, at speed. Same
> grounded extraction. Same SME-signed claims. Same cryptographic
> signature. Same audit trail. Different routine.

> **[2:50 – 3:10]** *(MINPACK corpus detail page — wide shot of counters)*
> Six signed in a single sitting. The economics are linear. The audit
> trail is end-to-end. What used to be a multi-quarter modernization
> is compressed to weeks. That is the time Nous gives you back.

---

## Value statements per beat (CIO sanity-check)

| Beat | Value claim |
|---|---|
| Opener | Discovery phase is pre-done — no "what's in scope" workshop |
| Extract | Two-day senior-engineer task compressed to under a minute |
| Caption 1 | Credibility — real Anthropic, not mocked |
| Claims surface | Experts review evidence, don't audit "the model said so" |
| Sign + signature | Audit-grade + supersession economics + compliance log (three CIO levers) |
| Caption 2 + scaffold | Provenance built in; compliance is a query |
| **Validation gates (new)** | **Three independent checks. Commit blocked until all green. No silent regressions.** |
| Commit | Lands in your existing workflow, with attestation attached |
| **Batch montage (new)** | **Per-routine economics are linear. The HYBRD1 walk wasn't a one-off.** |
| Wide-shot closer | Quarters → weeks; "time Nous gives you back" |

---

## Open calibration items (pre-record review)

1. **"Two days per routine" / "multi-quarter":** soften to "engineering days" /
   "months" if those numbers cannot be defended publicly.
2. **"Six signed in a single sitting":** verify the recording actually lands
   six signed routines (HYBRD1 + 5 montage). If a montage routine fails to
   match (e.g. HYBRJ1 not present in MINPACK), tweak the routine list in
   `minpack-demo.spec.ts:montageRoutines`.
3. **Closer line:** "time Nous gives you back" — swap for product-name-led
   close if preferred.
4. **CIO vs CTO calibration:** current draft leans CIO. If the audience is
   mixed CIO/CTO and we need slightly more architectural credibility, swap
   one or two beats to mention things like "type-safe scaffolding" or
   "dependency-injected output" without losing the executive register.

---

## Recording instructions

```powershell
# pre-flight
Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue   # see runbook §8 — shadowed empty var
docker compose --env-file .env up -d --force-recreate api         # picks up real Anthropic key

# clear stale state so HYBRD1 + montage routines start in PARSED
curl -X POST http://127.0.0.1:38080/api/v1/dev/reset -H "X-Dev-Persona: engineer"
# wait until MINPACK reseed is PARSED before continuing (see runbook §8)

# record (real Anthropic, ~12-15 min wall clock, ~$0.35 in API)
cd app/e2e
$env:RECORD_DEMO = "1"
npx playwright test minpack-demo --reporter=line

# trim cuts the LLM-wait segments and stitches captions
node scripts/trim-demo.mjs
# → outputs e2e/test-results/trim-work/demo.mp4
```

After the trim:
- 7 LLM-wait caption cards (1 HYBRD1 extract + 1 scaffold gen + 3 validation + 5 montage extracts) at 1.5s each = ~10s of captions replacing ~5-7 min of waits.
- Expected final length: **3:00 – 3:15**.
