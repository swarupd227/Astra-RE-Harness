using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Astra.Api.Docs;
using Astra.Api.Endpoints;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Llm.Dependency;
using Astra.Api.Llm.PatternAnalysis;
using Astra.Api.Llm.Schemas;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Specs;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Copilot;

/// <summary>
/// The orchestrator's tools. Each one calls the same service the REST
/// endpoint calls (never HTTP), enforces the same persona rule, and hands
/// back a compact payload for the model plus an artifact card for the
/// thread. State-changing tools are <see cref="CopilotTool.Mutating"/> and
/// pause for an explicit confirmation turn.
/// </summary>
public sealed class CopilotToolRegistry
{
    private readonly List<CopilotTool> _tools = new();

    public IReadOnlyList<CopilotTool> Tools => _tools;

    public CopilotTool? Find(string name) =>
        _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public CopilotToolRegistry()
    {
        // ── Reading the estate ───────────────────────────────────────────
        _tools.Add(new CopilotTool
        {
            Name = "list_programmes",
            Agent = "programme",
            Description = "List every programme (ingested codebase) with its language, routine count and funnel counts. Use when the user asks about programmes/projects in general or you need a corpusId.",
            InputSchema = Obj(),
            Execute = ListProgrammesAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_programme_status",
            Agent = "programme",
            Description = "Funnel counts (parsed → extracting → draft → in review → signed → built → committed), survey digests, pattern clusters and the latest pattern-analysis run for one programme. Defaults to the thread's programme.",
            InputSchema = Obj(("corpusId", Str("Programme id or name. Optional in a programme thread."))),
            Execute = ProgrammeStatusAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "search_routines",
            Agent = "discovery",
            Description = "Search routines by name/signature substring, optionally filtered by state (PARSED, DRAFT, IN_REVIEW, SIGNED, SCAFFOLDED, COMMITTED). Empty query lists routines. Returns ids you can pass to other tools.",
            InputSchema = Obj(
                ("query", Str("Substring of the routine name or signature. Empty = any.")),
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("state", Str("Optional state filter.")),
                ("path", Str("Optional substring of the source file path (module).")),
                ("language", Str("Optional source language id, e.g. delphi, cpp, cobol.")),
                ("hasSpec", Bool("Optional: true = only routines with a spec, false = only without.")),
                ("limit", Int("Max rows, default 20, max 100."))),
            Execute = SearchRoutinesAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "rank_routines",
            Agent = "planning",
            Description = "Rank a programme's routines by risk (transitive callers, cycles, shared storage, complexity), fan_in (direct callers), size (lines) or cycles. Use for 'riskiest', 'most depended-on', 'biggest' questions. Each row carries a score and a one-line why.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("by", Enum("risk", "fan_in", "size", "cycles")),
                ("limit", Int("Max rows, default 10, max 50."))),
            Execute = RankRoutinesAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "search_docs",
            Agent = "discovery",
            Description = "Full-text search over the generated documentation (overview, module docs, routine summaries, business rules, requirements, assessment). Returns matching sections with snippets.",
            InputSchema = Obj(
                ("query", Str("Words to look for.")),
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("kind", Str("Optional section kind filter, e.g. module, overview, business-rule, assessment."))),
            Execute = SearchDocsAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "list_modules",
            Agent = "discovery",
            Description = "The programme's modules (source files) with routine counts and progress per module. Use for 'what's in this codebase', 'which module is biggest / least done'.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("limit", Int("Max rows, default 40."))),
            Execute = ListModulesAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "explain_claim",
            Agent = "spec",
            Description = "Fetch one claim of a spec together with the cited source lines (± 15 lines of context) so you can explain it in plain language. Use for 'why?', 'explain INV-2', 'is this claim right?'.",
            InputSchema = Obj(
                ("claimId", Str("Claim id, e.g. INV-2, SE-1, EC-3, Q-1.")),
                ("specId", Str("Spec id. Optional in a spec thread.")),
                ("subroutineId", Str("Routine id or exact name (alternative to specId)."))),
            Execute = ExplainClaimAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "read_routine",
            Agent = "discovery",
            Description = "Read one routine: metadata, callees, caller count, its spec id/state, and the source lines (capped at 400). Use before explaining what a routine does.",
            InputSchema = Obj(("subroutineId", Str("Routine id, or its exact name (e.g. TIdSMTP.Connect)."))),
            Execute = ReadRoutineAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_spec",
            Agent = "spec",
            Description = "The current spec for a routine: every claim with id, section, plain text, citation and review state, plus sign-off status. Use before reviewing/explaining/signing.",
            InputSchema = Obj(
                ("subroutineId", Str("Routine id or exact name.")),
                ("specId", Str("Spec id (alternative to subroutineId)."))),
            Execute = GetSpecAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_pattern_clusters",
            Agent = "discovery",
            Description = "Pattern clusters from the programme's latest completed pattern analysis (label, suggested archetype, member count, rationale).",
            InputSchema = Obj(("corpusId", Str("Programme id or name. Optional in a programme thread."))),
            Execute = GetClustersAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_run_status",
            Agent = "programme",
            Description = "State, progress and summary of a pattern-analysis run.",
            InputSchema = Obj(("runId", Str("Run id."))),
            Execute = GetRunStatusAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "query_graph",
            Agent = "planning",
            Description = "Dependency facts for a routine: blast radius (direct/transitive callers, shared storage), migration readiness classification and wave assignment. Use for 'riskiest', 'what depends on', 'safe to migrate' questions.",
            InputSchema = Obj(("subroutineId", Str("Routine id or exact name."))),
            Execute = QueryGraphAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_migration_plan",
            Agent = "planning",
            Description = "The programme's current migration plan (approved, else latest draft): strategy, waves, routine counts.",
            InputSchema = Obj(("corpusId", Str("Programme id or name. Optional in a programme thread."))),
            Execute = GetMigrationPlanAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_validation_results",
            Agent = "validation",
            Description = "Latest result of each validation gate (COMPILE, TEST_PACK, EQUIVALENCE, FALSIFYING) for a routine's generated code.",
            InputSchema = Obj(
                ("subroutineId", Str("Routine id or exact name.")),
                ("scaffoldId", Str("Scaffold id (alternative)."))),
            Execute = GetValidationResultsAsync,
        });

        // ── Acting (confirmation required) ───────────────────────────────
        _tools.Add(new CopilotTool
        {
            Name = "survey_corpus",
            Agent = "discovery",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Admin },
            Description = "Start pattern analysis for a programme: survey every routine (Haiku digests, minutes not hours) then cluster the patterns. Resumes a paused run. Admin only.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("force", Bool("Re-survey every routine even if digests exist. Default false."))),
            Describe = async (input, ctx) => $"start the pattern survey for {(await ResolveCorpusAsync(input, ctx))?.Name ?? "this programme"}" + (ReadBool(input, "force") ? " (forcing a full re-survey)" : ""),
            Execute = SurveyCorpusAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "extract_spec",
            Agent = "spec",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Engineer, Persona.Admin },
            Description = "Draft the behavioural spec for one routine (Claude reads the source and writes claims). Allowed from PARSED or DRAFT. Runs in the background; the Spec agent posts when it's done.",
            InputSchema = Obj(("subroutineId", Str("Routine id or exact name."))),
            Describe = async (input, ctx) => $"draft the spec for `{(await ResolveRoutineAsync(input, ctx))?.Name ?? "the routine"}`",
            Execute = ExtractSpecAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "route_for_review",
            Agent = "spec",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Engineer },
            Description = "Send a DRAFT spec to SME review (DRAFT → IN_REVIEW). Engineer only.",
            InputSchema = Obj(
                ("subroutineId", Str("Routine id or exact name.")),
                ("specId", Str("Spec id (alternative).")),
                ("note", Str("Optional routing note for the reviewer."))),
            Describe = async (input, ctx) => $"route the spec for `{(await ResolveSpecAsync(input, ctx))?.Subroutine?.Name ?? "the routine"}` for SME review",
            Execute = RouteForReviewAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "review_claim",
            Agent = "spec",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Sme },
            Description = "Record an SME decision on one claim: accept | reject (reason ≥ 20 chars) | edit (editedText) | question (reason). Spec must be IN_REVIEW. SME only.",
            InputSchema = Obj(
                ("specId", Str("Spec id.")),
                ("subroutineId", Str("Routine id or exact name (alternative to specId).")),
                ("section", Str("Claim section, e.g. invariants, side_effects, edge_cases, open_questions.")),
                ("claimId", Str("Claim id, e.g. INV-1.")),
                ("action", Enum("accept", "reject", "edit", "question")),
                ("reason", Str("Required for reject (≥ 20 chars) and question.")),
                ("editedText", Str("Required for edit."))),
            Describe = (input, _) => Task.FromResult($"mark claim `{Read(input, "claimId")}` as {Read(input, "action")}" +
                                                      (Read(input, "reason") is { Length: > 0 } r ? $" — \"{r}\"" : "")),
            Execute = ReviewClaimAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "review_all_claims",
            Agent = "spec",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Sme },
            Description = "Accept every untouched claim in an IN_REVIEW spec, except the listed claim ids. Use for 'accept all' / 'accept everything except X'. SME only.",
            InputSchema = Obj(
                ("specId", Str("Spec id.")),
                ("subroutineId", Str("Routine id or exact name (alternative).")),
                ("except", Arr("Claim ids to leave untouched."))),
            Describe = async (input, ctx) =>
            {
                var spec = await ResolveSpecAsync(input, ctx);
                var except = ReadArray(input, "except");
                return $"accept every remaining claim in the spec for `{spec?.Subroutine?.Name ?? "the routine"}`" +
                       (except.Count > 0 ? $" except {string.Join(", ", except.Select(e => $"`{e}`"))}" : "");
            },
            Execute = ReviewAllClaimsAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "sign_spec",
            Agent = "spec",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Sme },
            Description = "SME sign-off: cryptographically signs an IN_REVIEW spec whose claims have all been reviewed. This is the human gate that makes a spec authoritative. SME only.",
            InputSchema = Obj(
                ("specId", Str("Spec id.")),
                ("subroutineId", Str("Routine id or exact name (alternative)."))),
            Describe = async (input, ctx) => $"sign the spec for `{(await ResolveSpecAsync(input, ctx))?.Subroutine?.Name ?? "the routine"}` as SME (this is the authoritative sign-off)",
            Execute = SignSpecAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "generate_scaffold",
            Agent = "migration",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Engineer },
            Description = "Generate target code for a SIGNED spec on a target stack (e.g. dotnet10, java-spring, angular-java, dotnet10-blazor). Omit targetStack for the default. Use targetStack 'dotnet10-faithful' when the user asks for a 1:1, faithful, like-for-like or as-is conversion: it converts the routine's whole Delphi unit into one C# file with the same names and call graph instead of the canonical archetype. Set repairFromLatestFailure when the user wants the code regenerated with the last failed gate's errors fixed. Runs in the background. Engineer only.",
            InputSchema = Obj(
                ("specId", Str("Spec id.")),
                ("subroutineId", Str("Routine id or exact name (alternative).")),
                ("targetStack", Str("Target stack id. Optional.")),
                ("repairFromLatestFailure", Bool("True to feed the routine's latest FAILED gate log (errors, failing tests) into the regeneration so they get fixed."))),
            Describe = async (input, ctx) =>
            {
                var spec = await ResolveSpecAsync(input, ctx);
                var target = Read(input, "targetStack");
                var repair = ReadBool(input, "repairFromLatestFailure") ? " with the last gate's errors fixed" : "";
                return $"{(repair.Length > 0 ? "regenerate" : "generate")} {(string.IsNullOrWhiteSpace(target) ? "the default target's" : target)} code for `{spec?.Subroutine?.Name ?? "the routine"}`{repair}";
            },
            Execute = GenerateScaffoldAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "run_gate",
            Agent = "validation",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Engineer },
            Description = "Run a validation gate on a routine's latest generated code: compile (builds it) or test-pack (generates and runs the claim tests). Runs in the background. Engineer only.",
            InputSchema = Obj(
                ("scaffoldId", Str("Scaffold id.")),
                ("subroutineId", Str("Routine id or exact name (alternative; uses the latest scaffold).")),
                ("gate", Enum("compile", "test-pack"))),
            Describe = async (input, ctx) => $"run the {Read(input, "gate") ?? "compile"} gate for `{(await ResolveScaffoldAsync(input, ctx))?.Spec?.Subroutine?.Name ?? "the routine"}`",
            Execute = RunGateAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "generate_docs",
            Agent = "discovery",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Admin },
            Description = "Generate documentation for a programme (routine summaries, module docs, overview, catalogs, diagrams). Long-running. Admin only.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("stages", Arr("Optional stage subset: routine-summary, module, overview, data-dictionary, glossary, interface, business-rules, sequence-diagram, dependency-diagram."))),
            Describe = async (input, ctx) => $"generate documentation for {(await ResolveCorpusAsync(input, ctx))?.Name ?? "this programme"}",
            Execute = GenerateDocsAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "run_assessment",
            Agent = "architecture",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Admin },
            Description = "Run the 10-minute Assessment for a programme: inventory, dependency hotspots and cycles, pattern clusters, complexity and modernization flags, an effort/risk model, and a recommended modernization mode (faithful-1to1 | replatform | modernize | strangler). Takes ~1 minute; the Architecture agent posts the assessment card when done. Admin only.",
            InputSchema = Obj(("corpusId", Str("Programme id or name. Optional in a programme thread."))),
            Describe = async (input, ctx) => $"run the assessment for {(await ResolveCorpusAsync(input, ctx))?.Name ?? "this programme"}",
            Execute = RunAssessmentAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "generate_migration_plan",
            Agent = "planning",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Admin },
            Description = "Draft a dependency-aware migration plan (waves) for a programme. Strategies: topological-leaves-first (default), risk-first, business-priority, pilot-then-scale. Admin only.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("strategy", Str("Strategy name. Optional."))),
            Describe = async (input, ctx) => $"draft a {Read(input, "strategy") ?? MigrationPlanner.DefaultStrategy} migration plan for {(await ResolveCorpusAsync(input, ctx))?.Name ?? "this programme"}",
            Execute = GenerateMigrationPlanAsync,
        });
    }

    // ═══ Read tools ══════════════════════════════════════════════════════

    private static async Task<ToolResult> ListProgrammesAsync(JsonElement input, ToolContext ctx)
    {
        var conversations = ctx.Services.GetRequiredService<ConversationService>();
        var corpora = await ctx.Db.Corpora.AsNoTracking().OrderByDescending(c => c.UpdatedAt).ToListAsync(ctx.Ct);
        var items = new List<object>();
        foreach (var c in corpora)
        {
            var (routines, files, language) = await conversations.ProgrammeStatsAsync(c, ctx.Ct);
            var counts = await conversations.FunnelAsync(c, ctx.Ct);
            var thread = c.LatestVersionId is null ? null : await conversations.EnsureProgrammeAsync(c.Id, ctx.Ct);
            items.Add(new
            {
                id = c.Id,
                conversationId = thread?.Id,
                name = c.Name,
                sourceLanguage = language,
                languageLabel = ConversationService.LanguageLabel(language),
                routineCount = routines,
                fileCount = files,
                state = c.State,
                counts,
                createdAt = c.CreatedAt,
            });
        }
        return ToolResult.Success(new { programmes = items }, $"{items.Count} programmes",
            ToolResult.Artifact("programmeList", null, new { items }));
    }

    private static async Task<ToolResult> ProgrammeStatusAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var conversations = ctx.Services.GetRequiredService<ConversationService>();
        var (artifact, payload) = await ArtifactBuilders.FunnelAsync(ctx.Db, conversations, corpus, ctx.Ct);
        return ToolResult.Success(payload, $"status of {corpus.Name}", artifact);
    }

    private static async Task<ToolResult> SearchRoutinesAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        var query = (Read(input, "query") ?? "").Trim();
        var state = Read(input, "state")?.Trim().ToUpperInvariant();
        var path = Read(input, "path")?.Trim();
        var language = Read(input, "language")?.Trim();
        var hasSpecRaw = Read(input, "hasSpec");
        var limit = Math.Clamp(ReadInt(input, "limit") ?? 20, 1, 100);

        var q =
            from s in ctx.Db.Subroutines.AsNoTracking()
            join f in ctx.Db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            join v in ctx.Db.SourceVersions.AsNoTracking() on f.SourceVersionId equals v.Id
            join c in ctx.Db.Corpora.AsNoTracking() on v.CorpusId equals c.Id
            where c.LatestVersionId == v.Id
            select new { s, f, c };
        if (corpus is not null) q = q.Where(x => x.c.Id == corpus.Id);
        if (!string.IsNullOrEmpty(query))
            q = q.Where(x => EF.Functions.ILike(x.s.Name, $"%{query}%") || EF.Functions.ILike(x.s.Signature, $"%{query}%"));
        if (!string.IsNullOrEmpty(state)) q = q.Where(x => x.s.State == state);
        if (!string.IsNullOrEmpty(path)) q = q.Where(x => EF.Functions.ILike(x.f.RelativePath, $"%{path}%"));
        if (!string.IsNullOrEmpty(language)) q = q.Where(x => x.s.SourceLanguage == language);
        if (string.Equals(hasSpecRaw, "true", StringComparison.OrdinalIgnoreCase))
            q = q.Where(x => ctx.Db.Specs.Any(sp => sp.SubroutineId == x.s.Id));
        else if (string.Equals(hasSpecRaw, "false", StringComparison.OrdinalIgnoreCase))
            q = q.Where(x => !ctx.Db.Specs.Any(sp => sp.SubroutineId == x.s.Id));

        var total = await q.CountAsync(ctx.Ct);
        var rows = await q.OrderBy(x => x.s.Name).Take(limit).ToListAsync(ctx.Ct);
        var items = rows.Select(x => new
        {
            id = x.s.Id,
            name = x.s.Name,
            signature = ArtifactBuilders.Truncate(x.s.Signature, 160),
            state = x.s.State,
            sourceLanguage = x.s.SourceLanguage,
            path = x.f.RelativePath,
            lineStart = x.s.LineStart,
            lineEnd = x.s.LineEnd,
            corpusId = x.c.Id,
            corpusName = x.c.Name,
        }).ToList();

        var label = string.IsNullOrEmpty(query) ? "routines" : $"routines matching \"{query}\"";
        return ToolResult.Success(new { query, state, total, returned = items.Count, items }, $"{total} {label}",
            ToolResult.Artifact("routineList", null, new { query, total, items }));
    }

    private static async Task<ToolResult> ReadRoutineAsync(JsonElement input, ToolContext ctx)
    {
        var sub = await ResolveRoutineAsync(input, ctx);
        if (sub is null) return NoRoutine(input);
        var blob = ctx.Services.GetRequiredService<IBlobClient>();
        var (artifact, payload) = await ArtifactBuilders.RoutineAsync(ctx.Db, blob, sub, includeSource: true, ctx.Ct);
        return ToolResult.Success(payload, $"read {sub.Name}", artifact);
    }

    private static async Task<ToolResult> GetSpecAsync(JsonElement input, ToolContext ctx)
    {
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return NoSpec(input);
        var schemas = ctx.Services.GetRequiredService<SpecSchemaProvider>();
        var (artifact, payload, _) = await ArtifactBuilders.SpecSummaryAsync(ctx.Db, schemas, spec, ctx.Ct);
        return ToolResult.Success(payload, $"spec for {spec.Subroutine?.Name} ({spec.State})", artifact);
    }

    private static async Task<ToolResult> GetClustersAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var grid = await ArtifactBuilders.ClusterGridAsync(ctx.Db, corpus.Id, ctx.Ct);
        if (grid is null)
        {
            var latest = await ctx.Db.PatternAnalysisRuns.AsNoTracking()
                .Where(r => r.CorpusId == corpus.Id).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ctx.Ct);
            return ToolResult.Failure("clusters.none",
                latest is null
                    ? $"No pattern analysis has been run for {corpus.Name} yet — offer to start one (survey_corpus)."
                    : $"The latest pattern analysis for {corpus.Name} is {latest.State} ({latest.Summary}); no clusters are available yet.",
                new { latestRunId = latest?.Id, latestRunState = latest?.State });
        }
        return ToolResult.Success(grid.Value.Payload, $"clusters for {corpus.Name}", grid.Value.Artifact);
    }

    private static async Task<ToolResult> GetRunStatusAsync(JsonElement input, ToolContext ctx)
    {
        if (!Guid.TryParse(Read(input, "runId"), out var runId))
            return ToolResult.Failure("run.invalid_id", "runId must be a GUID.");
        var run = await ctx.Db.PatternAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ctx.Ct);
        if (run is null) return ToolResult.Failure("run.not_found", "No pattern-analysis run with that id.");
        return ToolResult.Success(PatternAnalysisEndpoints.RenderRun(run), $"run {run.State}");
    }

    private static async Task<ToolResult> QueryGraphAsync(JsonElement input, ToolContext ctx)
    {
        var sub = await ResolveRoutineAsync(input, ctx);
        if (sub is null) return NoRoutine(input);
        var mc = ctx.Services.GetRequiredService<MigrationContextService>();
        var blast = await mc.ComputeBlastRadiusAsync(sub.Id, ctx.Ct);
        var readiness = await mc.ClassifyAsync(sub.Id, ctx.Ct);
        var wave = await mc.GetWaveAssignmentAsync(sub.Id, ctx.Ct);
        var payload = new
        {
            subroutineId = sub.Id,
            name = sub.Name,
            state = sub.State,
            blastRadius = blast is null ? null : new
            {
                blast.DirectCallerCount,
                blast.TransitiveCallerCount,
                blast.SharedStorageConsumerCount,
                affected = blast.Affected.Take(15).Select(a => new { a.Id, a.Name, a.State }),
                affectedTotal = blast.Affected.Count,
            },
            readiness = readiness is null ? null : new
            {
                readiness.Classification,
                readiness.Reasons,
                readiness.CalleeCount,
                readiness.CallerCount,
                readiness.SharedBlockNames,
                blockingRoutineCount = readiness.BlockingRoutineIds.Count,
            },
            wave = wave is null ? null : new { wave.WaveNumber, wave.TotalWaves, wave.WaveName, wave.StrategyName, wave.PlanStatus },
        };
        return ToolResult.Success(payload, $"graph facts for {sub.Name}");
    }

    private static async Task<ToolResult> GetMigrationPlanAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var plan = await ctx.Db.MigrationPlans.AsNoTracking()
            .Where(p => p.CorpusId == corpus.Id && p.Status != "archived")
            .OrderByDescending(p => p.Status == "approved")
            .ThenByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ctx.Ct);
        if (plan is null)
            return ToolResult.Failure("plan.none", $"No migration plan exists for {corpus.Name} — offer to draft one (generate_migration_plan).");
        var (artifact, payload) = await ArtifactBuilders.PlanWavesAsync(ctx.Db, plan, ctx.Ct);
        return ToolResult.Success(payload, $"plan: {plan.TotalWaves} waves ({plan.Status})", artifact);
    }

    // ═══ NL querying (Increment 2) ═══════════════════════════════════════

    private static async Task<ToolResult> RankRoutinesAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var by = (Read(input, "by") ?? "risk").Trim().ToLowerInvariant();
        var limit = Math.Clamp(ReadInt(input, "limit") ?? 10, 1, 50);

        var graphs = ctx.Services.GetRequiredService<DependencyGraphBuilder>();
        var graph = await graphs.BuildAsync(corpus.Id, ctx.Ct);
        if (graph is null || graph.Nodes.Count == 0)
            return ToolResult.Failure("graph.empty", $"No dependency graph for {corpus.Name} yet (no ingested version or no routines).");

        var hotspots = Astra.Api.Assessment.AssessmentService.Hotspots(graph, graph.Nodes.Count)
            .ToDictionary(h => h.Id);
        var sharedByNode = graph.Edges.Where(e => e.Type == "shared-storage")
            .SelectMany(e => new[] { e.From, e.To }).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var nodeIds = graph.Nodes.Select(n => n.Id).ToList();
        var digests = await ctx.Db.RoutineDigests.AsNoTracking()
            .Where(d => nodeIds.Contains(d.SubroutineId))
            .Select(d => new { d.SubroutineId, d.Complexity })
            .ToDictionaryAsync(d => d.SubroutineId, d => d.Complexity, ctx.Ct);
        var lines = await ctx.Db.Subroutines.AsNoTracking()
            .Where(s => nodeIds.Contains(s.Id))
            .Select(s => new { s.Id, Lines = s.LineEnd - s.LineStart + 1, s.State, s.SourceLanguage })
            .ToDictionaryAsync(s => s.Id, ctx.Ct);

        var maxTrans = Math.Max(1, hotspots.Values.Max(h => h.TransitiveCallers));
        var maxCallers = Math.Max(1, graph.Nodes.Max(n => n.CallerCount));
        var maxLines = Math.Max(1, lines.Values.Max(l => l.Lines));

        var rows = graph.Nodes.Select(n =>
        {
            var h = hotspots[n.Id];
            var shared = sharedByNode.GetValueOrDefault(n.Id);
            var complexity = digests.GetValueOrDefault(n.Id);
            var cx = complexity switch { "complex" => 1.0, "moderate" => 0.5, "simple" => 0.1, _ => 0.3 };
            var size = lines.TryGetValue(n.Id, out var l) ? l.Lines : 0;
            var why = new List<string>();
            double score;
            switch (by)
            {
                case "fan_in":
                    score = n.CallerCount;
                    why.Add($"{n.CallerCount} direct callers");
                    break;
                case "size":
                    score = size;
                    why.Add($"{size} lines");
                    break;
                case "cycles":
                    score = (h.InCycle ? 1000 : 0) + h.TransitiveCallers;
                    if (h.InCycle) why.Add("in a call cycle");
                    why.Add($"{h.TransitiveCallers} transitive callers");
                    break;
                default:
                    score = 0.5 * h.TransitiveCallers / maxTrans + (h.InCycle ? 0.2 : 0) + 0.15 * Math.Min(1, shared / 4.0) + 0.15 * cx;
                    why.Add($"{h.TransitiveCallers} transitive callers");
                    if (h.InCycle) why.Add("in a call cycle");
                    if (shared > 0) why.Add($"{shared} shared-storage couplings");
                    if (complexity is not null) why.Add($"{complexity} complexity");
                    break;
            }
            return new
            {
                id = n.Id,
                name = n.Name,
                path = n.SourcePath,
                state = lines.TryGetValue(n.Id, out var l2) ? l2.State : n.State,
                sourceLanguage = lines.TryGetValue(n.Id, out var l3) ? l3.SourceLanguage : null,
                callers = n.CallerCount,
                transitiveCallers = h.TransitiveCallers,
                inCycle = h.InCycle,
                lines = size,
                score = Math.Round(score, 3),
                why = string.Join(", ", why),
            };
        })
        .OrderByDescending(r => r.score).ThenBy(r => r.name)
        .Take(limit)
        .ToList();

        var items = rows.Select(r => new
        {
            r.id, r.name, signature = "", r.state, r.sourceLanguage, r.path, lineStart = 0, lineEnd = r.lines, corpusId = corpus.Id,
            corpusName = corpus.Name, r.score, r.why, r.callers, r.transitiveCallers, r.inCycle,
        }).ToList();
        var label = by switch { "fan_in" => "most-called", "size" => "largest", "cycles" => "cycle-bound", _ => "riskiest" };
        return ToolResult.Success(
            new { by, corpusId = corpus.Id, corpusName = corpus.Name, graph = new { graph.Stats.NodeCount, graph.Stats.CyclicSccCount, graph.Stats.SharedStorageEdgeCount }, rows },
            $"{rows.Count} {label} routines in {corpus.Name}",
            ToolResult.Artifact("routineList", null, new { query = $"{label} routines", total = rows.Count, items, ranked = true, by }));
    }

    private static async Task<ToolResult> SearchDocsAsync(JsonElement input, ToolContext ctx)
    {
        var query = (Read(input, "query") ?? "").Trim();
        if (query.Length < 2) return ToolResult.Failure("docs.query_required", "Give me a word or two to search for.");
        var corpus = await ResolveCorpusAsync(input, ctx);
        var kind = Read(input, "kind")?.Trim();

        var q = ctx.Db.DocSections.AsNoTracking()
            .Where(s => s.State != "SUPERSEDED" && s.RenderedMarkdown != null && EF.Functions.ILike(s.RenderedMarkdown!, $"%{query}%"));
        if (corpus is not null) q = q.Where(s => s.CorpusId == corpus.Id);
        if (!string.IsNullOrEmpty(kind)) q = q.Where(s => s.SectionKind == kind);
        var sections = await q.OrderByDescending(s => s.UpdatedAt).Take(5).ToListAsync(ctx.Ct);
        if (sections.Count == 0)
            return ToolResult.Failure("docs.no_match", $"Nothing in the generated documentation mentions \"{query}\"" +
                                                       (corpus is null ? "." : $" for {corpus.Name}. If no docs exist yet, offer generate_docs."));

        var artifacts = new List<ArtifactDto>();
        var hits = new List<object>();
        foreach (var s in sections)
        {
            var md = s.RenderedMarkdown ?? "";
            var idx = md.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            var start = Math.Max(0, idx - 200);
            var snippet = md.Substring(start, Math.Min(md.Length - start, 400)).Replace("\n", " ");
            var title = s.ModuleName is { Length: > 0 } m ? $"{Pretty(s.SectionKind)} · {m}"
                      : s.SubroutineId is not null ? $"{Pretty(s.SectionKind)} · routine"
                      : Pretty(s.SectionKind);
            var href = s.SectionKind == Astra.Api.Assessment.AssessmentService.SectionKind
                ? $"/projects/{s.CorpusId}/assessment"
                : $"/projects/{s.CorpusId}/docs";
            hits.Add(new { sectionId = s.Id, kind = s.SectionKind, scope = s.Scope, module = s.ModuleName, state = s.State, snippet = "…" + snippet + "…" });
            if (artifacts.Count < 2) artifacts.Add(ArtifactBuilders.DocSection(s, title, href));
        }
        return new ToolResult(true, new { query, matches = hits }, $"{sections.Count} doc sections mention \"{query}\"", artifacts);
    }

    private static async Task<ToolResult> ListModulesAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        if (corpus.LatestVersionId is not { } vid) return ToolResult.Failure("corpus.no_version", "No ingested version yet.");
        var limit = Math.Clamp(ReadInt(input, "limit") ?? 40, 1, 200);

        var rows = await (
            from s in ctx.Db.Subroutines.AsNoTracking()
            join f in ctx.Db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            where f.SourceVersionId == vid
            group s by new { f.RelativePath, f.LineCount } into g
            select new
            {
                path = g.Key.RelativePath,
                lines = g.Key.LineCount,
                routines = g.Count(),
                signed = g.Count(x => x.State == "SIGNED" || x.State == "SCAFFOLDED" || x.State == "COMMITTED"),
                parsed = g.Count(x => x.State == "PARSED"),
            }).OrderByDescending(x => x.routines).Take(limit).ToListAsync(ctx.Ct);

        var md = $"**Modules in {corpus.Name}** (top {rows.Count} by routine count)\n\n| Module | Routines | Lines | Signed+ | Untouched |\n|---|---:|---:|---:|---:|\n" +
                 string.Join("\n", rows.Select(r => $"| `{r.path}` | {r.routines} | {r.lines} | {r.signed} | {r.parsed} |"));
        return ToolResult.Success(new { corpusId = corpus.Id, modules = rows }, $"{rows.Count} modules",
            ToolResult.Artifact("text", corpus.Id.ToString(), new { title = "Modules", markdown = md, href = $"/projects/{corpus.Id}" }));
    }

    private static async Task<ToolResult> ExplainClaimAsync(JsonElement input, ToolContext ctx)
    {
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return NoSpec(input);
        var claimId = Read(input, "claimId")?.Trim();
        if (string.IsNullOrEmpty(claimId)) return ToolResult.Failure("claim.id_required", "Which claim? Give its id (e.g. INV-2).");

        var schemas = ctx.Services.GetRequiredService<SpecSchemaProvider>();
        var (_, _, claims) = await ArtifactBuilders.SpecSummaryAsync(ctx.Db, schemas, spec, ctx.Ct);
        var claim = claims.FirstOrDefault(c => string.Equals(c.Id, claimId, StringComparison.OrdinalIgnoreCase));
        if (claim is null)
            return ToolResult.Failure("claim.not_found", $"No claim `{claimId}` in this spec. Ids: {string.Join(", ", claims.Select(c => c.Id).Take(30))}.");

        var sub = spec.Subroutine ?? await ctx.Db.Subroutines.AsNoTracking().Include(s => s.SourceFile).FirstAsync(s => s.Id == spec.SubroutineId, ctx.Ct);
        if (sub.SourceFile is null) sub.SourceFile = await ctx.Db.SourceFiles.AsNoTracking().FirstAsync(f => f.Id == sub.SourceFileId, ctx.Ct);
        var blob = ctx.Services.GetRequiredService<IBlobClient>();

        // Citation "L120-134" / "L120–134" / "L120" → absolute lines; fall back to the routine's own range.
        int from = sub.LineStart, to = sub.LineEnd;
        var cit = (claim.Citation ?? "").Replace("L", "").Replace("–", "-");
        var first = cit.Split(',')[0].Trim();
        var parts = first.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && int.TryParse(parts[0], out var a))
        {
            from = a;
            to = parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : a;
        }
        string excerpt;
        try
        {
            var text = await blob.GetTextAsync(sub.SourceFile.BlobUri, ctx.Ct);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var s0 = Math.Max(1, from - 15);
            var e0 = Math.Min(lines.Length, to + 15);
            excerpt = string.Join("\n", Enumerable.Range(s0, Math.Max(0, e0 - s0 + 1)).Select(n => $"{n,5}  {lines[n - 1]}"));
        }
        catch (Exception ex)
        {
            excerpt = $"(source unavailable: {ex.Message})";
        }

        var payload = new
        {
            specId = spec.Id,
            routineName = sub.Name,
            claim = new { claim.Id, claim.Section, claim.Text, claim.Review, claim.Citation },
            citedLines = new { from, to },
            sourceExcerpt = excerpt,
            note = "Explain what the cited lines do and why the claim follows (or doesn't). Quote line numbers.",
        };
        var artifact = ToolResult.Artifact("routine", sub.Id.ToString(), new
        {
            name = sub.Name,
            signature = sub.Signature,
            path = sub.SourceFile.RelativePath,
            lineStart = sub.LineStart,
            lineEnd = sub.LineEnd,
            sourceLanguage = sub.SourceLanguage,
            state = sub.State,
            callees = ArtifactBuilders.ReadStringArray(sub.CalledSubroutines),
            callerCount = 0,
            corpusId = ctx.CorpusId,
            specId = spec.Id,
            source = excerpt,
            highlight = new { from, to },
            claimId = claim.Id,
        });
        return ToolResult.Success(payload, $"claim {claim.Id} with lines {from}–{to}", artifact);
    }

    private static async Task<ToolResult> RunAssessmentAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        if (corpus.LatestVersionId is null) return ToolResult.Failure("corpus.no_version", "No ingested version yet.");
        var conversations = ctx.Services.GetRequiredService<ConversationService>();
        var thread = await conversations.EnsureProgrammeAsync(corpus.Id, ctx.Ct);
        var svc = ctx.Services.GetRequiredService<Astra.Api.Assessment.AssessmentService>();
        var runId = svc.Start(corpus.Id, corpus.Name, thread.Id, ctx.Actor.Persona, ctx.Actor.DisplayName);
        var props = new
        {
            kind = "assessment",
            corpusId = corpus.Id,
            label = $"Assessment · {corpus.Name}",
            agent = "architecture",
            state = "RUNNING",
            startedAt = DateTimeOffset.UtcNow,
            links = new[] { new { label = "Open full view", href = $"/projects/{corpus.Id}/assessment" } },
        };
        return new ToolResult(true,
            new { runId, corpusId = corpus.Id, status = "started", note = "About a minute; the Architecture agent will post the assessment card." },
            $"assessment started for {corpus.Name}",
            new[] { ToolResult.Artifact("runProgress", runId.ToString(), props) }, runId);
    }

    private static string Pretty(string kind) => kind switch
    {
        "routine-summary" => "Routine summary",
        "business-rule" => "Business rule",
        "data-dictionary" => "Data dictionary",
        "assessment" => "Assessment",
        _ => char.ToUpperInvariant(kind[0]) + kind[1..].Replace('-', ' '),
    };

    private static async Task<ToolResult> GetValidationResultsAsync(JsonElement input, ToolContext ctx)
    {
        var scaffold = await ResolveScaffoldAsync(input, ctx);
        if (scaffold is null) return ToolResult.Failure("scaffold.not_found", "No generated code found for that routine — generate it first (generate_scaffold).");
        var gates = await ArtifactBuilders.GateResultsAsync(ctx.Db, scaffold.Id, ctx.Ct);
        return gates is null
            ? ToolResult.Failure("scaffold.not_found", "Scaffold not found.")
            : ToolResult.Success(gates.Value.Payload, $"gates for {scaffold.Spec?.Subroutine?.Name}", gates.Value.Artifact);
    }

    // ═══ Mutating tools ══════════════════════════════════════════════════

    private static async Task<ToolResult> SurveyCorpusAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var force = ReadBool(input, "force");
        var orchestrator = ctx.Services.GetRequiredService<PatternAnalysisOrchestrator>();
        var narrator = ctx.Services.GetRequiredService<Narrator>();
        var conversations = ctx.Services.GetRequiredService<ConversationService>();

        var inFlight = await ctx.Db.PatternAnalysisRuns.AsNoTracking()
            .Where(r => r.CorpusId == corpus.Id && (r.State == "QUEUED" || r.State == "RUNNING"))
            .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ctx.Ct);
        Guid runId;
        var resumed = false;
        if (inFlight is not null)
        {
            runId = inFlight.Id;
        }
        else
        {
            var resumable = force ? null : await ctx.Db.PatternAnalysisRuns.AsNoTracking()
                .Where(r => r.CorpusId == corpus.Id && r.State == "RESUMABLE")
                .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ctx.Ct);
            if (resumable is not null && await orchestrator.ResumeAsync(resumable.Id))
            {
                runId = resumable.Id;
                resumed = true;
            }
            else
            {
                try
                {
                    runId = await orchestrator.StartAsync(corpus.Id, force, ctx.Actor.DisplayName, "survey,cluster", ctx.Ct);
                }
                catch (InvalidOperationException ex)
                {
                    return ToolResult.Failure("pattern_analysis.precondition_failed", ex.Message);
                }
            }
        }

        var thread = await conversations.EnsureProgrammeAsync(corpus.Id, ctx.Ct);
        narrator.Track(runId, thread.Id, "discovery", "pattern-analysis", corpus.Name, corpus.Id,
            ctx.Actor.Persona.ToString().ToLowerInvariant(), ctx.Actor.DisplayName);

        var label = inFlight is not null ? "already running" : resumed ? "resumed" : "started";
        var props = new
        {
            kind = "pattern-analysis",
            corpusId = corpus.Id,
            label = $"Pattern survey · {corpus.Name}",
            agent = "discovery",
            state = "RUNNING",
            startedAt = DateTimeOffset.UtcNow,
            links = new[] { new { label = "Open full view", href = $"/projects/{corpus.Id}/pattern-analysis" } },
        };
        return new ToolResult(true,
            new { runId, corpusId = corpus.Id, corpusName = corpus.Name, status = label, note = "Progress streams to the thread; the Discovery agent will post when it finishes." },
            $"pattern survey {label} for {corpus.Name}",
            new[] { ToolResult.Artifact("runProgress", runId.ToString(), props) },
            runId);
    }

    private static async Task<ToolResult> ExtractSpecAsync(JsonElement input, ToolContext ctx)
    {
        var sub = await ResolveRoutineAsync(input, ctx);
        if (sub is null) return NoRoutine(input);
        if (sub.State is not ("PARSED" or "DRAFT"))
            return ToolResult.Failure("extract.invalid_state",
                $"`{sub.Name}` is {sub.State}; extraction is only allowed from PARSED or DRAFT (a spec in review or signed must be superseded instead).");

        var runs = ctx.Services.GetRequiredService<BackgroundRunService>();
        var narrator = ctx.Services.GetRequiredService<Narrator>();
        var runId = runs.StartExtraction(sub.Id, sub.Name, ctx.Actor.Persona, ctx.Actor.DisplayName);
        narrator.Track(runId, ctx.ConversationId, "spec", "extraction", sub.Name, ctx.CorpusId,
            ctx.Actor.Persona.ToString().ToLowerInvariant(), ctx.Actor.DisplayName);

        var props = new
        {
            kind = "extraction",
            corpusId = ctx.CorpusId,
            label = $"Drafting spec · {sub.Name}",
            agent = "spec",
            state = "RUNNING",
            startedAt = DateTimeOffset.UtcNow,
            links = new[] { new { label = "Watch live", href = $"/subroutines/{sub.Id}/extract" } },
        };
        return new ToolResult(true,
            new { runId, subroutineId = sub.Id, name = sub.Name, status = "started", note = "Takes about a minute; the Spec agent will post the claims when done." },
            $"extraction started for {sub.Name}",
            new[] { ToolResult.Artifact("runProgress", runId.ToString(), props) },
            runId);
    }

    private static async Task<ToolResult> RouteForReviewAsync(JsonElement input, ToolContext ctx)
    {
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return NoSpec(input);
        var svc = ctx.Services.GetRequiredService<SpecReviewService>();
        var (ok, error) = await svc.RouteAsync(spec.Id, new RouteRequest(null, Read(input, "note")), ctx.Actor, null, ctx.Ct);
        if (error is not null) return ToolResult.Failure(error.Code, error.Message, error.Details);
        var schemas = ctx.Services.GetRequiredService<SpecSchemaProvider>();
        var fresh = await ctx.Db.Specs.AsNoTracking().Include(s => s.Subroutine).FirstAsync(s => s.Id == spec.Id, ctx.Ct);
        var (artifact, payload, _) = await ArtifactBuilders.SpecSummaryAsync(ctx.Db, schemas, fresh, ctx.Ct);
        return ToolResult.Success(new { routed = ok, spec = payload }, $"routed {fresh.Subroutine?.Name} for review", artifact);
    }

    private static async Task<ToolResult> ReviewClaimAsync(JsonElement input, ToolContext ctx)
    {
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return NoSpec(input);
        var section = Read(input, "section");
        var claimId = Read(input, "claimId");
        var path = Read(input, "claimPath");
        if (string.IsNullOrWhiteSpace(path))
        {
            if (string.IsNullOrWhiteSpace(claimId)) return ToolResult.Failure("claim.id_required", "claimId is required.");
            if (string.IsNullOrWhiteSpace(section)) section = await SectionForClaimAsync(ctx, spec, claimId);
            if (section is null) return ToolResult.Failure("claim.not_found", $"No claim `{claimId}` in this spec.");
            path = ClaimPath.For(section, claimId);
        }
        var svc = ctx.Services.GetRequiredService<SpecReviewService>();
        var body = new ClaimReviewRequest(path, Read(input, "action") ?? "accept", Read(input, "reason"), Read(input, "editedText"));
        var (review, error) = await svc.ReviewClaimAsync(spec.Id, body, ctx.Actor, null, ctx.Ct);
        if (error is not null) return ToolResult.Failure(error.Code, error.Message, error.Details);
        var remaining = await svc.CheckPreconditionsAsync(spec, ctx.Ct);
        return ToolResult.Success(new
        {
            claimPath = review!.ClaimPath, action = review.Action, reviewedAt = review.ReviewedAt,
            remainingBeforeSign = remaining.Count,
        }, $"{review.Action} {claimId ?? path}");
    }

    private static async Task<ToolResult> ReviewAllClaimsAsync(JsonElement input, ToolContext ctx)
    {
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return NoSpec(input);
        if (spec.State != "IN_REVIEW")
            return ToolResult.Failure("spec.invalid_state", $"The spec is {spec.State}; claims can only be reviewed while IN_REVIEW.");
        var except = ReadArray(input, "except").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schemas = ctx.Services.GetRequiredService<SpecSchemaProvider>();
        var (_, _, claims) = await ArtifactBuilders.SpecSummaryAsync(ctx.Db, schemas, spec, ctx.Ct);
        var svc = ctx.Services.GetRequiredService<SpecReviewService>();
        var accepted = new List<string>();
        var skipped = new List<string>();
        foreach (var c in claims)
        {
            if (except.Contains(c.Id) || c.Review is not null) { skipped.Add(c.Id); continue; }
            var (_, error) = await svc.ReviewClaimAsync(spec.Id, new ClaimReviewRequest(ClaimPath.For(c.Section, c.Id), "accept", null, null), ctx.Actor, null, ctx.Ct);
            if (error is null) accepted.Add(c.Id);
        }
        var remaining = await svc.CheckPreconditionsAsync(spec, ctx.Ct);
        return ToolResult.Success(new { accepted, skipped, remainingBeforeSign = remaining.Count, remaining },
            $"accepted {accepted.Count} claims" + (except.Count > 0 ? $", left {except.Count} untouched" : ""));
    }

    private static async Task<ToolResult> SignSpecAsync(JsonElement input, ToolContext ctx)
    {
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return NoSpec(input);
        var svc = ctx.Services.GetRequiredService<SpecReviewService>();
        var (outcome, error) = await svc.SignAsync(spec.Id, "I have reviewed every claim", ctx.Actor, null, ctx.Ct);
        if (error is not null) return ToolResult.Failure(error.Code, error.Message, error.Details);
        var schemas = ctx.Services.GetRequiredService<SpecSchemaProvider>();
        var fresh = await ctx.Db.Specs.AsNoTracking().Include(s => s.Subroutine).FirstAsync(s => s.Id == spec.Id, ctx.Ct);
        var (artifact, payload, _) = await ArtifactBuilders.SpecSummaryAsync(ctx.Db, schemas, fresh, ctx.Ct);
        return ToolResult.Success(new
        {
            signed = true,
            idempotent = outcome!.Idempotent,
            signatureId = outcome.Signature.Id,
            signedAt = outcome.Signature.SignedAt,
            signerDisplay = outcome.Signature.SignerDisplay,
            keyId = outcome.Signature.SignatureKeyId,
            spec = payload,
        }, $"signed {fresh.Subroutine?.Name}", artifact);
    }

    private static async Task<ToolResult> GenerateScaffoldAsync(JsonElement input, ToolContext ctx)
    {
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return NoSpec(input);
        if (spec.State != "SIGNED")
            return ToolResult.Failure("spec.not_signed", $"The spec for `{spec.Subroutine?.Name}` is {spec.State}; only SIGNED specs can be built.");

        var archetypes = ctx.Services.GetRequiredService<ArchetypeRegistry>();
        var sourceLanguage = spec.Subroutine?.SourceLanguage ?? "";
        var requested = Read(input, "targetStack")?.Trim();
        string chosen;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            chosen = requested;
        }
        else
        {
            var compatible = archetypes.All()
                .Where(a => string.IsNullOrEmpty(sourceLanguage)
                            || a.Manifest.CompatibleSchemas.Any(s => string.Equals(s, sourceLanguage, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Manifest.TargetStack)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            chosen = Endpoints.ScaffoldEndpoints.PreferredStack(compatible);
        }

        var forTarget = archetypes.All()
            .Where(a => string.Equals(a.Manifest.TargetStack, chosen, StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Manifest.CompatibleSchemas.Count == 0 || string.IsNullOrEmpty(sourceLanguage)
                        || a.Manifest.CompatibleSchemas.Any(s => string.Equals(s, sourceLanguage, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (forTarget.Length == 0)
        {
            var available = archetypes.All()
                .Where(a => a.Manifest.CompatibleSchemas.Any(s => string.Equals(s, sourceLanguage, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Manifest.TargetStack).Distinct().ToArray();
            return ToolResult.Failure("scaffold.no_compatible_archetype",
                $"No archetype for {sourceLanguage} → {chosen}. Available targets for this language: {string.Join(", ", available)}.");
        }
        var match = archetypes.PickForSubroutine(chosen, spec.Subroutine?.Name ?? "", sourceLanguage) ?? forTarget[0];
        if (!(match.Manifest.Status ?? "").StartsWith("production", StringComparison.OrdinalIgnoreCase))
            return ToolResult.Failure("scaffold.target_gated", $"Archetype '{match.Manifest.Id}' for {chosen} is {match.Manifest.Status} — not self-service yet.");

        var runs = ctx.Services.GetRequiredService<BackgroundRunService>();
        var narrator = ctx.Services.GetRequiredService<Narrator>();
        var name = spec.Subroutine?.Name ?? spec.Id.ToString();

        // "Regenerate with the fix": the routine's latest failed gate becomes
        // a repair hint — file, line, code, message — appended to the prompt.
        string? repairHint = null;
        if (ReadBool(input, "repairFromLatestFailure"))
        {
            var lastFailed = await ctx.Db.ValidationRuns.AsNoTracking()
                .Where(r => r.SpecId == spec.Id && r.Status == "FAILED")
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ctx.Ct);
            if (lastFailed is not null)
            {
                string? log = null;
                if (lastFailed.LogBlobUri is { Length: > 0 } uri)
                {
                    try { log = await ctx.Services.GetRequiredService<Astra.Api.Storage.IBlobClient>().GetTextAsync(uri, ctx.Ct); }
                    catch (Exception) { /* the summary still describes the failure */ }
                }
                repairHint = Astra.Api.Validation.GateFailureDigest.Parse(lastFailed.Stage, log).ToRepairHint();
                if (string.IsNullOrWhiteSpace(repairHint))
                    repairHint = $"The previous attempt failed the {lastFailed.Stage} gate: {lastFailed.Summary}";
            }
        }

        var runId = runs.StartScaffold(spec.Id, name, chosen, ctx.Actor.Persona, ctx.Actor.DisplayName, repairHint);
        narrator.Track(runId, ctx.ConversationId, "migration", "scaffold", name, ctx.CorpusId,
            ctx.Actor.Persona.ToString().ToLowerInvariant(), ctx.Actor.DisplayName);

        var props = new
        {
            kind = "scaffold",
            corpusId = ctx.CorpusId,
            label = repairHint is null ? $"Generating {chosen} · {name}" : $"Regenerating {chosen} with the fix · {name}",
            agent = "migration",
            state = "RUNNING",
            startedAt = DateTimeOffset.UtcNow,
            links = new[] { new { label = "Watch live", href = $"/specs/{spec.Id}/scaffold?target={Uri.EscapeDataString(chosen)}" } },
        };
        return new ToolResult(true,
            new { runId, specId = spec.Id, name, targetStack = chosen, archetype = match.Manifest.Id, status = "started" },
            $"generating {chosen} for {name}",
            new[] { ToolResult.Artifact("runProgress", runId.ToString(), props) },
            runId);
    }

    private static async Task<ToolResult> RunGateAsync(JsonElement input, ToolContext ctx)
    {
        var scaffold = await ResolveScaffoldAsync(input, ctx);
        if (scaffold is null) return ToolResult.Failure("scaffold.not_found", "No generated code found for that routine — generate it first (generate_scaffold).");
        var gate = (Read(input, "gate") ?? "compile").Trim().ToLowerInvariant();
        if (gate is not ("compile" or "test-pack")) return ToolResult.Failure("gate.unknown", "gate must be compile or test-pack.");

        var runs = ctx.Services.GetRequiredService<BackgroundRunService>();
        var narrator = ctx.Services.GetRequiredService<Narrator>();
        var name = scaffold.Spec?.Subroutine?.Name ?? scaffold.Id.ToString();
        var runId = runs.StartGate(scaffold.Id, name, gate, ctx.Actor.Persona, ctx.Actor.DisplayName);
        narrator.Track(runId, ctx.ConversationId, "validation", "gate", name, ctx.CorpusId,
            ctx.Actor.Persona.ToString().ToLowerInvariant(), ctx.Actor.DisplayName);

        var props = new
        {
            kind = "gate",
            corpusId = ctx.CorpusId,
            label = $"{BackgroundRunService.GateLabel(gate)} gate · {name}",
            agent = "validation",
            state = "RUNNING",
            startedAt = DateTimeOffset.UtcNow,
            links = new[] { new { label = "Open full view", href = $"/scaffolds/{scaffold.Id}/validation" } },
        };
        return new ToolResult(true,
            new { runId, scaffoldId = scaffold.Id, name, gate, status = "started" },
            $"{gate} gate started for {name}",
            new[] { ToolResult.Artifact("runProgress", runId.ToString(), props) },
            runId);
    }

    private static async Task<ToolResult> GenerateDocsAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var stages = ReadArray(input, "stages");
        var orchestrator = ctx.Services.GetRequiredService<DocsGenerationOrchestrator>();
        Guid runId;
        try
        {
            runId = await orchestrator.StartAsync(corpus.Id,
                new DocsGenerationOrchestrator.GenerateOptions(stages.Count == 0 ? DocsGenerationOrchestrator.DefaultStages : stages, null, false), ctx.Ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ToolResult.Failure("docs.precondition_failed", ex.Message);
        }
        var narrator = ctx.Services.GetRequiredService<Narrator>();
        narrator.Track(runId, ctx.ConversationId, "discovery", "docs", corpus.Name, corpus.Id,
            ctx.Actor.Persona.ToString().ToLowerInvariant(), ctx.Actor.DisplayName);
        var props = new
        {
            kind = "docs",
            corpusId = corpus.Id,
            label = $"Documentation · {corpus.Name}",
            agent = "discovery",
            state = "RUNNING",
            startedAt = DateTimeOffset.UtcNow,
            links = new[] { new { label = "Open docs", href = $"/projects/{corpus.Id}/docs" } },
        };
        return new ToolResult(true, new { runId, corpusId = corpus.Id, stages = stages.Count == 0 ? DocsGenerationOrchestrator.DefaultStages : stages.ToArray(), status = "started" },
            $"docs generation started for {corpus.Name}",
            new[] { ToolResult.Artifact("runProgress", runId.ToString(), props) }, runId);
    }

    private static async Task<ToolResult> GenerateMigrationPlanAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var planner = ctx.Services.GetRequiredService<MigrationPlanner>();
        var strategy = Read(input, "strategy") ?? MigrationPlanner.DefaultStrategy;
        try
        {
            var plan = await planner.GenerateDraftAsync(corpus.Id, strategy, ctx.Actor, ctx.Ct);
            var (artifact, payload) = await ArtifactBuilders.PlanWavesAsync(ctx.Db, plan, ctx.Ct);
            return ToolResult.Success(payload, $"drafted a {plan.TotalWaves}-wave plan", artifact);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ToolResult.Failure("plan.precondition_failed", ex.Message);
        }
    }

    // ═══ Resolution helpers ══════════════════════════════════════════════

    public static async Task<Corpus?> ResolveCorpusAsync(JsonElement input, ToolContext ctx)
    {
        var raw = Read(input, "corpusId") ?? Read(input, "programme") ?? Read(input, "corpus");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (Guid.TryParse(raw, out var id))
                return await ctx.Db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ctx.Ct);
            var byName = await ctx.Db.Corpora.AsNoTracking()
                .Where(c => EF.Functions.ILike(c.Name, raw) || EF.Functions.ILike(c.Name, $"%{raw}%"))
                .OrderBy(c => c.Name.Length)
                .FirstOrDefaultAsync(ctx.Ct);
            if (byName is not null) return byName;
        }
        if (ctx.CorpusId is { } cid)
            return await ctx.Db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ctx.Ct);
        return null;
    }

    public static async Task<Subroutine?> ResolveRoutineAsync(JsonElement input, ToolContext ctx)
    {
        var raw = Read(input, "subroutineId") ?? Read(input, "routine") ?? Read(input, "name");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Guid.TryParse(raw, out var id))
            return await ctx.Db.Subroutines.AsNoTracking().Include(s => s.SourceFile).FirstOrDefaultAsync(s => s.Id == id, ctx.Ct);

        var q =
            from s in ctx.Db.Subroutines.AsNoTracking().Include(s => s.SourceFile)
            join f in ctx.Db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            join v in ctx.Db.SourceVersions.AsNoTracking() on f.SourceVersionId equals v.Id
            join c in ctx.Db.Corpora.AsNoTracking() on v.CorpusId equals c.Id
            where c.LatestVersionId == v.Id
            select new { s, c };
        if (ctx.CorpusId is { } cid) q = q.Where(x => x.c.Id == cid);
        var exact = await q.Where(x => x.s.Name == raw).Select(x => x.s).FirstOrDefaultAsync(ctx.Ct);
        if (exact is not null) return exact;
        var ci = await q.Where(x => EF.Functions.ILike(x.s.Name, raw)).Select(x => x.s).FirstOrDefaultAsync(ctx.Ct);
        if (ci is not null) return ci;
        var suffix = await q.Where(x => EF.Functions.ILike(x.s.Name, $"%{raw}")).OrderBy(x => x.s.Name.Length).Select(x => x.s).FirstOrDefaultAsync(ctx.Ct);
        return suffix;
    }

    public static async Task<Spec?> ResolveSpecAsync(JsonElement input, ToolContext ctx)
    {
        var raw = Read(input, "specId");
        if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var id))
            return await ctx.Db.Specs.AsNoTracking().Include(s => s.Subroutine).FirstOrDefaultAsync(s => s.Id == id, ctx.Ct);
        var sub = await ResolveRoutineAsync(input, ctx);
        if (sub is null)
        {
            // In a spec thread the spec is implied.
            if (ctx.SpecId is { } implied)
                return await ctx.Db.Specs.AsNoTracking().Include(s => s.Subroutine).FirstOrDefaultAsync(s => s.Id == implied, ctx.Ct);
            return null;
        }
        return await ctx.Db.Specs.AsNoTracking().Include(s => s.Subroutine)
            .Where(s => s.SubroutineId == sub.Id)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ctx.Ct);
    }

    public static async Task<Scaffold?> ResolveScaffoldAsync(JsonElement input, ToolContext ctx)
    {
        var raw = Read(input, "scaffoldId");
        if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var id))
            return await ctx.Db.Scaffolds.AsNoTracking().Include(s => s.Spec).ThenInclude(sp => sp!.Subroutine)
                .FirstOrDefaultAsync(s => s.Id == id, ctx.Ct);
        var spec = await ResolveSpecAsync(input, ctx);
        if (spec is null) return null;
        var target = Read(input, "targetStack");
        var q = ctx.Db.Scaffolds.AsNoTracking().Include(s => s.Spec).ThenInclude(sp => sp!.Subroutine).Where(s => s.SpecId == spec.Id);
        if (!string.IsNullOrWhiteSpace(target)) q = q.Where(s => s.TargetPlatform == target);
        return await q.OrderByDescending(s => s.GeneratedAt).FirstOrDefaultAsync(ctx.Ct);
    }

    private static async Task<string?> SectionForClaimAsync(ToolContext ctx, Spec spec, string claimId)
    {
        var schemas = ctx.Services.GetRequiredService<SpecSchemaProvider>();
        var (_, _, claims) = await ArtifactBuilders.SpecSummaryAsync(ctx.Db, schemas, spec, ctx.Ct);
        return claims.FirstOrDefault(c => string.Equals(c.Id, claimId, StringComparison.OrdinalIgnoreCase))?.Section;
    }

    private static ToolResult NoCorpus(JsonElement input) =>
        ToolResult.Failure("corpus.not_found",
            Read(input, "corpusId") is { Length: > 0 } r
                ? $"No programme matches \"{r}\". Call list_programmes to see the names."
                : "Which programme? This is the global thread — name one, or call list_programmes.");

    private static ToolResult NoRoutine(JsonElement input) =>
        ToolResult.Failure("routine.not_found",
            Read(input, "subroutineId") is { Length: > 0 } r
                ? $"No routine matches \"{r}\" in this programme. Try search_routines with a shorter substring."
                : "Which routine? Give its id or exact name (search_routines finds it).");

    private static ToolResult NoSpec(JsonElement input) =>
        ToolResult.Failure("spec.not_found",
            "No spec exists for that routine yet — offer to draft one (extract_spec).");

    // ═══ JSON helpers ════════════════════════════════════════════════════

    internal static string? Read(JsonElement input, string name)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in input.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }
        return null;
    }

    internal static int? ReadInt(JsonElement input, string name)
    {
        var s = Read(input, name);
        return int.TryParse(s, out var i) ? i : null;
    }

    internal static bool ReadBool(JsonElement input, string name) =>
        string.Equals(Read(input, name), "true", StringComparison.OrdinalIgnoreCase);

    internal static List<string> ReadArray(JsonElement input, string name)
    {
        var list = new List<string>();
        if (input.ValueKind != JsonValueKind.Object) return list;
        foreach (var p in input.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.Array)
                list.AddRange(p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            else if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                list.AddRange(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return list;
    }

    // JSON-schema builders
    private static Dictionary<string, object?> Obj(params (string Name, object Schema)[] props) => new()
    {
        ["type"] = "object",
        ["properties"] = props.ToDictionary(p => p.Name, p => p.Schema),
    };
    private static object Str(string description) => new Dictionary<string, object?> { ["type"] = "string", ["description"] = description };
    private static object Int(string description) => new Dictionary<string, object?> { ["type"] = "integer", ["description"] = description };
    private static object Bool(string description) => new Dictionary<string, object?> { ["type"] = "boolean", ["description"] = description };
    private static object Arr(string description) => new Dictionary<string, object?> { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "string" }, ["description"] = description };
    private static object Enum(params string[] values) => new Dictionary<string, object?> { ["type"] = "string", ["enum"] = values };
}
