using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Office;

/// <summary>
/// What a VBA routine's host document says about it, as a markdown block for the extraction prompt: the
/// workbook's named inputs (with their labels and input rules), its sheets, what starts this routine
/// (buttons, form controls, events), which sheets and names its module reaches, and — for a form's code —
/// the form's controls with their labels. Without it a model sees <c>Range("Premium").Value = x</c> and has
/// to guess what "Premium" is; with it the spec can say "writes the Premium output cell on the Quote sheet".
/// Built from <see cref="WorkbookSurface"/>; pure and deterministic.
/// </summary>
public static class OfficeHostContext
{
    private static readonly Regex OfficePath = new(
        @"^(?<doc>.+\.(?:xlsm|xltm|xlam|xls|xla|xlt|docm|dotm|doc|dot|pptm|potm|ppam))/(?<module>[^/]+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits <c>Quotes/Rating.xlsm/Pricing.bas</c> into the document path and the module file; null for ordinary source.</summary>
    public static (string Document, string ModuleFile)? SplitPath(string relativePath)
    {
        var m = OfficePath.Match(relativePath.Replace('\\', '/'));
        return m.Success ? (m.Groups["doc"].Value, m.Groups["module"].Value) : null;
    }

    public const int MaxNames = 60;
    public const int MaxFormLines = 160;

    public static string Build(WorkbookSurfaceResult r, string moduleFile, string routineName)
    {
        var module = Path.GetFileNameWithoutExtension(moduleFile);
        var sb = new StringBuilder();
        sb.AppendLine($"## Host document: {r.FileName} (the workbook this VBA lives in, read statically)");
        sb.AppendLine();
        sb.AppendLine("This routine is VBA inside an Office document, not a standalone program. The workbook below is its data and its user interface: named cells are its inputs and outputs, sheets are its tables, and buttons, form controls and events are what start it. Use this to name what the routine reads and writes in business terms; cite the code's lines, not this block.");
        sb.AppendLine();

        // What starts this routine.
        var starts = new List<string>();
        foreach (var s in r.Sheets)
            foreach (var c in s.Controls)
            {
                var runsIt = c.Handlers.Any(h => h.Equals(routineName, StringComparison.OrdinalIgnoreCase) && string.Equals(c.MacroModule, module, StringComparison.OrdinalIgnoreCase))
                             || (string.Equals(c.MacroRoutine, routineName, StringComparison.OrdinalIgnoreCase)
                                 && (c.MacroModule is null || string.Equals(c.MacroModule, module, StringComparison.OrdinalIgnoreCase)));
                if (runsIt) starts.Add($"- the {c.Kind} \"{c.Text ?? c.Name}\" on sheet \"{s.Name}\"");
            }
        foreach (var e in r.Events.Where(e => e.Routine.Equals(routineName, StringComparison.OrdinalIgnoreCase) && e.Module.Equals(module, StringComparison.OrdinalIgnoreCase)))
            starts.Add($"- the {(e.Sheet is null ? "workbook" : $"sheet \"{e.Sheet}\"")}'s {e.Trigger[(e.Trigger.IndexOf('_') + 1)..]} event (runs without a button)");
        var form = r.Vba?.Modules.FirstOrDefault(m => m.Name.Equals(module, StringComparison.OrdinalIgnoreCase) && m.Layout is not null);
        if (form is not null)
        {
            var us = routineName.IndexOf('_');
            if (us > 0 && Flatten(form.Layout!.Controls).FirstOrDefault(c => c.Name.Equals(routineName[..us], StringComparison.OrdinalIgnoreCase)) is { } ctl)
                starts.Add($"- the {ctl.Kind} {ctl.Name}{(ctl.Caption is null ? "" : $" \"{Clip(ctl.Caption, 60)}\"")} on form {form.Name} ({routineName[(us + 1)..]} event)");
            else if (routineName.StartsWith("UserForm_", StringComparison.OrdinalIgnoreCase))
                starts.Add($"- form {form.Name}'s own {routineName[9..]} event");
        }
        if (starts.Count > 0)
        {
            sb.AppendLine($"### What runs {routineName}");
            foreach (var s in starts.Distinct()) sb.AppendLine(s);
            sb.AppendLine();
        }

        // What this module reaches in the workbook.
        if (r.CodeReach.FirstOrDefault(c => c.Module.Equals(module, StringComparison.OrdinalIgnoreCase)) is { } reach)
        {
            sb.AppendLine($"### What module {module} names in the workbook");
            if (reach.Sheets.Count > 0) sb.AppendLine($"- sheets: {string.Join(", ", reach.Sheets.Select(x => $"\"{x}\""))}");
            if (reach.Names.Count > 0) sb.AppendLine($"- named cells: {string.Join(", ", reach.Names)}");
            if (reach.Cells.Count > 0) sb.AppendLine($"- cell addresses: {string.Join(", ", reach.Cells.Take(40))}{(reach.Cells.Count > 40 ? " …" : "")}");
            sb.AppendLine();
        }

        // The workbook's vocabulary: named cells with labels and the rules on them.
        if (r.Names.Count > 0)
        {
            var rules = r.Sheets.SelectMany(s => s.Validations.Select(v => (Sheet: s.Name, v))).ToList();
            // A control on a sheet that writes its value to a cell (a check box's linked cell) is how the user sets it.
            var setBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in r.Sheets)
                foreach (var c in s.Controls.Where(c => c.LinkedCell is not null))
                {
                    var link = c.LinkedCell!.Contains('!') ? c.LinkedCell : $"{s.Name}!{c.LinkedCell}";
                    if (CellOf(link, null) is { } cell)
                        setBy.TryAdd(cell, $"set by the {c.Kind}{(c.Text is null ? "" : $" \"{Clip(c.Text, 50)}\"")} on sheet \"{s.Name}\"");
                }
            sb.AppendLine("### Named cells (name = where — label beside it — input rule — what sets it)");
            foreach (var n in r.Names.Take(MaxNames))
            {
                var rule = RuleFor(n, rules);
                var by = CellOf(n.RefersTo, n.Scope) is { } cell && setBy.TryGetValue(cell, out var b) ? b : null;
                sb.AppendLine($"- {n.Name} = {n.RefersTo}{(n.Label is null ? "" : $" — \"{n.Label}\"")}{(rule is null ? "" : $" — {rule}")}{(by is null ? "" : $" — {by}")}");
            }
            if (r.Names.Count > MaxNames) sb.AppendLine($"- … and {r.Names.Count - MaxNames} more");
            sb.AppendLine();
        }
        var unnamedRules = r.Sheets.SelectMany(s => s.Validations.Where(v => !r.Names.Any(n => CellOf(n.RefersTo, n.Scope) is { } c && c.Equals($"{s.Name}!{v.Cells.Split(':')[0]}", StringComparison.OrdinalIgnoreCase)))
            .Select(v => $"- {s.Name}!{v.Cells}{(v.Label is null ? "" : $" \"{v.Label}\"")}: {Describe(v)}")).ToList();
        if (unnamedRules.Count > 0)
        {
            sb.AppendLine("### Other input rules");
            foreach (var u in unnamedRules.Take(30)) sb.AppendLine(u);
            sb.AppendLine();
        }

        sb.AppendLine("### Sheets");
        foreach (var s in r.Sheets)
            sb.AppendLine($"- \"{s.Name}\"{(s.CodeName is null ? "" : $" (code name {s.CodeName})")}{(s.State == "visible" ? "" : $", {s.State}")}: " +
                          $"{s.ValueCells:N0} values, {s.FormulaCells:N0} formulas{(s.ReadsFromSheets.Count > 0 ? $", formulas read {string.Join(", ", s.ReadsFromSheets.Select(x => $"\"{x}\""))}" : "")}" +
                          (s.Tables.Count > 0 ? $"; tables {string.Join(", ", s.Tables.Select(t => $"{t.Name}({string.Join(", ", t.Columns)})"))}" : ""));
        sb.AppendLine();

        if (form is not null)
        {
            sb.AppendLine($"### Form {form.Name}: its controls (kind name \"caption\" — label beside it)");
            var lines = 0;
            foreach (var (c, depth, label) in FlattenWithDepth(form.Layout!.Controls, 0))
            {
                if (++lines > MaxFormLines) { sb.AppendLine("- …"); break; }
                var extra = string.Join("", new[]
                {
                    c.Caption is null ? null : $" \"{Clip(c.Caption, 80)}\"",
                    label is null ? null : $" — \"{Clip(label, 60)}\"",
                    c.ControlSource is null ? null : $", bound to {c.ControlSource}",
                    c.RowSource is null ? null : $", items from {c.RowSource}",
                    c.MaxLength is int ml and > 0 ? $", max {ml} chars" : null,
                }.Where(x => x is not null));
                sb.AppendLine($"{new string(' ', depth * 2)}- {c.Kind} {c.Name}{extra}");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>For a document whose sheets could not be read (a Word or PowerPoint file, a legacy .xls).</summary>
    public static string Minimal(string document) =>
        $"## Host document: {document}\n\nThis routine is VBA inside an Office document, not a standalone program. Its sheets, named cells and forms were not available to read, so treat anything the code reads from or writes to the document (Range, Cells, Worksheets, form controls) as an input or output you name from the code itself.";

    private static string? RuleFor(WorkbookName n, List<(string Sheet, WorkbookValidation v)> rules)
    {
        if (CellOf(n.RefersTo, n.Scope) is not { } cell) return null;
        foreach (var (sheet, v) in rules)
            foreach (var part in v.Cells.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (Contains(part, cell, sheet)) return Describe(v);
        return null;
    }

    private static string Describe(WorkbookValidation v) =>
        v.Type == "list" ? $"one of {v.Source}" : v.Source is null ? $"a {v.Type}" : $"a {v.Type} {v.Source}";

    private static bool Contains(string range, string sheetCell, string sheet)
    {
        var bang = sheetCell.IndexOf('!');
        if (!sheetCell[..bang].Equals(sheet, StringComparison.OrdinalIgnoreCase)) return false;
        var target = sheetCell[(bang + 1)..];
        var ends = range.Replace("$", "").Split(':');
        if (ends.Length == 1) return ends[0].Equals(target, StringComparison.OrdinalIgnoreCase);
        var (c1, r1) = Split(ends[0]); var (c2, r2) = Split(ends[1]); var (c, rr) = Split(target);
        return c >= Math.Min(c1, c2) && c <= Math.Max(c1, c2) && rr >= Math.Min(r1, r2) && rr <= Math.Max(r1, r2);

        static (int Col, int Row) Split(string a)
        {
            var m = Regex.Match(a, @"^([A-Z]{1,3})(\d+)$", RegexOptions.IgnoreCase);
            return m.Success ? (m.Groups[1].Value.ToUpperInvariant().Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1), int.Parse(m.Groups[2].Value)) : (0, 0);
        }
    }

    private static string? CellOf(string refersTo, string? scope)
    {
        var m = Regex.Match(refersTo.Trim(), @"^(?:'?(?<s>(?:[^'!]|'')+)'?!)?\$?(?<c>[A-Z]{1,3})\$?(?<r>\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var sheet = m.Groups["s"].Success ? m.Groups["s"].Value.Replace("''", "'") : scope;
        return sheet is null ? null : $"{sheet}!{m.Groups["c"].Value.ToUpperInvariant()}{m.Groups["r"].Value}";
    }

    private static IEnumerable<FormControl> Flatten(IEnumerable<FormControl> cs) => cs.SelectMany(c => new[] { c }.Concat(Flatten(c.Children)));

    private static IEnumerable<(FormControl Control, int Depth, string? Label)> FlattenWithDepth(IReadOnlyList<FormControl> cs, int depth) =>
        cs.OrderBy(c => c.TabIndex ?? int.MaxValue)
          .SelectMany(c => new[] { (c, depth, UserForms.LabelFor(c, cs)) }.Concat(FlattenWithDepth(c.Children, depth + 1)));

    private static string Clip(string s, int max)
    {
        var one = Regex.Replace(s, @"\s+", " ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}
