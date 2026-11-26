using System.Text;
using Astra.Api.Llm.Prompts;

namespace Astra.Api.Llm;

/// <summary>
/// Quality gate Q5: the coding and security standards every generated package is written to. One file per
/// language under <c>Llm/Prompts/common/coding-standards.&lt;language&gt;.md</c>, stated in the same terms as the
/// QUALITY gate's rules and tools (QualityRules, SpotBugs/PMD/Checkstyle, ruff/bandit), so the generator is told
/// up front what the gate will check afterwards. The section is appended to the system block of every scaffold
/// prompt; it is the same text for every routine of a target stack, so the prompt cache still holds.
/// </summary>
public static class CodingStandards
{
    public const string Heading = "## Coding and security standards";

    /// <summary>The languages a target stack's packages are written in, back end first.</summary>
    public static IReadOnlyList<string> LanguagesFor(string targetStack)
    {
        var t = targetStack.ToLowerInvariant();
        if (t == "angular-java") return ["java", "typescript"];
        if (t == "angular-dotnet8") return ["csharp", "typescript"];
        if (t == "react-python") return ["python", "typescript"];
        if (t.StartsWith("java")) return ["java"];
        if (t.StartsWith("dotnet")) return ["csharp"];
        return [];
    }

    public static string FileName(string language) => $"coding-standards.{language}.md";

    /// <summary>The standards section for a target stack, or "" when none is registered for its languages.</summary>
    public static string SectionFor(string targetStack, Func<string, string?> readAsset)
    {
        var parts = LanguagesFor(targetStack)
            .Select(l => readAsset(FileName(l)))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
        if (parts.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine(Heading).AppendLine();
        sb.AppendLine("Write every file to these standards. The package is checked against them after generation and");
        sb.AppendLine("sent back for repair when it breaks one. Where a standard conflicts with a rule above (names, the");
        sb.AppendLine("reference's file set, the build file, a faithful structure), the rule above wins; never add a");
        sb.AppendLine("dependency to meet a standard.");
        foreach (var p in parts) sb.AppendLine().AppendLine(p);
        return sb.ToString().TrimEnd();
    }

    public static string SectionFor(string targetStack, PromptLibrary prompts) =>
        SectionFor(targetStack, f => prompts.TryReadAsset("common", f));

    /// <summary>The system block with the standards appended (unchanged when there are none).</summary>
    public static string AppendTo(string system, string section) =>
        string.IsNullOrEmpty(section) ? system : system.TrimEnd() + "\n\n" + section + "\n";
}
