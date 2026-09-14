using System.Diagnostics;
using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Astra.Api.Copilot;
using Astra.Api.Llm;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Llm.Dependency;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Runs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Assessment;

/// <summary>
/// WS5 — the "10-minute Assessment". Everything numeric is computed from
/// what the platform already knows (parse inventory, dependency graph,
/// survey digests, pattern clusters) plus a transparent effort/risk model;
/// one Sonnet call writes the executive narrative and picks the
/// modernization mode from those facts. Persisted as a DocSection of kind
/// <c>assessment</c> so it exports with the rest of the documentation, and
/// narrated into the programme thread as an <c>assessment</c> card.
/// </summary>
public sealed class AssessmentService
{
    public const string SectionKind = "assessment";
    private const string ToolName = "write_assessment";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunEventBus _bus;
    private readonly Narrator _narrator;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _anthropic;
    private readonly AnthropicRateLimiter _limiter;
    private readonly PromptLibrary _prompts;
    private readonly ICopilotBrain _brain;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AssessmentService> _logger;

    public AssessmentService(
        IServiceScopeFactory scopeFactory, RunEventBus bus, Narrator narrator, IHttpClientFactory httpFactory,
        IOptions<AnthropicOptions> anthropic, AnthropicRateLimiter limiter, PromptLibrary prompts, ICopilotBrain brain,
        IHostApplicationLifetime lifetime, ILogger<AssessmentService> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _narrator = narrator;
        _httpFactory = httpFactory;
        _anthropic = anthropic.Value;
        _limiter = limiter;
        _prompts = prompts;
        _brain = brain;
        _lifetime = lifetime;
        _logger = logger;
    }

    // ── Public surface ───────────────────────────────────────────────────

    /// <summary>Start an assessment run in the background; the Narrator posts
    /// the card into <paramref name="conversationId"/> when it finishes.</summary>
    public Guid Start(Guid corpusId, string corpusName, Guid? conversationId, Persona persona, string displayName)
    {
        var runId = Guid.NewGuid();
        _bus.State(runId, "architecture", "RUNNING", $"Assessing {corpusName}…");
        if (conversationId is { } cid)
            _narrator.Track(runId, cid, "architecture", "assessment", corpusName, corpusId,
                persona.ToString().ToLowerInvariant(), displayName);
        _ = Task.Run(() => RunAsync(runId, corpusId));
        return runId;
    }

    public static async Task<DocSection?> LatestAsync(AppDbContext db, Guid corpusId, CancellationToken ct) =>
        await db.DocSections.AsNoTracking()
            .Where(s => s.CorpusId == corpusId && s.SectionKind == SectionKind && s.State != "SUPERSEDED")
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>The `assessment` card props stored inside the section payload.</summary>
    public static JsonElement? CardOf(DocSection section)
    {
        var root = section.PayloadJson.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("card", out var card) ? card : null;
    }

    // ── The run ──────────────────────────────────────────────────────────

    private async Task RunAsync(Guid runId, Guid corpusId)
    {
        var ct = _lifetime.ApplicationStopping;
        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
            var graphs = scope.ServiceProvider.GetRequiredService<DependencyGraphBuilder>();
            var archetypes = scope.ServiceProvider.GetRequiredService<ArchetypeRegistry>();

            var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == corpusId, ct)
                ?? throw new InvalidOperationException("Corpus not found.");
            if (corpus.LatestVersionId is not { } versionId)
                throw new InvalidOperationException("Corpus has no ingested version yet.");

            Stage(runId, "inventory", 1, 5, "Inventory");
            var (routines, files, language) = await conversations.ProgrammeStatsAsync(corpus, ct);
            var byLanguage = await (
                from s in db.Subroutines.AsNoTracking()
                join f in db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
                where f.SourceVersionId == versionId
                group s by s.SourceLanguage into g
                select new { Language = g.Key, Count = g.Count() }).ToListAsync(ct);
            var funnel = await conversations.FunnelAsync(corpus, ct);

            Stage(runId, "graph", 2, 5, "Dependency hotspots");
            var graph = await graphs.BuildAsync(corpusId, ct);
            var hotspots = graph is null ? new List<Hotspot>() : Hotspots(graph, 10);
            var cyclicMembers = graph?.Sccs.Where(s => s.Members.Count > 1).Sum(s => s.Members.Count) ?? 0;
            var sharedStorageEdges = graph?.Stats.SharedStorageEdgeCount ?? 0;
            // Coupling is measured as the share of routines that touch shared
            // storage at all — edge counts are O(n²) for globals-heavy code
            // (a C++ estate had ~900 edges per routine) and say nothing useful.
            var sharedStorageRoutines = graph is null ? 0 : graph.Edges
                .Where(e => e.Type == "shared-storage").SelectMany(e => new[] { e.From, e.To }).Distinct().Count();
            var externalCallees = graph?.Stats.ExternalCalleeCount ?? 0;

            Stage(runId, "patterns", 3, 5, "Patterns and digests");
            var latestRun = await db.PatternAnalysisRuns.AsNoTracking()
                .Where(r => r.CorpusId == corpusId && (r.State == "SUCCEEDED" || r.State == "PARTIAL"))
                .OrderByDescending(r => r.CompletedAt).FirstOrDefaultAsync(ct);
            var clusters = latestRun is null
                ? new List<PatternCluster>()
                : await db.PatternClusters.AsNoTracking().Where(c => c.PatternAnalysisRunId == latestRun.Id)
                    .OrderByDescending(c => c.MemberCount).ToListAsync(ct);
            var digests = await db.RoutineDigests.AsNoTracking().Where(d => d.SourceVersionId == versionId)
                .Select(d => new { d.Complexity, d.ModernizationFlagsJson, d.DataAccessJson }).ToListAsync(ct);
            var complexCount = digests.Count(d => d.Complexity == "complex");
            var flagged = digests.Count(d => d.ModernizationFlagsJson is { Length: > 2 });
            var flagCounts = new Dictionary<string, int>();
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in digests)
            {
                foreach (var f in ReadStrings(d.ModernizationFlagsJson)) flagCounts[f] = flagCounts.GetValueOrDefault(f) + 1;
                foreach (var t in ReadTables(d.DataAccessJson)) tables.Add(t);
            }

            Stage(runId, "model", 4, 5, "Effort and risk");
            var model = EffortRiskModel.Compute(new EffortRiskModel.Inputs(
                Routines: routines,
                Language: language,
                CyclicFraction: routines == 0 ? 0 : (double)cyclicMembers / routines,
                HotspotShare: routines == 0 || hotspots.Count == 0 ? 0 : (double)hotspots[0].TransitiveCallers / routines,
                SharedStorageFraction: graph is null || graph.Stats.NodeCount == 0 ? 0 : (double)sharedStorageRoutines / graph.Stats.NodeCount,
                ComplexFraction: digests.Count == 0 ? 0 : (double)complexCount / digests.Count,
                FlaggedFraction: digests.Count == 0 ? 0 : (double)flagged / digests.Count,
                ExternalCallees: externalCallees,
                UiFlagged: flagCounts.Keys.Any(k => k.Contains("ui", StringComparison.OrdinalIgnoreCase)),
                DataFlagged: flagCounts.Keys.Any(k => k.Contains("data", StringComparison.OrdinalIgnoreCase)) || tables.Count > 0));

            var candidateTargets = archetypes.All()
                .Where(a => language is null || a.Manifest.CompatibleSchemas.Count == 0
                            || a.Manifest.CompatibleSchemas.Any(s => string.Equals(s, language, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Manifest.TargetStack).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

            var facts = new
            {
                programme = corpus.Name,
                sourceLanguage = language,
                languageLabel = ConversationService.LanguageLabel(language),
                inventory = new
                {
                    routines,
                    files,
                    loc = corpus.TotalLoc,
                    languages = byLanguage.Select(l => new { language = l.Language, routines = l.Count }),
                },
                progress = funnel,
                dependencies = graph is null ? null : new
                {
                    graph.Stats.NodeCount,
                    graph.Stats.CallEdgeCount,
                    graph.Stats.SharedStorageEdgeCount,
                    routinesTouchingSharedStorage = sharedStorageRoutines,
                    cyclicGroups = graph.Stats.CyclicSccCount,
                    routinesInCycles = cyclicMembers,
                    externalCallees,
                    leaves = graph.Stats.LeafCount,
                    roots = graph.Stats.RootCount,
                    hotspots = hotspots.Select(h => new { h.Name, h.Callers, h.TransitiveCallers, h.InCycle }),
                    moduleCycles = graph.ModuleGraph.Cycles.Count,
                },
                patterns = latestRun is null ? null : new
                {
                    analysedAt = latestRun.CompletedAt,
                    clusters = clusters.Count,
                    multiMember = clusters.Count(c => c.MemberCount > 1),
                    singletons = clusters.Count(c => c.MemberCount <= 1),
                    routinesInMultiMember = clusters.Where(c => c.MemberCount > 1).Sum(c => c.MemberCount),
                    topClusters = clusters.Take(8).Select(c => new { c.Label, c.MemberCount, c.SuggestedArchetypeName }),
                },
                digests = digests.Count == 0 ? null : new
                {
                    total = digests.Count,
                    complex = complexCount,
                    moderate = digests.Count(d => d.Complexity == "moderate"),
                    simple = digests.Count(d => d.Complexity == "simple"),
                    modernizationFlags = flagCounts.OrderByDescending(kv => kv.Value).Take(10).Select(kv => new { flag = kv.Key, routines = kv.Value }),
                    tablesTouched = tables.Count,
                    sampleTables = tables.Take(12),
                },
                effort = model.Effort,
                risk = model.Risk,
                heuristicRecommendation = model.Recommendation,
                candidateTargets,
            };

            Stage(runId, "narrative", 5, 5, "Writing the assessment");
            var narrative = await WriteNarrativeAsync(db, facts, model, candidateTargets, ct);

            var card = new
            {
                corpusName = corpus.Name,
                sourceLanguage = language,
                generatedAt = DateTimeOffset.UtcNow,
                inventory = new { routines, files, loc = corpus.TotalLoc, languages = byLanguage.Select(l => new { language = l.Language, routines = l.Count }) },
                hotspots = hotspots.Select(h => new { id = h.Id, name = h.Name, callers = h.Callers, transitiveCallers = h.TransitiveCallers, inCycle = h.InCycle }),
                cycles = graph?.Stats.CyclicSccCount ?? 0,
                patterns = new
                {
                    clusters = clusters.Count,
                    multiMember = clusters.Count(c => c.MemberCount > 1),
                    singletons = clusters.Count(c => c.MemberCount <= 1),
                    topClusters = clusters.Where(c => c.MemberCount > 1).Take(5).Select(c => new { label = c.Label, memberCount = c.MemberCount }),
                    analysed = latestRun is not null,
                },
                effort = model.Effort,
                risk = model.Risk,
                recommendation = new { mode = narrative.Mode, targetStack = narrative.TargetStack, summary = narrative.Summary },
                sectionId = (Guid?)null,
                href = $"/projects/{corpusId}/assessment",
            };

            // Persist as a DocSection (supersede the previous one).
            var previous = await db.DocSections
                .Where(s => s.CorpusId == corpusId && s.SectionKind == SectionKind && s.State != "SUPERSEDED")
                .ToListAsync(ct);
            foreach (var p in previous) { p.State = "SUPERSEDED"; p.UpdatedAt = DateTimeOffset.UtcNow; }

            var section = new DocSection
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = versionId,
                SectionKind = SectionKind,
                Scope = "corpus",
                State = "DRAFT",
                LlmCallId = narrative.LlmCallId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                PreviousSectionId = previous.OrderByDescending(p => p.CreatedAt).FirstOrDefault()?.Id,
            };
            var cardWithId = JsonSerializer.SerializeToNode(card, ConversationJson.Web)!.AsObject();
            cardWithId["sectionId"] = section.Id.ToString();
            section.PayloadJson = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                card = cardWithId,
                facts,
                narrative = new { narrative.Mode, narrative.TargetStack, narrative.Summary, narrative.Model },
            }, ConversationJson.Web));
            section.RenderedMarkdown = narrative.Markdown + "\n\n" + FactsAppendix(facts, model);
            db.DocSections.Add(section);
            await db.SaveChangesAsync(ct);

            sw.Stop();
            _bus.Publish(runId, "architecture", "", "item", new { kind = "assessment", sectionId = section.Id, corpusId });
            _bus.State(runId, "architecture", "SUCCEEDED",
                $"Assessment ready in {sw.Elapsed.TotalSeconds:0}s — effort {model.Effort.Band}, risk {model.Risk.Band}, recommended {narrative.Mode} on {narrative.TargetStack}.");
        }
        catch (OperationCanceledException)
        {
            _bus.State(runId, "architecture", "FAILED", "Interrupted by an API restart — run it again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assessment failed for corpus {CorpusId}", corpusId);
            _bus.State(runId, "architecture", "FAILED", ex.Message);
        }
        finally
        {
            _bus.Complete(runId);
        }
    }

    private void Stage(Guid runId, string stage, int step, int of, string label) =>
        _bus.Publish(runId, "architecture", stage, "stage", new { stage, step, of, label }, label);

    // ── Narrative ────────────────────────────────────────────────────────

    private sealed record Narrative(string Mode, string TargetStack, string Summary, string Markdown, string Model, Guid? LlmCallId);

    private async Task<Narrative> WriteNarrativeAsync(
        AppDbContext db, object facts, EffortRiskModel.Result model, IReadOnlyList<string> candidateTargets, CancellationToken ct)
    {
        var fallbackTarget = model.Recommendation.TargetStack is { } t && candidateTargets.Contains(t, StringComparer.OrdinalIgnoreCase)
            ? t
            : candidateTargets.FirstOrDefault() ?? model.Recommendation.TargetStack ?? "dotnet8";

        var loaded = _prompts.GetLatest("common", "dotnet8", "assessment");
        if (_brain.Name != "anthropic" || loaded is null || string.IsNullOrWhiteSpace(_anthropic.ApiKey))
            return new Narrative(model.Recommendation.Mode, fallbackTarget, model.Recommendation.Summary,
                DeterministicNarrative(facts, model, fallbackTarget), "deterministic", null);

        var factsJson = JsonSerializer.Serialize(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        var rendered = _prompts.Render(loaded, new Dictionary<string, string?> { ["facts"] = factsJson });
        var body = new Dictionary<string, object?>
        {
            ["model"] = _anthropic.Model,
            ["max_tokens"] = 4096,
            ["system"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = rendered.System, ["cache_control"] = new { type = "ephemeral" } } },
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = ToolName,
                    ["description"] = "Write the assessment.",
                    ["input_schema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "mode", "targetStack", "summary", "markdown" },
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["mode"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "faithful-1to1", "replatform", "modernize", "strangler" } },
                            ["targetStack"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = candidateTargets.Count == 0 ? new[] { fallbackTarget } : candidateTargets.ToArray() },
                            ["summary"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "One sentence a sponsor could repeat." },
                            ["markdown"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "250–450 words with the four headings." },
                        },
                    },
                },
            },
            ["tool_choice"] = new Dictionary<string, object?> { ["type"] = "tool", ["name"] = ToolName },
            ["messages"] = new[] { new Dictionary<string, object?> { ["role"] = "user", ["content"] = rendered.User } },
        };

        var sw = Stopwatch.StartNew();
        var http = _httpFactory.CreateClient("anthropic-copilot");
        var response = await AnthropicHttp.SendWithRetryAsync(
            http, () => AnthropicHttp.BuildMessagesRequest(_anthropic, JsonSerializer.Serialize(body)),
            _limiter, cacheKey: $"assessment:{loaded.Version}", _logger, ct, maxAttempts: 3);
        sw.Stop();

        using var doc = JsonDocument.Parse(response.Body);
        var input = AnthropicHttp.ReadToolInput(doc.RootElement) ?? "{}";
        var usage = AnthropicHttp.ReadUsage(doc.RootElement);
        using var parsed = JsonDocument.Parse(input);
        var r = parsed.RootElement;
        string Read(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var modelUsed = usage.Model ?? _anthropic.Model;
        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = "anthropic",
            Model = modelUsed,
            PromptTemplateId = loaded.PromptId,
            PromptTemplateVersion = loaded.Version,
            ProviderConfigVersion = "assessment",
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheCreationTokens = usage.CacheCreationTokens,
            LatencyMs = sw.ElapsedMilliseconds,
            CostUsd = ModelPricing.Estimate("anthropic", modelUsed, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens),
            Status = "success",
            CalledAt = DateTimeOffset.UtcNow,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync(ct);

        var mode = Read("mode") is { Length: > 0 } m ? m : model.Recommendation.Mode;
        var target = Read("targetStack") is { Length: > 0 } ts ? ts : fallbackTarget;
        var summary = Read("summary") is { Length: > 0 } s ? s : model.Recommendation.Summary;
        var markdown = Read("markdown") is { Length: > 0 } md ? md : DeterministicNarrative(facts, model, target);
        return new Narrative(mode, target, summary, markdown, modelUsed, call.Id);
    }

    private static string DeterministicNarrative(object facts, EffortRiskModel.Result model, string target) =>
        "## What we found\n\n" +
        $"The fact sheet below was computed from the parsed estate. Effort band **{model.Effort.Band}** " +
        $"({model.Effort.PersonWeeks.Low:0}–{model.Effort.PersonWeeks.High:0} person-weeks with the platform), risk band **{model.Risk.Band}**.\n\n" +
        "## Where the risk is\n\n" + string.Join("\n", model.Risk.Drivers.Select(d => $"- {d}")) + "\n\n" +
        "## Recommendation\n\n" +
        $"**{model.Recommendation.Mode}** on **{target}** — {model.Recommendation.Summary}\n\n" +
        "## First two weeks\n\n- Survey the patterns (if not done) and specify the highest fan-in routines first.\n- Decide the target stack and the conversion mode per module.\n" +
        "\n_(Narrative written deterministically — no model configured.)_";

    private static string FactsAppendix(object facts, EffortRiskModel.Result model)
    {
        var json = JsonSerializer.Serialize(facts, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return "---\n\n### Fact sheet\n\n" +
               $"| | |\n|---|---|\n| Effort | {model.Effort.Band} ({model.Effort.PersonWeeks.Low:0}–{model.Effort.PersonWeeks.High:0} person-weeks) |\n" +
               $"| Risk | {model.Risk.Band} ({model.Risk.Score}/5) |\n\n" +
               "<details><summary>Raw facts (JSON)</summary>\n\n```json\n" + json + "\n```\n\n</details>";
    }

    // ── Facts helpers ────────────────────────────────────────────────────

    public sealed record Hotspot(Guid Id, string Name, int Callers, int TransitiveCallers, bool InCycle);

    /// <summary>Top-N routines by transitive caller count (reverse BFS over
    /// call edges), the plain "what breaks if this changes" measure.</summary>
    public static List<Hotspot> Hotspots(DependencyGraph graph, int take)
    {
        var callersOf = new Dictionary<Guid, List<Guid>>();
        foreach (var e in graph.Edges)
        {
            if (e.Type != "call") continue;
            if (!callersOf.TryGetValue(e.To, out var list)) callersOf[e.To] = list = new List<Guid>();
            list.Add(e.From);
        }
        var cyclic = graph.Sccs.Where(s => s.Members.Count > 1).SelectMany(s => s.Members).ToHashSet();
        var result = new List<Hotspot>(graph.Nodes.Count);
        foreach (var n in graph.Nodes)
        {
            var seen = new HashSet<Guid> { n.Id };
            var queue = new Queue<Guid>();
            queue.Enqueue(n.Id);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!callersOf.TryGetValue(cur, out var callers)) continue;
                foreach (var c in callers) if (seen.Add(c)) queue.Enqueue(c);
            }
            result.Add(new Hotspot(n.Id, n.Name, n.CallerCount, seen.Count - 1, cyclic.Contains(n.Id)));
        }
        return result.OrderByDescending(h => h.TransitiveCallers).ThenByDescending(h => h.Callers).Take(take).ToList();
    }

    private static IEnumerable<string> ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s) yield return s;
        }
    }

    private static IEnumerable<string> ReadTables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch { yield break; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.String
                    && t.GetString() is { Length: > 0 } s) yield return s;
        }
    }
}

/// <summary>
/// A transparent effort/risk model. Every factor is visible in the output
/// (`drivers`) so a client can argue with it; the constants are the
/// platform's own throughput assumptions for faithful conversion with
/// signed specs and the four gates, not industry averages.
/// </summary>
public static class EffortRiskModel
{
    /// <summary>Every fraction is in [0, 1] — the multipliers below assume it.</summary>
    public sealed record Inputs(
        int Routines, string? Language, double CyclicFraction, double HotspotShare, double SharedStorageFraction,
        double ComplexFraction, double FlaggedFraction, int ExternalCallees, bool UiFlagged, bool DataFlagged);

    public sealed record PersonWeeks(double Low, double High);
    public sealed record Effort(int Score, string Band, PersonWeeks PersonWeeks, IReadOnlyList<string> Drivers);
    public sealed record Risk(int Score, string Band, IReadOnlyList<string> Drivers);
    public sealed record Recommendation(string Mode, string? TargetStack, string Summary);
    public sealed record Result(Effort Effort, Risk Risk, Recommendation Recommendation);

    /// <summary>Routines per person-week for faithful conversion through
    /// the platform (spec → sign → generate → gates), by source language.</summary>
    private static readonly Dictionary<string, double> Throughput = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fortran-f77"] = 60, ["java"] = 60, ["csharp"] = 60, ["vb6"] = 50, ["vbnet"] = 55,
        ["delphi"] = 45, ["unibasic"] = 45, ["abl"] = 45, ["cobol"] = 40, ["cpp"] = 35,
    };

    private static readonly Dictionary<string, string> DefaultTarget = new(StringComparer.OrdinalIgnoreCase)
    {
        // .NET 10 is the default .NET target everywhere; dotnet8 stays
        // selectable for clients standardised on the older LTS. C# and
        // VB.NET only ever had dotnet10 prompts — "dotnet8" here pointed
        // them at a stack with nothing behind it.
        ["fortran-f77"] = "dotnet10", ["cpp"] = "dotnet10", ["delphi"] = "dotnet10", ["cobol"] = "java-spring",
        ["vb6"] = "dotnet10-blazor", ["vbnet"] = "dotnet10-csharp", ["unibasic"] = "java-spring", ["abl"] = "java-spring",
        ["java"] = "java-spring", ["csharp"] = "dotnet10-webapi",
    };

    public static Result Compute(Inputs i)
    {
        var throughput = i.Language is not null && Throughput.TryGetValue(i.Language, out var t) ? t : 40;
        var baseWeeks = i.Routines / throughput;
        var drivers = new List<string>();
        var mult = 1.0;

        void Factor(double weight, double fraction, string label)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction <= 0) return;
            var f = 1 + weight * fraction;
            mult *= f;
            drivers.Add($"{label}: ×{f:0.00}");
        }
        drivers.Add($"{i.Routines:N0} routines at ~{throughput:0}/person-week for {i.Language ?? "unknown language"} → {baseWeeks:0.#} base weeks");
        Factor(0.5, i.CyclicFraction, $"{i.CyclicFraction:P0} of routines sit in call cycles");
        Factor(0.3, i.HotspotShare, $"the biggest hotspot reaches {i.HotspotShare:P0} of the estate");
        Factor(0.2, i.SharedStorageFraction, $"{i.SharedStorageFraction:P0} of routines touch shared storage");
        Factor(0.4, i.ComplexFraction, $"{i.ComplexFraction:P0} of surveyed routines are complex");
        Factor(0.3, i.FlaggedFraction, $"{i.FlaggedFraction:P0} carry modernization flags");

        var low = baseWeeks * mult * 0.8;
        var high = baseWeeks * mult * 1.4;
        // Bands are team-sized, not one-person: a 4-person migration squad
        // delivers ~16 person-weeks a month, so 24 pw ≈ 6 weeks, 60 pw ≈ a quarter.
        var (effortScore, effortBand) = high switch
        {
            < 8 => (1, "S"),
            < 24 => (2, "M"),
            < 60 => (3, "L"),
            < 160 => (4, "XL"),
            _ => (5, "XL"),
        };

        var riskPoints = 1.0;
        var riskDrivers = new List<string>();
        if (i.CyclicFraction > 0.10) { riskPoints += 1; riskDrivers.Add($"{i.CyclicFraction:P0} of routines are in call cycles — waves cannot be cleanly ordered"); }
        if (i.HotspotShare > 0.25) { riskPoints += 1; riskDrivers.Add($"one routine is transitively called by {i.HotspotShare:P0} of the estate"); }
        if (i.SharedStorageFraction > 0.3) { riskPoints += 1; riskDrivers.Add($"{i.SharedStorageFraction:P0} of routines share storage (COMMON blocks / globals / copybooks) — hidden coupling"); }
        if (i.ComplexFraction > 0.3) { riskPoints += 1; riskDrivers.Add($"{i.ComplexFraction:P0} of routines are complex"); }
        if (i.ExternalCallees > 50) { riskPoints += 0.5; riskDrivers.Add($"{i.ExternalCallees} unresolved external callees (libraries, OS, missing source)"); }
        if (i.Language is "cobol" or "unibasic" or "abl" or "vb6") { riskPoints += 0.5; riskDrivers.Add($"{i.Language}: scarce skills and runtime semantics that need equivalence testing"); }
        if (riskDrivers.Count == 0) riskDrivers.Add("no structural risk signal above threshold");
        var riskScore = Math.Clamp((int)Math.Round(riskPoints, MidpointRounding.AwayFromZero), 1, 5);
        var riskBand = riskScore switch { 1 => "S", 2 => "M", 3 => "L", _ => "XL" };

        // Structural signals first (they change the delivery shape), then the
        // modernization signal, then the clean default, then replatform.
        string mode;
        string summary;
        if (i.CyclicFraction > 0.10 && i.Routines > 800)
        {
            mode = "strangler";
            summary = "Cycles and size make a big-bang cutover risky: coexist behind façades and migrate wave by wave.";
        }
        else if (i.UiFlagged || i.FlaggedFraction >= 0.25)
        {
            mode = "modernize";
            summary = "The survey shows real modernization candidates (UI/API coupling, legacy constructs): re-architect where the evidence supports it, 1:1 elsewhere.";
        }
        else if (i.FlaggedFraction < 0.10 && riskScore <= 2)
        {
            mode = "faithful-1to1";
            summary = "The structure is sound and modernization flags are rare: convert like-for-like, keep names and units, and let the signed specs and gates carry the proof.";
        }
        else
        {
            mode = "replatform";
            summary = "The code is workable but the platform underneath is the problem: 1:1 conversion with a data/infrastructure lift and a thin architecture pass.";
        }

        var target = i.Language is not null && DefaultTarget.TryGetValue(i.Language, out var dt) ? dt : null;
        return new Result(
            new Effort(effortScore, effortBand, new PersonWeeks(low, high), drivers),
            new Risk(riskScore, riskBand, riskDrivers),
            new Recommendation(mode, target, summary));
    }
}
