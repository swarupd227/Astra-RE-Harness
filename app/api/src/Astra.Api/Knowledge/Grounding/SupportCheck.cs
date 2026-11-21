using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Docs;

namespace Astra.Api.Knowledge;

public sealed record SupportEvidence(string Id, string Text);

/// <summary>One statement and the evidence it cites, for the check of whether the evidence says what the statement says.</summary>
public sealed record SupportClaim(string Id, string Statement, IReadOnlyList<SupportEvidence> Evidence);

/// <summary>
/// The fourth check of the grounding gate, made by a small model so the writer never grades its own work: for each
/// statement, does the evidence it cites say it (<c>yes</c>), say part of it (<c>partly</c>), or not say it
/// (<c>no</c>)? Claims are sent in batches (a document is a handful of calls, not one per sentence). This file
/// builds the requests and reads the answers; sending is the writer service's job. The mock answers by how many
/// of the statement's words are in the evidence — deterministic, and it exercises all three outcomes offline.
/// </summary>
public static class SupportCheck
{
    public const string PromptId = "knowledge-support-check";
    public const string PromptVersion = "v1.0";
    public const string ToolName = "report_support";
    public const int BatchSize = 20;
    public const int MaxEvidenceChars = 2500;
    public const int MaxTokens = 4096;

    public static readonly object ToolSchema = new
    {
        type = "object",
        properties = new
        {
            verdicts = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = new { type = "string", description = "The claim's id, exactly as given." },
                        verdict = new { type = "string", @enum = new[] { SupportVerdict.Yes, SupportVerdict.Partly, SupportVerdict.No } },
                        reason = new { type = "string", description = "One short sentence." },
                    },
                    required = new[] { "id", "verdict" },
                },
            },
        },
        required = new[] { "verdicts" },
    };

    /// <param name="replayBase">What names this run's calls for replay (<c>step:item</c>); the batch number is added.</param>
    public static IReadOnlyList<DocWriteRequest> BuildRequests(
        IReadOnlyList<SupportClaim> claims, string systemPrompt, string model, string replayBase, int batchSize = BatchSize)
    {
        var requests = new List<DocWriteRequest>();
        var n = 0;
        foreach (var batch in claims.Chunk(Math.Max(1, batchSize)))
        {
            n++;
            var payload = new
            {
                claims = batch.Select(c => new
                {
                    id = c.Id,
                    statement = c.Statement,
                    evidence = c.Evidence.Select(e => new { id = e.Id, text = Clip(e.Text, MaxEvidenceChars) }).ToList(),
                }).ToList(),
            };
            var captured = batch;
            requests.Add(new DocWriteRequest(
                PromptId, PromptVersion,
                new[] { systemPrompt },
                JsonSerializer.Serialize(payload),
                ToolName,
                "Report, for each claim, whether the evidence it cites says it.",
                ToolSchema,
                model,
                MaxTokens,
                "knowledge-support",
                () => MockPayload(captured),
                ReplayKey: $"{replayBase}:support:{n}",
                Temperature: 0));
        }
        return requests;
    }

    /// <summary>Claim id → verdict. A claim the answer does not mention, or mentions with something other than yes/partly/no, is absent: it was not checked.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string toolInputJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(toolInputJson);
            if (!doc.RootElement.TryGetProperty("verdicts", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
            foreach (var v in arr.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                var id = v.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                var verdict = v.TryGetProperty("verdict", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim().ToLowerInvariant() : null;
                if (!string.IsNullOrEmpty(id) && SupportVerdict.IsKnown(verdict)) result[id] = verdict!;
            }
        }
        catch (JsonException)
        {
            // An answer that is not JSON checks nothing.
        }
        return result;
    }

    // ── the offline answer ──────────────────────────────────────────────

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "this", "that", "with", "from", "have", "were", "been", "will", "when", "then", "than", "into", "each", "does", "they",
        "their", "there", "which", "about", "after", "before", "also", "only", "must", "should", "would", "could", "your", "what",
    };

    public static string MockPayload(IEnumerable<SupportClaim> claims) =>
        JsonSerializer.Serialize(new
        {
            verdicts = claims.Select(c => new { id = c.Id, verdict = MockVerdict(c), reason = "Offline check by shared words." }).ToList(),
        });

    /// <summary>Share of the statement's words (four letters or more, not filler) found in the cited evidence: 60% or more is yes, 30% or more partly, less is no.</summary>
    public static string MockVerdict(SupportClaim claim)
    {
        var words = Words(claim.Statement);
        if (words.Count == 0) return SupportVerdict.Partly;
        var evidence = string.Join(' ', claim.Evidence.Select(e => e.Text)).ToLowerInvariant();
        var share = (double)words.Count(w => evidence.Contains(w, StringComparison.Ordinal)) / words.Count;
        return share >= 0.6 ? SupportVerdict.Yes : share >= 0.3 ? SupportVerdict.Partly : SupportVerdict.No;
    }

    private static List<string> Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}_]{4,}").Select(m => m.Value).Where(w => !Stop.Contains(w)).Distinct().ToList();

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
