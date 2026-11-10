using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

internal sealed record ProcedureRun(string Name, string? Arguments);
internal sealed record PackageRun(string Name, string Path);
internal sealed record FileUse(string Path, string Tool);

/// <summary>What the command text of a scheduled job starts or touches.</summary>
internal sealed record ScriptCommands(
    IReadOnlyList<ProcedureRun> Procedures,
    IReadOnlyList<PackageRun> Packages,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<FileUse> FilesWritten,
    IReadOnlyList<FileUse> FilesRead,
    IReadOnlyList<string> TablesRead,
    IReadOnlyList<string> TablesWritten)
{
    public static readonly ScriptCommands None = new(
        Array.Empty<ProcedureRun>(), Array.Empty<PackageRun>(), Array.Empty<string>(), Array.Empty<FileUse>(),
        Array.Empty<FileUse>(), Array.Empty<string>(), Array.Empty<string>());

    public bool IsEmpty =>
        Procedures.Count + Packages.Count + Scripts.Count + FilesWritten.Count + FilesRead.Count + TablesRead.Count + TablesWritten.Count == 0;
}

/// <summary>
/// Reads the command text of a job (a shell line, a few lines of script) for the things it runs:
/// <c>EXEC usp_X @Mode='A'</c> (also inside <c>sqlcmd -Q "…"</c>), <c>dtexec /F package.dtsx</c>,
/// <c>bcp … queryout \\share\file</c>, <c>powershell D:\Scripts\x.ps1</c>. Pure text rules — a command in a
/// form it does not know is simply not reported, never guessed.
/// </summary>
internal static class ScriptCommandReader
{
    private const string NamePart = @"(?:\[[^\]]+\]|\w+)";

    private static readonly Regex Exec = new(
        @"\bEXEC(?:UTE)?\s+(?!@)(?:" + NamePart + @"\s*\.\s*)?\[?(?<p>[A-Za-z_]\w*)\]?(?<args>[^\r\n""]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DtexecFile = new(
        @"\bdtexec(?:\.exe)?\b[^\r\n]*?\s/(?:F|File)\s+(?<q>""?)(?<path>[^""\r\n]+?)\k<q>(?=\s+/|\s*$|\s*[\r\n])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DtexecStored = new(
        @"\bdtexec(?:\.exe)?\b[^\r\n]*?\s/(?:SQ|SQL)\s+(?<q>""?)(?<name>[^""\s]+)\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BcpQuery = new(
        @"\bbcp(?:\.exe)?\s+""(?<q>[^""]+)""\s+(?<dir>queryout|out|in)\s+(?<path>""[^""]+""|\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BcpTable = new(
        @"\bbcp(?:\.exe)?\s+(?<table>[^\s""]+)\s+(?<dir>out|in)\s+(?<path>""[^""]+""|\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptPath = new(
        @"(?<!\w)(?<path>(?:[A-Za-z]:\\|\\\\)[^\s""'<>|]+\.(?:ps1|bat|cmd|vbs|py|sh)|/[\w./-]+\.(?:sh|py))(?!\w)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuotedSql = new(
        @"(?:-Q|-q|-Query|-query)\s+""(?<sql>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ScriptCommands Read(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return ScriptCommands.None;

        var procedures = new List<ProcedureRun>();
        var packages = new List<PackageRun>();
        var scripts = new List<string>();
        var written = new List<FileUse>();
        var read = new List<FileUse>();
        var tablesRead = new List<string>();
        var tablesWritten = new List<string>();

        foreach (Match m in Exec.Matches(script))
        {
            var name = m.Groups["p"].Value;
            if (name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase)) continue;
            var args = m.Groups["args"].Value.Trim().TrimEnd(';').Trim();
            if (!procedures.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                procedures.Add(new ProcedureRun(name, args.Length == 0 ? null : args));
        }

        foreach (Match m in DtexecFile.Matches(script))
        {
            var path = m.Groups["path"].Value.Trim();
            var name = PackageName(path);
            if (!packages.Any(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) packages.Add(new PackageRun(name, path));
        }
        foreach (Match m in DtexecStored.Matches(script))
        {
            var name = m.Groups["name"].Value.Trim('\\', '/');
            name = name[(Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/')) + 1)..];
            if (!packages.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) packages.Add(new PackageRun(name, m.Groups["name"].Value));
        }

        foreach (Match m in BcpQuery.Matches(script))
        {
            var path = m.Groups["path"].Value.Trim('"');
            AddFile(m.Groups["dir"].Value.Equals("in", StringComparison.OrdinalIgnoreCase) ? read : written, path, "bcp");
            var (r, w) = SqlTables(m.Groups["q"].Value);
            tablesRead.AddRange(r);
            tablesWritten.AddRange(w);
        }
        foreach (Match m in BcpTable.Matches(script))
        {
            // "bcp "SELECT …" queryout" is the form above; this is "bcp TLR_POSTED out file".
            if (BcpQuery.IsMatch(m.Value)) continue;
            var table = SqlPatternReader.Normalise(m.Groups["table"].Value);
            if (table.Length == 0 || table.Contains('\\') || table.Contains('/') || table.Contains(':')) continue;
            var inbound = m.Groups["dir"].Value.Equals("in", StringComparison.OrdinalIgnoreCase);
            AddFile(inbound ? read : written, m.Groups["path"].Value.Trim('"'), "bcp");
            (inbound ? tablesWritten : tablesRead).Add(table);
        }

        foreach (Match m in QuotedSql.Matches(script))
        {
            var (r, w) = SqlTables(m.Groups["sql"].Value);
            tablesRead.AddRange(r);
            tablesWritten.AddRange(w);
        }

        foreach (Match m in ScriptPath.Matches(script))
        {
            var path = m.Groups["path"].Value;
            if (!scripts.Contains(path, StringComparer.OrdinalIgnoreCase)) scripts.Add(path);
        }

        return new ScriptCommands(
            procedures, packages, scripts, written, read,
            tablesRead.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            tablesWritten.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary><c>D:\SSIS\DrawerRecon.dtsx</c> runs the package <c>DrawerRecon</c>.</summary>
    public static string PackageName(string path)
    {
        var file = path[(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1)..];
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    private static void AddFile(List<FileUse> into, string path, string tool)
    {
        if (path.Length > 0 && !into.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) into.Add(new FileUse(path, tool));
    }

    private static (IReadOnlyList<string> Read, IReadOnlyList<string> Written) SqlTables(string sql)
    {
        var o = SqlPatternReader.ScanBody(sql);
        return (o.Reads, o.Writes);
    }
}
