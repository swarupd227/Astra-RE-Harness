using Astra.Api.Office;
using Xunit;
using Xunit.Abstractions;

namespace Astra.Api.Tests;

/// <summary>
/// The same workbook in two formats read by two independent code paths: the .xlsx/.xlsm reader on the original
/// and the legacy .xls reader on a converted copy (ASTRA_XLS_ORIG and ASTRA_XLS_CONV, same base names). The
/// converter is not Excel, so a difference is a lead to check, not proof of a bug; each line says what differs.
/// Runs only with those variables set (the workbooks are local, never committed).
/// </summary>
public class LegacyDifferentialTests
{
    private readonly ITestOutputHelper _out;
    public LegacyDifferentialTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Converted_workbooks_read_like_their_originals()
    {
        var orig = Environment.GetEnvironmentVariable("ASTRA_XLS_ORIG");
        var conv = Environment.GetEnvironmentVariable("ASTRA_XLS_CONV");
        if (string.IsNullOrEmpty(orig) || string.IsNullOrEmpty(conv) || !Directory.Exists(orig) || !Directory.Exists(conv)) return;

        foreach (var xls in Directory.GetFiles(conv, "*.xls").OrderBy(f => f))
        {
            var baseName = Path.GetFileNameWithoutExtension(xls);
            var original = Directory.GetFiles(orig, baseName + ".xlsm", SearchOption.AllDirectories).FirstOrDefault();
            if (original is null) { _out.WriteLine($"{baseName}: no original found"); continue; }
            var a = WorkbookSurface.Read(File.ReadAllBytes(original), Path.GetFileName(original));
            var b = WorkbookSurface.Read(File.ReadAllBytes(xls), Path.GetFileName(xls));
            _out.WriteLine($"==== {baseName}");
            _out.WriteLine($"  legacy warnings: {string.Join(" | ", b.Warnings)}");

            Diff("sheets", a.Sheets.Select(s => $"{s.Name} [{s.State}]"), b.Sheets.Select(s => $"{s.Name} [{s.State}]"));
            Diff("sheet counts", a.Sheets.Select(s => $"{s.Name}: formulas {s.FormulaCells}"), b.Sheets.Select(s => $"{s.Name}: formulas {s.FormulaCells}"));
            Diff("used range", a.Sheets.Select(s => $"{s.Name}: {s.UsedRange}"), b.Sheets.Select(s => $"{s.Name}: {s.UsedRange}"));
            Diff("names", a.Names.Select(n => $"{n.Name} = {n.RefersTo} ({n.Label})"), b.Names.Select(n => $"{n.Name} = {n.RefersTo} ({n.Label})"));
            Diff("input rules", a.Sheets.SelectMany(s => s.Validations.Select(v => $"{s.Name}!{v.Cells} {v.Type} {v.Source} ({v.Label})")),
                                b.Sheets.SelectMany(s => s.Validations.Select(v => $"{s.Name}!{v.Cells} {v.Type} {v.Source} ({v.Label})")));
            Diff("controls", a.Sheets.SelectMany(s => s.Controls.Select(c => $"{s.Name}: {c.Kind} '{c.Text}' macro={c.MacroModule}.{c.MacroRoutine} found={c.MacroFound} link={c.LinkedCell} handlers={string.Join("+", c.Handlers)}")),
                                b.Sheets.SelectMany(s => s.Controls.Select(c => $"{s.Name}: {c.Kind} '{c.Text}' macro={c.MacroModule}.{c.MacroRoutine} found={c.MacroFound} link={c.LinkedCell} handlers={string.Join("+", c.Handlers)}")));
            Diff("events", a.Events.Select(e => e.Trigger), b.Events.Select(e => e.Trigger));
            Diff("vba modules", (a.Vba?.Modules ?? Array.Empty<VbaModule>()).Select(m => $"{m.Name} {m.Kind} layout={m.Layout?.Controls.Count}"),
                                (b.Vba?.Modules ?? Array.Empty<VbaModule>()).Select(m => $"{m.Name} {m.Kind} layout={m.Layout?.Controls.Count}"));
            // A sample of formulas, side by side: same sheet and cell, text as each reader renders it.
            var fa = a.Sheets.SelectMany(s => s.Formulas.Select(f => (Key: $"{s.Name}!{f.Cell}", f.Formula))).ToDictionary(x => x.Key, x => x.Formula);
            var fb = b.Sheets.SelectMany(s => s.Formulas.Select(f => (Key: $"{s.Name}!{f.Cell}", f.Formula))).ToDictionary(x => x.Key, x => x.Formula);
            var same = fa.Keys.Intersect(fb.Keys).Count(k => Normal(fa[k]) == Normal(fb[k]));
            var both = fa.Keys.Intersect(fb.Keys).Count();
            _out.WriteLine($"  formulas: {fa.Count} listed in the original, {fb.Count} in the copy; {both} cells in both, {same} with the same text");
            var other = fa.Keys.Intersect(fb.Keys).Where(k => Normal(fa[k]) != Normal(fb[k])).ToList();
            _out.WriteLine($"    of the differing: {other.Count(k => fb[k] == "#N/A")} are #N/A in the copy (a function the converter cannot write)");
            foreach (var k in other.Where(k => fb[k] != "#N/A").Take(12))
                _out.WriteLine($"    differ at {k}:  orig  {fa[k]}\n{new string(' ', 28 + k.Length)}  copy  {fb[k]}");
        }
    }

    private static string Normal(string f) => f.Replace(" ", "").Replace("_xlfn.", "").ToUpperInvariant();

    private void Diff(string what, IEnumerable<string> original, IEnumerable<string> copy)
    {
        var a = original.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var b = copy.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missing = a.Except(b).ToList(); var extra = b.Except(a).ToList();
        _out.WriteLine($"  {what}: {a.Count} in the original, {b.Count} in the copy" + (missing.Count + extra.Count == 0 ? "  — identical" : ""));
        foreach (var m in missing.Take(8)) _out.WriteLine($"      only in original: {m}");
        foreach (var e in extra.Take(8)) _out.WriteLine($"      only in copy:     {e}");
    }
}
