using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// Phase 6.0 — Runs the extract pipeline against one golden-dataset
/// entry's snippet and scores claim coverage. The score is a fraction:
/// <c>matched / total</c> where total is the number of expected claims
/// authored on the entry and matched is the number of those whose regex
/// pattern is satisfied by any claim of the same kind in the extract
/// output. A <see cref="GoldenDatasetRun"/> row is persisted per run so
/// the dashboard can chart score over time per (prompt, version).
///
/// The scorer is deliberately strict on KIND and permissive on TEXT:
/// an invariant pattern only matches invariant output claims (cross-kind
/// matches don't count). The text comparison is a regex match over the
/// claim's text fields concatenated (claim/description/behavior/question/
/// citations). Regex is the right compromise — robust to phrasing
/// variation, deterministic, cheap to author.
/// </summary>
public sealed class GoldenDatasetScorer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppDbContext _db;
    private readonly ILlmProvider _provider;
    private readonly PromptLibrary _prompts;
    private readonly ILogger<GoldenDatasetScorer> _log;

    public GoldenDatasetScorer(
        AppDbContext db,
        ILlmProvider provider,
        PromptLibrary prompts,
        ILogger<GoldenDatasetScorer> log)
    {
        _db = db;
        _provider = provider;
        _prompts = prompts;
        _log = log;
    }

    public sealed record ScoreOutcome(
        GoldenDatasetRun Run,
        IReadOnlyList<ClaimMatch> Detail);

    public sealed record ClaimMatch(
        string ExpectedClaimId,
        string Kind,
        string Pattern,
        bool Matched,
        string? MatchedAgainst);

    public async Task<ScoreOutcome> ScoreAsync(
        Guid entryDbId,
        DevPersonaContext? actor,
        CancellationToken ct = default)
    {
        var entry = await _db.GoldenDatasetEntries.FirstOrDefaultAsync(e => e.Id == entryDbId, ct)
            ?? throw new InvalidOperationException($"Golden dataset entry {entryDbId} not found.");

        // 1. Resolve the active extract prompt for this schema. Claim quality
        //    is a property of the EXTRACT pass, which is target-stack agnostic —
        //    but the prompt only EXISTS under specific target stacks (the
        //    vbnet/csharp extract prompts live under dotnet10, not dotnet8), so
        //    probe the known stacks and score against whichever one the schema
        //    actually ships an extract prompt for.
        string[] candidateStacks = { "dotnet8", "dotnet10", "java-spring" };
        var prompt = _prompts.GetLatest(entry.SchemaId, "dotnet8", "extract");
        var targetStack = "dotnet8";
        foreach (var ts in candidateStacks)
        {
            var p = _prompts.GetLatest(entry.SchemaId, ts, "extract");
            if (p is not null) { prompt = p; targetStack = ts; break; }
        }
        var promptId = prompt?.PromptId ?? $"{entry.SchemaId}-extract";
        var promptVersion = prompt?.Version ?? "v0";

        var startedAt = DateTimeOffset.UtcNow;
        var lineCount = entry.SourceContent.Count(c => c == '\n') + 1;

        // 2. Drive the provider with this entry's snippet inline.
        var request = new ExtractionRequest(
            SubroutineId: Guid.Empty, // pseudo — this run is not scaffold-bound
            SubroutineName: entry.EntryId,
            SourcePath: entry.SourcePath,
            SourceText: entry.SourceContent,
            LineCount: lineCount,
            PromptTemplateId: promptId,
            PromptTemplateVersion: promptVersion,
            SourceLanguage: entry.SchemaId,
            TargetStack: targetStack);

        string? finalSpecJson = null;
        await foreach (var ev in _provider.ExtractAsync(request, ct))
        {
            if (ev.Type == "__final__" && ev.Data is IDictionary<string, object?> dict)
            {
                if (dict.TryGetValue("specJson", out var specObj) && specObj is not null)
                {
                    // specJson can land as either a string or as an already-
                    // structured object depending on the provider.
                    finalSpecJson = specObj is string s
                        ? s
                        : JsonSerializer.Serialize(specObj, JsonOpts);
                }
                break;
            }
        }

        if (finalSpecJson is null)
        {
            throw new InvalidOperationException(
                "LLM provider did not emit a __final__ event with specJson — cannot score.");
        }

        // 3. Parse the entry's expected claims + the provider's output and
        //    do the kind-bucketed regex match.
        var expected = JsonSerializer.Deserialize<List<ExpectedClaim>>(entry.ExpectedClaimsJson, JsonOpts)
            ?? new List<ExpectedClaim>();

        using var specDoc = JsonDocument.Parse(finalSpecJson);
        var outputBuckets = BucketClaimsByKind(specDoc.RootElement);

        var detail = new List<ClaimMatch>();
        foreach (var exp in expected)
        {
            var regex = new Regex(exp.Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var bucket = outputBuckets.TryGetValue(exp.Kind, out var list) ? list : new List<string>();
            string? matchedAgainst = null;
            foreach (var text in bucket)
            {
                if (regex.IsMatch(text))
                {
                    matchedAgainst = text.Length > 240 ? text[..240] + "…" : text;
                    break;
                }
            }
            detail.Add(new ClaimMatch(exp.Id, exp.Kind, exp.Pattern, matchedAgainst is not null, matchedAgainst));
        }

        int total = detail.Count;
        int matched = detail.Count(d => d.Matched);
        double score = total == 0 ? 1.0 : (double)matched / total;

        // 4. Persist a run row.
        var run = new GoldenDatasetRun
        {
            Id = Guid.NewGuid(),
            EntryId = entry.Id,
            LlmCallId = null, // scorer bypasses the LlmCall persistence path
            PromptId = promptId,
            PromptVersion = promptVersion,
            ModelName = _provider.Info.Model,
            Matched = matched,
            Total = total,
            Score = score,
            DetailJson = JsonSerializer.Serialize(detail, JsonOpts),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            TriggeredBy = actor?.DisplayName,
        };
        _db.GoldenDatasetRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Golden dataset run for {EntryId}: {Matched}/{Total} = {Score:P0} (prompt {Prompt}@{Version})",
            entry.EntryId, matched, total, score, promptId, promptVersion);

        return new ScoreOutcome(run, detail);
    }

    /// <summary>
    /// Walk the spec/v1 output and bucket every claim's text-as-string by
    /// kind. Delegates to <see cref="ClaimKindBucketer"/> (Phase 12.0
    /// extracted this so the pattern-clustering pass could reuse the exact
    /// same kind taxonomy without a second copy to keep in sync).
    /// </summary>
    private static Dictionary<string, List<string>> BucketClaimsByKind(JsonElement root) =>
        ClaimKindBucketer.Bucket(root);

    private sealed class ExpectedClaim
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Pattern { get; set; } = "";
    }
}
