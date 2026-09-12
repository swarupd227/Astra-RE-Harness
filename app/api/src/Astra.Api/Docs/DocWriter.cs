using System.Diagnostics;
using System.Text.Json;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// One forced-tool-use call to a documentation writer, critic, or reviser.
/// <see cref="SystemBlocks"/> are sent in order as cached system text (the
/// style guide first, the kind prompt last). <see cref="MockPayload"/> is
/// the deterministic tool input the mock provider returns instead of
/// calling a model — every call site knows what a plausible answer to its
/// own prompt looks like, so the mock path exercises the same parsing and
/// persistence as the real one.
/// </summary>
public sealed record DocWriteRequest(
    string PromptId,
    string PromptVersion,
    IReadOnlyList<string> SystemBlocks,
    string UserMessage,
    string ToolName,
    string ToolDescription,
    object ToolSchema,
    string Model,
    int MaxTokens,
    string CacheKey,
    Func<string> MockPayload);

public sealed record DocWriteResult(
    string ToolInputJson,
    AnthropicUsage Usage,
    string Model,
    long LatencyMs,
    string StopReason);

/// <summary>A request/response pair, kept so the pipeline can record an
/// <see cref="LlmCall"/> row for every model call the quality loop made.</summary>
public sealed record DocCallRecord(DocWriteRequest Request, DocWriteResult Result);

public interface IDocWriter
{
    /// <summary>"anthropic" | "mock" — recorded on <see cref="LlmCall.Provider"/>.</summary>
    string ProviderName { get; }
    bool IsMock { get; }
    /// <summary>Recorded on <see cref="LlmCall.ProviderConfigVersion"/>.</summary>
    string ConfigVersion { get; }
    Task<DocWriteResult> WriteAsync(DocWriteRequest request, CancellationToken ct);
}

/// <summary>
/// Anthropic Messages API writer. Non-streaming, forced tool use (the API
/// assembles the JSON so a stray quote in prose cannot break the parse),
/// system blocks cached with a single breakpoint on the last block, and
/// every call through <see cref="AnthropicHttp.SendWithRetryAsync"/> under
/// the process-wide <see cref="AnthropicRateLimiter"/>. The API key is read
/// per call from the shared <see cref="AnthropicOptions"/> instance so the
/// settings UI can rotate it at runtime.
/// </summary>
public sealed class AnthropicDocWriter : IDocWriter
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _anthropic;
    private readonly AnthropicRateLimiter _limiter;
    private readonly ILogger<AnthropicDocWriter> _logger;

    public AnthropicDocWriter(
        IHttpClientFactory httpFactory,
        IOptions<AnthropicOptions> anthropic,
        AnthropicRateLimiter limiter,
        ILogger<AnthropicDocWriter> logger)
    {
        _httpFactory = httpFactory;
        _anthropic = anthropic.Value;
        _limiter = limiter;
        _logger = logger;
    }

    public string ProviderName => "anthropic";
    public bool IsMock => false;
    public string ConfigVersion => _anthropic.ConfigVersion;

    public async Task<DocWriteResult> WriteAsync(DocWriteRequest request, CancellationToken ct)
    {
        var system = new List<Dictionary<string, object?>>(request.SystemBlocks.Count);
        for (var i = 0; i < request.SystemBlocks.Count; i++)
        {
            var block = new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = request.SystemBlocks[i],
            };
            // One breakpoint on the last block caches the whole prefix
            // (tools → system). Blocks are stable per kind, so every call
            // after the first in a five-minute window reads the cache.
            if (i == request.SystemBlocks.Count - 1)
                block["cache_control"] = new { type = "ephemeral" };
            system.Add(block);
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = system,
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = request.ToolName,
                    ["description"] = request.ToolDescription,
                    ["input_schema"] = request.ToolSchema,
                },
            },
            ["tool_choice"] = new Dictionary<string, object?> { ["type"] = "tool", ["name"] = request.ToolName },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = request.UserMessage },
            },
        };
        var bodyJson = JsonSerializer.Serialize(body);

        // Long documents at 16k output tokens run for minutes; the default
        // 100 s client timeout is far too short (the catalog stage timed out
        // at exactly 180 s on large corpora before it was raised).
        var http = _httpFactory.CreateClient("docs-summary");
        http.Timeout = TimeSpan.FromMinutes(15);

        var sw = Stopwatch.StartNew();
        var response = await AnthropicHttp.SendWithRetryAsync(
            http, () => AnthropicHttp.BuildMessagesRequest(_anthropic, bodyJson),
            _limiter, cacheKey: request.CacheKey, _logger, ct);
        sw.Stop();

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;
        var toolInput = AnthropicHttp.ReadToolInput(root)
            ?? throw new InvalidOperationException(
                $"Anthropic response for {request.ToolName} had no tool_use block: {Truncate(response.Body, 300)}");
        var usage = AnthropicHttp.ReadUsage(root);
        var stopReason = root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
            ? sr.GetString() ?? ""
            : "";
        if (stopReason == "max_tokens")
            _logger.LogWarning(
                "Docs writer {Tool} hit max_tokens={Max} on {Model}; the document may be cut short",
                request.ToolName, request.MaxTokens, request.Model);

        return new DocWriteResult(toolInput, usage, usage.Model ?? request.Model, sw.ElapsedMilliseconds, stopReason);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// Offline writer for tests and <c>Docs__Generator__Provider=mock</c>. Returns
/// the request's own <see cref="DocWriteRequest.MockPayload"/> — the same
/// <c>{markdown, meta}</c> / <c>{entries}</c> envelope shape the real writer
/// produces — with zero usage.
/// </summary>
public sealed class MockDocWriter : IDocWriter
{
    public string ProviderName => "mock";
    public bool IsMock => true;
    public string ConfigVersion => "mock:offline";

    public Task<DocWriteResult> WriteAsync(DocWriteRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = request.MockPayload();
        using (JsonDocument.Parse(payload)) { /* the mock must produce valid JSON like the API does */ }
        return Task.FromResult(new DocWriteResult(
            payload, new AnthropicUsage(0, 0, 0, 0, "mock"), "mock", 0, "end_turn"));
    }
}

/// <summary>Records one <see cref="LlmCall"/> per writer/critic/reviser call,
/// priced from the model that actually answered.</summary>
public static class DocLlmCalls
{
    public static LlmCall Record(AppDbContext db, IDocWriter writer, DocWriteRequest request, DocWriteResult result)
    {
        var u = result.Usage;
        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = writer.ProviderName,
            Model = result.Model,
            PromptTemplateId = request.PromptId,
            PromptTemplateVersion = request.PromptVersion,
            ProviderConfigVersion = writer.ConfigVersion,
            InputTokens = u.InputTokens,
            OutputTokens = u.OutputTokens,
            CacheReadTokens = u.CacheReadTokens,
            CacheCreationTokens = u.CacheCreationTokens,
            LatencyMs = result.LatencyMs,
            CostUsd = ModelPricing.Estimate(
                writer.ProviderName, result.Model, u.InputTokens, u.OutputTokens,
                u.CacheReadTokens, u.CacheCreationTokens),
            Status = "success",
            CalledAt = DateTimeOffset.UtcNow,
        };
        db.LlmCalls.Add(call);
        return call;
    }

    public static void RecordAll(AppDbContext db, IDocWriter writer, IEnumerable<DocCallRecord> calls)
    {
        foreach (var c in calls) Record(db, writer, c.Request, c.Result);
    }
}
