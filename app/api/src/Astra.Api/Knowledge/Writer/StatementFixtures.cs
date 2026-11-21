using System.Text.RegularExpressions;

namespace Astra.Api.Knowledge;

/// <summary>
/// Canned answers for the offline writer, found by the call's replay key. They are how a screen or a test sees a
/// statement that is unverified, one the gate drops, or a conflict without a model: the fixture says exactly what a
/// writer would have said, and everything after it (parsing, the gate, the support check, the status) runs for real.
/// </summary>
public interface IStatementFixtures
{
    /// <summary>The tool-input JSON for a call, or null when there is no fixture for it.</summary>
    string? Find(string replayKey);
}

public sealed class NoStatementFixtures : IStatementFixtures
{
    public string? Find(string replayKey) => null;
}

/// <summary>Fixtures kept as files, one per replay key, in the <c>fixtures</c> folder of a pack.</summary>
public sealed class DirectoryStatementFixtures : IStatementFixtures
{
    private readonly IReadOnlyList<string> _directories;

    public DirectoryStatementFixtures(IEnumerable<string> directories) { _directories = directories.ToList(); }

    /// <summary><c>runbook:runbook:failure-handling:write:1</c> is the file <c>runbook_runbook_failure-handling_write_1.json</c>.</summary>
    public static string FileNameFor(string replayKey) =>
        Regex.Replace(replayKey.Trim(), @"[^A-Za-z0-9.\-]+", "_").Trim('_') + ".json";

    public string? Find(string replayKey)
    {
        if (string.IsNullOrWhiteSpace(replayKey)) return null;
        var name = FileNameFor(replayKey);
        foreach (var dir in _directories)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
