using System.Collections.Concurrent;
using System.Text.Json;
using Astra.Api.Conversations;
using Astra.Api.Llm.Schemas;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Runs;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Copilot;

/// <summary>
/// Agents narrate. The Narrator follows a tracked run on the
/// <see cref="RunEventBus"/> and posts into the run's thread: a short note at
/// stage boundaries and a real summary (with artifact card + next-step
/// chips) when the run ends. Deterministic templates — the facts come from
/// the run row / the bus, never from a model — so what it says is exactly
/// what happened.
/// </summary>
public sealed class Narrator
{
    private readonly RunEventBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Narrator> _logger;
    private readonly ConcurrentDictionary<Guid, Tracked> _tracked = new();

    public sealed record Tracked(Guid RunId, Guid ConversationId, string Agent, string Kind, string Label, Guid? CorpusId, string? Persona, string? DisplayName);

    public Narrator(RunEventBus bus, IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<Narrator> logger)
    {
        _bus = bus;
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>Start following a run. Idempotent per run id.</summary>
    public void Track(Guid runId, Guid conversationId, string agent, string kind, string label, Guid? corpusId = null, string? persona = null, string? displayName = null)
    {
        var t = new Tracked(runId, conversationId, agent, kind, label, corpusId, persona, displayName);
        if (!_tracked.TryAdd(runId, t)) return;
        _ = Task.Run(() => FollowAsync(t));
    }

    /// <summary>Runs currently being followed, grouped by agent — the rail's
    /// "who is busy" signal.</summary>
    public IReadOnlyDictionary<string, List<Tracked>> ActiveByAgent() =>
        _tracked.Values.GroupBy(t => t.Agent).ToDictionary(g => g.Key, g => g.ToList());

    private async Task FollowAsync(Tracked t)
    {
        var ct = _lifetime.ApplicationStopping;
        string? lastStage = null;
        var progressMilestone = 0;
        try
        {
            await foreach (var evt in _bus.SubscribeAsync(t.RunId, 0, ct))
            {
                try
                {
                    switch (evt.Type)
                    {
                        case "stage" when t.Kind == "pattern-analysis":
                            if (evt.Stage is { Length: > 0 } && evt.Stage != lastStage)
                            {
                                if (lastStage is not null)
                                    await PostAsync(t, $"{StageLabel(lastStage)} done — {StageLabel(evt.Stage).ToLowerInvariant()} next.", null, null, ct);
                                lastStage = evt.Stage;
                            }
                            break;
                        case "progress" when t.Kind == "pattern-analysis":
                            var data = ToElement(evt.Data);
                            if (data.TryGetProperty("total", out var totalEl) && data.TryGetProperty("done", out var doneEl)
                                && totalEl.ValueKind == JsonValueKind.Number && doneEl.ValueKind == JsonValueKind.Number)
                            {
                                var total = totalEl.GetInt32();
                                var done = doneEl.GetInt32();
                                if (total > 0 && done * 2 >= total && progressMilestone < 1)
                                {
                                    progressMilestone = 1;
                                    var propagated = data.TryGetProperty("propagated", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                                    // `propagated` is the run-wide count of structural
                                    // duplicates decided up front, not a share of `done`
                                    // — dividing the two printed "137%" on oatpp.
                                    var dup = propagated > 0 ? $" — {propagated:N0} more are structural duplicates I won't need to read" : "";
                                    await PostAsync(t, $"Halfway: {done:N0} / {total:N0} routines surveyed{dup}.", null, null, ct);
                                }
                            }
                            break;
                        case "state":
                            var st = ToElement(evt.Data);
                            var state = st.TryGetProperty("state", out var s) ? s.GetString() : null;
                            if (state is "SUCCEEDED" or "PARTIAL" or "FAILED" or "CANCELLED" or "RESUMABLE")
                            {
                                await OnTerminalAsync(t, state, st.TryGetProperty("summary", out var sm) ? sm.GetString() : evt.Message, ct);
                                _tracked.TryRemove(t.RunId, out _);
                                return;
                            }
                            break;
                        case "done":
                            _tracked.TryRemove(t.RunId, out _);
                            return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Narrator could not post for run {RunId}", t.RunId);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Narrator stopped following run {RunId}", t.RunId);
        }
        finally
        {
            _tracked.TryRemove(t.RunId, out _);
        }
    }

    private async Task OnTerminalAsync(Tracked t, string state, string? summary, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobClient>();
        var schemas = scope.ServiceProvider.GetRequiredService<SpecSchemaProvider>();
        var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();

        var artifacts = new List<ArtifactDto>();
        var suggestions = new List<SuggestionDto>();
        string markdown;

        switch (t.Kind)
        {
            case "pattern-analysis":
            {
                var run = await db.PatternAnalysisRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == t.RunId, ct);
                var multi = 0;
                var singles = 0;
                if (run is not null)
                {
                    var sizes = await db.PatternClusters.AsNoTracking()
                        .Where(c => c.PatternAnalysisRunId == run.Id).Select(c => c.MemberCount).ToListAsync(ct);
                    multi = sizes.Count(s => s > 1);
                    singles = sizes.Count(s => s <= 1);
                }
                markdown = PatternAnalysisSummary(run, state, summary, multi, singles);
                if (state is "SUCCEEDED" or "PARTIAL" && t.CorpusId is { } cid)
                {
                    var grid = await ArtifactBuilders.ClusterGridAsync(db, cid, ct);
                    if (grid is { } g) artifacts.Add(g.Artifact);
                    suggestions.Add(new("Show the clusters", "Show me the pattern clusters"));
                    suggestions.Add(new("Biggest pattern first", "Which pattern has the most routines, and what would one archetype for it look like?"));
                    suggestions.Add(new("What's trivial?", "How many routines are trivial accessors, and can we skip them?"));
                }
                else if (state == "RESUMABLE")
                {
                    suggestions.Add(new("Resume the survey", "Resume the pattern analysis"));
                }
                else if (state == "FAILED")
                {
                    suggestions.Add(new("Run it again", "Run the pattern analysis again"));
                }
                break;
            }
            case "extraction":
            {
                var item = await LatestItemAsync(t.RunId, "spec", ct);
                Guid? specId = item.TryGetProperty("specId", out var sid) && Guid.TryParse(sid.GetString(), out var g) ? g : null;
                if (state == "SUCCEEDED" && specId is { } id)
                {
                    var spec = await db.Specs.AsNoTracking().Include(s => s.Subroutine).FirstOrDefaultAsync(s => s.Id == id, ct);
                    if (spec is not null)
                    {
                        var (artifact, _, claims) = await ArtifactBuilders.SpecSummaryAsync(db, schemas, spec, ct);
                        artifacts.Add(artifact);
                        int c(string s) => claims.Count(x => x.Section == s);
                        markdown = $"Spec drafted for `{spec.Subroutine?.Name ?? t.Label}` — {claims.Count} claims " +
                                   $"({c("invariants")} invariants, {c("side_effects")} side effects, {c("edge_cases")} edge cases, {c("open_questions")} open questions). " +
                                   "It's a draft until an SME signs it: route it for review when you're happy with the claims.";
                        suggestions.Add(new("Route for review", $"Route the spec for {spec.Subroutine?.Name} for review"));
                        suggestions.Add(new("Explain the claims", $"Explain the claims in the spec for {spec.Subroutine?.Name} in plain language"));
                        suggestions.Add(new("Show the source", $"Show me the source of {spec.Subroutine?.Name}"));
                    }
                    else markdown = summary ?? "Spec drafted.";
                }
                else
                {
                    markdown = $"Extraction for `{t.Label}` {StateWord(state)}: {summary}";
                    suggestions.Add(new("Try again", $"Extract the spec for {t.Label} again"));
                }
                break;
            }
            case "scaffold":
            {
                var item = await LatestItemAsync(t.RunId, "scaffold", ct);
                Guid? scaffoldId = item.TryGetProperty("scaffoldId", out var sid) && Guid.TryParse(sid.GetString(), out var g) ? g : null;
                if (state == "SUCCEEDED" && scaffoldId is { } id)
                {
                    var tree = await ArtifactBuilders.ScaffoldTreeAsync(db, blob, id, ct);
                    if (tree is { } tr) artifacts.Add(tr.Artifact);
                    markdown = summary ?? "Code generated.";
                    markdown += " Next: run the compile gate to prove it builds.";
                    suggestions.Add(new("Run the compile gate", $"Run the compile gate for {t.Label}"));
                    suggestions.Add(new("Open the code", $"Show me the generated code for {t.Label}"));
                }
                else
                {
                    markdown = $"Code generation for `{t.Label}` {StateWord(state)}: {summary}";
                    suggestions.Add(new("Try again", $"Generate the code for {t.Label} again"));
                }
                break;
            }
            case "gate":
            {
                var item = await LatestItemAsync(t.RunId, "gate", ct);
                Guid? scaffoldId = item.TryGetProperty("scaffoldId", out var sid) && Guid.TryParse(sid.GetString(), out var g) ? g : null;
                if (scaffoldId is { } id)
                {
                    var gates = await ArtifactBuilders.GateResultsAsync(db, id, ct);
                    if (gates is { } gr) artifacts.Add(gr.Artifact);
                }
                markdown = summary ?? $"Gate {StateWord(state)}.";
                if (state == "SUCCEEDED")
                {
                    suggestions.Add(new("Run the test pack", $"Run the test-pack gate for {t.Label}"));
                    suggestions.Add(new("What's left before commit?", $"What still has to pass before {t.Label} can be committed?"));
                }
                else
                {
                    suggestions.Add(new("Explain the failure", $"Explain why the gate failed for {t.Label} and what would fix it"));
                    suggestions.Add(new("Regenerate and retry", $"Generate the code for {t.Label} again and re-run the compile gate"));
                }
                break;
            }
            case "docs":
                markdown = $"Documentation run {StateWord(state)}: {summary}";
                suggestions.Add(new("Open the docs", "Show me the generated documentation"));
                break;
            case "assessment":
            {
                var item = await LatestItemAsync(t.RunId, "assessment", ct);
                Guid? sectionId = item.TryGetProperty("sectionId", out var sid) && Guid.TryParse(sid.GetString(), out var g) ? g : null;
                var section = sectionId is { } id ? await db.DocSections.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) : null;
                var card = section is null ? null : Astra.Api.Assessment.AssessmentService.CardOf(section);
                if (state == "SUCCEEDED" && section is not null && card is { } c)
                {
                    artifacts.Add(new ArtifactDto("assessment", section.CorpusId.ToString(), c.Clone()));
                    string Str(string a, string b) => c.TryGetProperty(a, out var x) && x.TryGetProperty(b, out var y) && y.ValueKind == JsonValueKind.String ? y.GetString() ?? "" : "";
                    var mode = Str("recommendation", "mode");
                    var target = Str("recommendation", "targetStack");
                    var effortBand = Str("effort", "band");
                    var riskBand = Str("risk", "band");
                    markdown = $"Assessment ready for **{t.Label}**: effort **{effortBand}**, risk **{riskBand}** — I recommend **{mode}** on **{target}**. " +
                               Str("recommendation", "summary") + " The full write-up is in the card; open it for the fact sheet.";
                    suggestions.Add(new("Why that mode?", $"Why do you recommend {mode} for {t.Label}, and what would change the answer?"));
                    suggestions.Add(new("Riskiest routines", $"Show me the riskiest routines in {t.Label}"));
                    suggestions.Add(new("Draft the waves", $"Draft a migration plan for {t.Label}"));
                }
                else
                {
                    markdown = $"Assessment for `{t.Label}` {StateWord(state)}: {summary}";
                    suggestions.Add(new("Run it again", $"Run the assessment for {t.Label}"));
                }
                break;
            }
            default:
                markdown = $"{t.Label} {StateWord(state)}: {summary}";
                break;
        }

        await PostAsync(t, markdown, artifacts, suggestions, ct, conversations);
    }

    private static string StageLabel(string stage) => stage switch
    {
        "survey" => "Survey",
        "extract" => "Bulk extraction",
        "cluster" => "Clustering",
        _ => stage,
    };

    private static string StateWord(string state) => state switch
    {
        "SUCCEEDED" => "finished",
        "PARTIAL" => "finished with some failures",
        "FAILED" => "failed",
        "CANCELLED" => "was cancelled",
        "RESUMABLE" => "was paused",
        _ => state.ToLowerInvariant(),
    };

    private static string PatternAnalysisSummary(PatternAnalysisRun? run, string state, string? summary, int multiMember = 0, int singletons = 0)
    {
        if (run is null) return $"Pattern analysis {StateWord(state)}. {summary}";
        var mins = run.CompletedAt is { } c ? (c - run.StartedAt).TotalMinutes : (DateTimeOffset.UtcNow - run.StartedAt).TotalMinutes;
        var took = mins < 1 ? "under a minute" : $"{mins:0.#} min";

        int surveyed = 0, propagated = 0, trivial = 0, clusters = 0, total = 0, fromSpec = 0;
        decimal cost = 0;
        try
        {
            if (run.MetricsJson is not null)
            {
                using var doc = JsonDocument.Parse(run.MetricsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("survey", out var s))
                {
                    surveyed = Int(s, "surveyed");
                    propagated = Int(s, "propagated");
                    trivial = Int(s, "trivial");
                    total = Int(s, "total");
                    fromSpec = Int(s, "fromSpec");
                    if (s.TryGetProperty("costUsd", out var cu) && cu.ValueKind == JsonValueKind.Number) cost = cu.GetDecimal();
                }
                if (root.TryGetProperty("cluster", out var cl)) clusters = Int(cl, "clusterCount");
                if (clusters == 0) clusters = Int(root, "clusterCount");
            }
        }
        catch { /* metrics are informal */ }

        if (state is "SUCCEEDED" or "PARTIAL")
        {
            var parts = new List<string> { $"Pattern analysis {StateWord(state)} in {took}." };
            if (total > 0)
            {
                var read = surveyed + fromSpec;
                parts.Add($"{total:N0} routines: I read {read:N0}, propagated {propagated:N0} from structural duplicates and skipped {trivial:N0} trivial accessors" +
                          (cost > 0 ? $" (≈ ${cost:0.00})." : "."));
            }
            if (multiMember > 0 || singletons > 0)
                parts.Add($"**{multiMember:N0} shared patterns** (2+ routines each — the archetype candidates) and {singletons:N0} one-off routines to handle case by case.");
            else if (clusters > 0) parts.Add($"**{clusters:N0} distinct patterns** — each is a candidate archetype for the migration.");
            else if (!string.IsNullOrWhiteSpace(summary)) parts.Add(summary);
            return string.Join(" ", parts);
        }
        return $"Pattern analysis {StateWord(state)} after {took}. {summary}";
    }

    /// <summary>Case-insensitive numeric read — the run's MetricsJson is
    /// written from C# records whose casing depends on the serializer.</summary>
    private static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                return p.Value.TryGetInt32(out var i) ? i : (int)p.Value.GetDouble();
        return 0;
    }

    private async Task<JsonElement> LatestItemAsync(Guid runId, string kind, CancellationToken ct)
    {
        // The `item` event is in the ring buffer; replay it.
        JsonElement found = default;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var evt in _bus.SubscribeAsync(runId, 0, cts.Token))
            {
                if (evt.Type == "item")
                {
                    var el = ToElement(evt.Data);
                    if (!el.TryGetProperty("kind", out var k) || k.GetString() == kind) found = el;
                }
                if (evt.Type == "done") break;
            }
        }
        catch (OperationCanceledException) { }
        return found.ValueKind == JsonValueKind.Undefined ? JsonDocument.Parse("{}").RootElement : found;
    }

    private async Task PostAsync(Tracked t, string markdown, List<ArtifactDto>? artifacts, List<SuggestionDto>? suggestions,
        CancellationToken ct, ConversationService? conversations = null)
    {
        IServiceScope? scope = null;
        try
        {
            if (conversations is null)
            {
                scope = _scopeFactory.CreateScope();
                conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
            }
            await conversations.AppendAsync(new ConversationMessage
            {
                ConversationId = t.ConversationId,
                Role = "agent",
                Agent = t.Agent,
                Persona = t.Persona,
                AuthorDisplay = AgentName(t.Agent),
                Markdown = markdown,
                ArtifactsJson = ConversationJson.Serialize(artifacts ?? new()),
                SuggestionsJson = ConversationJson.Serialize(suggestions ?? new()),
                RunId = t.RunId,
            }, ct);
        }
        finally
        {
            scope?.Dispose();
        }
    }

    public static string AgentName(string agent) => agent switch
    {
        "orchestrator" => "Astra",
        _ => char.ToUpperInvariant(agent[0]) + agent[1..],
    };

    private static JsonElement ToElement(object? data) =>
        data is JsonElement je ? je : JsonSerializer.SerializeToElement(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
