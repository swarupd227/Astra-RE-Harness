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
                        new { label = "Search routines", intent = "Find routines named " },
                    },
                })),
            };
            return Task.FromResult(new BrainResponse(blocks, "tool_use", Raw(blocks), null, Model, 0));
        }

        var userText = lastContent.ValueKind == JsonValueKind.String ? lastContent.GetString() ?? "" : lastContent.GetRawText();
        var (tool, input) = Route(userText);
        var call = new List<BrainBlock>
        {
            new("text", $"On it — {Verb(tool)}.", null, null, null),
            new("tool_use", null, "toolu_mock_1", tool, JsonSerializer.SerializeToElement(input)),
        };
        return Task.FromResult(new BrainResponse(call, "tool_use", Raw(call), null, Model, 0));
    }

    private static string Verb(string tool) => tool switch
    {
        "get_programme_status" => "checking the programme status",
        "search_routines" => "searching the routines",
        "survey_corpus" => "starting the pattern survey",
        "get_pattern_clusters" => "loading the pattern clusters",
        "extract_spec" => "drafting the spec",
        "sign_spec" => "preparing the sign-off",
        "route_for_review" => "routing the spec",
        "generate_scaffold" => "generating the code",
        "run_gate" => "running the gate",
        "list_programmes" => "listing the programmes",
        "read_routine" => "reading the routine",
        "get_spec" => "loading the spec",
        "rank_routines" => "ranking the routines",
        "run_assessment" => "starting the assessment",
        "explain_claim" => "pulling up the claim and its source",
        "search_docs" => "searching the documentation",
        "list_modules" => "listing the modules",
        _ => "working on it",
    };

    private static (string Tool, object Input) Route(string text)
    {
        var t = text.ToLowerInvariant();
        var quotedMatch = Regex.Match(text, "[`\"']([^`\"']+)[`\"']").Groups[1].Value;
        string? quoted = quotedMatch.Length > 0 ? quotedMatch : null;
        var dottedMatch = Regex.Match(text, @"\b([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\b").Groups[1].Value;
        string? name = quoted ?? (dottedMatch.Length > 0 ? dottedMatch : null);

        if (Regex.IsMatch(t, @"\b(programmes|projects|corpora|every programme|all programmes)\b")) return ("list_programmes", new { });
        if (Regex.IsMatch(t, @"\bassess(ment)?\b")) return ("run_assessment", new { });
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
        if (Regex.IsMatch(t, @"\b(re)?generate\b|\bscaffold\b|\bbuild\b")) return ("generate_scaffold", new { subroutineId = name ?? "", repairFromLatestFailure = Regex.IsMatch(t, @"\bfix") });
        if (Regex.IsMatch(t, @"\bcompile\b|\bgate\b|\btest[- ]pack\b")) return ("run_gate", new { subroutineId = name ?? "", gate = t.Contains("test") ? "test-pack" : "compile" });
        if (Regex.IsMatch(t, @"\bexplain\b|\bclaims\b|\bspec for\b|\bthe spec\b")) return ("get_spec", new { subroutineId = name ?? "" });
        if (Regex.IsMatch(t, @"\bsource\b|\bread\b|\bshow me\b") && name is not null) return ("read_routine", new { subroutineId = name });
        if (Regex.IsMatch(t, @"\b(find|search|which routines|routines named|look for|riskiest)\b"))
        {
            var query = quoted ?? Regex.Match(text, @"(?:named|called|for|matching)\s+([A-Za-z0-9_.]+)").Groups[1].Value;
            return ("search_routines", new { query = string.IsNullOrWhiteSpace(query) ? "" : query, limit = 20 });
        }
        return ("get_programme_status", new { });
    }

    private static JsonElement Raw(IEnumerable<BrainBlock> blocks)
    {
        var arr = blocks.Select(b => b.Type == "text"
            ? (object)new { type = "text", text = b.Text }
            : new { type = "tool_use", id = b.ToolUseId, name = b.ToolName, input = b.Input }).ToList();
        return JsonSerializer.SerializeToElement(arr, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
