using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Validation;

/// <summary>
/// HTTP client for the maven sidecar (Phase 5.5). Mirrors
/// <see cref="GfortranClient"/> so the validator side can drive Java
/// scaffolds through the same compile + test pattern that Fortran
/// scaffolds already use.
/// </summary>
public sealed class MavenClient
{
    // The sidecar emits camelCase via FastAPI aliases.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<MavenClient> _log;

    public MavenClient(IHttpClientFactory http, IConfiguration cfg, ILogger<MavenClient> log)
    {
        _http = http.CreateClient("maven");
        _http.BaseAddress = new Uri(cfg["Validation:MavenEndpoint"]
            ?? "http://maven-sidecar:51053");
        // `mvn` runs offline + pre-cached, but the worst-case JUnit run
        // for a generated scaffold still takes 10-30 s. 5 minutes is a
        // generous ceiling that protects against pathological loops in
        // engineer-written test bodies.
        _http.Timeout = TimeSpan.FromMinutes(5);
        _log = log;
    }

    public sealed record JavaSource(string Path, string Content);

    public sealed record CompileRequest(
        IReadOnlyList<JavaSource> Sources,
        IReadOnlyList<string>? ExtraArgs = null);

    public sealed record TestRequest(
        string ArtifactId,
        IReadOnlyList<string>? ExtraArgs = null,
        int TimeoutMs = 180_000);

    public sealed record CompileAndTestRequest(
        IReadOnlyList<JavaSource> Sources,
        IReadOnlyList<string>? ExtraArgs = null,
        int TimeoutMs = 180_000);

    public sealed record CompileSummary(
        [property: JsonPropertyName("artifactId")] string ArtifactId,
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("log")] string Log,
        [property: JsonPropertyName("warningCount")] int WarningCount,
        [property: JsonPropertyName("errorCount")] int ErrorCount,
        [property: JsonPropertyName("durationMs")] int DurationMs);

    public sealed record TestSummary(
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("tests")] int Tests,
        [property: JsonPropertyName("failures")] int Failures,
        [property: JsonPropertyName("errors")] int Errors,
        [property: JsonPropertyName("skipped")] int Skipped,
        [property: JsonPropertyName("durationMs")] int DurationMs,
        [property: JsonPropertyName("stdout")] string Stdout,
        [property: JsonPropertyName("stderr")] string Stderr,
        [property: JsonPropertyName("timedOut")] bool TimedOut,
        [property: JsonPropertyName("coverage")] Coverage? Coverage = null);

    /// <summary>JaCoCo line coverage of the main classes during the test run.</summary>
    public sealed record Coverage(
        [property: JsonPropertyName("lines")] int Lines,
        [property: JsonPropertyName("covered")] int Covered,
        [property: JsonPropertyName("percent")] double? Percent);

    /// <summary>One SpotBugs/FindSecBugs, PMD or Checkstyle finding, with a project-relative path.</summary>
    public sealed record ToolFinding(
        [property: JsonPropertyName("tool")] string Tool,
        [property: JsonPropertyName("rule")] string Rule,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("line")] int? Line,
        [property: JsonPropertyName("message")] string Message);

    public sealed record ToolRun(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("durationMs")] int DurationMs,
        [property: JsonPropertyName("error")] string? Error);

    public sealed record AnalyzeResponse(
        [property: JsonPropertyName("compiled")] bool Compiled,
        [property: JsonPropertyName("tools")] Dictionary<string, ToolRun> Tools,
        [property: JsonPropertyName("findings")] List<ToolFinding> Findings,
        [property: JsonPropertyName("version")] string? Version);

    public sealed record CompileAndTestResponse(
        [property: JsonPropertyName("compile")] CompileSummary Compile,
        [property: JsonPropertyName("test")] TestSummary? Test,
        [property: JsonPropertyName("skippedTestReason")] string? SkippedTestReason);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "maven sidecar health probe failed");
            return false;
        }
    }

    public async Task<CompileSummary> CompileAsync(CompileRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            sources = request.Sources.Select(s => new { path = s.Path, content = s.Content }).ToArray(),
            extraArgs = request.ExtraArgs?.ToArray() ?? Array.Empty<string>(),
        };
        using var resp = await _http.PostAsJsonAsync("/compile", body, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"maven sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<CompileSummary>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty maven compile response.");
    }

    public async Task<TestSummary> TestAsync(TestRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            artifactId = request.ArtifactId,
            extraArgs = request.ExtraArgs?.ToArray() ?? Array.Empty<string>(),
            timeoutMs = request.TimeoutMs,
        };
        using var resp = await _http.PostAsJsonAsync("/test", body, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"maven sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<TestSummary>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty maven test response.");
    }

    /// <summary>
    /// Static analysis for the QUALITY gate (sidecar 0.2.0+): SpotBugs + FindSecBugs, PMD and Checkstyle. Returns
    /// null when the sidecar predates the route (404) so the gate still runs on the platform's own rules.
    /// </summary>
    public async Task<AnalyzeResponse?> AnalyzeAsync(IReadOnlyList<JavaSource> sources, CancellationToken ct = default)
    {
        var body = new { sources = sources.Select(s => new { path = s.Path, content = s.Content }).ToArray() };
        using var resp = await _http.PostAsJsonAsync("/analyze", body, JsonOpts, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"maven sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<AnalyzeResponse>(raw, JsonOpts);
    }

    public async Task<CompileAndTestResponse> CompileAndTestAsync(
        CompileAndTestRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            sources = request.Sources.Select(s => new { path = s.Path, content = s.Content }).ToArray(),
            extraArgs = request.ExtraArgs?.ToArray() ?? Array.Empty<string>(),
            timeoutMs = request.TimeoutMs,
        };
        using var resp = await _http.PostAsJsonAsync("/compile-and-test", body, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"maven sidecar returned {(int)resp.StatusCode}: {raw}");
        return JsonSerializer.Deserialize<CompileAndTestResponse>(raw, JsonOpts)
            ?? throw new InvalidOperationException("Empty maven response.");
    }
}
