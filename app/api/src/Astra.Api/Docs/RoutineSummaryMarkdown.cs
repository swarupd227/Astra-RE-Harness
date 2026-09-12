using System.Text;
using System.Text.Json;

namespace Astra.Api.Docs;

/// <summary>
/// Renders a routine-summary payload as prose. Routine summaries stay
/// structured JSON — they are the base facts every rollup builds on — but
/// the page a reader sees is paragraphs, not a numbered table per list:
/// the summary, a source citation, run-in paragraphs for inputs, outputs
/// and side effects, and short bulleted lists for the two things a reader
/// scans for — preconditions and edge cases.
/// </summary>
public static class RoutineSummaryMarkdown
{
    public static string Render(string payloadJson, string routineName, string? path = null, int lineStart = 0, int lineEnd = 0)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        sb.Append("### ").Append(routineName).Append("\n\n");

        var summary = Str(root, "summary");
        if (summary.Length > 0)
            sb.Append(summary.Trim()).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(path) && lineStart > 0 && lineEnd >= lineStart)
            sb.Append("Source: ").Append(DocCitations.Format(path, lineStart, lineEnd)).Append("\n\n");

        AppendRunIn(sb, root, "inputs", "Inputs", emptyText: null);
        AppendRunIn(sb, root, "outputs", "Outputs", emptyText: null);
        AppendRunIn(sb, root, "sideEffects", "Side effects", emptyText: "None; the routine modifies nothing beyond its return value.");
        AppendBullets(sb, root, "preconditions", "#### Preconditions");
        AppendBullets(sb, root, "edgeCases", "#### Edge cases");
        return sb.ToString();
    }

    private static void AppendRunIn(StringBuilder sb, JsonElement root, string prop, string label, string? emptyText)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var items = Items(arr);
        if (items.Count == 0)
        {
            if (emptyText is null) return;
            sb.Append("**").Append(label).Append(".** ").Append(emptyText).Append("\n\n");
            return;
        }
        sb.Append("**").Append(label).Append(".** ").Append(JoinProse(items)).Append("\n\n");
    }

    private static void AppendBullets(StringBuilder sb, JsonElement root, string prop, string heading)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var items = Items(arr);
        if (items.Count == 0) return;
        sb.Append(heading).Append("\n\n");
        foreach (var item in items)
            sb.Append("- ").Append(item).Append('\n');
        sb.Append('\n');
    }

    /// <summary>"a; b; c." — fragments joined as one sentence.</summary>
    public static string JoinProse(IReadOnlyList<string> items)
    {
        var parts = items
            .Select(s => s.Trim().TrimEnd('.', ';').Trim())
            .Where(s => s.Length > 0)
            .ToList();
        return parts.Count == 0 ? "" : string.Join("; ", parts) + ".";
    }

    private static List<string> Items(JsonElement arr)
    {
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && s.Trim().Length > 0)
                list.Add(s.Trim().Replace("\r", "").Replace('\n', ' '));
        }
        return list;
    }

    private static string Str(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
