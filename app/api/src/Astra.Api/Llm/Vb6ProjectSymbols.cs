using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Llm;

/// <summary>
/// Where a VB6 project's names live, so a unit converted on its own stubs another unit correctly: a routine of a
/// standard module becomes a member of that module's static class (not a class named after the routine), a global
/// variable a field of the module that declares it, a class or form a class of its own. Built from the project's
/// files (each one's VB_Name and module-level Public/Global declarations) and its parsed routines; filtered to the
/// names a unit's source actually uses. Pure.
/// </summary>
public sealed class Vb6ProjectSymbols
{
    public sealed record Symbol(string Name, string Kind, string Owner, string OwnerKind, string Declaration);

    private readonly Dictionary<string, List<Symbol>> _byName = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex VbName = new(@"^\s*Attribute\s+VB_Name\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Declaration = new(
        @"^\s*(Public|Global)\s+(?:(Const)\s+(\w+)|(Type)\s+(\w+)|(Enum)\s+(\w+)|(Declare)\s+(?:PtrSafe\s+)?(?:Function|Sub)\s+(\w+)|(?:WithEvents\s+)?(\w+)(?:\s*\([^)]*\))?\s+As\s+(?:New\s+)?([\w.]+))",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"\b[A-Za-z_]\w*\b", RegexOptions.CultureInvariant);

    /// <param name="files">Every VB6 file of the project: path and text.</param>
    /// <param name="routines">Every parsed routine: name, signature line, file path.</param>
    public static Vb6ProjectSymbols Build(IEnumerable<(string Path, string Text)> files, IEnumerable<(string Name, string Signature, string Path)> routines)
    {
        var s = new Vb6ProjectSymbols();
        var owners = new Dictionary<string, (string Name, string Kind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in files)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var kind = ext switch { ".bas" => "module", ".cls" => "class", ".frm" => "form", ".ctl" => "control", _ => null };
            if (kind is null) continue;
            var name = VbName.Match(text) is { Success: true } m ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(path);
            owners[path] = (name, kind);
            s.Add(new Symbol(name, kind, name, kind, $"{kind} {name} ({path})"));
            if (kind != "module") continue;   // only a standard module's Public declarations are project-wide
            // Module-level declarations come before the first routine.
            var head = text;
            var firstRoutine = Regex.Match(text, @"^\s*(Public\s+|Private\s+|Friend\s+)?(Static\s+)?(Sub|Function|Property)\s", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (firstRoutine.Success) head = text[..firstRoutine.Index];
            foreach (Match d in Declaration.Matches(head))
            {
                var line = d.Value.Trim();
                if (d.Groups[2].Success) s.Add(new(d.Groups[3].Value, "constant", name, kind, line));
                else if (d.Groups[4].Success) s.Add(new(d.Groups[5].Value, "type", name, kind, line));
                else if (d.Groups[6].Success) s.Add(new(d.Groups[7].Value, "enum", name, kind, line));
                else if (d.Groups[8].Success) s.Add(new(d.Groups[9].Value, "declare", name, kind, line));
                else if (d.Groups[10].Success) s.Add(new(d.Groups[10].Value, "variable", name, kind, line));
            }
        }
        foreach (var (rname, signature, path) in routines)
        {
            if (!owners.TryGetValue(path, out var o)) continue;
            if (o.Kind == "module" && signature.TrimStart().StartsWith("Private", StringComparison.OrdinalIgnoreCase)) continue;
            s.Add(new Symbol(rname, "routine", o.Name, o.Kind, signature.Trim()));
        }
        return s;
    }

    private void Add(Symbol sym)
    {
        if (!_byName.TryGetValue(sym.Name, out var list)) _byName[sym.Name] = list = new List<Symbol>();
        if (!list.Any(x => x.Owner == sym.Owner && x.Kind == sym.Kind)) list.Add(sym);
    }

    public IReadOnlyList<Symbol> Lookup(string name) => _byName.TryGetValue(name, out var l) ? l : Array.Empty<Symbol>();

    /// <summary>
    /// The project names <paramref name="unitText"/> uses that another unit declares, one line each, grouped by the
    /// unit that declares them — what the converter needs to stub the right kind of thing in the right place. A
    /// form's or class's own members are left out (reaching them needs an object, which the source names).
    /// </summary>
    public string RenderFor(string unitText, string unitOwner, int maxLines = 300)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in unitText.Replace("\r\n", "\n").Split('\n'))
        {
            var code = line.TrimStart().StartsWith('\'') ? "" : Regex.Replace(line, @"""[^""]*""", " ");
            foreach (Match m in Identifier.Matches(code)) used.Add(m.Value);
        }
        var hits = used.SelectMany(Lookup)
            .Where(x => !string.Equals(x.Owner, unitOwner, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Kind != "routine" || x.OwnerKind == "module")
            .OrderBy(x => x.Owner, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxLines)
            .ToList();
        if (hits.Count == 0) return "(this unit uses nothing declared in another unit of the project)";
        var sb = new StringBuilder();
        foreach (var g in hits.GroupBy(x => (x.Owner, x.OwnerKind)))
        {
            sb.Append($"- {g.Key.OwnerKind} `{g.Key.Owner}`:\n");
            foreach (var x in g.Where(x => x.Kind != g.Key.OwnerKind))
                sb.Append($"    - {x.Kind} `{x.Name}` — `{x.Declaration}`\n");
        }
        return sb.ToString().TrimEnd();
    }
}
