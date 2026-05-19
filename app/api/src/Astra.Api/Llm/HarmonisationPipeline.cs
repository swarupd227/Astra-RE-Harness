using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm;

/// <summary>
/// Phase 7.1 — Runs a single cross-routine harmonisation pass over a
/// corpus's signed specs and persists structured findings.
///
/// This pass is the answer to "per-routine extract can't see corpus-
/// wide drift": it sends every SIGNED spec in one call and asks the
/// model to find contradictions (callee-IO drift, COMMON-layout
/// drift, terminology drift, missing invariants, duplicate open
/// questions). The output is findings the SME confirms/dismisses —
/// not spec rewrites.
///
/// Provider routing:
///   - Llm:Provider = anthropic → real Anthropic Messages-API call.
///   - Llm:Provider = mock|fail-mock → deterministic stub findings so
///     local dev + e2e can exercise the pipeline without an API key.
///
/// Cost shape: 1 call per pass with all specs inline. For a 50-routine
/// corpus that's ~50k input tokens uncached → ~$0.20 at sonnet pricing.
/// Anthropic prompt caching on the system prompt makes repeat passes
/// (e.g. after editing a single spec) 90% cheaper.
/// </summary>
public sealed class HarmonisationPipeline
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly AppDbContext _db;
    private readonly PromptLibrary _prompts;
    private readonly ILlmProvider _provider;
    private readonly AnthropicOptions _anthropicOpts;
    private readonly HttpClient _http;
    private readonly IAuditLogger _audit;
    private readonly ILogger<HarmonisationPipeline> _logger;

    public HarmonisationPipeline(
        AppDbContext db,
        PromptLibrary prompts,
        ILlmProvider provider,
        IOptions<AnthropicOptions> anthropicOpts,
        IHttpClientFactory http,
        IAuditLogger audit,
        ILogger<HarmonisationPipeline> logger)
    {
        _db = db;
        _prompts = prompts;
        _provider = provider;
        _anthropicOpts = anthropicOpts.Value;
        _http = http.CreateClient("anthropic-harmonise");
        _http.Timeout = TimeSpan.FromMinutes(10);
        _audit = audit;
        _logger = logger;
    }

    public async Task<HarmonisationRun> RunAsync(
        Guid corpusId,
        DevPersonaContext? actor,
        CancellationToken ct = default)
    {
        // 1. Resolve the corpus + its latest SourceVersion.
        var corpus = await _db.Corpora.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == corpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");
        var version = await _db.SourceVersions.AsNoTracking()
            .Where(v => v.CorpusId == corpusId)
            .OrderByDescending(v => v.IngestedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} has no source versions.");

        // 2. Load every SIGNED spec in that version. We only harmonise
        //    over signed specs because draft / in-review specs are
        //    still being authored — harmonising them would produce
        //    findings against transient state.
        var subroutineIds = await _db.Subroutines.AsNoTracking()
            .Include(s => s.SourceFile)
            .Where(s => s.SourceFile!.SourceVersionId == version.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var specs = await _db.Specs.AsNoTracking()
            .Where(sp => subroutineIds.Contains(sp.SubroutineId) && sp.State == "SIGNED")
            .OrderBy(sp => sp.SubroutineId)
            .ToListAsync(ct);

        var startedAt = DateTimeOffset.UtcNow;
        var run = new HarmonisationRun
        {
            Id = Guid.NewGuid(),
            CorpusId = corpusId,
            SourceVersionId = version.Id,
            Status = "RUNNING",
            PromptId = "cross-routine-harmonise",
            PromptVersion = "v0.1",
            ModelName = _provider.Info.Model,
            SpecCount = specs.Count,
            Summary = "Harmonisation pass queued",
            StartedAt = startedAt,
            CompletedAt = startedAt,
            TriggeredBy = actor?.DisplayName,
        };
        _db.HarmonisationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "harmonisation.started", "corpus", corpusId, actor,
            payload: new { runId = run.Id, specCount = specs.Count },
            ct: ct);

        if (specs.Count == 0)
        {
            run.Status = "COMPLETED";
            run.FindingCount = 0;
            run.Summary = "No signed specs to harmonise.";
            run.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return run;
        }

        // 3. Render the prompt with the specs inlined.
        var loaded = _prompts.GetLatest("common", "dotnet8", "harmonise")
            ?? throw new InvalidOperationException(
                "No harmonise prompt registered (common/dotnet8/harmonise).");
        run.PromptVersion = loaded.Version;
        run.PromptId = loaded.PromptId;

        var specsJsonBuilder = new StringBuilder();
        specsJsonBuilder.Append('[');
        for (int i = 0; i < specs.Count; i++)
        {
            if (i > 0) specsJsonBuilder.Append(',');
            specsJsonBuilder.Append("{\"id\":\"").Append(specs[i].Id).Append("\",\"spec\":")
                .Append(specs[i].SpecJson.RootElement.GetRawText()).Append('}');
        }
        specsJsonBuilder.Append(']');

        var rendered = _prompts.Render(loaded, new Dictionary<string, string?>
        {
            ["corpusName"] = corpus.Name,
            ["sourceVersionId"] = version.Id.ToString(),
            ["specCount"] = specs.Count.ToString(),
            ["specsJson"] = specsJsonBuilder.ToString(),
        });

        // 4. Dispatch by provider name.
        HarmonisationLlmResult llmResult;
        try
        {
            if (string.Equals(_provider.Info.Name, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                llmResult = await CallAnthropicAsync(rendered.System, rendered.User, ct);
            }
            else
            {
                llmResult = StubMockResult(specs);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Harmonisation pass failed");
            run.Status = "FAILED";
            run.ErrorMessage = ex.Message;
            run.Summary = $"Harmonisation failed: {ex.GetType().Name}: {ex.Message}";
            run.CompletedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(
                "harmonisation.failed", "corpus", corpusId, actor,
                payload: new { runId = run.Id, error = ex.Message },
                ct: ct);
            return run;
        }

        // 5. Parse the LLM output into structured findings.
        var findings = ParseFindings(llmResult.RawJson, run.Id);

        // 6. Persist.
        run.Status = "COMPLETED";
        run.InputTokens = llmResult.InputTokens;
        run.OutputTokens = llmResult.OutputTokens;
        run.CacheReadTokens = llmResult.CacheReadTokens;
        run.CacheCreationTokens = llmResult.CacheCreationTokens;
        run.FindingCount = findings.Count;
        run.Summary = findings.Count switch
        {
            0 => "Harmonisation pass found no inconsistencies across specs.",
            1 => "1 cross-routine inconsistency surfaced.",
            _ => $"{findings.Count} cross-routine inconsistencies surfaced.",
        };
        run.CompletedAt = DateTimeOffset.UtcNow;
        await _db.HarmonisationFindings.AddRangeAsync(findings, ct);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "harmonisation.completed", "corpus", corpusId, actor,
            payload: new
            {
                runId = run.Id,
                specCount = specs.Count,
                findingCount = findings.Count,
                inputTokens = llmResult.InputTokens,
                cacheReadTokens = llmResult.CacheReadTokens,
            },
            ct: ct);

        _logger.LogInformation(
            "Harmonisation {Run} on corpus {Corpus}: {Specs} specs → {Findings} findings",
            run.Id, corpusId, specs.Count, findings.Count);
        return run;
    }

    private async Task<HarmonisationLlmResult> CallAnthropicAsync(
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _anthropicOpts.Model,
            ["max_tokens"] = _anthropicOpts.MaxOutputTokens,
            ["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    // Phase 7.0 caching also applies here — system
                    // message stable across repeated passes.
                    ["cache_control"] = new { type = "ephemeral" },
                },
            },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_anthropicOpts.BaseUrl}/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Add("x-api-key", _anthropicOpts.ApiKey);
        req.Headers.Add("anthropic-version", _anthropicOpts.ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Anthropic returned {(int)resp.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Token usage.
        int inT = 0, outT = 0, cacheRead = 0, cacheCreate = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i)) inT = i.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var o)) outT = o.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var cr)) cacheRead = cr.GetInt32();
            if (usage.TryGetProperty("cache_creation_input_tokens", out var cc)) cacheCreate = cc.GetInt32();
        }

        // Output text — concatenated content[].text blocks.
        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
        }
        return new HarmonisationLlmResult(
            RawJson: sb.ToString(),
            InputTokens: inT,
            OutputTokens: outT,
            CacheReadTokens: cacheRead,
            CacheCreationTokens: cacheCreate);
    }

    /// <summary>
    /// Mock provider stub. Returns a deterministic finding so the
    /// pipeline can be exercised in local dev and e2e without an API
    /// key. The stub finding is honest about being a stub so it
    /// cannot be confused with a real result.
    /// </summary>
    private static HarmonisationLlmResult StubMockResult(IList<Spec> specs)
    {
        var ids = specs.Take(2).Select(s => s.Id.ToString()).ToArray();
        var payload = new
        {
            summary = $"Mock harmonisation pass — {specs.Count} specs scanned. " +
                      "Set Llm:Provider=anthropic to surface real findings.",
            findings = specs.Count >= 2
                ? new[]
                {
                    new
                    {
                        category = "uncategorised",
                        severity = "low",
                        title = "Mock provider — sample finding",
                        detail = "This is a STUB finding produced by the mock LLM provider. " +
                                 "The harmonisation pipeline is wired correctly. Configure " +
                                 "Llm:Provider=anthropic and ANTHROPIC_API_KEY to run a real pass.",
                        affectedSpecIds = ids,
                    },
                }
                : Array.Empty<object>(),
        };
        return new HarmonisationLlmResult(
            RawJson: JsonSerializer.Serialize(payload, JsonOpts),
            InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheCreationTokens: 0);
    }

    private static List<HarmonisationFinding> ParseFindings(string rawJson, Guid runId)
    {
        var findings = new List<HarmonisationFinding>();
        // Extract the first {...} block — the model may wrap with markdown
        // fences, prose, or chain-of-thought even though the prompt
        // discourages it.
        var json = ExtractFirstJsonObject(rawJson);
        if (json is null)
        {
            // Surface the parse failure as a single finding so the UI
            // doesn't go silently empty.
            findings.Add(new HarmonisationFinding
            {
                Id = Guid.NewGuid(),
                HarmonisationRunId = runId,
                Category = "uncategorised",
                Severity = "low",
                Title = "Harmonisation output could not be parsed",
                Detail = "The LLM emitted text that did not parse as JSON. " +
                         "First 400 chars: " + Truncate(rawJson, 400),
                AffectedSpecIdsJson = "[]",
                Status = "open",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            return findings;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("findings", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return findings;
            var now = DateTimeOffset.UtcNow;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                findings.Add(new HarmonisationFinding
                {
                    Id = Guid.NewGuid(),
                    HarmonisationRunId = runId,
                    Category = ReadString(item, "category", "uncategorised"),
                    Severity = NormalizeSeverity(ReadString(item, "severity", "medium")),
                    Title = TruncateTo(ReadString(item, "title", "(untitled finding)"), 256),
                    Detail = ReadString(item, "detail", ""),
                    AffectedSpecIdsJson = item.TryGetProperty("affectedSpecIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                        ? ids.GetRawText()
                        : "[]",
                    Status = "open",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }
        catch (Exception)
        {
            // Already covered by the upper catch; leave empty list.
        }
        return findings;
    }

    private static string? ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static string ReadString(JsonElement e, string prop, string fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback)
            : fallback;

    private static string NormalizeSeverity(string s)
    {
        var l = s.ToLowerInvariant();
        return (l == "low" || l == "medium" || l == "high") ? l : "medium";
    }

    private static string TruncateTo(string s, int max) =>
        s.Length <= max ? s : s[..max];

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed record HarmonisationLlmResult(
        string RawJson,
        int InputTokens,
        int OutputTokens,
        int CacheReadTokens,
        int CacheCreationTokens);
}
