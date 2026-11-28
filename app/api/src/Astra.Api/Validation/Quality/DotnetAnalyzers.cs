using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation.Quality;

/// <summary>
/// The .NET SDK's own code analyzers for the QUALITY gate, for packages on a .NET stack (dotnet8, dotnet10 and its
/// variants, angular-dotnet8's backend/): a <c>dotnet build</c> with the analyzers switched on at the latest analysis
/// level, every security rule enabled (SQL, command and XSS injection, weak crypto, insecure deserialisation) and the
/// recommended reliability rules. Each <c>CAxxxx</c> warning becomes a finding; the build's own verdict stays with the
/// COMPILE gate. It takes a slot in <see cref="GateExecutorPool"/> like any build. Injection rules are critical (they
/// fail the gate, as the rule scan's injection patterns do); other security rules are major; the rest minor.
/// </summary>
public static partial class DotnetAnalyzers
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(150);

    internal static readonly string[] BuildArguments =
    [
        "build", "--nologo", "-clp:NoSummary", "-p:EnableNETAnalyzers=true", "-p:AnalysisLevel=latest",
        "-p:AnalysisModeSecurity=All", "-p:AnalysisModeReliability=Recommended", "-p:TreatWarningsAsErrors=false",
        "-p:RunAnalyzersDuringBuild=true",
    ];

    /// <summary>The folder of the package the .NET build runs in, or null when the stack has no .NET code.</summary>
    internal static string? Prefix(string targetPlatform) =>
        targetPlatform.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ? ""
        : string.Equals(targetPlatform, "angular-dotnet8", StringComparison.OrdinalIgnoreCase) ? "backend/"
        : null;

    public static async Task<(IReadOnlyList<QualityFinding> Findings, Dictionary<string, MavenClient.ToolRun> Runs)> RunAsync(
        string targetPlatform, IReadOnlyList<(string Path, string Content)> files, CancellationToken ct)
    {
        var runs = new Dictionary<string, MavenClient.ToolRun>();
        var prefix = Prefix(targetPlatform);
        if (prefix is null) return ([], runs);
        var sources = files.Select(f => (Path: f.Path.Replace('\\', '/'), f.Content))
            .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f => (Path: f.Path[prefix.Length..], f.Content)).ToList();
        if (!sources.Any(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))) return ([], runs);

        var dir = Path.Combine(Path.GetTempPath(), $"astra-dnquality-{Guid.NewGuid():N}");
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var (path, content) in sources)
            {
                var abs = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                await File.WriteAllTextAsync(abs, content, ct);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var slot = await GateExecutorPool.Shared.AcquireAsync(timeout.Token);
            var code = 0;
            var logs = new StringBuilder();
            foreach (var target in BuildTargets(sources.Select(s => s.Path)))
            {
                var (c, l) = await Build(dir, target, timeout.Token);
                logs.Append(l);
                if (c == 124) { code = 124; break; }
                if (c != 0) code = c;
            }
            var log = logs.ToString();
            if (code == 124)
            {
                runs["dotnet-analyzers"] = new(false, 0, (int)sw.ElapsedMilliseconds, $"the analyzer build did not finish within {Timeout.TotalSeconds:0} s");
                return ([], runs);
            }
            var found = ParseBuildLog(log, dir, prefix);
            // A build that fails stops the analyzers part-way; what they reported is kept, and the run says why.
            runs["dotnet-analyzers"] = new(code == 0, found.Count, (int)sw.ElapsedMilliseconds,
                code == 0 ? null : $"the code does not build, so the analyzers could not finish: {FirstError(log)}");
            return (found, runs);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            runs["dotnet-analyzers"] = new(false, 0, (int)sw.ElapsedMilliseconds, $"no build slot or no result within {Timeout.TotalSeconds:0} s");
            return ([], runs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            runs["dotnet-analyzers"] = new(false, 0, (int)sw.ElapsedMilliseconds, ex.Message);
            return ([], runs);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    // /tmp/x/src/Foo.cs(12,5): warning CA2100: Review if the query string ... [/tmp/x/src/App.csproj]
    [GeneratedRegex(@"^[ \t]*(?<file>[^\s(][^(\r\n]*?)\((?<line>\d+),(?<col>\d+)\):[ \t]+warning[ \t]+(?<rule>CA\d{4}):[ \t]+(?<msg>[^\r\n]*?)([ \t]+\[[^\]\r\n]+\])?[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex WarningLine();

    private static readonly HashSet<string> Injection = new(StringComparer.Ordinal)
    {
        "CA2100", "CA3001", "CA3002", "CA3003", "CA3004", "CA3005", "CA3006", "CA3007", "CA3008", "CA3009", "CA3010",
        "CA3011", "CA3012",
    };

    internal static IReadOnlyList<QualityFinding> ParseBuildLog(string log, string root, string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<QualityFinding>();
        foreach (Match m in WarningLine().Matches(log))
        {
            var rule = m.Groups["rule"].Value;
            var file = prefix + Rel(m.Groups["file"].Value, root);
            var line = int.Parse(m.Groups["line"].Value);
            var msg = m.Groups["msg"].Value.Trim();
            // A project built for two frameworks, or reached twice, reports the same warning twice.
            if (!seen.Add($"{rule}|{file}|{line}|{msg}")) continue;
            var (severity, category, why) = Classify(rule);
            list.Add(new($"DOTNET-{rule}", category, severity, file, line, msg, why,
                $"Change the code so {rule} no longer applies (see the rule's page on learn.microsoft.com).", Source: "tool"));
        }
        return list;
    }

    internal static (string Severity, string Category, string Why) Classify(string rule) => rule switch
    {
        _ when Injection.Contains(rule) => ("critical", "security",
            "Input reaches a query, a command, a path or a page without being made safe: a known route to injection."),
        _ when rule.StartsWith("CA5", StringComparison.Ordinal) || rule.StartsWith("CA3", StringComparison.Ordinal) || rule.StartsWith("CA23", StringComparison.Ordinal) => ("major", "security",
            "The .NET security analyzers flag this as weak cryptography, unsafe deserialisation or an insecure setting."),
        _ when rule.StartsWith("CA15", StringComparison.Ordinal) => ("minor", "maintainability",
            "The code-analysis maintainability rules flag this as hard to change safely."),
        _ => ("minor", "standards", "The .NET code analyzers flag this as a likely bug or against .NET guidance."),
    };

    /// <summary>The first error the build printed, without its project suffix, for the run's note.</summary>
    internal static string FirstError(string log)
    {
        var lines = log.Split('\n').Select(l => l.Trim()).ToList();
        var line = lines.FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))
            ?? lines.LastOrDefault(l => l.Length > 0) ?? "no output";
        var bracket = line.LastIndexOf(" [", StringComparison.Ordinal);
        if (bracket > 0 && line.EndsWith(']')) line = line[..bracket];
        return line.Length <= 300 ? line : line[..300];
    }

    private static string Rel(string path, string root)
    {
        var p = path.Replace('\\', '/').Trim();
        var r = root.Replace('\\', '/').TrimEnd('/') + "/";
        return p.StartsWith(r, StringComparison.Ordinal) ? p[r.Length..] : p;
    }

    /// <summary>What to build: the solution at the root when there is one (the whole package at once), otherwise
    /// every project, shallowest first (a project another one references is built again; its warnings are kept once).</summary>
    internal static IReadOnlyList<string?> BuildTargets(IEnumerable<string> paths)
    {
        var list = paths.Select(p => p.Replace('\\', '/')).ToList();
        if (list.Any(p => !p.Contains('/') && (p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))))
            return [null];
        return list.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.Ordinal).Cast<string?>().ToList();
    }

    private static async Task<(int Code, string Log)> Build(string cwd, string? target, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(BuildArguments[0]);
        if (target is not null) psi.ArgumentList.Add(target);
        foreach (var a in BuildArguments.Skip(1)) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (!ct.IsCancellationRequested) throw;
            return (124, output.ToString());
        }
        return (p.ExitCode, output.ToString());
    }
}
