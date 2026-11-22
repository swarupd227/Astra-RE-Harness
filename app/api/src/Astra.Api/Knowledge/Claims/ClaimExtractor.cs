using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Docs;
using Microsoft.Extensions.Options;

namespace Astra.Api.Knowledge;

/// <param name="Claims">What was kept.</param>
/// <param name="Dropped">What was cut and why (a name that is not in the evidence it came from, an evidence id that was not shown).</param>
public sealed record ClaimExtraction(
    IReadOnlyList<Claim> Claims,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<DocCallRecord> Calls,
    IReadOnlyList<string> Notes);

/// <summary>
/// Turns what people said or wrote into atomic claims, one forced-tool-use call per batch of narrative evidence at
/// temperature 0. The model reads; a program decides what stands: a claim must come from an item that was shown, and
/// every name in it must appear verbatim in that item, so an invented job name cannot become a claim. Goes through
/// <see cref="IDocWriter"/> like every knowledge call.
/// </summary>
public sealed class ClaimExtractor
{
    public const string PromptId = "knowledge-claim-extractor";
    public const string PromptVersion = "v1.0";
    public const string ToolName = "extract_claims";
    public const int BatchSize = 6;

    public static readonly object ToolSchema = new
    {
        type = "object",
        properties = new
        {
            claims = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        text = new { type = "string", description = "One fact, one sentence. Names, codes and paths exactly as said, in backticks." },
                        evidence = new { type = "string", description = "The id of the one evidence item it came from." },
                        at = new { type = "string", description = "Time stamp of the turn or the ticket number, if the item gives one." },
                        assertions = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    subject = new { type = "string" },
                                    predicate = new { type = "string" },
                                    @object = new { type = "string" },
                                    value = new { type = "string" },
                                },
                                required = new[] { "subject", "predicate" },
                            },
                        },
                    },
                    required = new[] { "text", "evidence" },
                },
            },
        },
        required = new[] { "claims" },
    };

    private readonly IDocWriter _writer;
    private readonly DocsOptions _docs;
    private readonly KnowledgeOptions _options;
    private readonly StatementPrompts _prompts;
    private readonly IStatementFixtures _fixtures;

    public ClaimExtractor(IDocWriter writer, IOptions<DocsOptions> docs, IOptions<KnowledgeOptions> options, StatementPrompts prompts, IStatementFixtures fixtures)
    {
        _writer = writer;
        _docs = docs.Value;
        _options = options.Value;
        _prompts = prompts;
        _fixtures = fixtures;
    }

    public async Task<ClaimExtraction> ExtractAsync(IReadOnlyList<GateChunk> evidence, CancellationToken ct)
    {
        var claims = new List<Claim>();
        var dropped = new List<string>();
        var calls = new List<DocCallRecord>();
        var notes = new List<string>();

        var batchNo = 0;
        foreach (var batch in evidence.Chunk(BatchSize))
        {
            batchNo++;
            var replayKey = $"claims:extract:{batchNo}";
            var request = new DocWriteRequest(
                PromptId, PromptVersion,
                new[] { _prompts.Claims },
                BuildUserMessage(batch),
                ToolName,
                "Report the claims in this evidence, each from one item.",
                ToolSchema,
                string.IsNullOrWhiteSpace(_options.ClaimsModel) ? _docs.SonnetModel : _options.ClaimsModel!,
                _options.WriterMaxTokens,
                "knowledge-claims",
                () => _fixtures.Find(replayKey) ?? MockAnswer(batch),
                ReplayKey: replayKey,
                Temperature: 0);

            DocWriteResult result;
            try
            {
                result = await _writer.WriteAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reported with the provider's reason; the other batches still count.
                notes.Add($"Batch {batchNo} could not be read for claims ({ex.Message}).");
                continue;
            }
            calls.Add(new DocCallRecord(request, result));

            var parsed = Parse(result.ToolInputJson, batch);
            claims.AddRange(parsed.Claims.Where(c => !claims.Any(k => k.Id == c.Id)));
            dropped.AddRange(parsed.Dropped);
            notes.AddRange(parsed.Notes.Select(n => $"Batch {batchNo}: {n}"));
        }
        return new ClaimExtraction(claims, dropped, calls, notes);
    }

    internal string BuildUserMessage(IReadOnlyList<GateChunk> batch)
    {
        var sb = new StringBuilder("Evidence (cite by the id in square brackets):\n");
        foreach (var c in batch)
        {
            sb.Append("\n[").Append(c.ShortId).Append("] ").Append(c.Kind);
            if (!string.IsNullOrWhiteSpace(c.Source)) sb.Append(" · ").Append(c.Source);
            else if (!string.IsNullOrWhiteSpace(c.Label)) sb.Append(" · ").Append(c.Label);
            var text = c.Text.Length <= _options.MaxChunkChars ? c.Text : c.Text[.._options.MaxChunkChars] + "…";
            sb.Append('\n').Append(text).Append("\n---\n");
        }
        return sb.ToString();
    }

    /// <summary>Reads an answer forgivingly and applies the rules that need no model.</summary>
    internal static (IReadOnlyList<Claim> Claims, IReadOnlyList<string> Dropped, IReadOnlyList<string> Notes) Parse(string? json, IReadOnlyList<GateChunk> shown)
    {
        var claims = new List<Claim>();
        var dropped = new List<string>();
        var notes = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) { notes.Add("The answer was empty."); return (claims, dropped, notes); }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { notes.Add("The answer could not be read."); return (claims, dropped, notes); }

        using (doc)
        {
            if (!TryGet(doc.RootElement, "claims", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                notes.Add("The answer had no list of claims.");
                return (claims, dropped, notes);
            }

            var byId = shown.ToDictionary(c => c.ShortId.ToUpperInvariant(), c => c, StringComparer.Ordinal);
            foreach (var e in list.EnumerateArray())
            {
                var text = Str(e, "text")?.Trim() ?? "";
                if (text.Length == 0) continue;

                var rawId = Str(e, "evidence")?.Trim().Trim('[', ']').ToUpperInvariant() ?? "";
                if (!byId.TryGetValue(rawId, out var chunk))
                {
                    dropped.Add($"\"{Clip(text)}\" — it names evidence that was not shown.");
                    continue;
                }

                var texts = new[] { IdentifierExtractor.Normalise(chunk.Text + "\n" + chunk.Label) };
                var missing = IdentifierExtractor.Extract(text).Where(i => !IdentifierExtractor.IsIn(i, texts)).Select(i => i.Text).ToList();
                if (missing.Count > 0)
                {
                    dropped.Add($"\"{Clip(text)}\" — {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not in the evidence it came from.");
                    continue;
                }

                var assertions = new List<ClaimAssertion>();
                if (TryGet(e, "assertions", out var al) && al.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in al.EnumerateArray())
                    {
                        var subject = Str(a, "subject")?.Trim() ?? "";
                        var predicate = (Str(a, "predicate") ?? "").Trim().ToLowerInvariant();
                        if (subject.Length == 0 || !ClaimPredicates.IsKnown(predicate))
                        {
                            notes.Add($"An assertion with predicate '{predicate}' was left out ({Clip(text)}).");
                            continue;
                        }
                        assertions.Add(new ClaimAssertion(subject, predicate, NullIfEmpty(Str(a, "object")), NullIfEmpty(Str(a, "value"))));
                    }
                }

                claims.Add(new Claim(ClaimIds.Stable(text, chunk.ShortId), text, chunk.ShortId, NullIfEmpty(Str(e, "at")), assertions));
            }
        }
        return (claims, dropped, notes);
    }

    /// <summary>The offline answer without a fixture: each line of each item that names something becomes a claim, with nothing to check, so every claim comes back Unverified.</summary>
    internal static string MockAnswer(IReadOnlyList<GateChunk> batch)
    {
        var claims = new List<object>();
        foreach (var c in batch)
        {
            var n = 0;
            foreach (var raw in Regex.Split(c.Text, @"(?<=[.!?])\s+|\n+"))
            {
                // A ticket or record reads "column: value"; the claim is the value, not the column name.
                var line = TicketClusterer.ValuesOnly(raw.Trim()).Trim();
                if (line.Length < 20 || IdentifierExtractor.Extract(line).Count == 0) continue;
                claims.Add(new { text = line.Length <= 240 ? line : line[..239] + "…", evidence = c.ShortId });
                if (++n == 6) break;
            }
        }
        return JsonSerializer.Serialize(new { claims });
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        TryGet(e, name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.ToString() : null) : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Clip(string s) => s.Length <= 80 ? s : s[..79] + "…";
}
