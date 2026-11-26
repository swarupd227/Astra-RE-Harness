using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Astra.Api.Validation;

/// <summary>
/// HTTP client for the PHP sidecar (<c>app/php-sidecar</c>): runs one PHP function or method with input vectors in
/// isolated php processes and returns what each run observed. Endpoint: <c>Validation:PhpEndpoint</c>.
/// </summary>
public sealed class PhpClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<PhpClient> _log;

    public PhpClient(IHttpClientFactory http, IConfiguration cfg, ILogger<PhpClient> log)
    {
        _http = http.CreateClient("php");
        _http.BaseAddress = new Uri(cfg["Validation:PhpEndpoint"] ?? "http://php-sidecar:51060");
        // 64 vectors × 30 s worst case run 4 at a time; a routine that loops forever is cut per vector by the sidecar.
        _http.Timeout = TimeSpan.FromMinutes(4);
        // On Azure the sidecar has an HTTPS address; it only runs code for callers that hold the shared token.
        if (cfg["Validation:PhpToken"] is { Length: > 0 } token)
            _http.DefaultRequestHeaders.Add("X-Astra-Sidecar-Token", token);
        _log = log;
    }

    public string Endpoint => _http.BaseAddress?.ToString() ?? "";

    public sealed record Source(string Path, string Content);

    public sealed record Entry(string Name, string? Path = null, int? Line = null, string? ClassName = null);

    public sealed record Vector(
        string Id,
        JsonObject? Named = null,
        JsonArray? Args = null,
        JsonObject? Superglobals = null);

    public sealed record RunRequest(
        IReadOnlyList<Source> Files,
        Entry Entry,
        IReadOnlyList<Vector> Vectors,
        int TimeoutMs = 10_000,
        int MemoryLimitMb = 128);

    public async Task<JsonObject?> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/health", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PHP sidecar health probe failed at {Endpoint}", Endpoint);
            return null;
        }
    }

    /// <summary>Runs the vectors; the response is kept as JSON — the comparer reads it field by field.</summary>
    public async Task<JsonObject> RunAsync(RunRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/run", request, JsonOpts, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PHP sidecar /run returned {(int)resp.StatusCode}: {Truncate(raw)}");
        return JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidOperationException("PHP sidecar /run returned no JSON object.");
    }

    private static string Truncate(string s) => s.Length <= 600 ? s : s[..600] + "…";
}
