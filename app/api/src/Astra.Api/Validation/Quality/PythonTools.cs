using System.Diagnostics;
using System.Text.Json;

namespace Astra.Api.Validation.Quality;

/// <summary>
/// Python static analysis for the QUALITY gate (Q4), for packages with Python code (react-python's backend):
/// <b>ruff</b> for bugs, undefined names and complexity (rules F, E9, B, C901) and <b>bandit</b> for security
/// (injection, unsafe deserialisation, weak crypto, shell calls, hard-coded secrets, insecure requests). Both are
/// installed in the API image under <c>/opt/astra-pytools</c> (override with <c>ASTRA_PYTOOLS</c>); where they are
/// missing (a local dev box) the step reports it and the rest of the gate runs. The parsers are pure and tested.
/// </summary>
public static class PythonTools
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static string BinDir => Environment.GetEnvironmentVariable("ASTRA_PYTOOLS") ?? "/opt/astra-pytools/bin";

    public static async Task<(IReadOnlyList<QualityFinding> Findings, Dictionary<string, MavenClient.ToolRun> Runs)> RunAsync(
        IReadOnlyList<(string Path, string Content)> files, CancellationToken ct)
    {
        var runs = new Dictionary<string, MavenClient.ToolRun>();
        var py = files.Where(f => f.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)).ToList();
        if (py.Count == 0) return ([], runs);
        var ruff = Path.Combine(BinDir, "ruff");
        var bandit = Path.Combine(BinDir, "bandit");
        if (!File.Exists(ruff) && !File.Exists(bandit))
        {
            runs["python-tools"] = new(false, 0, 0, $"ruff and bandit are not installed at {BinDir}");
            return ([], runs);
        }

        var dir = Path.Combine(Path.GetTempPath(), $"astra-pyquality-{Guid.NewGuid():N}");
        var findings = new List<QualityFinding>();
        try
        {
            foreach (var (path, content) in py)
            {
                var abs = Path.Combine(dir, path.Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                await File.WriteAllTextAsync(abs, content, ct);
            }
            if (File.Exists(ruff))
            {
                var (code, stdout, ms) = await Run(ruff, ["check", ".", "--output-format", "json", "--no-cache", "--exit-zero",
                    "--select", "F,E9,B,C901", "--config", "lint.mccabe.max-complexity = 15"], dir, ct);
                var found = code == 0 ? ParseRuff(stdout, dir) : [];
                findings.AddRange(found);
                runs["ruff"] = new(code == 0, found.Count, ms, code == 0 ? null : Tail(stdout));
            }
            if (File.Exists(bandit))
            {
                var (code, stdout, ms) = await Run(bandit, ["-r", ".", "-f", "json", "-q", "-x", "./tests,./backend/tests,./.venv"], dir, ct);
                // bandit exits 1 when it finds issues; its JSON is still complete.
                var ok = code is 0 or 1 && stdout.TrimStart().StartsWith('{');
                var found = ok ? ParseBandit(stdout, dir) : [];
                findings.AddRange(found);
                runs["bandit"] = new(ok, found.Count, ms, ok ? null : Tail(stdout));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
        return (findings, runs);
    }

    internal static IReadOnlyList<QualityFinding> ParseRuff(string json, string root)
    {
        var list = new List<QualityFinding>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var f in doc.RootElement.EnumerateArray())
        {
            var code = f.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "RUFF";
            var file = Rel(f.GetProperty("filename").GetString() ?? "", root);
            int? line = f.TryGetProperty("location", out var loc) && loc.TryGetProperty("row", out var row) ? row.GetInt32() : null;
            var msg = f.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var (severity, category) = code switch
            {
                _ when code.StartsWith("E9") || code is "F821" or "F811" or "F823" or "F632" => ("major", "standards"),
                "C901" => ("minor", "maintainability"),
                _ when code.StartsWith("B") => ("minor", "standards"),
                _ => ("minor", "standards"),
            };
            list.Add(new($"RUFF-{code}", category, severity, file, line, msg,
                category == "maintainability"
                    ? "The function is past the complexity limit; such code is hard to test and change safely."
                    : "ruff flags this as a likely bug or a breach of Python best practice.",
                $"Change the code so ruff's {code} no longer applies.", Source: "tool"));
        }
        return list;
    }

    internal static IReadOnlyList<QualityFinding> ParseBandit(string json, string root)
    {
        var list = new List<QualityFinding>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            var sev = r.GetProperty("issue_severity").GetString() ?? "LOW";
            var conf = r.GetProperty("issue_confidence").GetString() ?? "LOW";
            var severity = (sev, conf) switch
            {
                ("HIGH", "HIGH" or "MEDIUM") => "critical",
                ("HIGH", _) or ("MEDIUM", "HIGH" or "MEDIUM") => "major",
                ("MEDIUM", _) => "minor",
                _ => "minor",
            };
            var id = r.GetProperty("test_id").GetString() ?? "B000";
            list.Add(new($"BANDIT-{id}", "security", severity, Rel(r.GetProperty("filename").GetString() ?? "", root),
                r.TryGetProperty("line_number", out var ln) ? ln.GetInt32() : null,
                r.GetProperty("issue_text").GetString() ?? "",
                $"bandit rates this {sev.ToLowerInvariant()} severity ({conf.ToLowerInvariant()} confidence): a known pattern behind security weaknesses in Python services.",
                $"See bandit's {id} guidance and change the code so it no longer applies.", Source: "tool"));
        }
        return list;
    }

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
