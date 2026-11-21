using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Astra.Api.Evidence;

namespace Astra.Api.Knowledge;

public sealed record StatementParseResult(StatementDocument? Document, IReadOnlyList<string> Problems)
{
    public bool Ok => Document is not null;
}

/// <summary>
/// Reads and writes <c>statements/v1</c>. Reading is forgiving about what a model is likely to get slightly
/// wrong (property case, a citation written as <c>[SQL-3FA9C01E]</c>, a missing id) and strict about what makes
/// a statement unusable (no text); everything it had to fix or drop is returned as a plain sentence.
/// </summary>
public static class StatementJson
{
    public const int MaxItems = 400;
    public const int MaxTextChars = 1200;

    private static readonly JsonSerializerOptions Write = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string ToJson(StatementDocument doc) => JsonSerializer.Serialize(new
    {
        schema = StatementSchema.Id,
        kind = doc.Kind,
        title = doc.Title,
        confidence = doc.Confidence,
        sections = doc.Sections,
        conflicts = doc.Conflicts,
        openQuestions = doc.OpenQuestions,
    }, Write);

    /// <param name="defaultSlotKey">The slot an item without one belongs to (the slot the writer was asked about).</param>
    public static StatementParseResult Parse(string json, string kind, string defaultSlotKey)
    {
        var problems = new List<string>();
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new StatementParseResult(null, new[] { $"Not valid JSON ({ex.Message})." });
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new StatementParseResult(null, new[] { "The document must be a JSON object." });

            var title = Str(root, "title") ?? "";
            var sections = new List<StatementSection>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;

            if (Prop(root, "sections") is { ValueKind: JsonValueKind.Array } sectionArray)
            {
                foreach (var s in sectionArray.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) { problems.Add("A section that is not an object was skipped."); continue; }
                    var heading = (Str(s, "heading") ?? Str(s, "title") ?? "").Trim();
                    var items = new List<StatementItem>();
                    if (Prop(s, "items") is { ValueKind: JsonValueKind.Array } itemArray)
                    {
                        foreach (var i in itemArray.EnumerateArray())
                        {
                            if (++count > MaxItems) { problems.Add($"Only the first {MaxItems} statements were read."); break; }
                            var item = ReadItem(i, defaultSlotKey, problems);
                            if (item is null) continue;
                            if (!seenIds.Add(item.Id)) { problems.Add($"A second statement with the same text and evidence ('{Clip(item.Text, 60)}') was skipped."); continue; }
                            items.Add(item);
                        }
                    }
                    if (items.Count > 0) sections.Add(new StatementSection(heading, items));
                }
            }

            var conflicts = new List<StatementConflict>();
            if (Prop(root, "conflicts") is { ValueKind: JsonValueKind.Array } conflictArray)
            {
                foreach (var c in conflictArray.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object) continue;
                    var itemId = Str(c, "itemId") ?? Str(c, "item") ?? "";
                    if (itemId.Length == 0) { problems.Add("A conflict that names no statement was skipped."); continue; }
                    conflicts.Add(new StatementConflict(itemId, Citations(c, "evidenceIds"), (Str(c, "summary") ?? "").Trim()));
                }
            }

            var questions = new List<OpenQuestion>();
            if (Prop(root, "openQuestions") is { ValueKind: JsonValueKind.Array } questionArray)
            {
                foreach (var q in questionArray.EnumerateArray())
                {
                    var text = q.ValueKind == JsonValueKind.String ? q.GetString() : q.ValueKind == JsonValueKind.Object ? Str(q, "text") : null;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var slot = q.ValueKind == JsonValueKind.Object ? Str(q, "slotKey") ?? defaultSlotKey : defaultSlotKey;
                    var cites = q.ValueKind == JsonValueKind.Object ? Citations(q, "citations") : Array.Empty<string>();
                    questions.Add(new OpenQuestion(Str(q, "id") is { Length: > 0 } qid ? qid : QuestionId(slot, text!), text!.Trim(), slot, cites.Count == 0 ? null : cites));
                }
            }

            double? confidence = Prop(root, "confidence") is { ValueKind: JsonValueKind.Number } c0 && c0.TryGetDouble(out var cv) ? Math.Clamp(cv, 0, 1) : null;
            return new StatementParseResult(new StatementDocument(kind, title, sections, conflicts, questions, confidence), problems);
        }
    }

    private static StatementItem? ReadItem(JsonElement e, string defaultSlotKey, List<string> problems)
    {
        if (e.ValueKind != JsonValueKind.Object) { problems.Add("A statement that is not an object was skipped."); return null; }
        var text = Regex.Replace((Str(e, "text") ?? Str(e, "statement") ?? "").Trim(), @"\s+", " ");
        if (text.Length == 0) { problems.Add("A statement with no text was skipped."); return null; }
        if (text.Length > MaxTextChars)
        {
            problems.Add($"A statement was cut to {MaxTextChars:N0} characters.");
            text = text[..MaxTextChars];
        }

        var slot = (Str(e, "slotKey") ?? "").Trim();
        if (slot.Length == 0) slot = defaultSlotKey;
        var cites = Citations(e, "citations");
        var id = Str(e, "id") is { Length: > 0 } given ? given.Trim() : StatementIds.Stable(slot, text, cites);
        var status = Str(e, "status");
        var support = Str(e, "support");
        return new StatementItem(
            id, slot, text, cites,
            StatementStatus.IsKnown(status) ? status : null,
            SupportVerdict.IsKnown(support) ? support : null);
    }

    /// <summary>Citations as the evidence's own short ids: <c>[sql-3FA9C01E]</c> becomes <c>SQL-3fa9c01e</c>; what is not shaped like one is kept as written, for the gate to refuse.</summary>
    private static IReadOnlyList<string> Citations(JsonElement parent, string name)
    {
        var list = new List<string>();
        if (Prop(parent, name) is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var c in arr.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(c.GetString())) continue;
            var raw = c.GetString()!.Trim();
            var id = EvidenceIds.Normalise(raw) ?? raw;
            if (!list.Contains(id, StringComparer.Ordinal)) list.Add(id);
        }
        return list;
    }

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static string QuestionId(string slotKey, string text) =>
        "oq-" + Hash(slotKey + "\n" + Normalise(text))[..10];

    // A full stop inside a word or a path is kept (it is part of the name), one that ends the sentence is not.
    internal static string Normalise(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}_:/.\\\-]+", " ").Trim().TrimEnd('.', ':');

    internal static string Hash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

public static class StatementIds
{
    /// <summary>
    /// An id that is the same for the same slot, the same words and the same first piece of evidence, so a
    /// statement keeps its identity when a document is written again (an edit or a review attached to it still
    /// finds it), and changes when what it says or what it rests on changes.
    /// </summary>
    public static string Stable(string slotKey, string text, IReadOnlyList<string> citations)
    {
        var primary = citations.Count > 0 ? citations.OrderBy(c => c, StringComparer.Ordinal).First() : "";
        return "st-" + StatementJson.Hash(slotKey + "\n" + StatementJson.Normalise(text) + "\n" + primary)[..10];
    }
}
