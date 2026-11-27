using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation.WorkbookEquivalence;

/// <summary>
/// The signed spec's <c>workbook_cell_contracts</c> and <c>outside_requests</c> claims (vb6 schema 1.1), read into data
/// the workbook equivalence gate can act on: which cell each input lives in, which Yes/No rows each selection reads and
/// how an item's id is formed, which cell each output is (or is checked against), and the request template sent to an
/// outside service. Pure; tolerant of a model's small shape variations (a number written as text, a missing list).
/// </summary>
public sealed record WorkbookContract(
    IReadOnlyList<WorkbookContract.Input> Inputs,
    IReadOnlyList<WorkbookContract.Selection> Selections,
    IReadOnlyList<WorkbookContract.Output> Outputs,
    IReadOnlyList<WorkbookContract.Request> Requests,
    IReadOnlyList<string> ClaimIds)
{
    public sealed record Input(string Field, string? Label, string Sheet, string Cell, string Type, IReadOnlyList<string> Allowed, double? Min, double? Max);

    /// <param name="IdOffset">id = row + offset (for <c>id_from: "row - 1"</c>), when the id comes from the row.</param>
    /// <param name="IdColumn">The column whose value is the id (for <c>id_from: "column B"</c>), when it comes from a column.</param>
    public sealed record Selection(string Field, string? Label, string Sheet, string Column, int FirstRow, int LastRow,
        string Marked, string Unmarked, int? IdOffset, string? IdColumn, IReadOnlyList<int> AlwaysIncluded);

    public sealed record Output(string Field, string? Label, string Sheet, string Cell, string ComputedBy,
        (string Sheet, string Cell)? CompareWith, double Tolerance);

    public sealed record ResponseField(string Path, string Type, string? Writes);

    public sealed record Request(string Id, string Method, string Url, JsonNode Body, IReadOnlyList<ResponseField> Response);

    public const double DefaultTolerance = 0.005;

    public bool IsEmpty => Inputs.Count == 0 && Selections.Count == 0 && Outputs.Count == 0;

    private static readonly Regex CellRef = new(@"^\$?(?<col>[A-Za-z]{1,3})\$?(?<row>\d+)$", RegexOptions.Compiled);
    private static readonly Regex RowExpr = new(@"^\s*row\s*(?:(?<op>[+-])\s*(?<n>\d+))?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ColumnExpr = new(@"^\s*column\s+(?<col>[A-Za-z]{1,3})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Reads every workbook_cell_contracts and outside_requests claim of a spec; an empty contract when there is none.</summary>
    public static WorkbookContract FromSpec(JsonElement spec)
    {
        var inputs = new List<Input>();
        var selections = new List<Selection>();
        var outputs = new List<Output>();
        var requests = new List<Request>();
        var ids = new List<string>();
        if (spec.ValueKind != JsonValueKind.Object) return new(inputs, selections, outputs, requests, ids);

        if (spec.TryGetProperty("workbook_cell_contracts", out var wccs) && wccs.ValueKind == JsonValueKind.Array)
            foreach (var c in wccs.EnumerateArray())
            {
                if (Str(c, "id") is { } id) ids.Add(id);
                foreach (var i in Arr(c, "inputs"))
                    if (Str(i, "field") is { } f && Str(i, "sheet") is { } sh && NormCell(Str(i, "cell")) is { } cell)
                        inputs.Add(new Input(f, Str(i, "label"), sh, cell, (Str(i, "type") ?? "text").ToLowerInvariant(),
                            Arr(i, "allowed").Select(ValueText).Where(v => v is not null).Select(v => v!).ToList(), Num(i, "min"), Num(i, "max")));
                foreach (var s in Arr(c, "selections"))
                    if (Str(s, "field") is { } f && Str(s, "sheet") is { } sh && Range(Str(s, "range")) is { } r)
                    {
                        int? offset = null; string? idCol = null;
                        var idFrom = Str(s, "id_from") ?? "row";
                        if (RowExpr.Match(idFrom) is { Success: true } m)
                            offset = m.Groups["n"].Success ? (m.Groups["op"].Value == "-" ? -1 : 1) * int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 0;
                        else if (ColumnExpr.Match(idFrom) is { Success: true } mc)
                            idCol = mc.Groups["col"].Value.ToUpperInvariant();
                        else continue;                                   // an id rule we cannot follow: leave the list out rather than guess
                        selections.Add(new Selection(f, Str(s, "label"), sh, r.Col, r.First, r.Last,
                            Str(s, "marked") ?? "Yes", Str(s, "unmarked") ?? "No", offset, idCol,
                            Arr(s, "always_included").Select(Int).Where(n => n is not null).Select(n => n!.Value).ToList()));
                    }
                foreach (var o in Arr(c, "outputs"))
                    if (Str(o, "field") is { } f && Str(o, "sheet") is { } sh && NormCell(Str(o, "cell")) is { } cell)
                        outputs.Add(new Output(f, Str(o, "label"), sh, cell, (Str(o, "computed_by") ?? "routine").ToLowerInvariant(),
                            SheetCell(Str(o, "compare_with"), sh), Num(o, "tolerance") ?? DefaultTolerance));
            }

        if (spec.TryGetProperty("outside_requests", out var osrs) && osrs.ValueKind == JsonValueKind.Array)
            foreach (var q in osrs.EnumerateArray())
            {
                if (Str(q, "url") is not { } url || !q.TryGetProperty("body", out var body)) continue;
                var id = Str(q, "id") ?? $"OSR-{requests.Count + 1}";
                ids.Add(id);
                var node = body.ValueKind == JsonValueKind.String && TryParse(body.GetString()) is { } parsed ? parsed : JsonNode.Parse(body.GetRawText())!;
                requests.Add(new Request(id, (Str(q, "method") ?? "POST").ToUpperInvariant(), url, node,
                    Arr(q, "response").Where(r => Str(r, "path") is not null)
                        .Select(r => new ResponseField(Str(r, "path")!, (Str(r, "type") ?? "number").ToLowerInvariant(), Str(r, "writes"))).ToList()));
            }
        return new(inputs, selections, outputs, requests, ids);
    }

    /// <summary>The id of the item on <paramref name="row"/>, or null when it is read from a column whose value is not a whole number.</summary>
    public static int? IdFor(Selection s, int row, Func<string, object?> cellValue)
    {
        if (s.IdOffset is { } off) return row + off;
        var v = cellValue($"{s.IdColumn}{row}");
        return v switch
        {
            double d when d == Math.Floor(d) => (int)d,
            string t when int.TryParse(t.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    // ── small readers ──
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static double? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int? Int(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
        : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m) ? m : null;

    private static string? ValueText(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        _ => null,
    };

    private static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); } catch (JsonException) { return null; }
    }

    internal static string? NormCell(string? cell)
    {
        if (cell is null) return null;
        var m = CellRef.Match(cell.Trim());
        return m.Success ? m.Groups["col"].Value.ToUpperInvariant() + m.Groups["row"].Value : null;
    }

    /// <summary>"D6:D58" → (D, 6, 58); a single cell is a one-row range; a range over several columns is not a Yes/No list.</summary>
    internal static (string Col, int First, int Last)? Range(string? range)
    {
        if (range is null) return null;
        var ends = range.Replace("$", "").Split(':', StringSplitOptions.TrimEntries);
        var a = CellRef.Match(ends[0]);
        var b = CellRef.Match(ends.Length > 1 ? ends[1] : ends[0]);
        if (!a.Success || !b.Success || !a.Groups["col"].Value.Equals(b.Groups["col"].Value, StringComparison.OrdinalIgnoreCase)) return null;
        var r1 = int.Parse(a.Groups["row"].Value, CultureInfo.InvariantCulture);
        var r2 = int.Parse(b.Groups["row"].Value, CultureInfo.InvariantCulture);
        return (a.Groups["col"].Value.ToUpperInvariant(), Math.Min(r1, r2), Math.Max(r1, r2));
    }

    /// <summary>"'1. Rating Dashboard'!F15" or "F15" (on <paramref name="defaultSheet"/>).</summary>
    internal static (string Sheet, string Cell)? SheetCell(string? text, string defaultSheet)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var bang = text.LastIndexOf('!');
        var sheet = bang > 0 ? text[..bang].Trim().Trim('\'') : defaultSheet;
        return NormCell(bang > 0 ? text[(bang + 1)..] : text) is { } cell ? (sheet, cell) : null;
    }
}
