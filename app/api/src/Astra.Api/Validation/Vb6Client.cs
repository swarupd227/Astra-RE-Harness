using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Validation;

/// <summary>
/// HTTP client for the vb6 sidecar (Phase 10.0.f / 10.3.c). Mirrors
/// <see cref="GnuCobolClient"/> and <see cref="GfortranClient"/>
/// one-for-one so the equivalence harness can drive a VB6/VBScript
/// reference binary alongside the generated .NET 10 scaffold via the
/// same shape of call.
///
/// Per the user's choice on the Phase 10.3 kick-off, the dev tier
/// runs VBScript via Wine + cscript rather than vb6.exe + msvbvm60.dll
/// — no licensed Microsoft runtime needed. The sidecar detects .vbs
/// sources and routes to cscript; .bas/.cls/.frm continue to use the
/// vb6.exe path on the windows / windows-server-core tier.
/// </summary>
public sealed class Vb6Client
{
    // The sidecar emits camelCase via FastAPI aliases.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<Vb6Client> _log;

    public Vb6Client(IHttpClientFactory http, IConfiguration cfg, ILogger<Vb6Client> log)
    {
        _http = http.CreateClient("vb6");
        _http.BaseAddress = new Uri(cfg["Validation:Vb6Endpoint"]
            ?? "http://vb6-sidecar:51058");
        // vb6.exe / cscript on a tiny harness runs in <2s; even with
        // Wine's first-call cold start (msvbvm60 / wscript registration)
        // the worst observed wall-clock is ~10s. 3 minutes is a generous
        // ceiling matching the other sidecars.
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
            _log.LogWarning(ex, "vb6 sidecar health probe failed");
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
                $"vb6 sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<CompileAndRunResponse>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty vb6 response.");
    }
}
