using System.Text.RegularExpressions;

namespace Astra.Api.Assembly;

/// <summary>One code unit's checked package, as the assembler receives it (paths relative to the package root).</summary>
public sealed record UnitPackage(
    Guid UnitId,
    string UnitName,
    string SourcePath,
    Guid ScaffoldId,
    IReadOnlyDictionary<string, string> Files);

/// <summary>What to call the assembled application.</summary>
public sealed record AssemblyOptions(string ModuleName, string? BasePackage = null);

/// <summary>A unit that went into the application, and the package its code now lives in.</summary>
public sealed record AssembledUnit(Guid UnitId, string UnitName, string SourcePath, Guid ScaffoldId, string Package, int FileCount);

/// <summary>A file or class that several units carried identically and the application now holds once.</summary>
public sealed record SharedItem(string Kind, string Name, string Location, IReadOnlyList<string> Units);

/// <summary>Something two units disagree on; the application keeps one and says which.</summary>
public sealed record AssemblyConflict(string Kind, string Name, string Detail, IReadOnlyList<string> Units);

/// <summary>Everything the assembler did, so a reviewer can trust the merged project without diffing it.</summary>
public sealed record AssemblyReport(
    string ArtifactId,
    string BasePackage,
    string ApplicationClass,
    IReadOnlyList<AssembledUnit> Units,
    IReadOnlyList<SharedItem> Shared,
    IReadOnlyList<AssemblyConflict> Conflicts,
    IReadOnlyList<string> Notes);

public sealed record AssembledModule(IReadOnlyDictionary<string, string> Files, AssemblyReport Report);

/// <summary>
/// Turns many per-unit packages of one target stack into a single buildable project. Deterministic and pure: the same
/// packages in the same order give the same files byte for byte, and no model is involved. One implementation per
/// target stack; <see cref="ModuleAssemblers.For"/> finds it.
/// </summary>
public interface IModuleAssembler
{
    string TargetStack { get; }
    AssembledModule Assemble(IReadOnlyList<UnitPackage> units, AssemblyOptions options);
}

public static class ModuleAssemblers
{
    private static readonly IModuleAssembler[] All = { new JavaSpringAssembler() };

    /// <summary>The assembler for a target stack, or null when that stack cannot be assembled yet.</summary>
    public static IModuleAssembler? For(string targetStack) =>
        All.FirstOrDefault(a => string.Equals(a.TargetStack, targetStack, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Supported => All.Select(a => a.TargetStack).ToList();

    /// <summary>"Third parties (societe)" → "third-parties-societe": the slug used for artifact ids and file names.</summary>
    public static string Slug(string name)
    {
        var plain = string.Concat(name.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark));
        var s = Regex.Replace(plain.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 48) s = s[..48].Trim('-');
        return s.Length == 0 ? "module" : s;
    }
}
