using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Astra.Api.Persistence.Entities;
using Astra.Api.Work;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Copilot;

/// <summary>
/// P6 — the batch tools: many per-routine agents working in parallel, queued
/// behind ONE confirmation. They stop at the human wait state: none of them can
/// review a claim, sign a spec, approve a plan or commit code.
/// </summary>
public sealed partial class CopilotToolRegistry
{
    private static readonly Persona[] Builders = { Persona.Engineer };
    private static readonly Persona[] Drafters = { Persona.Engineer, Persona.Admin };

    private void RegisterWorkTools()
    {
        _tools.Add(new CopilotTool
        {
            Name = "get_work_run",
            Agent = "programme",
            Description = "Progress of a queued multi-routine run (wave run, extract-many, generate-many, gates run): counts per stage, what is running, what is waiting for SME sign-off, failures with reasons, cost, and serial-vs-parallel agent time. Defaults to the latest open run of this thread or programme. Use for 'how is the wave going', 'run status', 'what failed in the run'.",
            InputSchema = Obj(
                ("runId", Str("Run id or its first characters. Optional.")),
                ("corpusId", Str("Programme id or name. Optional in a programme thread."))),
            Execute = GetWorkRunAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_review_queue",
            Agent = "spec",
            Description = "Specs waiting for an SME to review and sign, oldest first, with the run (if any) whose later steps are held until each is signed. Use for 'review queue', 'what's waiting for sign-off', 'what should I sign next'.",
            InputSchema = Obj(("corpusId", Str("Programme id or name. Optional; omit in the global thread for every programme."))),
            Execute = GetReviewQueueAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "run_wave",
            Agent = "planning",
            Mutating = true,
            AllowedPersonas = Builders,
            Description = "Execute a wave of the APPROVED migration plan with many agents in parallel: for each routine draft the spec, wait for SME sign-off (agents never sign), then generate code, compile it and run the test pack. Omit wave for the next wave that has work; allWaves=true for the whole plan, leaves first. One confirmation covers the batch; progress is narrated and can be paused, cancelled or retried. Engineer only.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("wave", Int("Wave number. Optional.")),
                ("allWaves", Bool("True to queue every wave. Default false.")),
                ("stages", Arr("Optional subset of extract, scaffold, compile, test-pack (the sign-off wait is always kept between drafting and generating).")),
                ("targetStack", Str("Target stack id. Optional.")),
                ("budgetUsd", Str("Optional cost ceiling in USD; the run pauses when it is reached.")),
                ("maxParallel", Int("Optional ceiling on agents working at once per stage."))),
            Describe = (input, ctx) => DescribeBatchAsync(input, ctx, BuildWaveRequestAsync),
            Execute = (input, ctx) => StartBatchAsync(input, ctx, BuildWaveRequestAsync),
        });
        _tools.Add(new CopilotTool
        {
            Name = "extract_many",
            Agent = "spec",
            Mutating = true,
            AllowedPersonas = Drafters,
            Description = "Draft specs for many un-specified routines in parallel; each draft then waits in the review queue for an SME. Select by names, a source-path substring, or just a count; with none of these it takes every un-specified routine. One confirmation covers the batch. Use for 'extract all', 'draft specs for these routines'. For ONE routine use extract_spec.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("routines", Arr("Routine ids or exact names. Optional.")),
                ("path", Str("Optional substring of the source file path.")),
                ("limit", Int("Max routines. Omit for every matching routine (a run takes at most 500).")),
                ("budgetUsd", Str("Optional cost ceiling in USD.")),
                ("maxParallel", Int("Optional ceiling on agents working at once."))),
            Describe = (input, ctx) => DescribeBatchAsync(input, ctx, BuildExtractRequestAsync),
            Execute = (input, ctx) => StartBatchAsync(input, ctx, BuildExtractRequestAsync),
        });
        _tools.Add(new CopilotTool
        {
            Name = "generate_many",
            Agent = "migration",
            Mutating = true,
            AllowedPersonas = Builders,
            Description = "Generate target code in parallel for many routines whose specs are SIGNED; set thenGates to also compile and test each. One confirmation covers the batch. For ONE routine use generate_scaffold. Engineer only.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("routines", Arr("Routine ids or exact names. Optional; default every signed routine.")),
                ("path", Str("Optional substring of the source file path.")),
                ("limit", Int("Max routines. Omit for every matching routine (a run takes at most 500).")),
                ("targetStack", Str("Target stack id. Optional.")),
                ("thenGates", Bool("True to run compile and test-pack on each package afterwards.")),
                ("budgetUsd", Str("Optional cost ceiling in USD.")),
                ("maxParallel", Int("Optional ceiling on agents working at once."))),
            Describe = (input, ctx) => DescribeBatchAsync(input, ctx, BuildGenerateRequestAsync),
            Execute = (input, ctx) => StartBatchAsync(input, ctx, BuildGenerateRequestAsync),
        });
        _tools.Add(new CopilotTool
        {
            Name = "run_gates_many",
            Agent = "validation",
            Mutating = true,
            AllowedPersonas = Builders,
            Description = "Run the compile and/or test-pack gate on many generated packages; builds queue in a bounded pool. One confirmation covers the batch. For ONE routine use run_gate. Engineer only.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("routines", Arr("Routine ids or exact names. Optional; default every routine with uncommitted generated code.")),
                ("gates", Arr("compile and/or test-pack. Default both.")),
                ("limit", Int("Max routines. Omit for every matching routine (a run takes at most 500)."))),
            Describe = (input, ctx) => DescribeBatchAsync(input, ctx, BuildGatesRequestAsync),
            Execute = (input, ctx) => StartBatchAsync(input, ctx, BuildGatesRequestAsync),
        });
        _tools.Add(new CopilotTool
        {
            Name = "control_work_run",
            Agent = "programme",
            Mutating = true,
            AllowedPersonas = Drafters,
            Description = "Pause, resume, cancel, or retry the failed steps of a multi-routine run. Resume accepts a higher cost ceiling. Engineer or admin.",
            InputSchema = Obj(
                ("action", Enum("pause", "resume", "cancel", "retry-failed")),
                ("runId", Str("Run id or its first characters. Optional: the latest open run.")),
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("budgetUsd", Str("New cost ceiling in USD (resume only)."))),
            Describe = async (input, ctx) =>
            {
                var run = await ResolveWorkRunAsync(input, ctx);
                var action = (Read(input, "action") ?? "pause").Replace('-', ' ');
                var budget = ReadMoney(input, "budgetUsd");
                return run is null
                    ? $"{action} the run"
                    : $"{(action == "retry failed" ? "retry the failed steps of" : action)} \"{run.Label}\"" + (budget is { } b ? $" with a ${b:0.00} ceiling" : "");
            },
            Execute = ControlWorkRunAsync,
        });
    }

    // ═══ Reads ═══════════════════════════════════════════════════════════

    private static async Task<ToolResult> GetWorkRunAsync(JsonElement input, ToolContext ctx)
    {
        var run = await ResolveWorkRunAsync(input, ctx);
        if (run is null) return ToolResult.Failure("work.run_not_found", "No multi-unit run yet. Start one with run_wave or extract_many.");
        var snapshot = await ctx.Services.GetRequiredService<WorkQueue>().SnapshotAsync(run.Id, ctx.Ct);
        return ToolResult.Success(RunPayload(snapshot!), $"{snapshot!.Label}: {snapshot.State.ToLowerInvariant()}",
            WorkRunArtifact(snapshot));
    }

    private static async Task<ToolResult> GetReviewQueueAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null && Read(input, "corpusId") is { Length: > 0 }) return NoCorpus(input);
        var items = await ctx.Services.GetRequiredService<WorkRunService>().ReviewQueueAsync(corpus?.Id, 50, ctx.Ct);
        return ToolResult.Success(new
            {
                count = items.Count,
                holdingAgentWork = items.Count(i => i.StepsBlocked > 0),
                items = items.Take(25).Select(i => new { i.SubroutineId, i.Name, i.SpecId, programme = i.CorpusName, i.WaitingSince, run = i.RunLabel, i.StepsBlocked }),
                note = "Only an SME can review claims and sign; each can be done in any order.",
            },
            items.Count == 0 ? "review queue is empty" : $"{items.Count} spec(s) waiting for sign-off",
            ArtifactBuilders.ReviewQueue(corpus?.Id, items));
    }

    // ═══ Batch start ═════════════════════════════════════════════════════

    private delegate Task<(WorkRunRequest? Request, ToolResult? Failure)> RequestBuilder(JsonElement input, ToolContext ctx);

    private static async Task<string> DescribeBatchAsync(JsonElement input, ToolContext ctx, RequestBuilder build)
    {
        var (request, failure) = await build(input, ctx);
        if (request is null) return $"queue this batch (it will report: {failure?.Summary})";
        var preview = await ctx.Services.GetRequiredService<WorkRunService>().PreviewAsync(request, ctx.Ct);
        return BatchSentence.Describe(request, preview);
    }

    private static async Task<ToolResult> StartBatchAsync(JsonElement input, ToolContext ctx, RequestBuilder build)
    {
        var (request, failure) = await build(input, ctx);
        if (request is null) return failure!;
        var work = ctx.Services.GetRequiredService<WorkRunService>();
        var (run, preview, error) = await work.StartAsync(request, ctx.Actor, ctx.Ct);
        if (error is not null) return ToolResult.Failure(error.Code, error.Message, preview?.Plan.Skipped.Take(20));

        var snapshot = (await ctx.Services.GetRequiredService<WorkQueue>().SnapshotAsync(run!.Id, ctx.Ct))!;
        return new ToolResult(true,
            new
            {
                runId = run.Id,
                label = run.Label,
                status = "queued",
                routines = preview!.Plan.RoutineCount,
                queued = WorkStages.Order.Where(k => preview.Plan.Count(k) > 0).ToDictionary(k => k, k => preview.Plan.Count(k)),
                parallel = preview.Caps,
                skipped = preview.Plan.Skipped.Take(20),
                skippedCount = preview.Plan.Skipped.Count,
                warnings = preview.Warnings,
                note = "Runs in the background; progress and the sign-off wait are posted to this thread.",
            },
            $"queued {preview.Plan.RoutineCount} code unit(s): {run.Label}",
            new[] { WorkRunArtifact(snapshot) },
            run.Id);
    }

    private static async Task<(WorkRunRequest?, ToolResult?)> BuildWaveRequestAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return (null, NoCorpus(input));
        var stages = WorkStages.Normalise(ReadArray(input, "stages") is { Count: > 0 } s ? s : null);
        var all = ReadBool(input, "allWaves");
        var work = ctx.Services.GetRequiredService<WorkRunService>();
        var (selection, error) = await work.SelectWaveAsync(corpus.Id, ReadInt(input, "wave"), all, stages, ctx.Ct);
        if (error is not null) return (null, ToolResult.Failure(error.Code, error.Message));
        var label = all ? $"All waves · {corpus.Name}" : $"Wave {selection!.Waves[0].WaveNumber} · {corpus.Name}";
        return (new WorkRunRequest("wave", corpus.Id, selection!.Routines, stages, label,
            Read(input, "targetStack"), ReadMoney(input, "budgetUsd"), ReadCaps(input), ctx.ConversationId,
            selection.Waves.Count == 1 ? selection.Waves[0].Id : null), null);
    }

    private static Task<(WorkRunRequest?, ToolResult?)> BuildExtractRequestAsync(JsonElement input, ToolContext ctx) =>
        BuildListRequestAsync(input, ctx, "extract-many", "Spec drafts", new[] { "PARSED" },
            new[] { WorkStages.Extract, WorkStages.Signoff }, "no un-specified code unit");

    private static Task<(WorkRunRequest?, ToolResult?)> BuildGenerateRequestAsync(JsonElement input, ToolContext ctx) =>
        BuildListRequestAsync(input, ctx, "generate-many", "Code generation", new[] { "SIGNED" },
            ReadBool(input, "thenGates")
                ? new[] { WorkStages.Scaffold, WorkStages.Compile, WorkStages.TestPack }
                : new[] { WorkStages.Scaffold },
            "no code unit with a signed spec waiting for code");

    private static Task<(WorkRunRequest?, ToolResult?)> BuildGatesRequestAsync(JsonElement input, ToolContext ctx)
    {
        var asked = ReadArray(input, "gates").Select(g => g.Trim().ToLowerInvariant().Replace('_', '-')).ToHashSet();
        var stages = new[] { WorkStages.Compile, WorkStages.TestPack }.Where(g => asked.Count == 0 || asked.Contains(g)).ToArray();
        if (stages.Length == 0)
            return Task.FromResult<(WorkRunRequest?, ToolResult?)>((null, ToolResult.Failure("gate.unknown", "gates must be compile and/or test-pack.")));
        return BuildListRequestAsync(input, ctx, "gates-many", "Gates", new[] { "SCAFFOLDED" }, stages,
            "no code unit with uncommitted generated code");
    }

    private static async Task<(WorkRunRequest?, ToolResult?)> BuildListRequestAsync(
        JsonElement input, ToolContext ctx, string kind, string title, string[] states, string[] stages, string nothing)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return (null, NoCorpus(input));
        var names = ReadArray(input, "routines");
        var path = Read(input, "path")?.Trim();
        // No number means every matching routine; the Confirm card says how many that is.
        var limit = Math.Clamp(ReadInt(input, "limit") ?? WorkRunService.MaxRoutinesPerRun, 1, WorkRunService.MaxRoutinesPerRun);

        var q =
            from s in ctx.Db.Subroutines.AsNoTracking()
            join f in ctx.Db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            join v in ctx.Db.SourceVersions.AsNoTracking() on f.SourceVersionId equals v.Id
            where v.Id == corpus.LatestVersionId
            select new { s.Id, s.Name, s.State, f.RelativePath };
        if (!string.IsNullOrEmpty(path)) q = q.Where(x => EF.Functions.ILike(x.RelativePath, $"%{path}%"));

        List<Guid> ids;
        var matching = 0;
        if (names.Count > 0)
        {
            ids = new();
            var unknown = new List<string>();
            foreach (var name in names.Take(WorkRunService.MaxRoutinesPerRun))
            {
                var matches = Guid.TryParse(name, out var g)
                    ? await q.Where(x => x.Id == g).Select(x => x.Id).ToListAsync(ctx.Ct)
                    : await q.Where(x => EF.Functions.ILike(x.Name, name)).Select(x => x.Id).ToListAsync(ctx.Ct);
                if (matches.Count == 0) unknown.Add(name);
                ids.AddRange(matches);
            }
            if (unknown.Count > 0)
                return (null, ToolResult.Failure("routine.not_found", $"No code unit matches: {string.Join(", ", unknown.Take(10))}. Try search_routines."));
        }
        else
        {
            var inState = q.Where(x => states.Contains(x.State));
            matching = await inState.CountAsync(ctx.Ct);
            ids = await inState.OrderBy(x => x.RelativePath).ThenBy(x => x.Name)
                .Select(x => x.Id).Take(limit).ToListAsync(ctx.Ct);
        }
        if (ids.Count == 0)
            return (null, ToolResult.Failure("work.nothing_to_do", $"There is {nothing} in {corpus.Name}" + (string.IsNullOrEmpty(path) ? "." : $" under \"{path}\".")));

        var label = BatchSentence.ListLabel(title, ids.Distinct().Count(), matching, corpus.Name);
        return (new WorkRunRequest(kind, corpus.Id, ids.Distinct().Select(i => (i, 0)).ToList(), stages, label,
            Read(input, "targetStack"), ReadMoney(input, "budgetUsd"), ReadCaps(input), ctx.ConversationId), null);
    }

    private static async Task<ToolResult> ControlWorkRunAsync(JsonElement input, ToolContext ctx)
    {
        var run = await ResolveWorkRunAsync(input, ctx);
        if (run is null) return ToolResult.Failure("work.run_not_found", "No multi-unit run to control.");
        var action = (Read(input, "action") ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        var (message, error) = await ctx.Services.GetRequiredService<WorkRunService>()
            .ControlAsync(run.Id, action, ReadMoney(input, "budgetUsd"), ctx.Actor, ctx.Ct);
        if (error is not null) return ToolResult.Failure(error.Code, error.Message);
        var snapshot = (await ctx.Services.GetRequiredService<WorkQueue>().SnapshotAsync(run.Id, ctx.Ct))!;
        return new ToolResult(true, new { message, run = RunPayload(snapshot) }, $"{action} · {snapshot.Label}",
            new[] { WorkRunArtifact(snapshot) }, run.Id);
    }

    // ═══ helpers ═════════════════════════════════════════════════════════

    private static async Task<WorkRun?> ResolveWorkRunAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        return await ctx.Services.GetRequiredService<WorkRunService>()
            .FindRunAsync(Read(input, "runId"), corpus?.Id, ctx.ConversationId, ctx.Ct);
    }

    internal static decimal? ReadMoney(JsonElement input, string name) =>
        decimal.TryParse(Read(input, name)?.TrimStart('$'), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private static IReadOnlyDictionary<string, int>? ReadCaps(JsonElement input) =>
        ReadInt(input, "maxParallel") is { } n && n > 0
            ? WorkStages.Machine.ToDictionary(k => k, k => Math.Min(n, WorkStages.DefaultCaps[k]))
            : null;

    private static object RunPayload(WorkRunSnapshot s) => new
    {
        runId = s.RunId,
        s.Label,
        s.State,
        progress = s.Summary,
        stages = s.Stages,
        running = s.Running.Select(i => new { i.Kind, i.Name }),
        waitingForSignoff = new { count = s.WaitingForSignoffCount, routines = s.WaitingForSignoff.Select(i => i.Name) },
        failures = s.Failures.Select(i => new { i.Kind, i.Name, i.Error }),
        costUsd = s.CostUsd,
        s.Calls,
        s.InputTokens,
        s.OutputTokens,
        s.BudgetUsd,
        timing = s.Metrics,
    };

    internal static ArtifactDto WorkRunArtifact(WorkRunSnapshot s) =>
        ToolResult.Artifact("workRun", s.RunId.ToString(), s);
}
