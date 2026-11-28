using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Llm;
using Microsoft.Extensions.Options;

namespace Astra.Api.Copilot;

/// <summary>One content block of a model turn.</summary>
public sealed record BrainBlock(string Type, string? Text, string? ToolUseId, string? ToolName, JsonElement? Input);

/// <summary>A model turn: parsed blocks plus the raw assistant `content`
/// array so it can be appended to the transcript verbatim.</summary>
public sealed record BrainResponse(
    IReadOnlyList<BrainBlock> Blocks,
    string StopReason,
    JsonElement RawAssistantContent,
    AnthropicUsage? Usage,
    string Model,
    long LatencyMs);

public sealed record BrainRequest(string System, IReadOnlyList<object> Messages, IReadOnlyList<object> Tools);

/// <summary>The model behind the orchestrator loop. Anthropic in production;
/// a deterministic keyword router when no key is configured so the
/// Workspace works offline and in e2e.</summary>
public interface ICopilotBrain
{
    string Name { get; }
    string Model { get; }
    Task<BrainResponse> CompleteAsync(BrainRequest request, CancellationToken ct);
}

// ─── Anthropic ───────────────────────────────────────────────────────────

public sealed class AnthropicCopilotBrain : ICopilotBrain
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _anthropic;
    private readonly CopilotOptions _opts;
    private readonly AnthropicRateLimiter _limiter;
    private readonly ILogger<AnthropicCopilotBrain> _logger;

    public AnthropicCopilotBrain(
        IHttpClientFactory httpFactory, IOptions<AnthropicOptions> anthropic, IOptions<CopilotOptions> opts,
        AnthropicRateLimiter limiter, ILogger<AnthropicCopilotBrain> logger)
    {
        _httpFactory = httpFactory;
        _anthropic = anthropic.Value;
        _opts = opts.Value;
        _limiter = limiter;
        _logger = logger;
    }

    public string Name => "anthropic";
    public string Model => string.IsNullOrWhiteSpace(_opts.Model) ? _anthropic.Model : _opts.Model!;

    public async Task<BrainResponse> CompleteAsync(BrainRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["max_tokens"] = _opts.MaxOutputTokens,
            ["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = request.System,
                    ["cache_control"] = new { type = "ephemeral" },
                },
            },
            ["tools"] = request.Tools,
            ["messages"] = request.Messages,
        };
        var bodyJson = JsonSerializer.Serialize(body, Json);

        var sw = Stopwatch.StartNew();
        var http = _httpFactory.CreateClient("anthropic-copilot");
        var response = await AnthropicHttp.SendWithRetryAsync(
            http, () => AnthropicHttp.BuildMessagesRequest(_anthropic, bodyJson),
            _limiter, cacheKey: "copilot", _logger, ct, maxAttempts: 3);
        sw.Stop();

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        var blocks = new List<BrainBlock>();
        var content = root.TryGetProperty("content", out var c) ? c.Clone() : JsonDocument.Parse("[]").RootElement;
        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "text":
                    blocks.Add(new BrainBlock("text", block.GetProperty("text").GetString(), null, null, null));
                    break;
                case "tool_use":
                    blocks.Add(new BrainBlock("tool_use", null,
                        block.GetProperty("id").GetString(),
                        block.GetProperty("name").GetString(),
                        block.TryGetProperty("input", out var input) ? input.Clone() : JsonDocument.Parse("{}").RootElement));
                    break;
            }
        }
        var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "end_turn" : "end_turn";
        var usage = AnthropicHttp.ReadUsage(root);
        return new BrainResponse(blocks, stop, content, usage, usage.Model ?? Model, sw.ElapsedMilliseconds);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

// ─── Mock (offline / e2e) ────────────────────────────────────────────────

/// <summary>
/// Keyword router. First call on a user turn → one tool_use picked from the
/// text; the call after the tool_result → a short text answer plus a
/// <c>finish_turn</c> with chips. Enough for the Workspace, the golden demo
/// and Playwright to run without a key.
/// </summary>
public sealed class MockCopilotBrain : ICopilotBrain
{
    public string Name => "mock";
    public string Model => "mock-router";

    public Task<BrainResponse> CompleteAsync(BrainRequest request, CancellationToken ct)
    {
        var last = request.Messages.LastOrDefault();
        var lastJson = JsonSerializer.SerializeToElement(last, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var lastRole = lastJson.TryGetProperty("role", out var r) ? r.GetString() : null;
        var lastContent = lastJson.TryGetProperty("content", out var c) ? c : default;

        // After tool results → answer.
        if (lastRole == "user" && lastContent.ValueKind == JsonValueKind.Array
            && lastContent.EnumerateArray().Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_result"))
        {
            var results = lastContent.EnumerateArray()
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_result")
                .Select(b => b.TryGetProperty("content", out var cc) ? cc.ValueKind == JsonValueKind.String ? cc.GetString() ?? "" : cc.GetRawText() : "")
                .ToList();

            // The orchestrator held back a tool-less answer (grounding check). Behave like
            // a model: if a tool would answer the question, call it; else send the same reply.
            if (results.Any(x => x.Contains("grounding.no_tool")))
            {
                var original = LastUserText(request.Messages);
                if (RouteOrNull(original) is { } routed)
                {
                    var retry = new List<BrainBlock>
                    {
                        new("tool_use", null, "toolu_mock_grounded", routed.Tool, JsonSerializer.SerializeToElement(routed.Input)),
                    };
                    return Task.FromResult(new BrainResponse(retry, "tool_use", Raw(retry), null, Model, 0));
                }
                return Task.FromResult(NoToolFinish(NoToolReply(original) ?? "OK."));
            }
            var failed = results.Any(x => x.Contains("\"error\""));
            var text = failed
                ? "That didn't go through — the details are in the source chip below. Tell me how you'd like to proceed."
                : "Done — the details are on the card below.";
            var blocks = new List<BrainBlock>
            {
                new("text", text, null, null, null),
                new("tool_use", null, "toolu_mock_finish", "finish_turn", JsonSerializer.SerializeToElement(new
                {
                    markdown = text,
                    suggestions = new object[]
                    {
                        new { label = "What's the status?", intent = "What's the status of this programme?" },
                        new { label = "Search code units", intent = "Find code units named " },
                    },
                })),
            };
            return Task.FromResult(new BrainResponse(blocks, "tool_use", Raw(blocks), null, Model, 0));
        }

        var userText = lastContent.ValueKind == JsonValueKind.String ? lastContent.GetString() ?? "" : lastContent.GetRawText();
        // Scripted tool-less answers: "from memory" reproduces the real failure (a status
        // answered from the thread); thanks/ok are turns that genuinely need no tool.
        if (NoToolReply(userText) is { } noTool) return Task.FromResult(NoToolFinish(noTool));
        var (tool, input) = Route(userText);
        var call = new List<BrainBlock>
        {
            new("text", $"On it — {Verb(tool)}.", null, null, null),
            new("tool_use", null, "toolu_mock_1", tool, JsonSerializer.SerializeToElement(input)),
        };
        return Task.FromResult(new BrainResponse(call, "tool_use", Raw(call), null, Model, 0));
    }

    private static string? NoToolReply(string userText)
    {
        if (userText.Contains("from memory", StringComparison.OrdinalIgnoreCase))
            return "From what I remember, nothing has moved since last time.";
        if (Regex.IsMatch(userText.Trim(), @"^(thanks|thank you|ok|okay)[.! ]*$", RegexOptions.IgnoreCase))
            return "You're welcome.";
        return null;
    }

    private static BrainResponse NoToolFinish(string text)
    {
        var blocks = new List<BrainBlock>
        {
            new("tool_use", null, "toolu_mock_finish_notool", "finish_turn", JsonSerializer.SerializeToElement(new
            {
                markdown = text,
                suggestions = new object[] { new { label = "What's the status?", intent = "What's the status of this programme?" } },
            })),
        };
        return new BrainResponse(blocks, "tool_use", Raw(blocks), null, "mock-router", 0);
    }

    private static string LastUserText(IEnumerable<object> messages)
    {
        foreach (var m in messages.Reverse())
        {
            var j = JsonSerializer.SerializeToElement(m, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (j.TryGetProperty("role", out var r) && r.GetString() == "user"
                && j.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var text = c.GetString() ?? "";
                if (!text.StartsWith("[system]", StringComparison.Ordinal)) return text;
            }
        }
        return "";
    }

    private static string Verb(string tool) => tool switch
    {
        "get_programme_status" => "checking the programme status",
        "get_flow_board" => "reading the flow board",
        "search_routines" => "searching the code units",
        "survey_corpus" => "starting the pattern survey",
        "get_pattern_clusters" => "loading the pattern clusters",
        "extract_spec" => "drafting the spec",
        "sign_spec" => "preparing the sign-off",
        "route_for_review" => "routing the spec",
        "assign_review" => "assigning the review",
        "generate_scaffold" => "generating the code",
        "run_gate" => "running the gate",
        "sign_all_in_review" => "preparing the bulk sign-off",
        "get_quality_report" => "putting the quality report together",
        "list_programmes" => "listing the programmes",
        "read_routine" => "reading the code unit",
        "get_spec" => "loading the spec",
        "rank_routines" => "ranking the code units",
        "run_assessment" => "starting the assessment",
        "assemble_module" => "assembling the application",
        "get_assembly" => "reading the assembled application",
        "explain_claim" => "pulling up the claim and its source",
        "search_docs" => "searching the documentation",
        "list_modules" => "listing the modules",
        "run_wave" => "queueing the wave",
        "extract_many" => "queueing the spec drafts",
        "generate_many" => "queueing the code generation",
        "run_gates_many" => "queueing the gates",
        "get_work_run" => "reading the run's progress",
        "get_review_queue" => "reading the review queue",
        "control_work_run" => "updating the run",
        "set_target_stack" => "setting the migration target",
        _ => "working on it",
    };

    private static (string Tool, object Input) Route(string text) =>
        RouteOrNull(text) ?? ("get_programme_status", new { });

    /// <summary>The keyword rule that matches, or null when only the default would apply.</summary>
    internal static (string Tool, object Input)? RouteOrNull(string text)
    {
        var t = text.ToLowerInvariant();
        var quotedMatch = Regex.Match(text, "[`\"']([^`\"']+)[`\"']").Groups[1].Value;
        string? quoted = quotedMatch.Length > 0 ? quotedMatch : null;
        var dottedMatch = Regex.Match(text, @"\b([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\b").Groups[1].Value;
        string? name = quoted ?? (dottedMatch.Length > 0 ? dottedMatch : null);

        // A bulk sign-off names the review queue too, so it must win over reading the queue.
        if (Regex.IsMatch(t, @"\b(sign|accept and sign)\b.*\b(all|every|everything|them all)\b|\bbulk sign")
            && !Regex.IsMatch(t, @"\bclaims? in the spec for\b"))
            return ("sign_all_in_review", new { });
        if (RouteBatch(t, text) is { } batch) return batch;
        if (RouteTarget(t) is { } target) return target;
        if (Regex.IsMatch(t, @"\bassembl(e|ed|ing|y)\b|\bone (application|app|service)\b|\bmerge (these |the )?(code )?units\b"))
        {
            if (Regex.IsMatch(t, @"\b(how did|show|status|what'?s in|did it)\b") && !Regex.IsMatch(t, @"\b(assemble the|build one|merge)\b"))
                return ("get_assembly", new { });
            var waveNo = Regex.Match(t, @"\bwave\s+(\d+)\b").Groups[1].Value;
            var path = quoted is { } q && q.Contains('/') ? q
                : Regex.Match(t, @"\b(?:the|for)\s+([a-z0-9_.-]+)\s+module\b").Groups[1].Value is { Length: > 0 } mod ? mod
                : Regex.Match(t, @"\bmodule\s+([a-z0-9_./-]+)").Groups[1].Value;
            return ("assemble_module", new
            {
                pathPrefix = path.Length > 0 ? path : null,
                wave = waveNo.Length > 0 ? int.Parse(waveNo) : (int?)null,
                requireTests = Regex.IsMatch(t, @"\btests? pass|\bpassing tests\b|\bgreen\b"),
            });
        }
        // "Quality report for X" reads the stored proof; it must win over the quality gate (an action) below.
        if (Regex.IsMatch(t, @"\bquality (report|certificate|evidence|proof)\b|\bproof of (the )?(quality|checks)\b|\bwhat checks (were|have been) (run|done)\b"))
            return ("get_quality_report", new { subroutineId = name ?? "", scope = Regex.IsMatch(t, @"\b(programme|project|portfolio|every routine|all routines)\b") && name is null ? "programme" : "routine" });
        if (Regex.IsMatch(t, @"\b(programmes|projects|corpora|every programme|all programmes)\b")) return ("list_programmes", new { });
        if (Regex.IsMatch(t, @"\bassess(ment)?\b")) return ("run_assessment", new { });
        if (Regex.IsMatch(t, @"\bmigration plan\b|\bwaves?\b"))
            return Regex.IsMatch(t, @"\b(plan the|draft|generate|create|make)\b")
                ? ("generate_migration_plan", new { })
                : ("get_migration_plan", new { });
        if (Regex.IsMatch(t, @"\bflow board\b|\bthe board\b|\bpipeline\b|\bstuck\b|\bwhere are we\b|\bhow far along\b|\bnot (yet )?verified\b"))
            return ("get_flow_board", new { });
        // "Assign `X` to Priya" / "unassign `X`" — before "accept", which a reviewer's name could contain.
        if (Regex.IsMatch(t, @"\b(un)?assign\b"))
        {
            var reviewer = t.Contains("unassign") ? "" : Regex.Match(text, @"\bto\s+@?([A-Za-z][\w.'-]*(?:\s+[A-Z][\w.'-]*)?)").Groups[1].Value;
            return ("assign_review", new { subroutineId = name ?? "", reviewer });
        }
        if (Regex.IsMatch(t, @"\baccept\b")) return ("review_all_claims", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\b(riskiest|most (called|depended)|biggest|largest|hotspots?)\b"))
            return ("rank_routines", new { by = t.Contains("called") || t.Contains("depended") ? "fan_in" : t.Contains("biggest") || t.Contains("largest") ? "size" : "risk", limit = 10 });
        if (Regex.IsMatch(t, @"\bexplain (claim )?([A-Z]{1,3}-\d+)\b|\bwhy\b.*\b([A-Z]{1,3}-\d+)\b", RegexOptions.IgnoreCase))
        {
            var cm = Regex.Match(text, @"\b([A-Za-z]{1,3}-\d+)\b");
            return ("explain_claim", new { claimId = cm.Success ? cm.Groups[1].Value.ToUpperInvariant() : "", subroutineId = name ?? "" });
        }
        if (Regex.IsMatch(t, @"\b(docs|documentation|documented)\b"))
            return ("search_docs", new { query = quoted ?? Regex.Match(text, @"(?:about|mention|for)\s+([A-Za-z0-9_.]+)").Groups[1].Value });
        if (Regex.IsMatch(t, @"\bmodules?\b|\bwhat'?s in\b")) return ("list_modules", new { });
        if (Regex.IsMatch(t, @"\bsurvey\b|\bpattern analysis\b|\banaly[sz]e the patterns\b|\brun the pattern")) return ("survey_corpus", new { });
        if (Regex.IsMatch(t, @"\bclusters?\b|\bpatterns\b")) return ("get_pattern_clusters", new { });
        if (Regex.IsMatch(t, @"\bsign\b")) return ("sign_spec", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\broute\b")) return ("route_for_review", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\bextract\b|\bdraft (a|the) spec\b|\bspecify\b")) return ("extract_spec", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\b(re)?generate\b|\bscaffold\b|\bbuild\b|\bconvert\b|\bport\b"))
        {
            var fix = Regex.IsMatch(t, @"\bfix");
            // "Convert `X` 1:1 to .NET 10" — the faithful mode is a target stack.
            return Regex.IsMatch(t, @"\b1:1\b|\bone[- ]to[- ]one\b|\bfaithful\b|\blike[- ]for[- ]like\b|\bas[- ]is\b")
                ? ("generate_scaffold", new { subroutineId = name ?? "", repairFromLatestFailure = fix, targetStack = FaithfulConversion.Stack })
                : ("generate_scaffold", new { subroutineId = name ?? "", repairFromLatestFailure = fix });
        }
        // Read-only questions about a spec's generated code (the dock on scaffold /
        // validation / audit pages). Plural "gates" and "validation" are questions;
        // the singular "gate" below is still the action.
        if (Regex.IsMatch(t, @"\bgates\b|\bvalidation\b")) return ("get_validation_results", new { });
        if (Regex.IsMatch(t, @"\bwho signed\b|\bsigned by\b|\bthis spec\b|\bwhat spec\b|\breview decisions\b"))
            return ("get_spec", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\buntil (it'?s |they'?re |all )?green\b|\bfix loop\b|\bauto[- ]?fix\b|\bmake (it|them) green\b"))
            return ("fix_until_green", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\bquality\b|\bsecurity (review|check|checks|scan)\b|\bcoding standards?\b|\bstatic analysis\b"))
            return ("run_gate", new { subroutineId = name ?? "", gate = "quality" });
        if (Regex.IsMatch(t, @"\bequivalen(ce|t)\b|\bbehaves the same\b|\bsame behaviou?r\b"))
            return ("run_gate", new { subroutineId = name ?? "", gate = "equivalence" });
        if (Regex.IsMatch(t, @"\bcompile\b|\bgate\b|\btest[- ]pack\b")) return ("run_gate", new { subroutineId = name ?? "", gate = t.Contains("test") ? "test-pack" : "compile" });
        if (Regex.IsMatch(t, @"\bexplain\b|\bclaims\b|\bspec for\b|\bthe spec\b")) return ("get_spec", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\bsource\b|\bread\b|\bshow me\b") && name is not null) return ("read_routine", new { subroutineId = name });
        if (Regex.IsMatch(t, @"\b(find|search|which routines|routines named|which (?:code )?units|(?:code )?units named|look for|riskiest)\b"))
        {
            var query = quoted ?? Regex.Match(text, @"(?:named|called|for|matching)\s+([A-Za-z0-9_.]+)").Groups[1].Value;
            return ("search_routines", new { query = string.IsNullOrWhiteSpace(query) ? "" : query, limit = 20 });
        }
        return null;
    }

    /// <summary>"Migrate this project to Java Spring", "target .NET 10", "use the default target": the project's target.</summary>
    internal static (string Tool, object Input)? RouteTarget(string t)
    {
        var aboutProject = Regex.IsMatch(t, @"\b(migrate|move|switch)\s+(this|the)\s+(project|programme|program)\s+to\b")
            || Regex.IsMatch(t, @"\b(target stack|migration target|default target)\b")
            || Regex.IsMatch(t, @"^\s*(please\s+)?target\s+(java|spring|\.net|dotnet|net\s*\d|react|python|angular|the default)\b");
        if (!aboutProject) return null;
        string stack =
            Regex.IsMatch(t, @"\bangular\b") ? (Regex.IsMatch(t, @"\.net|dotnet") ? "angular-dotnet8" : "angular-java")
            : Regex.IsMatch(t, @"\breact\b|\bpython\b") ? "react-python"
            : Regex.IsMatch(t, @"\bjava\b|\bspring\b") ? "java-spring"
            : Regex.IsMatch(t, @"(\.net|dotnet|net)\s*8\b") ? "dotnet8"
            : Regex.IsMatch(t, @"\.net|dotnet") ? "dotnet10"
            : "default";
        return ("set_target_stack", new { targetStack = stack });
    }

    /// <summary>Sentences about many routines at once: waves, batches, the review queue.</summary>
    private static (string Tool, object Input)? RouteBatch(string t, string text)
    {
        var budget = Regex.Match(t, @"(?:budget|ceiling)\D{0,12}?(\d+(?:\.\d+)?)").Groups[1].Value;
        var money = budget.Length > 0 ? budget : null;
        var parallel = Regex.Match(t, @"\b(\d+)\s+(?:at a time|at once|in parallel|agents)\b").Groups[1].Value;
        int? maxParallel = parallel.Length > 0 ? int.Parse(parallel) : null;
        var countMatch = Regex.Match(t, @"\b(\d+)\s+(?:routines|(?:code )?units|specs|packages)\b").Groups[1].Value;
        int? limit = countMatch.Length > 0 ? int.Parse(countMatch) : null;
        var path = Regex.Match(text, @"\b(?:in|under|from)\s+[`""']([^`""']+)[`""']").Groups[1].Value;
        var names = Regex.Matches(text, "`([^`]+)`").Select(m => m.Groups[1].Value).Where(n => n != path).ToArray();

        // "Cancel the wave run 39571149": a card's button names the run it belongs to.
        var runRef = Regex.Match(t, @"\brun\s+([0-9a-f]{8})\b").Groups[1].Value;
        var runId = runRef.Length > 0 ? runRef : null;

        if (Regex.IsMatch(t, @"\breview queue\b|\bwaiting for (a )?sign[- ]?off\b|\bwhat should i sign\b"))
            return ("get_review_queue", new { });

        if (Regex.IsMatch(t, @"\bretry\b.*\bfailed\b"))
            return ("control_work_run", new { action = "retry-failed", runId });
        var control = Regex.Match(t, @"\b(pause|resume|cancel|stop)\b.*\b(wave|batch|run)\b");
        if (control.Success && !t.Contains("pattern"))
        {
            var action = control.Groups[1].Value == "stop" ? "cancel" : control.Groups[1].Value;
            return ("control_work_run", new { action, budgetUsd = money, runId });
        }
        if (Regex.IsMatch(t, @"\b(wave|batch) run\b|\bhow is (the )?(wave|batch)\b|\brun progress\b"))
            return ("get_work_run", new { runId });

        var many = Regex.IsMatch(t, @"\b(all|every|many)\b|\b\d+\s+(routines|(?:code )?units|specs|packages)\b|\bthese (?:routines|(?:code )?units)\b") || names.Length > 1;

        if (Regex.IsMatch(t, @"\b(run|execute|start|kick off)\b.*\bwaves?\b") && !t.Contains("plan the"))
        {
            var n = Regex.Match(t, @"\bwave\s+(\d+)\b").Groups[1].Value;
            return ("run_wave", new
            {
                wave = n.Length > 0 ? int.Parse(n) : (int?)null,
                allWaves = Regex.IsMatch(t, @"\b(all|every)\s+(the\s+)?waves\b"),
                budgetUsd = money,
                maxParallel,
            });
        }
        // A feature slice: "Draft specs for ConflictMatrix and everything it calls", "Migrate X and what it calls".
        var slice = Regex.Match(text, @"\b(?:for|migrate|convert|extract)\s+[`""']?([A-Za-z0-9_./\-]+)[`""']?\s+and\s+(?:everything|all|what)\s+(?:it|they)\s+calls?\b", RegexOptions.IgnoreCase);
        if (slice.Success)
            return ("extract_many", new
            {
                path = slice.Groups[1].Value,
                withCallees = true,
                throughGates = Regex.IsMatch(t, @"\b(migrate|convert|end[- ]to[- ]end|through the gates)\b"),
                budgetUsd = money,
                maxParallel,
            });
        if (!many) return null;
        // The fix-until-green loop repairs one routine at a time; a batch of gates is not that loop.
        if (Regex.IsMatch(t, @"\buntil (it'?s |they'?re |all )?green\b|\bfix loop\b|\bauto[- ]?fix\b|\bmake (it|them) green\b"))
            return null;
        // Batches queue compile and test-pack only: "the quality gate" must not become those two.
        var qualityOnly = Regex.IsMatch(t, @"\bquality\b|\bsecurity (review|check|checks|scan)\b|\bcoding standards?\b|\bstatic analysis\b")
            && !Regex.IsMatch(t, @"\bcompile\b|\btest[- ]packs?\b");
        if (!qualityOnly && Regex.IsMatch(t, @"\bgates?\b|\bcompile\b|\btest[- ]packs?\b") && Regex.IsMatch(t, @"\b(run|compile|test)\b") && !Regex.IsMatch(t, @"\bgenerate\b"))
        {
            var gates = new List<string>();
            if (t.Contains("compile")) gates.Add("compile");
            if (t.Contains("test")) gates.Add("test-pack");
            return ("run_gates_many", new { gates, routines = names, limit });
        }
        if (Regex.IsMatch(t, @"\b(extract|draft)\b"))
            return ("extract_many", new { routines = names, path, limit, budgetUsd = money, maxParallel });
        if (Regex.IsMatch(t, @"\b(re)?generate\b|\bscaffold\b|\bconvert\b"))
            return ("generate_many", new
            {
                routines = names, path, limit, budgetUsd = money, maxParallel,
                thenGates = Regex.IsMatch(t, @"\bgates?\b|\bcompile\b|\btest\b"),
                regenerate = Regex.IsMatch(t, @"\bregenerate\b|\bgenerate (?:\w+ )?again\b"),
            });
        return null;
    }

    private static JsonElement Raw(IEnumerable<BrainBlock> blocks)
    {
        var arr = blocks.Select(b => b.Type == "text"
            ? (object)new { type = "text", text = b.Text }
            : new { type = "tool_use", id = b.ToolUseId, name = b.ToolName, input = b.Input }).ToList();
        return JsonSerializer.SerializeToElement(arr, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
