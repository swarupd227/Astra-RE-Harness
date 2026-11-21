using System.Text;
using System.Text.Json;
using Astra.Api.Office;

namespace Astra.Api.Evidence;

/// <summary>
/// A spreadsheet's surface as citable pieces and graph facts, read by <see cref="WorkbookSurface"/>
/// (statically; nothing is calculated or run). One piece per sheet, named range, input rule, control,
/// event, link out and module that reaches into the sheets, plus an overview and, per sheet, the
/// formulas that read other sheets or names. Locators say what the piece is:
/// <c>workbook</c>, <c>sheet:Quote</c>, <c>name:Premium</c>, <c>input:Quote!B4</c>,
/// <c>control:Quote/cmdStart</c>, <c>formulas:Projection</c>, <c>event:ThisWorkbook.Workbook_Open</c>,
/// <c>code:Module1</c>, <c>link:Policy DB</c>.
/// <para>
/// Facts: the workbook <c>contains</c> its sheets; a sheet <c>contains</c> its named ranges, inputs and
/// controls and <c>reads</c> the sheets and names its formulas use; a control <c>runs</c> the macro it is
/// bound to and <c>writes</c> its linked cell; the workbook or a sheet <c>triggers</c> its event
/// handlers; a VBA module <c>contains</c> those macros and <c>references</c> the sheets and names its code
/// names; the workbook <c>reads</c> the files and data sources it links to. Names are qualified by the
/// workbook's path (<c>Rating.xlsm/Quote</c>), so two workbooks with a sheet called "Inputs" stay two.
/// Connection strings arrive with credentials already removed.
/// </para>
/// </summary>
public sealed class WorkbookAdapter : IEvidenceAdapter
{
    public const string WorkbookType = "workbook";
    public const string SheetType = "sheet";
    public const string NamedRangeType = "named-range";
    public const string InputType = "input";
    public const string ControlType = "control";
    public const string MacroType = "macro";
    public const string ModuleType = "vba-module";
    public const string DataSourceType = "data-source";

    /// <summary>Formulas quoted in one sheet's formulas piece; every formula is still counted.</summary>
    public const int MaxFormulasQuoted = 120;
    public const int MaxNames = 1000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Bytes is null) return AdapterResult.Nothing("It was not read as a binary file, so its sheets could not be opened.");
        var name = Path.GetFileName(input.Path);
        var r = WorkbookSurface.Read(input.Bytes, name);
        var wb = input.Path;

        var chunks = new List<EvidenceChunkDraft>();
        var entities = new List<EntityFact>();
        var links = new List<LinkFact>();
        // The reader names the file in its sentences; under the file that is redundant.
        var warnings = r.Warnings.Select(w => w.StartsWith(name + ": ", StringComparison.Ordinal) ? w[(name.Length + 2)..] : w).ToList();

        EntityRefFact Ref(string type, string n) => new(type, n);
        string Q(string part) => $"{wb}/{part}";
        var self = Ref(WorkbookType, wb);
        var modules = r.Vba?.Modules ?? Array.Empty<VbaModule>();

        // ── Overview ─────────────────────────────────────────────────────
        var overview = new StringBuilder();
        overview.AppendLine($"Workbook {name}: {Count(r.Sheets.Count, "sheet")}, {Count(r.Names.Count, "named range")}, " +
                            $"{Count(r.Sheets.Sum(s => s.FormulaCells), "formula")}, {Count(r.Sheets.Sum(s => s.Controls.Count), "control")}, " +
                            $"{Count(modules.Count, "VBA module")}.");
        foreach (var s in r.Sheets)
            overview.AppendLine($"- Sheet \"{s.Name}\"{(s.CodeName is null ? "" : $" (code name {s.CodeName})")}{(s.State == "visible" ? "" : $", {s.State}")}");
        foreach (var m in modules) overview.AppendLine($"- VBA {m.Kind.ToString().ToLowerInvariant()} module {m.Name}");
        chunks.Add(new("workbook", name, overview.ToString().TrimEnd(),
            JsonSerializer.Serialize(new { sheets = r.Sheets.Count, names = r.Names.Count, modules = modules.Select(m => new { m.Name, kind = m.Kind.ToString() }) }, Json)));
        entities.Add(new(WorkbookType, wb, "workbook", Attrs(("sheets", r.Sheets.Count), ("modules", modules.Count), ("vbaProject", r.Vba?.Name))));

        var nameByCell = new Dictionary<string, WorkbookName>(StringComparer.OrdinalIgnoreCase);   // "Sheet!B4" → name
        foreach (var n in r.Names)
            if (CellOf(n.RefersTo, n.Scope) is { } cell) nameByCell.TryAdd(cell, n);

        // ── Sheets, and what lives on them ─────────────────────────────
        foreach (var s in r.Sheets)
        {
            var sheetRef = Ref(SheetType, Q(s.Name));
            var loc = $"sheet:{s.Name}";
            var text = $"Sheet \"{s.Name}\"" + (s.CodeName is null ? "" : $", code name {s.CodeName}") +
                       (s.State == "visible" ? "" : s.State == "veryHidden" ? ", very hidden (only code can show it)" : ", hidden") +
                       (s.UsedRange is null ? "" : $", used range {s.UsedRange}") +
                       $": {Count(s.ValueCells, "value")} and {Count(s.FormulaCells, "formula")}" +
                       (s.ReadsFromSheets.Count > 0 ? $"; its formulas read {string.Join(", ", s.ReadsFromSheets.Select(x => $"\"{x}\""))}" : "") +
                       (s.Tables.Count > 0 ? $"; tables {string.Join(", ", s.Tables.Select(t => $"{t.Name} ({t.Range}: {string.Join(", ", t.Columns)})"))}" : "") + ".";
            chunks.Add(new(loc, $"{name} · {s.Name}", text,
                JsonSerializer.Serialize(new { s.Name, s.CodeName, s.State, s.UsedRange, s.ValueCells, s.FormulaCells, s.ReadsFromSheets, tables = s.Tables }, Json)));
            entities.Add(new(SheetType, Q(s.Name), loc, Attrs(("codeName", s.CodeName), ("state", s.State), ("usedRange", s.UsedRange),
                ("formulas", s.FormulaCells), ("values", s.ValueCells))));
            links.Add(new(EntityVocabulary.Contains, self, sheetRef, loc));

            // Formulas that reach beyond their own sheet: what makes one sheet depend on another.
            var reaching = s.Formulas.Where(f => f.Refs.Count > 0).ToList();
            if (reaching.Count > 0)
            {
                var floc = $"formulas:{s.Name}";
                var sb = new StringBuilder($"Formulas on \"{s.Name}\" that read other sheets, names or tables ({reaching.Count:N0} listed of {s.FormulaCells:N0} formulas on the sheet):\n");
                foreach (var f in reaching.Take(MaxFormulasQuoted))
                    sb.AppendLine($"{f.Cell}{(f.Range is { } rg && rg != f.Cell ? $" (shared over {rg})" : "")}: ={f.Formula}");
                if (reaching.Count > MaxFormulasQuoted) sb.AppendLine($"… and {reaching.Count - MaxFormulasQuoted:N0} more.");
                chunks.Add(new(floc, $"{name} · {s.Name} · formulas", sb.ToString().TrimEnd()));

                foreach (var g in reaching.SelectMany(f => f.Refs).GroupBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var (kind, target) = Split(g.Key);
                    var to = kind switch
                    {
                        "sheet" => Ref(SheetType, Q(target)),
                        "name" => Ref(NamedRangeType, Q(target)),
                        _ => null,
                    };
                    if (to is not null) links.Add(new(EntityVocabulary.Reads, sheetRef, to, floc, EntityVocabulary.High, Attrs(("formulas", g.Count()))));
                }
            }

            foreach (var v in s.Validations)
            {
                var first = v.Cells.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? v.Cells;
                var iloc = $"input:{s.Name}!{first}";
                var named = nameByCell.GetValueOrDefault($"{s.Name}!{first.Split(':')[0].Replace("$", "")}");
                var describe = v.Type == "list" ? $"one of {v.Source}" : v.Source is null ? $"a {v.Type}" : $"a {v.Type} {v.Source}";
                chunks.Add(new(iloc, $"{name} · {s.Name}!{v.Cells}",
                    $"Input {s.Name}!{v.Cells}{(v.Label is null ? "" : $" (\"{v.Label}\")")}{(named is null ? "" : $", named {named.Name}")} must be {describe}" +
                    (v.Prompt is null ? "." : $"; the prompt says \"{v.Prompt}\"."),
                    JsonSerializer.Serialize(v, Json)));
                var inputName = Q($"{s.Name}!{v.Cells}");
                entities.Add(new(InputType, inputName, iloc, Attrs(("label", v.Label), ("rule", v.Type), ("allowed", v.Source), ("name", named?.Name))));
                links.Add(new(EntityVocabulary.Contains, sheetRef, Ref(InputType, inputName), iloc));
                if (named is not null) links.Add(new(EntityVocabulary.References, Ref(InputType, inputName), Ref(NamedRangeType, Q(named.Name)), iloc));
            }

            foreach (var c in s.Controls)
            {
                var cloc = $"control:{s.Name}/{c.Name}";
                var controlName = Q($"{s.Name}/{c.Name}");
                var runs = c.Handlers.Count > 0
                    ? $"runs {string.Join(", ", c.Handlers.Select(h => $"{c.MacroModule}.{h}"))} (event handlers in the sheet's code)"
                    : c.Macro is null ? "runs no macro"
                    : c.MacroFound ? $"runs {c.MacroModule}.{c.MacroRoutine}"
                    : $"is set to run {c.Macro}, which is not in this workbook's VBA";
                chunks.Add(new(cloc, $"{name} · {s.Name} · {c.Name}",
                    $"{(c.Technology == "activex" ? "ActiveX" : c.Technology == "shape" ? "Shape" : "Form")} {c.Kind} \"{c.Name}\" on \"{s.Name}\"" +
                    (c.Text is null ? "" : $" labelled \"{c.Text}\"") + $" {runs}" +
                    (c.LinkedCell is null ? "" : $"; it writes its value to {c.LinkedCell}") +
                    (c.InputRange is null ? "" : $"; its items come from {c.InputRange}") + ".",
                    JsonSerializer.Serialize(c, Json)));
                entities.Add(new(ControlType, controlName, cloc, Attrs(("kind", c.Kind), ("technology", c.Technology), ("caption", c.Text), ("linkedCell", c.LinkedCell))));
                links.Add(new(EntityVocabulary.Contains, sheetRef, Ref(ControlType, controlName), cloc));

                if (c.Handlers.Count > 0 && c.MacroModule is not null)
                    foreach (var h in c.Handlers)
                    {
                        AddMacro(c.MacroModule, h, cloc, true);
                        links.Add(new(EntityVocabulary.Runs, Ref(ControlType, controlName), Ref(MacroType, Q($"{c.MacroModule}.{h}")), cloc,
                            EntityVocabulary.High, Attrs(("event", h[(h.IndexOf('_') + 1)..]))));
                    }
                else if (c.MacroRoutine is not null)
                {
                    var module = c.MacroModule ?? "?";
                    AddMacro(module, c.MacroRoutine, cloc, c.MacroFound);
                    links.Add(new(EntityVocabulary.Runs, Ref(ControlType, controlName), Ref(MacroType, Q($"{module}.{c.MacroRoutine}")), cloc,
                        c.MacroFound ? EntityVocabulary.High : EntityVocabulary.Medium, Attrs(("macro", c.Macro))));
                }
                if (c.LinkedCell is not null)
                {
                    var cell = c.LinkedCell.Contains('!') ? c.LinkedCell : $"{s.Name}!{c.LinkedCell}";
                    var (sheetPart, cellPart) = (cell[..cell.LastIndexOf('!')].Trim('\''), cell[(cell.LastIndexOf('!') + 1)..].Replace("$", ""));
                    var to = nameByCell.TryGetValue($"{sheetPart}!{cellPart}", out var nm) ? Ref(NamedRangeType, Q(nm.Name)) : Ref(SheetType, Q(sheetPart));
                    links.Add(new(EntityVocabulary.Writes, Ref(ControlType, controlName), to, cloc, EntityVocabulary.High, Attrs(("cell", cellPart))));
                }
            }
        }

        // ── Named ranges ──────────────────────────────────────────────
        foreach (var n in r.Names.Take(MaxNames))
        {
            var nloc = $"name:{(n.Scope is null ? "" : n.Scope + "!")}{n.Name}";
            chunks.Add(new(nloc, $"{name} · {n.Name}",
                $"Name {n.Name} refers to {n.RefersTo}{(n.Label is null ? "" : $"; the cell is labelled \"{n.Label}\"")}" +
                (n.Scope is null ? "" : $"; it is local to sheet \"{n.Scope}\"") + (n.Hidden ? "; it is hidden" : "") + ".",
                JsonSerializer.Serialize(n, Json)));
            entities.Add(new(NamedRangeType, Q(n.Name), nloc, Attrs(("refersTo", n.RefersTo), ("label", n.Label), ("scope", n.Scope))));
            var sheet = SheetOf(n.RefersTo) ?? n.Scope;
            if (sheet is not null && r.Sheets.Any(s => s.Name.Equals(sheet, StringComparison.OrdinalIgnoreCase)))
                links.Add(new(EntityVocabulary.Contains, Ref(SheetType, Q(sheet)), Ref(NamedRangeType, Q(n.Name)), nloc));
        }
        if (r.Names.Count > MaxNames) warnings.Add($"Only the first {MaxNames:N0} of {r.Names.Count:N0} names were indexed.");

        // ── Events: code that runs without a button ───────────────────
        foreach (var e in r.Events)
        {
            var eloc = $"event:{e.Module}.{e.Routine}";
            var when = e.Trigger[(e.Trigger.IndexOf('_') + 1)..];
            chunks.Add(new(eloc, $"{name} · {e.Module}.{e.Routine}",
                $"{e.Module}.{e.Routine} runs on the {(e.Sheet is null ? "workbook" : $"sheet \"{e.Sheet}\"")}'s {when} event, without anyone pressing a button."));
            AddMacro(e.Module, e.Routine, eloc, true);
            var from = e.Sheet is null ? self : Ref(SheetType, Q(e.Sheet));
            links.Add(new(EntityVocabulary.Triggers, from, Ref(MacroType, Q($"{e.Module}.{e.Routine}")), eloc, EntityVocabulary.High, Attrs(("event", when))));
        }

        // ── Where the code reaches into the sheets ────────────────────
        foreach (var c in r.CodeReach)
        {
            var mloc = $"code:{c.Module}";
            chunks.Add(new(mloc, $"{name} · {c.Module}",
                $"VBA module {c.Module} names " + string.Join("; ", new[]
                {
                    c.Sheets.Count > 0 ? $"sheets {string.Join(", ", c.Sheets.Select(x => $"\"{x}\""))}" : null,
                    c.Names.Count > 0 ? $"names {string.Join(", ", c.Names)}" : null,
                    c.Cells.Count > 0 ? $"cells {string.Join(", ", c.Cells.Take(40))}{(c.Cells.Count > 40 ? " …" : "")}" : null,
                }.Where(x => x is not null)) + ".",
                JsonSerializer.Serialize(c, Json)));
            AddModule(c.Module, mloc);
            foreach (var s in c.Sheets)
                links.Add(new(EntityVocabulary.References, Ref(ModuleType, Q(c.Module)), Ref(SheetType, Q(s)), mloc, EntityVocabulary.Medium));
            foreach (var n in c.Names)
                links.Add(new(EntityVocabulary.References, Ref(ModuleType, Q(c.Module)), Ref(NamedRangeType, Q(n)), mloc, EntityVocabulary.Medium));
        }

        // ── UserForms: the dialogs the macros show ────────────────────
        foreach (var m in modules.Where(x => x.Layout is not null))
        {
            var floc = $"form:{m.Name}";
            var formName = Q(m.Name);
            var handlers = HandlerNames(m.Source);
            var flat = Flatten(m.Layout!.Controls, null).ToList();
            var text = new StringBuilder();
            text.AppendLine($"UserForm {m.Name}" + (m.Layout.Width is { } w && m.Layout.Height is { } h ? $" ({w:0} x {h:0} pt)" : "") +
                            $": {Count(flat.Count, "control")}" + (m.Layout.Complete ? "." : " (part of its designer data could not be read)."));
            foreach (var (c, parent, indent, label) in flat)
            {
                var caption = c.Caption is null ? "" : $" \"{Clip(c.Caption, 120)}\"";
                var labelled = label is null ? "" : $" labelled \"{Clip(label, 60)}\"";
                var bound = c.ControlSource is null ? "" : $", bound to {c.ControlSource}";
                var items = c.RowSource is null ? "" : $", items from {c.RowSource}";
                var group = c.GroupName is null ? "" : $", group {c.GroupName}";
                var runs = handlers.Where(hn => hn.StartsWith(c.Name + "_", StringComparison.OrdinalIgnoreCase)).ToList();
                text.AppendLine($"{new string(' ', indent * 2)}- {c.Kind} {c.Name}{caption}{labelled}{bound}{items}{group}" +
                                (runs.Count > 0 ? $"; runs {string.Join(", ", runs)}" : ""));
            }
            chunks.Add(new(floc, $"{name} · {m.Name}", text.ToString().TrimEnd(),
                JsonSerializer.Serialize(new { m.Name, m.Layout.Width, m.Layout.Height, m.Layout.Complete, controls = m.Layout.Controls }, Json)));
            entities.Add(new(UserFormType, formName, floc, Attrs(("controls", flat.Count), ("complete", m.Layout.Complete),
                ("codePath", $"{wb}/{m.ExportFileName}"))));
            links.Add(new(EntityVocabulary.Contains, self, Ref(UserFormType, formName), floc));

            foreach (var (c, parent, _, label) in flat)
            {
                var controlName = Q($"{m.Name}/{c.Name}");
                entities.Add(new(ControlType, controlName, floc, Attrs(("kind", c.Kind), ("technology", "userform"), ("caption", c.Caption is null ? null : Clip(c.Caption, 200)),
                    ("label", label), ("controlSource", c.ControlSource), ("rowSource", c.RowSource), ("group", c.GroupName), ("tabIndex", c.TabIndex))));
                var owner = parent is null ? Ref(UserFormType, formName) : Ref(ControlType, Q($"{m.Name}/{parent.Name}"));
                links.Add(new(EntityVocabulary.Contains, owner, Ref(ControlType, controlName), floc));
                foreach (var hn in handlers.Where(hn => hn.StartsWith(c.Name + "_", StringComparison.OrdinalIgnoreCase)))
                {
                    AddMacro(m.Name, hn, floc, true);
                    links.Add(new(EntityVocabulary.Runs, Ref(ControlType, controlName), Ref(MacroType, Q($"{m.Name}.{hn}")), floc,
                        EntityVocabulary.High, Attrs(("event", hn[(hn.IndexOf('_') + 1)..]))));
                }
                if (c.ControlSource is not null)
                    links.Add(new(EntityVocabulary.Writes, Ref(ControlType, controlName), Ref(SheetType, Q(SheetOf(c.ControlSource) ?? "?")), floc,
                        EntityVocabulary.High, Attrs(("cell", c.ControlSource))));
                if (c.RowSource is not null && SheetOf(c.RowSource) is { } rs)
                    links.Add(new(EntityVocabulary.Reads, Ref(ControlType, controlName), Ref(SheetType, Q(rs)), floc,
                        EntityVocabulary.High, Attrs(("range", c.RowSource))));
            }
        }

        // ── Links out ─────────────────────────────────────────────────
        foreach (var x in r.Externals)
        {
            var xloc = $"link:{x.Name}";
            chunks.Add(new(xloc, $"{name} · {x.Name}",
                x.Kind == "workbook" ? $"A link to another workbook: {x.Target ?? "(target not stated)"}."
                                     : $"A {x.Kind} data connection \"{x.Name}\": {x.Target ?? "(no connection string)"}.",
                JsonSerializer.Serialize(x, Json)));
            if (x.Kind == "workbook" && x.Target is not null)
                links.Add(new(EntityVocabulary.Reads, self, Ref(EntityVocabulary.File, x.Target), xloc));
            else if (x.Kind != "workbook")
            {
                entities.Add(new(DataSourceType, Q(x.Name), xloc, Attrs(("connection", x.Kind), ("target", x.Target))));
                links.Add(new(EntityVocabulary.Reads, self, Ref(DataSourceType, Q(x.Name)), xloc));
            }
        }

        var summary = $"Read {Count(r.Sheets.Count, "sheet")}, {Count(r.Names.Count, "named range")}, " +
                      $"{Count(r.Sheets.Sum(s => s.Validations.Count), "input rule")}, {Count(r.Sheets.Sum(s => s.Controls.Count), "control")} " +
                      $"({r.Sheets.SelectMany(s => s.Controls).Count(c => c.MacroFound)} bound to VBA) and {Count(r.Events.Count, "event handler")}" +
                      (modules.Any(x => x.Layout is not null)
                          ? $"; {Count(modules.Count(x => x.Layout is not null), "UserForm")} with {modules.Where(x => x.Layout is not null).Sum(x => CountControls(x.Layout!.Controls)):N0} controls."
                          : ".");
        return new AdapterResult(chunks, warnings, new FactSet(Dedupe(entities), links), summary);

        void AddModule(string module, string locator)
        {
            if (entities.Any(e => e.Type == ModuleType && e.Name.Equals(Q(module), StringComparison.OrdinalIgnoreCase))) return;
            var m = modules.FirstOrDefault(x => x.Name.Equals(module, StringComparison.OrdinalIgnoreCase));
            entities.Add(new(ModuleType, Q(module), locator, Attrs(("kind", m?.Kind.ToString()), ("codePath", m is null ? null : $"{wb}/{m.ExportFileName}"))));
        }

        void AddMacro(string module, string routine, string locator, bool found)
        {
            AddModule(module, locator);
            var full = Q($"{module}.{routine}");
            if (entities.Any(e => e.Type == MacroType && e.Name.Equals(full, StringComparison.OrdinalIgnoreCase))) return;
            var m = modules.FirstOrDefault(x => x.Name.Equals(module, StringComparison.OrdinalIgnoreCase));
            // The source this macro is in once the workbook is uploaded as code (OfficeIntake names it so).
            entities.Add(new(MacroType, full, locator, Attrs(("module", module), ("routine", routine), ("found", found),
                ("codePath", m is null ? null : $"{wb}/{m.ExportFileName}"))));
            links.Add(new(EntityVocabulary.Contains, new EntityRefFact(ModuleType, Q(module)), new EntityRefFact(MacroType, full), locator));
        }
    }

    public const string UserFormType = "user-form";

    private static int CountControls(IReadOnlyList<FormControl> controls) => controls.Sum(c => 1 + CountControls(c.Children));

    private static readonly System.Text.RegularExpressions.Regex HandlerRe = new(
        @"^[ \t]*(?:(?:Public|Private)[ \t]+)?Sub[ \t]+(\w+_\w+)\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static List<string> HandlerNames(string source) =>
        HandlerRe.Matches(source).Select(x => x.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every control depth-first, with its container, nesting depth and the label beside it.</summary>
    private static IEnumerable<(FormControl Control, FormControl? Parent, int Depth, string? Label)> Flatten(
        IReadOnlyList<FormControl> controls, FormControl? parent, int depth = 0)
    {
        foreach (var c in controls.OrderBy(c => c.TabIndex ?? int.MaxValue))
        {
            yield return (c, parent, depth, LabelFor(c, controls));
            foreach (var x in Flatten(c.Children, c, depth + 1)) yield return x;
        }
    }

    /// <summary>
    /// The caption of the Label a user reads as this input's name: on the same line and to its left (nearest
    /// first), else directly above. Only for inputs, which carry no caption of their own.
    /// </summary>
    private static string? LabelFor(FormControl c, IReadOnlyList<FormControl> siblings)
    {
        if (c.Kind is not ("TextBox" or "ComboBox" or "ListBox" or "SpinButton" or "ScrollBar")) return null;
        // A tall list's label sits by its first line, not its middle: compare against the top 20 pt only.
        var lineTop = c.Top;
        var lineBottom = c.Top + Math.Min(c.Height ?? 12, 20);
        var lineMid = (lineTop + lineBottom) / 2;
        var labels = siblings.Where(s => s.Kind == "Label" && s.Caption is not null && s.Width is not null).ToList();
        var left = labels
            .Where(l => l.Left + l.Width!.Value <= c.Left + 2
                        && Math.Min(l.Top + (l.Height ?? 12), lineBottom) - Math.Max(l.Top, lineTop) >= 2)   // overlaps the first line
            .OrderBy(l => (c.Left - (l.Left + l.Width!.Value)) + 3 * Math.Abs(l.Top + (l.Height ?? 12) / 2 - lineMid))
            .FirstOrDefault();
        var above = left is null
            ? labels.Where(l => l.Top + (l.Height ?? 0) <= c.Top + 2 && c.Top - (l.Top + (l.Height ?? 0)) <= 18 && Math.Abs(l.Left - c.Left) <= 24)
                .OrderBy(l => c.Top - l.Top).FirstOrDefault()
            : null;
        var caption = (left ?? above)?.Caption;
        return caption is null ? null : System.Text.RegularExpressions.Regex.Replace(caption, @"\s+", " ").Trim();
    }

    private static string Clip(string s, int max)
    {
        var one = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }

    /// <summary>Exposed for tests: the label a user reads as this input's name.</summary>
    internal static string? LabelForTest(FormControl c, IReadOnlyList<FormControl> siblings) => LabelFor(c, siblings);

    private static List<EntityFact> Dedupe(List<EntityFact> entities) =>
        entities.GroupBy(e => (e.Type, e.Name.ToLowerInvariant())).Select(g => g.First()).ToList();

    private static IReadOnlyDictionary<string, object?> Attrs(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in pairs) if (v is not null) d[k] = v;
        return d;
    }

    private static (string Kind, string Target) Split(string r)
    {
        var i = r.IndexOf(':');
        return i < 0 ? (r, "") : (r[..i], r[(i + 1)..]);
    }

    /// <summary><c>'Quote Sheet'!$B$4</c> → <c>Quote Sheet!B4</c>; a range or formula → null.</summary>
    private static string? CellOf(string refersTo, string? scope)
    {
        var m = System.Text.RegularExpressions.Regex.Match(refersTo.Trim(), @"^(?:'?(?<s>(?:[^'!]|'')+)'?!)?\$?(?<c>[A-Z]{1,3})\$?(?<r>\d+)$");
        if (!m.Success) return null;
        var sheet = m.Groups["s"].Success ? m.Groups["s"].Value.Replace("''", "'") : scope;
        return sheet is null ? null : $"{sheet}!{m.Groups["c"].Value}{m.Groups["r"].Value}";
    }

    private static string? SheetOf(string refersTo)
    {
        var bang = refersTo.IndexOf('!');
        if (bang <= 0 || refersTo.StartsWith('[')) return null;
        return refersTo[..bang].Trim('=', '\'').Replace("''", "'");
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
