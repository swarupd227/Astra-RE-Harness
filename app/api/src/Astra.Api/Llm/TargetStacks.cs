using System.Text.RegularExpressions;

namespace Astra.Api.Llm;

/// <summary>
/// Which target stack a routine is migrated to, decided in one place. A project may name its target
/// (<c>ProjectSettings.TargetStack</c>); otherwise each source language has a default (COBOL, UniBasic, OpenEdge,
/// Java and PHP go to Java/Spring, the .NET-bound languages to .NET 10). Extraction picks its prompt from this
/// target, so a spec is written for the stack the code is actually going to — the assessment recommends the same
/// default.
/// </summary>
public static class TargetStacks
{
    /// <summary>The target every .NET-bound language falls back to, and the last resort for an extract prompt.</summary>
    public const string Fallback = "dotnet10";

    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fortran-f77"] = "dotnet10",
        ["cpp"] = "dotnet10",
        ["delphi"] = "dotnet10",
        ["vb6"] = "dotnet10-blazor",
        ["vbnet"] = "dotnet10-csharp",
        ["csharp"] = "dotnet10-webapi",
        ["cobol"] = "java-spring",
        ["unibasic"] = "java-spring",
        ["openedge"] = "java-spring",
        ["abl"] = "java-spring",
        ["java"] = "java-spring",
        ["php"] = "java-spring",
    };

    // dotnet10 has variants (dotnet10-blazor, dotnet10-faithful …) that share its scaffold family.
    private static readonly Regex Known = new(
        "^(dotnet8|dotnet10|dotnet10-[a-z0-9]+(-[a-z0-9]+)*|java-spring|angular-dotnet8|angular-java|react-python)$",
        RegexOptions.Compiled);

    /// <summary>The default target for a source language, or null when the platform has none for it.</summary>
    public static string? DefaultFor(string? sourceLanguage) =>
        sourceLanguage is not null && Defaults.TryGetValue(sourceLanguage, out var t) ? t : null;

    /// <summary>Whether <paramref name="stack"/> names a target stack the platform has.</summary>
    public static bool IsKnown(string? stack) => stack is not null && Known.IsMatch(stack);

    /// <summary>
    /// The targets to try, best first: the project's own target, the language's default, then
    /// <see cref="Fallback"/>. Duplicates and unknown names are dropped.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? projectTarget, string? sourceLanguage)
    {
        var list = new List<string>();
        void Add(string? s)
        {
            if (IsKnown(s) && !list.Contains(s!, StringComparer.OrdinalIgnoreCase)) list.Add(s!);
        }
        Add(projectTarget);
        Add(DefaultFor(sourceLanguage));
        Add(Fallback);
        return list;
    }

    /// <summary>
    /// The first candidate target that has a prompt of <paramref name="hasPrompt"/>; <see cref="Fallback"/> when none
    /// does (the provider then applies its own any-target fallback, as before).
    /// </summary>
    public static string Resolve(string? projectTarget, string? sourceLanguage, Func<string, bool> hasPrompt)
    {
        foreach (var c in Candidates(projectTarget, sourceLanguage))
            if (hasPrompt(c)) return c;
        return Fallback;
    }
}
