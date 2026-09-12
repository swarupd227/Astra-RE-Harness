using System.Diagnostics;
using System.Text.Json;
using Astra.Api.Llm.Prompts;
using Astra.Api.Validation;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>
/// Survey digest via the Anthropic Messages API. Non-streaming, forced
/// tool-use output (the API assembles the JSON, so a stray quote can't
/// break the parse — same approach as <c>Docs/RoutineSummaryPipeline</c>),
/// system block cached, the routine's own line slice only, and every
/// call goes through the shared retrying send under the process-wide
/// rate limiter.
/// </summary>
public sealed class AnthropicSurveyProvider : ISurveyProvider
{
    private const string ToolName = "emit_survey_digest";

    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _anthropic;
    private readonly SurveyOptions _opts;
    private readonly PromptLibrary _prompts;
    private readonly AnthropicRateLimiter _limiter;
    private readonly ILogger<AnthropicSurveyProvider> _logger;
    private readonly Lazy<Dictionary<string, object?>> _toolSchema;

    private static readonly HashSet<string> KnownKindKeys =
        ClaimKindBucketer.KnownKinds.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);

    public AnthropicSurveyProvider(
        IHttpClientFactory httpFactory,
        IOptions<AnthropicOptions> anthropic,
        IOptions<SurveyOptions> opts,
        PromptLibrary prompts,
        AnthropicRateLimiter limiter,
        ILogger<AnthropicSurveyProvider> logger)
    {
        _httpFactory = httpFactory;
        _anthropic = anthropic.Value;
        _opts = opts.Value;
        _prompts = prompts;
        _limiter = limiter;
        _logger = logger;
        _toolSchema = new Lazy<Dictionary<string, object?>>(BuildToolSchema);
    }

    public string Name => "anthropic";

    public async Task<SurveyResult> SurveyAsync(SurveyRequest request, CancellationToken ct)
    {
        var loaded = _prompts.GetLatest("common", "dotnet8", "survey-digest")
            ?? throw new InvalidOperationException(
                "No survey-digest prompt registered (Llm/Prompts/common/dotnet8/survey-digest.v*.md).");

        var rendered = _prompts.Render(loaded, new Dictionary<string, string?>
        {
            ["claimKindTaxonomy"] = RenderTaxonomy(),
            ["archetypeHints"] = string.Join(", ", SurveyVocabulary.ArchetypeHints),
            ["modernizationFlags"] = string.Join(", ", SurveyVocabulary.ModernizationFlags),
            ["subroutineName"] = request.Name,
            ["signature"] = string.IsNullOrWhiteSpace(request.Signature) ? "(not available)" : request.Signature,
            ["sourceLanguage"] = request.SourceLanguage,
            ["sourcePath"] = request.SourcePath,
            ["lineStart"] = request.LineStart.ToString(),
            ["lineEnd"] = request.LineEnd.ToString(),
            ["callees"] = request.Callees.Count == 0 ? "(none detected)" : string.Join(", ", request.Callees),
            ["callerCount"] = request.CallerCount.ToString(),
            ["lineSlice"] = request.LineSlice,
        });

        var body = new Dictionary<string, object?>
        {
            ["model"] = _opts.Model,
            ["max_tokens"] = _opts.MaxOutputTokens,
            ["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = rendered.System,
                    ["cache_control"] = new { type = "ephemeral" },
                },
            },
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = ToolName,
                    ["description"] = "Emit the routine's survey digest as structured fields.",
                    ["input_schema"] = _toolSchema.Value,
                },
            },
            ["tool_choice"] = new Dictionary<string, object?> { ["type"] = "tool", ["name"] = ToolName },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = rendered.User },
            },
        };
        var bodyJson = JsonSerializer.Serialize(body);

        var sw = Stopwatch.StartNew();
        var http = _httpFactory.CreateClient("anthropic-survey");
        var response = await AnthropicHttp.SendWithRetryAsync(
            http, () => AnthropicHttp.BuildMessagesRequest(_anthropic, bodyJson),
            _limiter, cacheKey: $"survey:{loaded.Version}", _logger, ct);
        sw.Stop();

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        var toolInput = AnthropicHttp.ReadToolInput(root)
            ?? throw new InvalidOperationException("Survey response had no tool_use block.");
        var usage = AnthropicHttp.ReadUsage(root);

        return new SurveyResult(
            ParseDigest(toolInput),
            usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens,
            usage.Model ?? _opts.Model,
            loaded.PromptId, loaded.Version,
            sw.ElapsedMilliseconds);
    }

    // ── Output parsing ───────────────────────────────────────────────────

    internal static SurveyDigest ParseDigest(string toolInputJson)
    {
        using var doc = JsonDocument.Parse(toolInputJson);
        var r = doc.RootElement;

        var purpose = ReadString(r, "purpose") ?? "";
        if (purpose.Length > 240) purpose = purpose[..240] + "…";

        var kinds = ReadStringArray(r, "claimKinds")
            .Where(KnownKindKeys.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var hint = ReadString(r, "archetypeHint")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hint)) hint = null;

        var data = new List<SurveyDataAccess>();
        if (r.TryGetProperty("dataAccess", out var da) && da.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in da.EnumerateArray())
            {
                var table = ReadString(item, "table");
                var op = ReadString(item, "op");
                if (!string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(op))
                    data.Add(new SurveyDataAccess(table.Trim(), op.Trim().ToUpperInvariant()[..1]));
            }
        }

        var flags = ReadStringArray(r, "modernizationFlags")
            .Where(f => SurveyVocabulary.ModernizationFlags.Contains(f))
            .Distinct()
            .ToList();

        var complexity = ReadString(r, "complexity")?.ToLowerInvariant();
        if (complexity is null || !SurveyVocabulary.Complexities.Contains(complexity)) complexity = "moderate";

        return new SurveyDigest(purpose, kinds, hint, data, flags, complexity);
    }

    private static string? ReadString(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static IEnumerable<string> ReadStringArray(JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) yield return s;
    }

    // ── Prompt pieces ────────────────────────────────────────────────────

    private static string RenderTaxonomy() =>
        string.Join("\n", ClaimKindBucketer.KnownKinds.Select(k => $"- `{k.Key}` — {k.Definition}"));

    private static Dictionary<string, object?> BuildToolSchema() => new()
    {
        ["type"] = "object",
        ["required"] = new[] { "purpose", "claimKinds", "complexity" },
        ["properties"] = new Dictionary<string, object?>
        {
            ["purpose"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "One sentence, at most 160 characters: what this routine does, in the vocabulary of the business or subsystem it serves.",
            },
            ["claimKinds"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "Every kind of behavioural claim a full extraction would produce for this routine.",
                ["items"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = KnownKindKeys.OrderBy(k => k, StringComparer.Ordinal).ToArray() },
            },
            ["archetypeHint"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The routine's shape, from the vocabulary in the system prompt.",
                ["enum"] = SurveyVocabulary.ArchetypeHints,
            },
            ["dataAccess"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "Tables, files or datasets this routine reads or writes, when visible in the source. Empty when none.",
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new[] { "table", "op" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["table"] = new Dictionary<string, object?> { ["type"] = "string" },
                        ["op"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "C", "R", "U", "D", "X" } },
                    },
                },
            },
            ["modernizationFlags"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = SurveyVocabulary.ModernizationFlags },
            },
            ["complexity"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = SurveyVocabulary.Complexities,
            },
        },
    };
}
