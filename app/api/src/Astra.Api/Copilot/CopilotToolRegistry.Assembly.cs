using System.Text.Json;
using Astra.Api.Assembly;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Copilot;

/// <summary>
/// One application from many code units: the per-unit packages that passed their gates are merged into a single
/// project, which is then built and tested once (<see cref="AssemblyService"/>). Deterministic — no model call.
/// </summary>
public sealed partial class CopilotToolRegistry
{
    private void RegisterAssemblyTools()
    {
        _tools.Add(new CopilotTool
        {
            Name = "assemble_module",
            Agent = "migration",
            Mutating = true,
            AllowedPersonas = new[] { Persona.Engineer },
            Description = "Assemble one deployable application from a module's code units: take each unit's latest generated package that compiles (or whose tests pass, with requireTests), merge them into a single project (one build file, one application, shared classes held once, schemas and settings merged, every disagreement reported), then build it and run all its tests together. Select the module by source-path prefix (e.g. 'htdocs/societe/'), a migration wave number, or code unit names; with none, the whole programme. java-spring only so far. Runs in the background and is narrated. Engineer only. Use for 'assemble the societe module', 'build one application for wave 2', 'merge these units into one service'.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("pathPrefix", Str("Source-path prefix of the module, e.g. htdocs/societe/. Optional.")),
                ("wave", Int("Migration wave number of the latest plan. Optional.")),
                ("units", Arr("Code unit names or ids. Optional.")),
                ("requireTests", Bool("Only include units whose test pack passes (default: units that compile).")),
                ("name", Str("Name for the application. Optional.")),
                ("targetStack", Str("Target stack; java-spring (default) is the one that can be assembled so far."))),
            Describe = async (input, ctx) =>
            {
                var corpus = await ResolveCorpusAsync(input, ctx);
                var what = Read(input, "pathPrefix") is { Length: > 0 } p ? $"the code units under `{p}`"
                    : ReadInt(input, "wave") is { } w ? $"wave {w}'s code units"
                    : ReadArray(input, "units").Count is var n and > 0 ? $"{n} code unit{(n == 1 ? "" : "s")}"
                    : "every code unit";
                return $"assemble {what} of {corpus?.Name ?? "this programme"} into one application, then build it and run all its tests together";
            },
            Execute = AssembleModuleAsync,
        });
        _tools.Add(new CopilotTool
        {
            Name = "get_assembly",
            Agent = "migration",
            Description = "An assembled application: which code units it holds, which were left out and why, what is held once, the disagreements found, and whether it builds and its tests pass. Defaults to the programme's latest. Use for 'how did the assembly go', 'show the assembled application'.",
            InputSchema = Obj(
                ("assemblyId", Str("Assembly id. Optional.")),
                ("corpusId", Str("Programme id or name. Optional in a programme thread."))),
            Execute = GetAssemblyAsync,
        });
    }

    private static async Task<ToolResult> AssembleModuleAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return ToolResult.Failure("corpus.not_found", "Which programme? Name it, or ask from its thread.");
        var target = Read(input, "targetStack") is { Length: > 0 } t ? t.Trim().ToLowerInvariant() : "java-spring";
        if (ModuleAssemblers.For(target) is null)
            return ToolResult.Failure("assembly.stack_unsupported", $"Only {string.Join(", ", ModuleAssemblers.Supported)} code can be assembled so far.");

        Guid? waveId = null;
        if (ReadInt(input, "wave") is { } waveNo)
        {
            var plan = await ctx.Db.MigrationPlans.AsNoTracking()
                .Where(p => p.CorpusId == corpus.Id && p.ArchivedAt == null)
                .OrderByDescending(p => p.Status == "approved").ThenByDescending(p => p.CreatedAt)
                .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ctx.Ct);
            waveId = plan is null ? null : await ctx.Db.MigrationWaves.AsNoTracking()
                .Where(w => w.MigrationPlanId == plan && w.WaveNumber == waveNo).Select(w => (Guid?)w.Id).FirstOrDefaultAsync(ctx.Ct);
            if (waveId is null) return ToolResult.Failure("wave.not_found", $"{corpus.Name} has no wave {waveNo} in its migration plan.");
        }

        var unitIds = new List<Guid>();
        foreach (var u in ReadArray(input, "units"))
        {
            if (Guid.TryParse(u, out var g)) { unitIds.Add(g); continue; }
            var ids = await ctx.Db.Subroutines.AsNoTracking()
                .Where(s => s.Name == u && s.SourceFile!.SourceVersionId == corpus.LatestVersionId).Select(s => s.Id).ToListAsync(ctx.Ct);
            if (ids.Count == 0) return ToolResult.Failure("unit.not_found", $"No code unit named `{u}` in {corpus.Name}.");
            unitIds.AddRange(ids);
        }

        var request = new AssemblyRequest(Read(input, "pathPrefix"), waveId, unitIds.Count > 0 ? unitIds : null, target,
            ReadBool(input, "requireTests"), Read(input, "name"));
        var row = await ctx.Services.GetRequiredService<AssemblyService>()
            .StartAsync(ctx.Db, corpus, request, ctx.Actor.Persona, ctx.Actor.DisplayName, ctx.Ct);
        ctx.Services.GetRequiredService<Narrator>().Track(row.Id, ctx.ConversationId, AssemblyService.Agent, "assembly", row.Name, corpus.Id,
            ctx.Actor.Persona.ToString().ToLowerInvariant(), ctx.Actor.DisplayName);
        var props = new
        {
            kind = "assembly",
            corpusId = corpus.Id,
            label = $"Assembling · {row.Name}",
            agent = AssemblyService.Agent,
            state = "RUNNING",
            startedAt = DateTimeOffset.UtcNow,
            links = new[] { new { label = "Status", href = $"/api/v1/assemblies/{row.Id}" } },
        };
        return new ToolResult(true,
            new { runId = row.Id, assemblyId = row.Id, name = row.Name, targetStack = target, status = "started" },
            $"assembling {row.Name}",
            new[] { ToolResult.Artifact("runProgress", row.Id.ToString(), props) },
            row.Id);
    }

    private static async Task<ToolResult> GetAssemblyAsync(JsonElement input, ToolContext ctx)
    {
        Persistence.Entities.ModuleAssembly? a = null;
        if (Read(input, "assemblyId") is { Length: > 0 } s && Guid.TryParse(s, out var id))
            a = await ctx.Db.ModuleAssemblies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ctx.Ct);
        else
        {
            var corpus = await ResolveCorpusAsync(input, ctx);
            if (corpus is not null)
                a = await ctx.Db.ModuleAssemblies.AsNoTracking().Where(x => x.CorpusId == corpus.Id)
                    .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ctx.Ct);
        }
        if (a is null) return ToolResult.Failure("assembly.not_found", "No assembled application yet — ask me to assemble a module first.");
        return new ToolResult(true, AssemblyService.Shape(a), $"{a.Name}: {a.Summary}",
            new[] { AssemblyArtifact(a) });
    }

    /// <summary>The <c>assembly</c> card: what went in, what was left out and why, what is shared, what disagrees, how the build went.</summary>
    public static ArtifactDto AssemblyArtifact(Persistence.Entities.ModuleAssembly a) =>
        ToolResult.Artifact("assembly", a.Id.ToString(), AssemblyService.Shape(a));
}
