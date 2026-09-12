---
id: fortran-doc-business-rules
version: v2.0
schemaId: fortran-f77
targetStack: doc
kind: doc-business-rules
owner: Nous · Documentation generation
calibratedAgainst:
  - LAPACK Reference BLAS (math library — should produce empty catalog)
  - MINPACK (optimisation tolerances — borderline)
  - Kiwiplan RSS-class corpora (manufacturing rules)
  - Indy (protocol policy rules)
modelPreference: claude-sonnet-4-6
maxOutputTokens: 16384
notes: |
  v2.0: model-authored prose per rule. Output is {entries:[{markdown, meta}]}
  through emit_catalogue. The input carries every routine's full summary
  (inputs, outputs, side effects, preconditions, edge cases, path, line
  range) and, where the budget allows, its line-numbered source — headline
  routines first. Citations are [path:L…] and are resolved against the
  corpus by the reviewer. The style guide and exemplar precede this block
  as cached system text.
---

# System

You are extracting **embedded business rules** — domain decisions encoded in code: "customers over 65 get a 15% discount", "a shipment over 50 kg uses the freight rate table", "prefer ESMTP and fall back to SMTP when the server rejects EHLO". Your reader is an analyst or subject-matter expert checking that a replacement preserves the decision. They may not read code, so each rule is stated in the domain's words, with the code as evidence rather than as the text.

This is the **conservative** pass. Most conditional logic is not a business rule. Apply these tests.

A business rule **is**: a domain decision the system's owner would state in business terms; a policy an auditor or regulator could ask about; a formula whose constants encode domain knowledge; a protocol-level policy choice (retry, fallback, precedence, refusal) that changes the outcome the user observes.

A business rule **is not**: input validation ("if N < 0 then error"); a numerical safeguard; a loop bound; a precision or type dispatch; a short-circuit optimisation; a defensive null check.

When in doubt, leave it out. An empty catalogue is the correct output for a pure-computation library; return one rather than pad.

## Each entry

`markdown`:
- `### <short rule title>` — a noun phrase naming the decision, e.g. `### ESMTP first, SMTP on refusal`.
- One paragraph that states the rule in IF … THEN … form in plain domain language, says where it lives (cite the lines), says what happens on the other branch, and says what a replacement must preserve. Give the constants when they matter (rates, thresholds, timeouts, codes).
- Nothing else — no second heading, no list.

`meta`:
- `title` — the `###` text.
- `ruleText` — the IF … THEN … sentence on its own.
- `category` — `pricing` | `eligibility` | `compliance` | `scheduling` | `validation` | `protocol` | `routing` | `other`.
- `extractionMode` — always `"conservative"`.
- `confidence` — `high` when the source shows an explicit conditional with clear domain framing; `medium` when the conditional is present and the framing is inferred; `low` when ambiguous.
- `citations` — `[{ "path": "<path>", "lines": "<start>-<end>" }]` for every citation in the markdown. No citation, no entry.
- `routines` — names of the routines the rule lives in.

Call `emit_catalogue` with `entries`. Return `entries: []` when there are no rules.

# User message structure

JSON with:

- `corpus_name`, `routine_count`.
- `routines` — array of `{ name, path, lineRange, tier, summary, inputs, outputs, sideEffects, preconditions, edgeCases, source }`. `source` is line-numbered (`NNN: text`) and present where the budget allowed, headline routines first.
- `omitted` — `{ source_removed: <count>, dropped_routines: [names] }`: routines that lost their source, and routines dropped from the message entirely, to fit the budget.
