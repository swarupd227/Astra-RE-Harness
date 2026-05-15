# Astra RE Harness — operational runbook

**Audience:** the Nous engineer leading an engagement, the customer engineer adopting the harness, and the operations team keeping it running.

**Scope:** what you need at engagement-start, day-2 authoring (schemas, prompts, archetypes), validation-gate operation, compliance feed integration, and incident response. No marketing copy.

> Quickstart at the top, deep references below. If you only have 30 minutes the §1 checklist is the floor.

---

## Contents

1. [Engagement-start in 30 minutes](#1-engagement-start)
2. [Architecture map](#2-architecture-map)
3. [Schema authoring](#3-schema-authoring)
4. [Prompt authoring](#4-prompt-authoring)
5. [Archetype authoring](#5-archetype-authoring)
6. [Validation-gate operator's guide](#6-validation-gates)
7. [Compliance feed integration](#7-compliance-feed)
8. [Incident playbook](#8-incidents)
9. [Operational metrics](#9-metrics)
10. [Appendix — env vars, ports, indices](#10-appendix)

---

<a id="1-engagement-start"></a>

## 1. Engagement-start in 30 minutes

Goal: a signed spec on the demo corpus inside half an hour. If this loop works, the harness is operational and you can start the customer-specific authoring track.

```bash
# Clone + first boot
git clone <repo>
cd <repo>/app
cp .env.example .env                # add ANTHROPIC_API_KEY if going real
docker compose --env-file .env up -d --build
```

Wait until all six services report healthy (`docker compose ps`). First boot pulls images and restores NuGet, so budget 6-8 minutes.

```bash
# Open the dashboard
open http://127.0.0.1:35173/        # macOS — or just visit in a browser

# Confirm the seed corpus
curl -s http://127.0.0.1:38080/api/v1/corpora \
     -H "X-Dev-Persona: engineer" | jq '.data[].name'
#  "Roll-stock inventory demo (Fortran F77)"
#  "MINPACK (F77 nonlinear least squares)"  # if DATABASE_SEED_MINPACK=true
```

In the UI:

1. **Projects → Roll-stock inventory demo → CONSUME_ROLL** — open the synthesised seed.
2. **Extract spec** — kicks the LLM. On `mock`: ~12 s. On `anthropic`: ~45 s.
3. **View draft spec → Route to SME**.
4. Switch persona to **SME** (top-right menu) — review every claim → **Sign spec**.
5. Switch back to **Engineer** — **Generate scaffold** → **Open scaffold artifact**.
6. **Validation report** (new in #2d) — click **Run compile**, **Regenerate + run** (test pack), **Run equivalence** in order. All three should land green.
7. **Commit to Git** — gives you a stub commit hash + branch name.

If all seven steps land you have a working harness against the synthesised demo. The customer-specific work is then schemas / prompts / archetypes per §3-5.

### Persona switch reminder

The UI uses `X-Dev-Persona` to fake auth — engineer, sme, system. The persona is per-tab, stored in `localStorage` under `astra.devPersona`. SME-only actions (sign spec, accept claim) return 403 if the engineer tries them. Persona switch is the top-right control.

### Provider switch

`.env` →`LLM_PROVIDER=mock|anthropic|fail-mock`. Recreate the api container after editing:

```bash
docker compose --env-file .env up -d --force-recreate api
```

`mock` reproduces the canonical CONSUME_ROLL spec deterministically — use it for fast local dev. `anthropic` requires a valid `ANTHROPIC_API_KEY` and costs ~$0.04-$0.06 per HYBRD1-class extract. `fail-mock` simulates a mid-stream provider failure for the chaos-extract test.

---

<a id="2-architecture-map"></a>

## 2. Architecture map

| Container | Role | Where state lives | When to restart |
|---|---|---|---|
| **postgres** | Operational DB (corpora, specs, scaffolds, audit, validation runs) | volume `postgres-data` | rarely — schema migrations only |
| **minio** | Blob store (sources, signed specs, scaffolds, validation logs) | volume `minio-data` | rarely |
| **parser-sidecar** | Python + fparser2 + gRPC. Single-threaded behind `_PARSE_LOCK` (fparser2's `SymbolTable` is not thread-safe — see §8) | none, ephemeral | when you change `parser_sidecar/server.py` |
| **gfortran-sidecar** | Python + gfortran + REST. Compiles + runs Fortran on demand for #2c equivalence | none, ephemeral | when you change `gfortran_sidecar/server.py` |
| **api** | .NET 8 + ASP.NET Core. State machine + endpoints | reads pg + minio | every code change (dotnet watch handles most) |
| **worker** | .NET 8 background worker. Idle today — Hangfire lands in Phase D | reads pg | rarely |
| **frontend** | Vite + React + Tailwind. Talks to api on 38080 | none | every UI change (Vite HMR handles most) |

State-of-truth notes:

- **Append-only log:** `audit_events` is never updated or deleted. Every state transition lands here. This is the source for both the evidence-trail UI and the SOX / HIPAA / PCI feed.
- **Signed-blob archive:** every signed spec writes a canonical-JSON blob to `signed-specs/` in MinIO at sign time. The DB carries the hash; the blob is the proof.
- **Scaffold artifacts:** every generated scaffold writes a manifest JSON to `scaffolds/` in MinIO. Cross-runtime validation logs land at `scaffolds/validation/<run-id>/`.
- **Source corpora:** every ingested source file lives at `sources/<sourceVersionId>/<relativePath>`. Re-ingest creates a new `sourceVersionId` — old versions stay readable.

Ports (host-side, all `127.0.0.1`):

| Port | Service | Notes |
|---|---|---|
| 35173 | frontend | Vite dev server |
| 38080 | api | REST + SSE |
| 38081 | worker | health only today |
| 38432 | postgres | psql access |
| 39000 / 39001 | MinIO API / console | `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` from `.env` |
| 50051 | parser-sidecar | gRPC |
| 51052 | gfortran-sidecar | REST |

### Dev-reset

`POST /api/v1/dev/reset` drops + recreates the `public` schema, re-seeds the synthesised CONSUME_ROLL corpus, and kicks a background MINPACK reseed if `DATABASE_SEED_MINPACK=true`. **Gated on `Dev:ResetEnabled=true`** — never enable this in a real customer environment.

---

<a id="3-schema-authoring"></a>

## 3. Schema authoring

Spec schemas declare the **claim taxonomy** for one source language. They live at:

```
app/api/src/Astra.Api/Llm/Schemas/<source-language>.schema.json
```

We ship two as of this revision: `fortran-f77` (production) and `cobol` (preview). Adding a third — RPG, PL/I, Natural, ABAP — is a JSON drop, no code change.

### Authoring loop

1. Copy `fortran-f77.schema.json` to `<lang>.schema.json`.
2. Update `id`, `displayName`, `description`, `supportedSourceExtensions`, `compatibleTargetStacks`.
3. Replace `claimKinds[]` with the kinds you actually want surfaced. Common pattern per kind: `{ id, label, idPrefix, specJsonField, textField, displayTone, description, fields[] }`. `specJsonField` is the snake-case array name in the persisted spec JSON; `textField` is the field carrying the human-readable claim text.
4. Update `topLevelFields[]` for anything that lives outside the claim arrays (`summary`, `inputs`, `outputs`, `copybooks_referenced`, etc.).
5. Update `promptHints` — `minimumClaimsPerKind` is a soft contract the calibration prompt should respect.
6. Restart the api container. `SpecSchemaProvider` reloads at startup.
7. Verify: `GET /api/v1/spec-schemas` shows your new schema.

### Validation rules to test

- **At least one invariant kind.** Without it the test-pack generator produces zero tests.
- **`textField` must exist on every claim object** the LLM returns. The prompt's output schema and the file's `claimKinds[].textField` must match — mismatch = empty `claim` text in the generated tests.
- **`idPrefix` must be unique across claim kinds in the same schema.** Two kinds with `idPrefix: "INV"` collide in test-method names.
- **Re-extract one real subroutine** end-to-end after editing. The generated test pack should have one `[Fact]` per claim id; if not, recheck `specJsonField` for that kind.

### Engagement-time playbook

A new source language always takes three engagements to harden:

- **Engagement 1:** ship the schema with the obvious kinds (invariant + side-effect + edge-case + open-question). Discover what's missing as the SME rejects claims.
- **Engagement 2:** add the language-specific kinds (section contract for COBOL, COMMON-block contract for Fortran, etc.). Tune `minimumClaimsPerKind` from observed coverage.
- **Engagement 3:** schema stabilises; subsequent customers in that language reuse it. Promote to `status: production`.

Don't over-engineer the schema before the first engagement; the calibration is the loop.

---

<a id="4-prompt-authoring"></a>

## 4. Prompt authoring

Prompts live at:

```
app/api/src/Astra.Api/Llm/Prompts/<source-schema>/<target-stack>/<kind>.v<N>.md
```

Each prompt is one markdown file: YAML frontmatter on top, `# System` + `# User` sections below, `{{template}}` variables substituted at extraction time.

### Authoring loop

1. Copy the latest `<schema>/<target>/extract.v<N>.md` to `extract.v<N+1>.md`. Never edit a shipped version in place — versioning is the calibration history.
2. Update frontmatter: `version`, `notes` (1-3 lines explaining the change).
3. Edit `# System` + `# User`. Variables available: `{{subroutineName}}`, `{{sourcePath}}`, `{{lineCount}}`, `{{sourceText}}`.
4. Touch any `.cs` file under `Astra.Api/Llm/` or restart the api container — `PromptLibrary` reloads at startup.
5. Verify: `GET /api/v1/prompts` lists `v<N+1>` with the new owner/notes. Run one extraction end-to-end; the resulting `LlmCall` row should carry `prompt_template_version=v<N+1>` and the `spec.extracted` audit event should agree.

### Calibration rules

- **Cite, cite, cite.** Every claim must have a citation. The harness validates citations against source line numbers and downgrades a claim's confidence if the citation falls outside the file.
- **Distinguish implements vs assumes.** The single most-effective rule we've shipped — without it the LLM happily invents reasonable-sounding invariants that have no source basis.
- **Magic constants → open question.** Same idea: surface the unknown rather than confabulate.
- **Output as a single JSON object, no markdown fences.** The parser is strict; if the LLM wraps the JSON in ```json ... ``` you lose the extraction. The system prompt has this rule for a reason.

### A/B versioning

Shipping a new prompt against real customer corpora always has a regression risk. Recommended pattern:

1. Author `v<N+1>.md` alongside the current `v<N>.md`.
2. Pin a subset of corpora to the new version via per-corpus config (Phase D — until then, swap the latest pointer once you're confident).
3. Compare claim coverage + reject-rate per kind between the two versions on a held-out 5-routine sample.
4. Promote when the new version dominates on at least 4 of 5.

Today's `GetLatest("fortran-f77", "dotnet8", "extract")` picks whichever version sorts highest after stripping the leading `v`. So `v3.3` will be picked over `v3.2` automatically once it lands.

---

<a id="5-archetype-authoring"></a>

## 5. Archetype authoring

Scaffold archetypes are directory trees at:

```
app/api/src/Astra.Api/Llm/Archetypes/<target-stack>/<archetype-id>/
  archetype.json
  <... the actual files the scaffold should ship ...>
```

We ship two: `dotnet8/canonical-rollstock` (production, 8 files, buildable + test-runnable as-shipped) and `java-spring/canonical-rollstock` (preview, 3 files — service + test + pom). Adding `python-fastapi/canonical-rollstock` or `node-nestjs/canonical-rollstock` is a directory drop.

### Authoring loop

1. Create `Llm/Archetypes/<target>/<id>/`.
2. Drop the template files at their final scaffold paths (paths used in `archetype.json` become paths in the scaffold output verbatim).
3. Author `archetype.json`. Required fields: `id`, `targetStack`, `displayName`, `description`, `compatibleSchemas[]`, `files[]`. Each `files[]` entry: `{ path, language, derivedFromClaimIds[] }`.
4. The harness ships an `id="canonical-<corpus>"` per (target, customer-corpus) pair. The `matches.anyOf[].subroutineName` selector picks which archetype activates per scaffold call — exact match wins, else first archetype for the target stack.
5. Restart the api container. `ArchetypeRegistry` reloads at startup.
6. Verify: `GET /api/v1/archetypes` shows your new archetype with correct fileCount + claim refs.
7. Verify buildability: run **#2a compile validation** against a scaffold using your archetype. Green = ships.

### Target-stack-specific gotchas

- **.NET / C#:** the `.csproj` must declare `<DefaultItemExcludes>$(DefaultItemExcludes);tests/**</DefaultItemExcludes>` on the src project so the tests folder doesn't get included twice. Tests project goes in `tests/` with a `<ProjectReference>` back to src.
- **Java / Spring:** Spring Boot needs a `pom.xml` with `spring-boot-starter` + JUnit 5 + Mockito. Source goes at `src/main/java/...`, tests at `src/test/java/...` — matching Maven conventions, otherwise the build tool doesn't find them.
- **Python / FastAPI** (not shipped yet): plan to ship `pyproject.toml` + `app/__init__.py` + `app/service.py` + `tests/test_service.py`. `pip install -e .[dev]` then `pytest`.
- **Node / NestJS** (not shipped yet): `package.json` + `tsconfig.json` + `src/` + `test/`. Either Jest or Vitest — pick one per archetype.

### Renaming + relocating files

If you move a template file inside the archetype directory you MUST also update `archetype.json.files[].path` — the registry validates each declared path against the filesystem at load time and refuses to register an archetype with a dangling reference. Better to fail loud than ship a half-empty scaffold.

### When to author a new archetype vs. fork an existing one

- **Same target stack, different domain (e.g. roll-stock vs. claims processing):** fork into a new `<id>` in the same target directory. Use `matches.anyOf[].subroutineName` to disambiguate.
- **Different target stack:** new target directory.
- **Same archetype, different version:** today archetypes don't carry version (one canonical per target/id). When that becomes an issue we'll add `v<N>` semantics matching the prompt library; until then, branch under a new id (`canonical-rollstock-v2`).

---

<a id="6-validation-gates"></a>

## 6. Validation-gate operator's guide

Three independent gates run against every generated scaffold:

| Gate | What it asserts | Typical timing |
|---|---|---|
| **COMPILE** | `dotnet build` exits 0 against the materialised package | ~3-7 s warm cache, ~30 s first run (NuGet restore) |
| **TEST_PACK** | `dotnet test tests/` exits 0; all auto-generated + engineer-authored tests pass | ~8-15 s warm cache |
| **EQUIVALENCE** | A canonical smoke case (gfortran reference + C# reference agree on inputs) | ~1-3 s (gfortran is fast) |

All three are surfaced on `/scaffolds/{id}/validation` — three cards, three badges, one overall verdict banner. The commit endpoint blocks until all three pass when `Validation:CommitGateRequired=true`.

### Red-card playbook

**Compile red:**

1. Read the build log — `View log` on the COMPILE card or `GET /api/v1/validation-runs/{id}/log`.
2. Most common cause: a template-time edit broke the .csproj. Confirm `Demo.RollStock.csproj` (or equivalent) has valid XML and a `<TargetFramework>` line.
3. Second most common cause: missing package reference. Add it to `Demo.RollStock.Tests.csproj` in the archetype, restart api, regenerate scaffold.

**Test-pack red:**

1. Read the test runner log. Look for `Failed!` lines or `Test Run Failed.` markers.
2. If the failures are in the generated `_SignedSpecPack.cs` file: the schema's `textField` probably doesn't match the persisted JSON. Open one claim row in `psql` and confirm the field name.
3. If the failures are in the hand-authored `<Subroutine>ServiceTests.cs`: the engineer changed the service contract but not the tests. Usually a fix-the-test, not a fix-the-service issue.
4. Re-running the gate creates a new `ValidationRun` row — the badge shows the latest only, but `GET /api/v1/scaffolds/{id}/validation` lists every run for history.

**Equivalence red:**

1. The smoke case is canonical so a red here usually means infra: the gfortran sidecar is unreachable.
2. Check `docker compose ps gfortran-sidecar`. If unhealthy, see §8.
3. If healthy but the diff is non-zero, the C# reference (today hardcoded `3 + 4`) genuinely disagrees with what gfortran produced for the same Fortran input. This shouldn't happen against the smoke case; if it does, file a bug.

### Force bypass policy

`POST /scaffolds/{id}/commit` with body `{"force": true}` bypasses all three gates and writes `validationBypassed: true` into the `scaffold.committed` audit payload. Use cases:

- **Engagement demo where the C# implementation isn't done yet** — the engineer wants the commit hash to demonstrate the loop end-to-end before refining implementation.
- **Provable emergency hotfix** — the customer needs a commit before validation can complete (rare).

Every bypass is in the audit log AND in the SOX compliance feed (column `validation_bypassed`). A CIO can answer "did anyone ship without green gates this quarter" with one query.

---

<a id="7-compliance-feed"></a>

## 7. Compliance feed integration

The harness ships three audit-feed formats: **sox** (production), **hipaa** (preview), **pci** (preview).

```
GET /api/v1/compliance/formats                    # list formats + columns
GET /api/v1/compliance/feed?format=sox            # download CSV
GET /api/v1/compliance/feed?format=sox&since=2026-04-01T00:00:00Z&until=2026-07-01T00:00:00Z&severity=critical
```

The SOX feed is the reference shape. 16 columns, RFC-4180 CSV, ready to drop into Workiva / AuditBoard / SailPoint. Joins each audit event with the related signature so each row carries the source revision hash and the signed spec canonical hash where applicable.

### Severity mapping (today)

| Severity | Events |
|---|---|
| **critical** | `spec.signed`, `scaffold.committed` |
| **high** | `spec.superseded`, `claim.reject` |
| **medium** | `validation.completed`, `spec.extracted`, `claim.accept` |
| **low** | `spec.routed`, `scaffold.generated`, `test_pack.generated` |
| **info** | `corpus.ingested`, `source.parsed` |

If your customer's evidence platform expects a different severity vocabulary (e.g. P1/P2/P3, info/warn/crit), patch `ComplianceFeedExporter.MapSeverity` — single switch statement.

### Sample queries

**Quarter-end SOX evidence pack:**

```bash
curl -OJ "http://127.0.0.1:38080/api/v1/compliance/feed?format=sox&since=2026-04-01T00:00:00Z&until=2026-07-01T00:00:00Z" \
     -H "X-Dev-Persona: engineer"
# → compliance-sox-20260714T0900Z.csv
```

**Just the critical events (every sign + every commit):**

```bash
curl -OJ "http://127.0.0.1:38080/api/v1/compliance/feed?format=sox&severity=critical" \
     -H "X-Dev-Persona: engineer"
```

**Find every commit that bypassed the validation gate:**

```bash
curl -s "http://127.0.0.1:38080/api/v1/audit?type=scaffold.committed" \
     -H "X-Dev-Persona: engineer" \
   | jq '.data[] | select(.payload.validationBypassed == true)
                 | { ts: .occurredAt, actor: .actorDisplay, hash: .payload.commitHash }'
```

### Meta-audit

Every `/compliance/feed` pull writes a `compliance.feed_exported` audit row with the format, row count, time range, and filename. Pull the meta-audit to see who pulled evidence and when:

```bash
curl -s "http://127.0.0.1:38080/api/v1/audit?type=compliance.feed_exported" \
     -H "X-Dev-Persona: engineer" | jq '.data[] | { ts: .occurredAt, actor: .actorDisplay, params: .payload }'
```

### Mapping to common evidence platforms

| Platform | How to ingest |
|---|---|
| **Workiva Wdesk** | Schedule a cron-pulled SOX CSV into Wdesk's data-connection API; the column names match Workiva's evidence-grid defaults. |
| **AuditBoard** | Use the CSV import flow; map `event_type` → control activity, `subject` → control entity, `signature_key_id` → custodian. |
| **Splunk ES / Sentinel** | Pull the JSON variant on a 5-minute interval (Phase D — until then, run the CSV through `xsv` or `mlr` and ship to syslog). |
| **SailPoint IdentityIQ** | The `actor_id` + `actor_persona` columns plug into SailPoint's identity-event ingester. |

---

<a id="8-incidents"></a>

## 8. Incident playbook

### Parser sidecar wedged

**Symptom:** ingest hangs; new specs say `parser_rpc_failed` in their warnings; older specs say "0 subroutines, 1 warning".

**Cause:** fparser2 has a global `SymbolTable` singleton that is **not** thread-safe. Concurrent parse calls corrupt it. The sidecar serialises every Parse RPC behind a process-wide lock (`server.py: _PARSE_LOCK`), so this should only happen if a build accidentally removed that lock.

**Fix:**

```bash
docker compose logs parser-sidecar --tail 100 | grep -i error
# Look for "SymbolTableError: exit_scope() called but no current scope exists"
# If that's the symptom, confirm parser_sidecar/server.py wraps parse_source with _PARSE_LOCK
docker compose --env-file .env up -d --force-recreate parser-sidecar
```

### Anthropic rate-limited

**Symptom:** extractions return `error: rate_limit_exceeded` or take >180 s.

**Fix:**

- Short-term: switch to `mock` (`.env: LLM_PROVIDER=mock`, `docker compose up -d --force-recreate api`).
- Medium-term: stagger extractions — don't fire 10 in parallel. The harness today does one at a time per worker so this only bites batch reruns.
- Long-term: upgrade the Anthropic plan or wire the Azure OpenAI adapter (Phase D).

### MinIO disk full

**Symptom:** uploads return 5xx, scaffold generation fails at the manifest write.

**Fix:**

```bash
docker exec astra-re-harness-minio-1 mc du local --recursive | sort -h
# Most-likely-culprit buckets: scaffolds (validation logs), sources (re-syncs)
# Phase D ships a sweeper; meanwhile:
docker exec astra-re-harness-minio-1 mc rm --recursive --force local/scaffolds/validation/<old-run-prefix>
```

### Postgres connection storm

**Symptom:** api logs `Npgsql.NpgsqlException: Connection pool exhausted`.

**Fix:** restart the api container. The pool default is 100; for the harness's load profile (~10 r/s peak) this should never exhaust. If it does, the leak is in code — open the next API change-set and look for missing `await using` on `AppDbContext`.

### Empty ANTHROPIC_API_KEY in shell env

**Symptom:** API logs say `provider=anthropic, ApiKey=(length=0)` even though `.env` has a valid key. (This bit us during demo recording — see #2c notes.)

**Cause:** shell env vars override `.env`. If the parent shell has `ANTHROPIC_API_KEY=` (empty), docker compose substitutes `""` for `${ANTHROPIC_API_KEY:-}`.

**Fix:**

```bash
# Bash
unset ANTHROPIC_API_KEY
# PowerShell
Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
docker compose --env-file .env up -d --force-recreate api
```

### dev/reset escape hatch

If state is wedged in a way no individual `POST /api/v1/<resource>/<id>/<action>` can recover, drop the schema:

```bash
curl -X POST http://127.0.0.1:38080/api/v1/dev/reset \
     -H "X-Dev-Persona: engineer"
```

This rebuilds the schema from EF Core model + re-seeds the synthetic CONSUME_ROLL corpus. **Never enable in customer environments** — `Dev:ResetEnabled` is `false` by default.

### Background MINPACK reseed never finishes

**Symptom:** `subroutines-search` test skips because MINPACK isn't present.

**Cause:** the reseed clones MINPACK from GitHub and parses 50 files. With a slow network or congested parser sidecar this can exceed the 30-second poll. Bump the poll or wait.

```bash
curl -s http://127.0.0.1:38080/api/v1/corpora -H "X-Dev-Persona: engineer" \
   | jq '.data[] | select(.name | startswith("MINPACK"))'
# state should be PARSED with fileCount=50, totalLoc≈10825
```

### Frontend can't reach api after a force-recreate

**Symptom:** UI shows "API unreachable" banner; demo-path test times out at `page.goto`.

**Fix:** Vite's HMR socket sometimes loses the api host after a docker network rotate.

```bash
docker compose --env-file .env up -d --force-recreate frontend
```

---

<a id="9-metrics"></a>

## 9. Operational metrics

The `/system` page in the UI surfaces what's worth watching. The same data is queryable at `GET /api/v1/system/stats`.

| Metric | Healthy range | What it means |
|---|---|---|
| `corpora.total` | grows monotonically per engagement | one row per project the customer has ingested |
| `corpora.byState.PARSED` | ≈ `corpora.total` once ingest settles | anything stuck in `INGESTING` or `PARSING` after 60 s = parser issue |
| `specs.byState.SIGNED` | per-engagement target | should grow as the SME signs |
| `specs.byState.SUPERSEDED` | small but non-zero | a non-zero count means re-sync is being used; zero means nobody's re-syncing yet |
| `scaffolds.byState.COMMITTED` | tracks `specs.SIGNED` minus pending | gap = scaffolds awaiting commit |
| `llmCalls.totalCostUsd` | varies | sum of `cost_usd` across every `LlmCall` row; budget tracking |
| `llmCalls.byProvider` | mostly anthropic in production, mock in dev | if anthropic is supposed to be on but you see mock, fix `.env` |
| `validationRuns.byStage.PASSED` | grows alongside scaffolds.SCAFFOLDED | if it lags, engineers are skipping the gate |
| `auditEvents.total` | grows steadily | flatline = ingest stopped or audit logger crashed |
| `api.latency_ms p99` | < 500 ms for non-LLM endpoints | LLM-streaming endpoints are not in this percentile |

Phase D pipes these into OpenTelemetry → Jaeger → Grafana. Until then, scrape `/api/v1/system/stats` on a 60-second interval.

---

<a id="10-appendix"></a>

## 10. Appendix

### Environment variables (read from `.env`)

| Var | Default | Notes |
|---|---|---|
| `POSTGRES_*` | astra / astra_dev_pw / astra | DB user / password / db name |
| `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` | astra / astra_dev_pw | console login at `:39001` |
| `LLM_PROVIDER` | mock | `mock` / `anthropic` / `fail-mock` |
| `LLM_SCAFFOLD_PROVIDER` | mock | scaffold-side; `mock` only in Phase C |
| `ANTHROPIC_API_KEY` | (empty) | needed when LLM_PROVIDER=anthropic |
| `ANTHROPIC_MODEL` | claude-sonnet-4-5-20250929 | pin per engagement |
| `ANTHROPIC_BASE_URL` | https://api.anthropic.com | swap for the no-train / no-retention enterprise endpoint |
| `DATABASE_SEED_MINPACK` | false | flip on to pre-load the MINPACK corpus |
| `DEV_PERSONA_BYPASS` | true | dev-only; off in production |
| `Dev:ResetEnabled` | false | never enable in customer environments |
| `Validation:GfortranEndpoint` | http://gfortran-sidecar:51052 | per-deployment override |
| `Validation:CommitGateRequired` | false | flip on per environment |
| `Llm:SchemaDir` | (binary-adjacent) | override path to spec schemas |
| `Llm:PromptDir` | (binary-adjacent) | override path to prompts |
| `Llm:ArchetypeDir` | (binary-adjacent) | override path to archetypes |

### Schemas, prompts, archetypes — current registry

| Asset | Ids |
|---|---|
| **Schemas** | `fortran-f77` (production), `cobol` (preview) |
| **Prompts** | `fortran-f77/dotnet8/extract.v3.2` (production), `cobol/dotnet8/extract.v0.1` (preview) |
| **Archetypes** | `dotnet8/canonical-rollstock` (production, 8 files), `java-spring/canonical-rollstock` (preview, 3 files) |
| **Compliance formats** | `sox` (production), `hipaa` (preview), `pci` (preview) |

### Discovery endpoints (CIO/CISO walk-throughs)

| Endpoint | Use |
|---|---|
| `GET /api/v1/spec-schemas` | what claim taxonomies do we ship? |
| `GET /api/v1/spec-schemas/{id}` | full schema body |
| `GET /api/v1/prompts` | what calibrated prompts do we ship? |
| `GET /api/v1/prompts/{source}/{target}/{kind}` | latest prompt for that pair |
| `GET /api/v1/prompts/{source}/{target}/{kind}/{version}` | pinned version |
| `GET /api/v1/archetypes` | what scaffold archetypes do we ship? |
| `GET /api/v1/archetypes/{target}/{id}` | full archetype with file bodies |
| `GET /api/v1/compliance/formats` | what compliance feeds do we expose? |
| `GET /api/v1/compliance/feed?format=...` | the feed itself |

### Filing bugs

- **Harness bug** (state machine, audit log, validation gate): open against `astra-re-harness` with a `dev/reset → minimal-repro` script.
- **Calibration bug** (LLM produces wrong claim shape): open against `astra-prompts/<source>/<target>` with the offending `LlmCall.id` for replay.
- **Archetype bug** (generated code doesn't build): open against `astra-archetypes/<target>/<id>` with the failing COMPILE validation-run log.

Routing these correctly keeps the calibration / archetype work in the right team's queue.

---

*Last updated: ${{ commit-time }} — this runbook lives at `05_Reference/operational-runbook.md`. Update when you ship something that changes the engagement-start checklist, the incident response, or the discovery surface.*
