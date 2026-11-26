using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Copilot;
using Astra.Api.Llm;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.Extensions.Options;

namespace Astra.Api.Validation.Quality;

/// <summary>What the reviewer agent produced, after verification.</summary>
public sealed record QualityReviewResult(
    string Status,                       // reviewed | skipped | failed | timed_out
    IReadOnlyList<QualityFinding> Findings,
    int Proposed,
    int Dropped,
    string? Model,
    Guid? LlmCallId,
    decimal CostUsd,
    string? Note);

/// <summary>
/// The reviewer agent of the QUALITY gate (Q2): Sonnet reads the generated package beside its signed spec and reports
/// what rules cannot see — missing authorisation, a signed claim the code drops, money rounding, personal data in
/// logs, logic in the wrong layer. Its findings are advisory (the verdict stays with <see cref="QualityRules"/>), and
/// none reaches a reviewer unverified: <see cref="QualityReviewVerifier"/> keeps a finding only when the code it
/// quotes is really in the named file. One forced tool call, rate-limited, priced and recorded as an
/// <see cref="LlmCall"/>. Without the Anthropic provider (mock, e2e) it reports "skipped" and the rules still run.
/// </summary>
public sealed class QualityReviewer
{
    private const string ToolName = "report_findings";
    private const int MaxPackageChars = 140_000;
    private const int MaxSpecChars = 30_000;

    private readonly IHttpClientFactory _httpFactory;
    private readonly AnthropicOptions _anthropic;
    private readonly AnthropicRateLimiter _limiter;
    private readonly PromptLibrary _prompts;
    private readonly ICopilotBrain _brain;
    private readonly ILogger<QualityReviewer> _log;

    public QualityReviewer(IHttpClientFactory httpFactory, IOptions<AnthropicOptions> anthropic, AnthropicRateLimiter limiter,
        PromptLibrary prompts, ICopilotBrain brain, ILogger<QualityReviewer> log)
    {
        _httpFactory = httpFactory;
        _anthropic = anthropic.Value;
        _limiter = limiter;
        _prompts = prompts;
        _brain = brain;
        _log = log;
    }

    public bool Available => _brain.Name == "anthropic" && !string.IsNullOrWhiteSpace(_anthropic.ApiKey)
                             && _prompts.GetLatest("common", "quality", "code-review") is not null;

    public async Task<QualityReviewResult> ReviewAsync(
        AppDbContext db, Scaffold scaffold, string routineName, string? specJson,
        IReadOnlyList<(string Path, string Content)> files, CancellationToken ct)
    {
        var prompt = _prompts.GetLatest("common", "quality", "code-review");
        if (!Available || prompt is null)
            return new("skipped", [], 0, 0, null, null, 0m, "The AI reviewer needs the Anthropic provider; the rule checks ran.");

        var rendered = _prompts.Render(prompt, new Dictionary<string, string?>
        {
            ["targetStack"] = scaffold.TargetPlatform,
            ["routineName"] = routineName,
            ["spec"] = Trim(specJson ?? "(no signed spec)", MaxSpecChars),
            ["files"] = RenderFiles(files, MaxPackageChars),
        });
        var severity = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "critical", "major", "minor" } };
        var category = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "security", "spec", "correctness", "design" } };
        var str = new Dictionary<string, object?> { ["type"] = "string" };
        var body = new Dictionary<string, object?>
        {
            ["model"] = _anthropic.Model,
            ["max_tokens"] = 8192,
            ["temperature"] = 0,
            ["system"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = rendered.System, ["cache_control"] = new { type = "ephemeral" } } },
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = ToolName,
                    ["description"] = "Report the review findings (an empty list when there are none).",
                    ["input_schema"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "findings" },
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["findings"] = new Dictionary<string, object?>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "object",
                                    ["required"] = new[] { "file", "line", "severity", "category", "title", "quote", "why", "fix" },
                                    ["properties"] = new Dictionary<string, object?>
                                    {
                                        ["file"] = str, ["line"] = new Dictionary<string, object?> { ["type"] = "integer" },
                                        ["severity"] = severity, ["category"] = category,
                                        ["title"] = str, ["quote"] = str, ["why"] = str, ["fix"] = str,
                                        ["specClaimId"] = str,
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
                _limiter, cacheKey: $"code-review:{prompt.Version}", _log, ct, maxAttempts: 2);
            responseBody = response.Body;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new("timed_out", [], 0, 0, _anthropic.Model, null, 0m, "The AI review did not finish in time; the rule checks ran.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI code review failed for scaffold {Scaffold}", scaffold.Id);
            return new("failed", [], 0, 0, _anthropic.Model, null, 0m, $"The AI review could not run: {ex.Message}");
        }
        sw.Stop();

        using var doc = JsonDocument.Parse(responseBody);
        var input = AnthropicHttp.ReadToolInput(doc.RootElement) ?? "{}";
        var usage = AnthropicHttp.ReadUsage(doc.RootElement);
        var modelUsed = usage.Model ?? _anthropic.Model;
        var cost = ModelPricing.Estimate("anthropic", modelUsed, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens);
        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = "anthropic",
            Model = modelUsed,
            PromptTemplateId = prompt.PromptId,
            PromptTemplateVersion = prompt.Version,
            ProviderConfigVersion = "code-review",
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens,
            CacheCreationTokens = usage.CacheCreationTokens,
            LatencyMs = sw.ElapsedMilliseconds,
            CostUsd = cost,
            Status = "success",
            CalledAt = DateTimeOffset.UtcNow,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        var proposed = QualityReviewVerifier.ParseProposals(input);
        var claimIds = QualityReviewVerifier.ClaimIds(specJson);
        var (kept, dropped) = QualityReviewVerifier.Verify(proposed, files, claimIds);
        return new("reviewed", kept, proposed.Count, dropped, modelUsed, call.Id, cost,
            dropped > 0 ? $"{dropped} proposed finding(s) were dropped because the code they quote is not in the package." : null);
    }

    /// <summary>The package as the reviewer sees it: main code first, tests after, each line numbered, within a budget.</summary>
    internal static string RenderFiles(IReadOnlyList<(string Path, string Content)> files, int budget)
    {
        var sb = new StringBuilder();
        var ordered = files
            .Where(f => !f.Path.EndsWith("package-lock.json", StringComparison.Ordinal) && !f.Path.Contains("/node_modules/"))
            .OrderBy(f => QualityRules.IsTestPath(f.Path) ? 1 : 0).ThenBy(f => f.Path, StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var (path, content) in ordered)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var block = new StringBuilder($"### {path}\n```\n");
            for (var i = 0; i < lines.Length; i++) block.Append(i + 1).Append("| ").Append(lines[i]).Append('\n');
            block.Append("```\n\n");
            if (sb.Length + block.Length > budget) { skipped.Add(path); continue; }
            sb.Append(block);
        }
        if (skipped.Count > 0) sb.Append($"(Not shown for size: {string.Join(", ", skipped)})\n");
        return sb.ToString();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "\n… (truncated)";
}

/// <summary>
/// The reviewer agent never grades itself: a proposed finding survives only when the file it names is in the package
/// and the code it quotes is really there (whitespace aside). The line is taken from where the quote is found, not
/// from what the model said, and a claim id the signed spec does not have is removed. Pure and deterministic.
/// </summary>
public static class QualityReviewVerifier
{
    public sealed record Proposal(string File, int? Line, string Severity, string Category, string Title, string Quote, string Why, string Fix, string? SpecClaimId);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LineNumberPrefix = new(@"^\s*\d+\|\s?", RegexOptions.Compiled);
    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "critical", "major", "minor" };

    public static IReadOnlyList<Proposal> ParseProposals(string toolInputJson)
    {
        var list = new List<Proposal>();
        try
        {
            using var doc = JsonDocument.Parse(toolInputJson);
            if (!doc.RootElement.TryGetProperty("findings", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var f in arr.EnumerateArray())
            {
                string S(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                int? line = f.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n) ? n : null;
                list.Add(new(S("file"), line, S("severity"), S("category"), S("title"), S("quote"), S("why"), S("fix"),
                    S("specClaimId") is { Length: > 0 } c ? c : null));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Every <c>"id"</c> value in the signed spec — the claims a finding may name.</summary>
    public static IReadOnlySet<string> ClaimIds(string? specJson)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(specJson)) return ids;
        try
        {
            using var doc = JsonDocument.Parse(specJson);
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.NameEquals("id") && p.Value.ValueKind == JsonValueKind.String) ids.Add(p.Value.GetString()!);
                        Walk(p.Value);
                    }
                else if (e.ValueKind == JsonValueKind.Array)
                    foreach (var x in e.EnumerateArray()) Walk(x);
            }
            Walk(doc.RootElement);
        }
        catch (JsonException) { }
        return ids;
    }

    public static (IReadOnlyList<QualityFinding> Kept, int Dropped) Verify(
        IReadOnlyList<Proposal> proposals, IReadOnlyList<(string Path, string Content)> files, IReadOnlySet<string> claimIds)
    {
        var byPath = files.GroupBy(f => Norm(f.Path)).ToDictionary(g => g.Key, g => g.First().Content);
        var kept = new List<QualityFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;
        foreach (var p in proposals)
        {
            var key = Norm(p.File);
            if (!byPath.TryGetValue(key, out var content))
            {
                // the model sometimes drops a leading folder: accept a unique suffix match
                var matches = byPath.Keys.Where(k => k.EndsWith("/" + key, StringComparison.Ordinal)).ToList();
                if (matches.Count != 1) { dropped++; continue; }
                key = matches[0];
                content = byPath[key];
            }
            var line = Locate(content, p.Quote, p.Line);
            if (line is null || !Severities.Contains(p.Severity) || string.IsNullOrWhiteSpace(p.Title)) { dropped++; continue; }
            if (!seen.Add($"{key}|{line}|{p.Title}")) { dropped++; continue; }
            var claim = p.SpecClaimId is not null && claimIds.Contains(p.SpecClaimId) ? p.SpecClaimId : null;
            var category = p.Category is "security" ? "security" : p.Category is "design" ? "maintainability" : "review";
            kept.Add(new QualityFinding(
                $"AI-{(p.Category is { Length: > 0 } c ? c.ToUpperInvariant() : "REVIEW")}", category, p.Severity, key, line,
                p.Title.Trim() + (claim is not null ? $" (claim {claim})" : ""), p.Why.Trim(), p.Fix.Trim(), Source: "ai", SpecClaimId: claim));
        }
        return (kept, dropped);
    }

    /// <summary>The 1-based line where the quote starts, nearest the stated line; null when the quote is not in the file.</summary>
    internal static int? Locate(string content, string quote, int? statedLine)
    {
        var quoteLines = quote.Replace("\r\n", "\n").Split('\n')
            .Select(l => Squash(LineNumberPrefix.Replace(l, ""))).Where(l => l.Length > 0).ToList();
        if (quoteLines.Count == 0 || quoteLines.Sum(l => l.Count(c => !char.IsWhiteSpace(c))) < 6) return null;
        var lines = content.Replace("\r\n", "\n").Split('\n').Select(Squash).ToList();
        var hits = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var j = i; var ok = true;
            foreach (var q in quoteLines)
            {
                while (j < lines.Count && lines[j].Length == 0) j++;
                if (j >= lines.Count || !lines[j].Contains(q, StringComparison.Ordinal)) { ok = false; break; }
                j++;
            }
            if (ok) hits.Add(i + 1);
        }
        if (hits.Count == 0) return null;
        return statedLine is { } s ? hits.OrderBy(h => Math.Abs(h - s)).First() : hits[0];
    }

    private static string Squash(string s) => Spaces.Replace(s.Trim(), " ");
    private static string Norm(string p) => p.Replace('\\', '/').TrimStart('.', '/');
}
