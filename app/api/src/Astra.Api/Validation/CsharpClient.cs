using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Validation;

/// <summary>
/// HTTP client for the csharp sidecar (Phase 12.0.a).
/// Mirrors <see cref="Vb6Client"/> and <see cref="GfortranClient"/>
/// one-for-one so CrossRuntimeValidator can drive a .NET 10 reference
/// binary alongside the generated scaffold via the same call shape.
///
/// The sidecar compiles C# sources using `dotnet publish` (Release,
/// net10.0) and runs the resulting binary with the supplied stdin payload.
/// Compile timeout is 180s (cold SDK start); run timeout follows the
/// caller's TimeoutMs field.
/// </summary>
public sealed class CsharpClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<CsharpClient> _log;

    public CsharpClient(IHttpClientFactory http, IConfiguration cfg, ILogger<CsharpClient> log)
    {
        _http = http.CreateClient("csharp");
        _http.BaseAddress = new Uri(cfg["Validation:CsharpEndpoint"]
            ?? "http://csharp-sidecar:51059");
        // dotnet publish cold start on first compile can take 30–60s on a warm
        // layer; subsequent compiles are faster. 5 minutes is the ceiling.
        _http.Timeout = TimeSpan.FromMinutes(5);
        _log = log;
    }

    public sealed record Source(string Path, string Content);

    public sealed record CompileAndRunRequest(
        IReadOnlyList<Source> Sources,
        string Stdin = "",
        int TimeoutMs = 30_000,
        IReadOnlyList<string>? ExtraFlags = null);

    public sealed record CompileSummary(
        [property: JsonPropertyName("artifactId")] string ArtifactId,
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("log")] string Log,
        [property: JsonPropertyName("warningCount")] int WarningCount,
        [property: JsonPropertyName("errorCount")] int ErrorCount,
        [property: JsonPropertyName("durationMs")] int DurationMs);

    public sealed record RunSummary(
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("durationMs")] int DurationMs,
        [property: JsonPropertyName("timedOut")] bool TimedOut);

    public sealed record CompileAndRunResponse(
        [property: JsonPropertyName("compile")] CompileSummary Compile,
        [property: JsonPropertyName("run")] RunSummary? Run,
        [property: JsonPropertyName("skippedRunReason")] string? SkippedRunReason);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "csharp sidecar health probe failed");
            return false;
        }
    }

    public async Task<CompileAndRunResponse> CompileAndRunAsync(
        CompileAndRunRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            sources = request.Sources.Select(s => new { path = s.Path, content = s.Content }).ToArray(),
            stdin = request.Stdin,
            timeoutMs = request.TimeoutMs,
            extraFlags = request.ExtraFlags?.ToArray() ?? Array.Empty<string>(),
        };
        using var resp = await _http.PostAsJsonAsync("/compile-and-run", body, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"csharp sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<CompileAndRunResponse>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty csharp sidecar response.");
    }
}
