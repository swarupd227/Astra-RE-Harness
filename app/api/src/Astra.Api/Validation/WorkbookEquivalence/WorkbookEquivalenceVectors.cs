using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Office;

namespace Astra.Api.Validation.WorkbookEquivalence;

/// <summary>
/// The cases the workbook equivalence gate runs: the workbook exactly as saved, then one change at a time — another
/// allowed value of each choice input, each number doubled and halved within its range, a yes/no flipped — and each
/// selection list emptied and cut to its first marked item. Every case is stated both as the package's inputs (by
/// contract field) and as the cells to set in the workbook. Pure.
/// </summary>
public static class WorkbookEquivalenceVectors
{
    public const int MaxVectors = 30;

    public sealed record CellValue(string Sheet, string Cell, JsonNode? Value);

    public sealed record Vector(string Id, string Label, bool AsSaved, JsonObject Inputs, IReadOnlyList<CellValue> Cells);

    public static IReadOnlyList<Vector> Build(WorkbookContract c, IReadOnlyDictionary<string, List<WorkbookInputs.Cell>> sheets)
    {
        object? Read(string sheet, string cell) =>
            sheets.TryGetValue(sheet, out var cells) ? cells.FirstOrDefault(x => x.Address.Equals(cell, StringComparison.OrdinalIgnoreCase))?.Value : null;

        // The saved state.
        var baseInputs = new Dictionary<string, JsonNode?>();
        foreach (var i in c.Inputs) baseInputs[i.Field] = Typed(i, Read(i.Sheet, i.Cell));
        var baseSelections = new Dictionary<string, List<int>>();
        foreach (var s in c.Selections)
        {
            var ids = new List<int>();
            for (var row = s.FirstRow; row <= s.LastRow; row++)
                if (string.Equals(Read(s.Sheet, $"{s.Column}{row}")?.ToString()?.Trim(), s.Marked, StringComparison.OrdinalIgnoreCase)
                    && WorkbookContract.IdFor(s, row, a => Read(s.Sheet, a)) is { } id && !s.AlwaysIncluded.Contains(id))
                    ids.Add(id);
            baseSelections[s.Field] = ids;
        }

        var cases = new List<(string Label, bool Saved, Dictionary<string, JsonNode?> In, Dictionary<string, List<int>> Sel)>
        {
            ("the workbook as saved", true, baseInputs, baseSelections),
        };
        void Add(string label, string field, JsonNode? value)
        {
            var copy = baseInputs.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());
            copy[field] = value;
            cases.Add((label, false, copy, baseSelections));
        }
        foreach (var i in c.Inputs)
        {
            var now = baseInputs[i.Field];
            switch (i.Type)
            {
                case "choice":
                    foreach (var other in i.Allowed.Where(a => a != now?.ToString()).Take(2))
                        Add($"{i.Label ?? i.Field} = {other}", i.Field, JsonValue.Create(other));
                    break;
                case "number":
                case "integer":
                    if (Num(now) is not { } v) break;
                    foreach (var (factor, what) in new[] { (2.0, "doubled"), (0.5, "halved") })
                    {
                        var next = v == 0 ? (factor > 1 ? 10 : 1) : v * factor;
                        if (i.Type == "integer") next = Math.Round(next, MidpointRounding.AwayFromZero);
                        if (i.Max is { } max) next = Math.Min(next, max);
                        if (i.Min is { } min) next = Math.Max(next, min);
                        if (next == v) continue;
                        Add($"{i.Label ?? i.Field} {what} ({Show(v)} → {Show(next)})", i.Field,
                            i.Type == "integer" ? JsonValue.Create((long)next) : JsonValue.Create(next));
                    }
                    break;
                case "yes_no":
                    var flipped = string.Equals(now?.ToString(), "Yes", StringComparison.OrdinalIgnoreCase) ? "No" : "Yes";
                    Add($"{i.Label ?? i.Field} = {flipped}", i.Field, JsonValue.Create(flipped));
                    break;
            }
        }
        foreach (var s in c.Selections)
        {
            var now = baseSelections[s.Field];
            if (now.Count == 0) continue;
            cases.Add(($"no {s.Label ?? s.Field} marked", false, baseInputs, With(baseSelections, s.Field, new List<int>())));
            if (now.Count > 1)
                cases.Add(($"only {s.Label ?? s.Field} {now[0]} marked", false, baseInputs, With(baseSelections, s.Field, new List<int> { now[0] })));
        }

        var vectors = new List<Vector>();
        foreach (var (label, saved, inputs, selections) in cases.Take(MaxVectors))
        {
            var json = new JsonObject();
            var cells = new List<CellValue>();
            foreach (var i in c.Inputs)
            {
                json[i.Field] = inputs[i.Field]?.DeepClone();
                cells.Add(new CellValue(i.Sheet, i.Cell, inputs[i.Field]?.DeepClone()));
            }
            foreach (var s in c.Selections)
            {
                var ids = selections[s.Field];
                json[s.Field] = new JsonArray(ids.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
                for (var row = s.FirstRow; row <= s.LastRow; row++)
                {
                    if (WorkbookContract.IdFor(s, row, a => Read(s.Sheet, a)) is not { } id || s.AlwaysIncluded.Contains(id)) continue;
                    cells.Add(new CellValue(s.Sheet, $"{s.Column}{row}", JsonValue.Create(ids.Contains(id) ? s.Marked : s.Unmarked)));
                }
            }
            vectors.Add(new Vector($"v{vectors.Count + 1:00}", label, saved, json, cells));
        }
        return vectors;
    }

    private static Dictionary<string, List<int>> With(Dictionary<string, List<int>> sel, string field, List<int> ids)
    {
        var copy = sel.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        copy[field] = ids;
        return copy;
    }

    /// <summary>A cell's value as the contract types it: numbers as numbers (whole for integer), everything else as text.</summary>
    internal static JsonNode? Typed(WorkbookContract.Input i, object? value)
    {
        switch (i.Type)
        {
            case "number":
                return Num(value) is { } d ? JsonValue.Create(d) : value is null ? null : JsonValue.Create(value.ToString());
            case "integer":
                return Num(value) is { } n ? JsonValue.Create((long)Math.Round(n, MidpointRounding.AwayFromZero)) : value is null ? null : JsonValue.Create(value.ToString());
            default:
                return value switch
                {
                    null => null,
                    double dv => JsonValue.Create(dv.ToString("0.##########", CultureInfo.InvariantCulture)),
                    bool b => JsonValue.Create(b ? "TRUE" : "FALSE"),
                    _ => JsonValue.Create(value.ToString()),
                };
        }
    }

    private static double? Num(object? v) => v switch
    {
        double d => d,
        JsonValue j when j.TryGetValue<double>(out var d) => d,
        JsonValue j when j.TryGetValue<long>(out var l) => l,
        JsonValue j when j.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    private static string Show(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
