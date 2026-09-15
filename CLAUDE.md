# Astra RE Harness — working rules for Claude sessions

Astra is Artizent's agentic legacy-modernization platform: React + Vite + Tailwind frontend (`app/frontend`),
.NET 10 minimal API (`app/api`), Postgres, Anthropic Claude, language parser sidecars (`app/parser-sidecar` and
per-language validation sidecars), deployed to Azure App Service. Branch of record: `phase-8.0.e-strategy-plugins`.

## The product model (read before touching any UI or agent code)

**You talk to a team of agents, and they talk back.** The conversation is the primary surface; pages are
artifact views the agents open. Full philosophy, agents, surfaces, card registry and extension recipe:
**`docs/02_UX/agentic-ux-philosophy.md`** — read it before UI work. The non-negotiables:

1. Natural language is the primary way to do anything; buttons inside artifact views are the *same* actions.
2. Every fact the orchestrator states comes from a tool call in the same turn; tool calls stay on the message
   as Sources, tool cards become artifacts. Failures are reported with the provider's reason, never hidden.
3. State-changing tools pause on a Confirm / Not now card; persona rules are enforced server-side.
4. Long runs narrate themselves into the thread (stage, halfway figure, final summary + suggestion chips).
5. Results are explained in one or two plain sentences, then shown as a card.
6. Dark-first Artizent tokens only (`src/theme/palette.json`); `#FFDD00` volt is the single accent and means
   *agent working / primary action / focus*. No `bg-white`, `slate-*`, hex literals in components.
7. Routes, `data-testid`s and demo-spec button labels are stable. New surfaces add ids; they never rename.

New capability → a tool in `Copilot/CopilotToolRegistry.cs` (persona, mutating, describe, payload, artifact)
→ maybe a card in `src/workspace/artifacts/` → maybe a page. New long run → publish on `RunEventBus` and
`Narrator.Track` it. Anything new must be demonstrable as a sentence typed into a thread.

## Engineering conventions

- **Schema**: no EF migrations. Startup applies additive raw SQL (`CREATE/ALTER … IF NOT EXISTS`) in
  `Program.cs`; a fresh database builds its schema from the model. Mirror every new column in `AppDbContext`.
- **Anthropic calls** go through `Llm/AnthropicHttp.SendWithRetryAsync` + `AnthropicRateLimiter`, record an
  `LlmCall` row priced by `ModelPricing.Estimate`, cache the system block, and use forced tool-use for
  structured output. Sonnet for anything signable and for the orchestrator; Haiku for survey digests and
  narration-scale calls.
- **Ingest is a narrated background run** (`Ingest/IngestRunService`): every ingest/re-sync route hands the
  files to the run service, which runs `IngestPipeline` detached on the app-lifetime token and publishes
  stages/progress on the `RunEventBus` for the Discovery agent to narrate. The route answers with the finished
  result when the run ends within 60 s (the shape every caller always got) and with `202 {runId, corpusId,
  statusUrl}` otherwise; `GET /api/v1/ingest/runs/{runId}` carries the same result once done and the frontend's
  `awaitIngestRun` polls it. Never make a route wait on a long pipeline: App Service cuts requests at 230 s.
- **Target stacks**: `.NET 10` (`dotnet10`, and the `dotnet10-*` variants for C#/VB6/VB.NET) is the default
  target for every .NET-bound source language; `dotnet8` stays selectable for estates on the older LTS,
  `java-spring` is the default for COBOL/UniBasic/ABL/Java. Prompts live under
  `Llm/Prompts/<source>/<target>/`, scaffold prompts under `Llm/Prompts/common/<family>/` (every `dotnet10-*`
  variant shares `common/dotnet10`; see `AnthropicScaffoldProvider.ScaffoldPromptFamily`), archetypes under
  `Llm/Archetypes/<target>/<id>/` with `net10.0` csproj files. Adding a target = prompt + archetype + entry in
  `ScaffoldEndpoints.PreferredStack` / `AssessmentService.DefaultTarget` / frontend `targetStacks.ts`. The API
  image carries the .NET 10 and .NET 8 SDKs so the compile gate can build either. The API itself runs on
  .NET 10 (`DOTNET_VERSION` in `app/api/Dockerfile`, EF Core 10, Npgsql 10); the worker (`app/worker`) is still
  on .NET 8 and moves separately.
- **Two conversion modes** (WS3). The canonical path substitutes one routine into an archetype package. The
  **faithful 1:1 mode** is the target stack `dotnet10-faithful` (`Llm/FaithfulConversion.cs`): the routine's
  whole source unit becomes one C# file with the same types, names, order and call graph; the unit's signed
  specs are guardrails and are cited with `[SpecClaim]`, every member cites its source lines with
  `[SourceRoutine]`, foreign types are stubbed under `src/Stubs/` with a TODO. The archetype
  (`Archetypes/dotnet10-faithful/faithful-delphi-unit`) is only the build shell plus a shape exemplar
  (`src/Unit.cs`, never shipped); the prompt is `Prompts/delphi/dotnet10-faithful/faithful-transform.v1.md`,
  answered through a forced tool call. Adding a source language to the mode = a prompt under
  `Prompts/<source>/dotnet10-faithful/` + `compatibleSchemas` on an archetype under `dotnet10-faithful/`.
  Typed as "Convert `X` 1:1 to .NET 10"; the mock brain routes 1:1 / faithful / like-for-like to it.
- **Mock providers must keep working** (`Llm:Provider=mock`, mock survey, mock copilot brain, mock doc
  writer) — that is how the UI loop is verified locally and in e2e.
- **Build/verify**: no local .NET SDK — Docker `mcr.microsoft.com/dotnet/sdk:10.0` for `dotnet build` / `dotnet test`
  (`app/api/tests/Astra.Api.Tests`); frontend `npx tsc -b && npx vite build`. Parser sidecar tests run inside
  its image (`astra-re-harness-parser-sidecar`, pytest). Local stack: see `.claude/launch.json`
  (`frontend-localapi`) and the compose file; run only `postgres minio minio-bootstrap parser-sidecar` via
  compose and the API from the `runtime` image (the compose `api` dev target does not boot on a Windows bind
  mount). MinIO images live on `quay.io/minio/…`.
- **Commits**: only verified work (build + tests + local run), one increment per commit with a message that
  says what changed and why. Push right after committing on this branch — the user deploys from GitHub the
  moment a commit is reported. Never push or deploy something half-verified.
- **Deploy** (user runs it in Azure Cloud Shell): every block must start with
  `az account set --subscription "Microsoft Azure Sponsorship"`, then `cd ~`, `rm -rf ~/astra`, a fresh clone,
  `git checkout <branch>`, then `az acr build … --target runtime ./api` / `./frontend` (with
  `--build-arg VITE_API_BASE_URL="https://astra-api.azurewebsites.net"`) and `az webapp restart` for each app
  that changed. State the origin tip in the same message. **Every build passes
  `--build-arg BUILD_SHA=$(git rev-parse --short HEAD)`**: the API reports it as `build` on `/health` and
  `/health/ready`, the frontend stamps it on `<html data-build>` (and the bundle contains the literal sha), so
  "is commit X deployed?" is answered by one GET, never by guessing from behaviour.
  **The parser sidecar the API uses is an Azure Container App** (`Parser__GrpcEndpoint` points at
  `parser-sidecar.<env>.centralus.azurecontainerapps.io`), not the App Service `astra-parser-sidecar`, which
  cannot pass App Service's warm-up probe on a gRPC-only port and serves nothing. Deploy it with
  `az acr build --registry astraharnessacr --image parser-sidecar:<sha> ./parser-sidecar` then
  `az containerapp update -g <rg> -n parser-sidecar --image astraharnessacr.azurecr.io/parser-sidecar:<sha>
  --revision-suffix v<sha>` (find rg/name with `az containerapp list`). Bump `parser_sidecar/__init__.py`
  `__version__` with every sidecar change; `GET /health/ready` on the API reports `astra-parser <version>` and is
  the only external proof the deploy landed (direct gRPC to the sidecar from outside times out).
- **Production caution**: Compile / Test-pack validation runs execute in-process on the live API container;
  weigh before triggering them for testing. Never perform permanent deletions.
- **Persona model**: Engineer extracts/routes/generates/runs gates; SME reviews and signs; Admin surveys,
  generates docs and plans, runs assessments, manages the LLM key (Platform → LLM); Observer reads.
