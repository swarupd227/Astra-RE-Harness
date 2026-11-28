using System.Diagnostics;
using System.Text.Json;

namespace Astra.Api.Validation.Quality;

/// <summary>
/// TypeScript / JavaScript static analysis for the QUALITY gate, for packages with a web front end (react-python,
/// the angular-* stacks): <b>ESLint</b> with the recommended JavaScript and typescript-eslint rules, React's rules
/// of hooks and eslint-plugin-security, plus no-eval / no-implied-eval / no-new-func and a complexity limit.
/// ESLint and its plugins are installed in the API image under <c>/opt/astra-jstools</c> (override with
/// <c>ASTRA_JSTOOLS</c>) beside <see cref="ConfigFileName"/>; a generated package's own dependencies never meet them.
/// Where they are missing (a local dev box) the step reports it and the rest of the gate runs. No ESLint finding is
/// blocker or critical: the build gate already fails code that does not parse, and the rule scan blocks the
/// patterns that must never ship. The parser is pure and tested.
/// </summary>
public static class JsTools
{
    public const string ConfigFileName = "eslint.config.mjs";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private static readonly string[] Extensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];

    public static string Home => Environment.GetEnvironmentVariable("ASTRA_JSTOOLS") ?? "/opt/astra-jstools";

    /// <summary>A file ESLint reads: script source outside installed or built folders.</summary>
    internal static bool IsLinted(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Contains("node_modules/", StringComparison.Ordinal) || p.Contains("/dist/", StringComparison.Ordinal) || p.StartsWith("dist/", StringComparison.Ordinal)
            || p.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
            return false;
        return Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<(IReadOnlyList<QualityFinding> Findings, Dictionary<string, MavenClient.ToolRun> Runs)> RunAsync(
        IReadOnlyList<(string Path, string Content)> files, CancellationToken ct)
    {
        var runs = new Dictionary<string, MavenClient.ToolRun>();
        var js = files.Where(f => IsLinted(f.Path)).ToList();
        if (js.Count == 0) return ([], runs);
        var eslint = Path.Combine(Home, "node_modules", "eslint", "bin", "eslint.js");
        var config = Path.Combine(Home, ConfigFileName);
        if (!File.Exists(eslint) || !File.Exists(config))
        {
            runs["js-tools"] = new(false, 0, 0, $"ESLint is not installed at {Home}");
            return ([], runs);
        }

        var dir = Path.Combine(Path.GetTempPath(), $"astra-jsquality-{Guid.NewGuid():N}");
        try
        {
            foreach (var (path, content) in js)
            {
                var abs = Path.Combine(dir, path.Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                await File.WriteAllTextAsync(abs, content, ct);
            }
            // The config sits at the root of the copy (its file patterns are relative to it) and finds its plugins
            // through a link to the installed node_modules.
            File.Copy(config, Path.Combine(dir, ConfigFileName));
            Directory.CreateSymbolicLink(Path.Combine(dir, "node_modules"), Path.Combine(Home, "node_modules"));

            var (code, stdout, ms) = await Run("node", [eslint, "--format", "json", "--no-error-on-unmatched-pattern", "--no-warn-ignored", "."], dir, ct);
            // ESLint exits 1 when it finds problems; its JSON is still complete. 2 is a crash or a bad config.
            var ok = code is 0 or 1 && stdout.TrimStart().StartsWith('[');
            var found = ok ? ParseEslint(stdout, dir) : [];
            runs["eslint"] = new(ok, found.Count, ms, ok ? null : Tail(stdout));
            return (found, runs);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    private static readonly HashSet<string> MajorSecurity = new(StringComparer.Ordinal)
    {
        "no-eval", "no-implied-eval", "no-new-func", "security/detect-eval-with-expression", "security/detect-child-process",
        "security/detect-unsafe-regex", "security/detect-bidi-characters", "security/detect-pseudoRandomBytes",
        "security/detect-disable-mustache-escape", "security/detect-buffer-noassert", "security/detect-new-buffer",
    };

    internal static IReadOnlyList<QualityFinding> ParseEslint(string json, string root)
    {
        var list = new List<QualityFinding>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var file in doc.RootElement.EnumerateArray())
        {
            var path = Rel(file.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "", root);
            if (!file.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) continue;
            foreach (var m in messages.EnumerateArray())
            {
                var rule = m.TryGetProperty("ruleId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : null;
                int? line = m.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
                var msg = (m.TryGetProperty("message", out var mm) ? mm.GetString() ?? "" : "").Trim();
                var (severity, category, why) = Classify(rule);
                list.Add(new($"ESLINT-{rule ?? "parse"}", category, severity, path, line, msg, why,
                    rule is null ? "Fix the syntax so the file parses." : $"Change the code so ESLint's `{rule}` rule no longer applies.",
                    Source: "tool"));
            }
        }
        return list;
    }

    internal static (string Severity, string Category, string Why) Classify(string? rule) => rule switch
    {
        null => ("major", "standards", "ESLint could not parse the file, so nothing else in it was checked."),
        _ when MajorSecurity.Contains(rule) => ("major", "security",
            "Code evaluated from a string, spawned processes and unsafe regular expressions are known routes to injection and denial of service."),
        _ when rule.StartsWith("security/", StringComparison.Ordinal) => ("minor", "security",
            "eslint-plugin-security flags this pattern as a possible weakness worth a second look."),
        "react-hooks/rules-of-hooks" => ("major", "standards",
            "A hook called conditionally or outside a component breaks React's state between renders."),
        "complexity" => ("minor", "maintainability",
            "The function is past the complexity limit; such code is hard to test and change safely."),
        _ => ("minor", "standards", "ESLint flags this as a likely bug or a breach of TypeScript / React best practice."),
    };

    private static string Rel(string path, string root)
    {
        var p = path.Replace('\\', '/');
        var r = root.Replace('\\', '/').TrimEnd('/') + "/";
        if (p.StartsWith(r, StringComparison.Ordinal)) p = p[r.Length..];
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    private static string Tail(string s) => s.Length <= 1500 ? s : s[^1500..];

    private static async Task<(int Code, string Stdout, int Ms)> Run(string exe, string[] args, string cwd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (124, "timed out", (int)sw.ElapsedMilliseconds);
        }
        var output = await stdout;
        if (p.ExitCode is not (0 or 1)) output += "\n" + await stderr;
        return (p.ExitCode, output, (int)sw.ElapsedMilliseconds);
    }
}
