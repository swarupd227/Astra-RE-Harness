# Statements, grounding, packs and completeness

*How a deliverable is written as checkable statements, what keeps each one honest, how a programme's
deliverables and scoring are described as data, and how the completeness score is computed. The evidence
these statements cite is described in `artefact-adapters.md`; the code is under `Knowledge/`.*

## Statement documents (`statements/v1`)

A deliverable is not a blob of markdown. It is a list of **statements**, each tied to the **slot** it fills and to
the **evidence** that shows it, so every sentence can be checked, reviewed and scored on its own. Markdown is
*derived* from the document for export (`StatementMarkdown.Render`); it is never the source.

```
document  { schema, kind, title, confidence?, sections[], conflicts[], openQuestions[] }
section   { heading, items[] }
item      { id, slotKey, text, citations[], status?, support?, notes? }
conflict  { itemId, evidenceIds[], summary }
question  { id, text, slotKey?, citations? }
```

- `citations` are evidence short ids (`SQL-3fa9c01e`) of the project's current version.
- `id` is `st-` plus a hash of the slot, the normalised words and the first piece of evidence, so a statement keeps its
  identity when a document is written again (a review or an edit attached to it still finds it) and changes when what
  it says, or what it rests on, changes.
- `status` and `support` are set **only by the gate**, never trusted from the writer's answer.
- A model's answer is read forgivingly (property case, `[sql-3FA9C01E]` becomes `SQL-3fa9c01e`, a missing id is
  assigned) and strictly about what makes a statement unusable (no text). What was fixed or dropped comes back as plain
  sentences.

## The grounding gate (`Knowledge/Grounding/GroundingGate.cs`)

Pure: a document and the evidence of one version in, a document in which every statement is grounded or gone out.

| # | Check | Fails when | Needs a model |
|---|---|---|---|
| 1 | Every statement cites | no citation → **dropped** (`no-citation`) | no |
| 2 | Every citation exists in this version | a citation not in the version is **removed**; a statement left with none is dropped (`unknown-citation`) | no |
| 3 | Every identifier appears verbatim in a chunk it cites | a name, code, path, URL or setting not in the cited evidence → **dropped**, naming each (`identifier-not-in-evidence`) | no |
| 4 | The cited evidence says what the statement says | verdict `no` → **dropped** (`support-no`); `partly` → kept, **unverified** | yes (a small model, batched) |

**Identifiers** (`IdentifierExtractor`): backticked spans (checked whole, or word by word for a command with
arguments), names with an underscore (`TLR_EOD_POST`, `usp_PostStage`), compound names (`OfacScreeningClient`; common
product names such as `PowerShell` are exempt), error numbers after `error`/`errno`/`sqlcode`, ticket codes
(`INC-T-0412`), settings (`Ofac:TimeoutSeconds`), URLs and file paths. A plain word is never an identifier. Matching ignores
case and whitespace and treats `\`, `/` and doubled slashes as the same, so `\\dw-stage\teller\` is found in
`//dw-stage/teller/`. The evidence's label counts as evidence text.

**Status** is decided by a rule, in this order, for a statement that passed:

1. **Conflict** — only when a conflict for it names **two different pieces of evidence that exist**. A conflict that
   names one, the same one twice, or an invented one is *not recorded*; the statement is downgraded to Unverified and the
   gate says so (`conflict-not-evidenced`).
2. **Supported** — the claims pipeline verified it, **or** structured evidence is cited and the support check said `yes`.
3. **Unverified** — everything else: only narrative evidence (a transcript, a ticket, an old document), a `partly`
   verdict, or a check that was not run (`support-not-checked`: nothing can be Supported on unchecked evidence).

`Check` runs 1–3, `Finish` runs 4 and the status, `Evaluate` runs both. After the last attempt a statement that still
cannot be grounded is not asserted and not lost: `DemoteDropped` turns it into an open question,
*"Not confirmed by the evidence: …"*.

## The statement writer (`Knowledge/Writer/StatementWriter.cs`)

Writes the statements for one slot. The model writes; a program decides what stands.

1. One forced-tool-use call (`write_statements`, temperature 0) with only the given evidence in front of it. The prompt
   is `Llm/Prompts/knowledge/statement-writer.v1.md`. Evidence the writer cites without having been shown is looked up in
   the version; what is still missing does not exist.
2. The gate's checks 1–3 throw out what fails them.
3. What is left goes to a **separate, smaller model** (`Knowledge:SupportModel`, Haiku by default) in batches of 20: does
   the cited evidence say what each statement says? (`support-check.v1.md`, tool `report_support`.) The writer never grades
   its own work. If that call fails, the reason is reported and those statements are Unverified.
4. The gate gives every survivor its status.
5. If anything was thrown away and an attempt remains (`Knowledge:MaxAttempts`, default 2), **one retry** rewrites only
   that part; it is told what already stands and why each statement was thrown away. Survivors of both attempts are merged.
6. What the last readable attempt still threw away becomes an open question.

Every call goes through `IDocWriter` (so mock, pricing, retry and the rate limiter apply) and is returned so it can be
recorded as an `LlmCall`; nothing under `Knowledge/` talks to a model directly (a unit test enforces it). Failures are
reported with the provider's own reason.

`DocWriteRequest` gained three optional trailing fields for this: `Temperature` (sent to Anthropic only when set — 0 for
every knowledge call; every other pipeline still sends none), `ReplayKey` (`step:item:purpose:attempt`, names a call so a
recorded or canned answer can be found again) and `RunMode`.

### Offline: the mock and fixtures

With `Docs__Generator__Provider=mock` the writer returns the request's own mock answer. For the statement writer that is,
in order: a **fixture** for the call's replay key if one exists, else one statement per piece of evidence taken from its
own first line. The mock support check answers by how many of the statement's words (4+ letters) are in the cited
evidence: ≥ 60% `yes`, ≥ 30% `partly`, else `no` — deterministic, and it exercises all three outcomes.

A fixture is a file `Llm/Packs/<pack>/fixtures/<key with every run of other characters replaced by "_">.json` holding the
tool input JSON — what a writer would have said. It is how a screen or a test sees a statement the gate drops, one that is
unverified, or a conflict, **without a model**: everything after the fixture (parsing, the gate, the support check, the
status) runs for real.

## Packs (`Llm/Packs/<id>/pack.json`)

A pack is data: the deliverables a kind of programme produces, their weights, the slots each must fill, how approvals earn
credit, and how the result is banded. Nothing in the platform is named for a client or an application. A project chooses
a pack in its settings; `GET /api/v1/packs` lists what ships and `GET /api/v1/packs/{id}` returns one.

```
{ id, title, version, vocabulary{}, credit{approvedAllSupported, approvedWithUnverified, otherwise},
  bands{highAbove, mediumFrom}, targets{day90, day180, day365}, tierWeights{},
  deliverables[ { id, label, weight, generator,
                  slots { static[ {key, label, brief} ], entity { keyPrefix, countKey, label, brief } } } ] }
```

- A **static slot** exists once (`runbook:failure-handling`); its `brief` is what a writer is told to write about.
- **Entity slots** exist once per thing the project turned out to have (`entity:job:TLR_EOD_POST`: prefix plus the thing's
  name). `countKey` names the entry of the project's `expectedCounts` that says how many there should be.
- A pack that is wrong is refused whole with every reason (`PackLoader`); a pack that does not load is reported in
  `problems` and never stops the API starting.
- A deliverable is stored under the section kind `d-<id>` with scope `deliverable` (`DeliverableKinds`), never under a
  legacy kind; `DeliverableRegistry` stands in for the lists of kinds the older documentation code hard-wires.

The shipped pack, `knowledge-transfer`: overview 10, context diagram 10, interface map 15, batch job sheets 15, runbook
20, known errors 15, environments/access/contacts 10, open questions 5.

## Completeness (`Knowledge/Slots/Completeness.cs`, `POST /api/v1/packs/{id}/completeness`)

Pure arithmetic a person can check by hand: a pack, the things the project has (`derived`), the counts it expects and what
has been approved in, a score out. No clock, no model, no database.

- A **version** of a slot earns **full credit** when it is approved and every statement in it is Supported, **partial
  credit** (0.5) when approved with at least one Unverified, and **nothing** when it is not approved or a conflict is open.
- A slot earns its **best credit over every approved version**: approving a later version can never lower the score.
- A deliverable's share is earned over its slots. For entity slots the project's **expected count is the denominator**
  when it gives one (a thing nobody found still counts against it, shown as a `missing` slot), and credit beyond it is not
  counted. Expected counts set denominators only; they are never shown to a writer.
- A deliverable with **no slots to fill** (no jobs found or expected) takes no part in the score.
- The total is the weighted mean of the deliverables that apply. `dependencyPercent` is 100 minus it; `dependencyBand` is
  `high` above `highAbove`, `medium` from `mediumFrom`, else `low`.

The design's worked example is a test: only what an older document covered scores **18.2%**; after a scripted review
(credits slot by slot) **76.5%** (overview 4.5/5, interfaces 7.5/8, jobs 5.5/6, runbook 3.5/5, known errors 3/5,
environments 2/3, the rest as stated).

## Statement preview (`POST /api/v1/corpora/{id}/statements/preview`)

What the writer and the gate make of one slot, **shown and not saved** — the seam the rest of capture is built on, and
the way to try a slot by hand. Engineer, SME or Admin (it calls a model). Body: `slotKey`, optionally `instruction` (else
the slot's brief from the pack), and the evidence the writer may cite as `evidenceIds` or a `query` (best matches, up to
`limit`, at most 40). The answer carries the document, derived markdown, the gate's counts / attempts / what was dropped
and why, notes, the evidence shown, the provider and every call (prompt, model, replay key, temperature, tokens).

## Not here yet

Persisting statement documents as sections and approving them (K10a), the claims and conflict linker that supply
`VerifiedItemIds` and `conflicts` (K7), the per-deliverable writers that choose a slot's evidence (K8), the capture run
and recordings (K9), and the first live run with a real key against the sample dataset (the "tracer bullet").
