using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Validation;

/// <summary>
/// HTTP client for the gnucobol sidecar (Phase 5.6). Mirrors
/// <see cref="GfortranClient"/> one-for-one so the per-routine
/// equivalence harness can drive a COBOL reference binary alongside
/// the generated Java / .NET scaffolds via the same shape of call.
/// </summary>
public sealed class GnuCobolClient
{
    // The sidecar emits camelCase via FastAPI aliases.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<GnuCobolClient> _log;

    public GnuCobolClient(IHttpClientFactory http, IConfiguration cfg, ILogger<GnuCobolClient> log)
    {
        _http = http.CreateClient("gnucobol");
        _http.BaseAddress = new Uri(cfg["Validation:GnucobolEndpoint"]
            ?? "http://gnucobol-sidecar:51054");
        // cobc on a tiny driver runs in <1s; even a worst-case 200-row
        // canonical input loop is sub-second. 3 minutes is a generous
        // ceiling that protects against pathological driver-author bugs.
        _http.Timeout = TimeSpan.FromMinutes(3);
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
            _log.LogWarning(ex, "gnucobol sidecar health probe failed");
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
                $"gnucobol sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<CompileAndRunResponse>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty gnucobol response.");
    }
}
