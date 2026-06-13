using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Validation;

/// <summary>
/// HTTP client for the property-test sidecar (Phase 9.3.b). Wraps the
/// <c>/falsify</c> endpoint that drives Hypothesis-driven counterexample
/// search per ADR-029. Same shape as <see cref="GfortranClient"/> /
/// <see cref="MavenClient"/> / <see cref="GnuCobolClient"/> so callers
/// stay symmetric across all four equivalence sidecars + this one.
/// </summary>
public sealed class PropertyTestClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<PropertyTestClient> _log;

    public PropertyTestClient(IHttpClientFactory http, IConfiguration cfg, ILogger<PropertyTestClient> log)
    {
        _http = http.CreateClient("property-test");
        _http.BaseAddress = new Uri(cfg["Validation:PropertyTestEndpoint"]
            ?? "http://property-test-sidecar:51056");
        // The sidecar caps a single /falsify call at 10 min per spec; we
        // give the HTTP layer a slightly larger budget so we don't time
        // the call out before the sidecar's own deadline fires.
        _http.Timeout = TimeSpan.FromMinutes(12);
        _log = log;
    }

    // ──────────────────────────────────────────────────────────────────
    // Wire types — must round-trip with property_test_sidecar/server.py.
    // ──────────────────────────────────────────────────────────────────

    public sealed record InputHint(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("min")] double? Min = null,
        [property: JsonPropertyName("max")] double? Max = null,
        [property: JsonPropertyName("maxLen")] int? MaxLen = null,
        [property: JsonPropertyName("alphabet")] string? Alphabet = null);

    public sealed record GeneratorHints(
        [property: JsonPropertyName("inputs")] IReadOnlyList<InputHint> Inputs,
        [property: JsonPropertyName("constraint")] string? Constraint = null,
        [property: JsonPropertyName("examples")] IReadOnlyList<Dictionary<string, object?>>? Examples = null);

    public sealed record ClaimSpec(
        [property: JsonPropertyName("claimId")] string ClaimId,
        [property: JsonPropertyName("generatorHints")] GeneratorHints GeneratorHints,
        [property: JsonPropertyName("budget")] int Budget = 100,
        [property: JsonPropertyName("timeoutMs")] int TimeoutMs = 30_000);

    public sealed record FalsifyRequest(
        [property: JsonPropertyName("specId")] string SpecId,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("callbackUrl")] string CallbackUrl,
        [property: JsonPropertyName("claims")] IReadOnlyList<ClaimSpec> Claims,
        [property: JsonPropertyName("perSpecTimeoutMs")] int PerSpecTimeoutMs = 600_000);

    public sealed record ClaimResult(
        [property: JsonPropertyName("claimId")] string ClaimId,
        [property: JsonPropertyName("examplesTried")] int ExamplesTried,
        [property: JsonPropertyName("falsifying")] JsonElement? Falsifying,
        [property: JsonPropertyName("refOutput")] string? RefOutput,
        [property: JsonPropertyName("candOutput")] string? CandOutput,
        [property: JsonPropertyName("elapsedMs")] int ElapsedMs,
        [property: JsonPropertyName("timedOut")] bool TimedOut,
        [property: JsonPropertyName("callbackErrors")] int CallbackErrors,
        [property: JsonPropertyName("skipReason")] string? SkipReason);

    public sealed record FalsifyResponse(
        [property: JsonPropertyName("specId")] string SpecId,
        [property: JsonPropertyName("claimResults")] IReadOnlyList<ClaimResult> ClaimResults,
        [property: JsonPropertyName("overallFalsified")] bool OverallFalsified,
        [property: JsonPropertyName("totalElapsedMs")] int TotalElapsedMs);

    // ──────────────────────────────────────────────────────────────────
    // Operations
    // ──────────────────────────────────────────────────────────────────

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "property-test sidecar health probe failed");
            return false;
        }
    }

    public async Task<FalsifyResponse> FalsifyAsync(FalsifyRequest req, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/falsify", req, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"property-test sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<FalsifyResponse>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty property-test response.");
    }
}
