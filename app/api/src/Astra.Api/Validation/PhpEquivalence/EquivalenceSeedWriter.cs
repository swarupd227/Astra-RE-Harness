using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Copilot;
using Astra.Api.Llm;
using Astra.Api.Llm.PatternAnalysis;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace Astra.Api.Validation.PhpEquivalence;

/// <summary>The rows (and session values) each vector of a seeded equivalence run starts from.</summary>
public sealed record SeedPlan(
    IReadOnlyDictionary<string, JsonObject> Tables,     // vector id → {table: [rows]}
    IReadOnlyDictionary<string, JsonObject> Session,    // vector id → $_SESSION values the model added
    string Source,                                       // engineer | model | cache | mock | none
    string? Model,
    decimal CostUsd,
    string? Note)
{
    public static SeedPlan Empty(string source, string? note = null) =>
        new(new Dictionary<string, JsonObject>(), new Dictionary<string, JsonObject>(), source, null, 0m, note);
}

/// <summary>
/// Writes the seed rows of a seeded equivalence run: one forced tool call (the survey tier's small model, temperature
/// 0) reads the code unit, its signed spec, the package's schema and the vectors, and returns rows per vector. Cached
/// in the scaffolds bucket per spec + schema + vectors + prompt version, so a re-run is reproducible and free; recorded
/// as an <see cref="LlmCall"/>. Engineer-supplied seeds (<c>vectors[].db</c>) always win. Without the Anthropic provider
/// (mock, e2e) every vector starts from empty tables, which is deterministic.
/// </summary>
public sealed class EquivalenceSeedWriter
{
    private const string ToolName = "write_seeds";
    private const int MaxSourceChars = 20_000, MaxSpecChars = 20_000, MaxSchemaChars = 20_000;

    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _anthropic;
    private readonly SurveyOptions _survey;
    private readonly AnthropicRateLimiter _limiter;
    private readonly PromptLibrary _prompts;
    private readonly ICopilotBrain _brain;
    private readonly ILogger<EquivalenceSeedWriter> _log;

    public EquivalenceSeedWriter(IHttpClientFactory httpFactory, IOptions<AnthropicOptions> anthropic, IOptions<SurveyOptions> survey,
        AnthropicRateLimiter limiter, PromptLibrary prompts, ICopilotBrain brain, ILogger<EquivalenceSeedWriter> log)
    {
        _httpFactory = httpFactory;
        _anthropic = anthropic.Value;
        _survey = survey.Value;
        _limiter = limiter;
        _prompts = prompts;
        _brain = brain;
        _log = log;
    }

    private PromptLibrary.LoadedPrompt? Prompt => _prompts.GetLatest("common", "equivalence", "db-seeds");

    public bool Available => _brain.Name == "anthropic" && !string.IsNullOrWhiteSpace(_anthropic.ApiKey) && Prompt is not null;

    /// <summary>The cache key: what the seeds depend on.</summary>
    public string CacheKey(string specJson, string schemaSql, IEnumerable<PhpEquivalenceVectors.EqVector> vectors)
    {
        var sb = new StringBuilder(specJson).Append('\n').Append(schemaSql).Append('\n').Append(Prompt?.Version ?? "none");
        foreach (var v in vectors) sb.Append('\n').Append(v.Id).Append(v.Label).Append(v.Named.ToJsonString()).Append(v.Superglobals?.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32].ToLowerInvariant();
    }

    public async Task<SeedPlan> WriteAsync(AppDbContext db, string routineName, string routineSource, string specJson, string schemaSql,
        IReadOnlyList<PhpEquivalenceVectors.EqVector> vectors, CancellationToken ct)
    {
        var prompt = Prompt;
        if (!Available || prompt is null)
            return SeedPlan.Empty("mock", "No model provider here: every vector starts from empty tables.");

        var rendered = _prompts.Render(prompt, new Dictionary<string, string?>
        {
            ["routineName"] = routineName,
            ["source"] = Trim(routineSource, MaxSourceChars),
            ["spec"] = Trim(specJson, MaxSpecChars),
            ["schema"] = Trim(schemaSql, MaxSchemaChars),
            ["vectors"] = string.Join("\n", vectors.Select(v =>
                $"- `{v.Id}` — {v.Label} · inputs {v.Named.ToJsonString()}" + (v.Superglobals is null ? "" : $" · request {v.Superglobals.ToJsonString()}"))),
        });
        var obj = new Dictionary<string, object?> { ["type"] = "object" };
        var body = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(_survey.Model) ? _anthropic.Model : _survey.Model,
            ["max_tokens"] = 4096,
            ["temperature"] = 0,
            ["system"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = rendered.System, ["cache_control"] = new { type = "ephemeral" } } },
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = ToolName,
                    ["description"] = "The rows each vector starts from (and session values it needs).",
                    ["input_schema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "vectors" },
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["vectors"] = new Dictionary<string, object?>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "object",
                                    ["required"] = new[] { "id", "tables" },
                                    ["properties"] = new Dictionary<string, object?>
                                    {
                                        ["id"] = new Dictionary<string, object?> { ["type"] = "string" },
                                        ["tables"] = new Dictionary<string, object?>
                                        {
                                            ["type"] = "object",
                                            ["description"] = "table name → list of rows (column → value)",
                                            ["additionalProperties"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = obj },
                                        },
                                        ["session"] = obj,
                                    },
                                },
                            },
                        },
                    },
                },
            },
            ["tool_choice"] = new Dictionary<string, object?> { ["type"] = "tool", ["name"] = ToolName },
            ["messages"] = new[] { new Dictionary<string, object?> { ["role"] = "user", ["content"] = rendered.User } },
        };

        var sw = Stopwatch.StartNew();
        string responseBody;
        try
        {
            var http = _httpFactory.CreateClient("anthropic-copilot");
            var response = await AnthropicHttp.SendWithRetryAsync(
                http, () => AnthropicHttp.BuildMessagesRequest(_anthropic, JsonSerializer.Serialize(body)),
                _limiter, cacheKey: $"db-seeds:{prompt.Version}", _log, ct, maxAttempts: 2);
            responseBody = response.Body;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Equivalence seeds failed for {Routine}", routineName);
            return SeedPlan.Empty("none", $"The seed rows could not be written ({ex.Message}); every vector starts from empty tables.");
        }
        sw.Stop();

        using var doc = JsonDocument.Parse(responseBody);
        var input = AnthropicHttp.ReadToolInput(doc.RootElement) ?? "{}";
        var usage = AnthropicHttp.ReadUsage(doc.RootElement);
        var modelUsed = usage.Model ?? (string)body["model"]!;
        var cost = ModelPricing.Estimate("anthropic", modelUsed, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens);
        db.LlmCalls.Add(new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = "anthropic",
            Model = modelUsed,
            PromptTemplateId = prompt.PromptId,
            PromptTemplateVersion = prompt.Version,
            ProviderConfigVersion = "db-seeds",
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheCreationTokens = usage.CacheCreationTokens,
            LatencyMs = sw.ElapsedMilliseconds,
            CostUsd = cost,
            Status = "success",
            CalledAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        var plan = Parse(input, vectors.Select(v => v.Id), EquivalenceDatabase.Tables(EquivalenceDatabase.Statements(schemaSql)));
        return plan with { Source = "model", Model = modelUsed, CostUsd = cost };
    }

    /// <summary>
    /// The tool input as seeds: only known vector ids, only tables the schema has (others are dropped and named in the
    /// note), rows as plain column → value objects with identifier-shaped column names.
    /// </summary>
    public static SeedPlan Parse(string toolInputJson, IEnumerable<string> vectorIds, IReadOnlyCollection<string> schemaTables)
    {
        var ids = new HashSet<string>(vectorIds, StringComparer.Ordinal);
        var known = new HashSet<string>(schemaTables, StringComparer.OrdinalIgnoreCase);
        var tables = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var session = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        JsonNode? root;
        try { root = JsonNode.Parse(toolInputJson); } catch (JsonException) { root = null; }
        foreach (var v in (root?["vectors"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var id = v["id"]?.GetValueKind() == JsonValueKind.String ? v["id"]!.GetValue<string>() : null;
            if (id is null || !ids.Contains(id)) continue;
            var t = new JsonObject();
            foreach (var (table, rows) in v["tables"] as JsonObject ?? new JsonObject())
            {
                if (!known.Contains(table)) { dropped.Add(table); continue; }
                var clean = new JsonArray();
                foreach (var row in (rows as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var r = new JsonObject();
                    foreach (var (col, val) in row)
                        if (System.Text.RegularExpressions.Regex.IsMatch(col, "^[A-Za-z_][A-Za-z0-9_]*$") && val is not JsonObject and not JsonArray)
                            r[col] = val?.DeepClone();
                    if (r.Count > 0) clean.Add(r);
                }
                t[table.ToLowerInvariant()] = clean;
            }
            tables[id] = t;
            if (v["session"] is JsonObject s && s.Count > 0) session[id] = (JsonObject)s.DeepClone();
        }
        return new SeedPlan(tables, session, "model", null, 0m,
            dropped.Count > 0 ? $"Rows for tables the schema does not have were dropped: {string.Join(", ", dropped)}." : null);
    }

    /// <summary>The plan as stored (cache and run metrics).</summary>
    public static JsonObject ToJson(SeedPlan p) => new()
    {
        ["source"] = p.Source,
        ["model"] = p.Model,
        ["costUsd"] = p.CostUsd,
        ["note"] = p.Note,
        ["tables"] = new JsonObject(p.Tables.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value.DeepClone()))),
        ["session"] = new JsonObject(p.Session.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value.DeepClone()))),
    };

    public static SeedPlan? FromJson(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject o) return null;
            static Dictionary<string, JsonObject> Map(JsonNode? n) =>
                (n as JsonObject ?? new JsonObject()).Where(kv => kv.Value is JsonObject)
                    .ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value!.DeepClone(), StringComparer.Ordinal);
            return new SeedPlan(Map(o["tables"]), Map(o["session"]), "cache", o["model"]?.ToString(), 0m, o["note"]?.ToString());
        }
        catch (JsonException) { return null; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "\n… (truncated)";
}
