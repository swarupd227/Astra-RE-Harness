using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Validation;

/// <summary>
/// HTTP client for the fpc sidecar (Delphi/Object Pascal, via Free Pascal
/// Compiler). Phase 15.1.b. Mirrors <see cref="GnuCobolClient"/> /
/// <see cref="GfortranClient"/> one-for-one — the fpc-sidecar's own
/// server.py docstring states its contract matches the gfortran sidecar's
/// verbatim except for the optional <c>mainProgram</c> field (needed
/// because a Delphi source set can contain more than one .dpr/.pas
/// candidate entry point, unlike a single Fortran/COBOL driver file).
///
/// This client existed nowhere in the codebase before 15.1.b even though
/// the fpc-sidecar container has been running and healthy the whole
/// session — real, working infrastructure with zero consumers. Building
/// this is what actually turns it on.
/// </summary>
public sealed class FpcClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<FpcClient> _log;

    public FpcClient(IHttpClientFactory http, IConfiguration cfg, ILogger<FpcClient> log)
    {
        _http = http.CreateClient("fpc");
        _http.BaseAddress = new Uri(cfg["Validation:FpcEndpoint"]
            ?? "http://fpc-sidecar:51055");
        _http.Timeout = TimeSpan.FromMinutes(3);
        _log = log;
    }

    public sealed record Source(string Path, string Content);

    public sealed record CompileAndRunRequest(
        IReadOnlyList<Source> Sources,
        string Stdin = "",
        int TimeoutMs = 30_000,
        IReadOnlyList<string>? ExtraFlags = null,
        string? MainProgram = null);

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
            _log.LogWarning(ex, "fpc sidecar health probe failed");
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
            mainProgram = request.MainProgram,
        };
        using var resp = await _http.PostAsJsonAsync("/compile-and-run", body, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"fpc sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<CompileAndRunResponse>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty fpc response.");
    }
}
