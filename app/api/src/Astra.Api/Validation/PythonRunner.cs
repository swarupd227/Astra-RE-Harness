using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation;

/// <summary>
/// The Python half of a react-python scaffold's gates, run in-process like the npm half: a virtual
/// environment per run (so the scaffold's pinned requirements never touch the image's Python), then
/// a byte-compile plus an import of every module for COMPILE, or pytest for TEST_PACK. Needs
/// <c>python3</c> with the venv module on PATH (the API image installs python3-venv).
/// </summary>
public static class PythonRunner
{
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(6);
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    private static string VenvPython(string backendDir) => OperatingSystem.IsWindows()
        ? Path.Combine(backendDir, ".venv", "Scripts", "python.exe")
        : Path.Combine(backendDir, ".venv", "bin", "python");

    /// <summary>
    /// Imports every module under the backend's packages (tests and the venv excluded), so a missing
    /// name or a dependency that isn't in requirements.txt fails here, not first in production.
    /// </summary>
    private const string ImportAll = """
        import importlib, pathlib, sys, traceback
        sys.path.insert(0, '.')
        failed = 0
        for py in sorted(pathlib.Path('.').rglob('*.py')):
            parts = py.with_suffix('').parts
            if parts[0] in ('.venv', 'tests') or parts[0].startswith('.'):
                continue
            name = '.'.join(p for p in parts if p != '__init__') or None
            if not name:
                continue
            try:
                importlib.import_module(name)
                print('import ok   ' + name)
            except Exception:
                failed += 1
                print('import FAIL ' + name)
                traceback.print_exc()
        sys.exit(1 if failed else 0)
        """;

    /// <summary>venv + pip install -r requirements.txt. A missing requirements file installs nothing.</summary>
    public static async Task<(int ExitCode, string Log)> PrepareAsync(string backendDir, CancellationToken ct)
    {
        var log = new StringBuilder();
        var python = OperatingSystem.IsWindows() ? "python" : "python3";
        var (venvExit, venvLog) = await RunAsync(python, "-m venv .venv", backendDir, InstallTimeout, ct);
        log.AppendLine("=== python3 -m venv .venv ===").AppendLine(venvLog);
        if (venvExit != 0) return (venvExit, log.ToString());

        if (!File.Exists(Path.Combine(backendDir, "requirements.txt")))
        {
            log.AppendLine("=== no requirements.txt; nothing to install ===");
            return (0, log.ToString());
        }
        var (pipExit, pipLog) = await RunAsync(VenvPython(backendDir),
            "-m pip install --disable-pip-version-check --no-input -q -r requirements.txt", backendDir, InstallTimeout, ct);
        log.AppendLine("=== pip install -r requirements.txt ===").AppendLine(pipLog);
        return (pipExit, log.ToString());
    }

    /// <summary>COMPILE: install, byte-compile every file, import every module.</summary>
    public static async Task<(int ExitCode, string Log)> CompileAsync(string backendDir, CancellationToken ct)
    {
        var (prepExit, log) = await PrepareAsync(backendDir, ct);
        if (prepExit != 0) return (prepExit, log);
        var sb = new StringBuilder(log);
        var (compileExit, compileLog) = await RunAsync(VenvPython(backendDir), "-m compileall -q -x \\.venv .", backendDir, RunTimeout, ct);
        sb.AppendLine("=== python -m compileall ===").AppendLine(compileLog);
        if (compileExit != 0) return (compileExit, sb.ToString());

        var script = Path.Combine(backendDir, ".astra_import_check.py");
        await File.WriteAllTextAsync(script, ImportAll, ct);
        var (importExit, importLog) = await RunAsync(VenvPython(backendDir), ".astra_import_check.py", backendDir, RunTimeout, ct);
        sb.AppendLine("=== import every module ===").AppendLine(importLog);
        return (importExit, sb.ToString());
    }

    /// <summary>TEST_PACK: install, then pytest from the backend directory.</summary>
    public static async Task<(int ExitCode, string Log)> TestAsync(string backendDir, CancellationToken ct)
    {
        var (prepExit, log) = await PrepareAsync(backendDir, ct);
        if (prepExit != 0) return (prepExit, log);
        var (testExit, testLog) = await RunAsync(VenvPython(backendDir), "-m pytest -p no:cacheprovider -rA", backendDir, RunTimeout, ct);
        return (testExit, log + "=== python -m pytest ===\n" + testLog);
    }

    /// <summary>Runs a script in the backend's virtual environment (prepared by <see cref="PrepareAsync"/>), from the backend directory.</summary>
    public static Task<(int ExitCode, string Log)> RunInVenvAsync(string backendDir, string args, CancellationToken ct) =>
        RunAsync(VenvPython(backendDir), args, backendDir, RunTimeout, ct);

    private static readonly SemaphoreSlim ToolLock = new(1, 1);

    /// <summary>
    /// A virtual environment for one of the platform's own Python tools (e.g. the formula engine), made once per server
    /// and reused: keyed by <paramref name="name"/> and the exact requirements, so a version bump makes a fresh one.
    /// Returns its python, or null with the install log when it could not be made.
    /// </summary>
    public static async Task<(string? Python, string Log)> EnsureToolVenvAsync(string name, string requirements, CancellationToken ct)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(requirements)))[..12].ToLowerInvariant();
        var dir = Path.Combine(Path.GetTempPath(), $"astra-tool-{name}-{key}");
        var ready = Path.Combine(dir, ".ready");
        await ToolLock.WaitAsync(ct);
        try
        {
            if (File.Exists(ready)) return (VenvPython(dir), "=== tool environment reused ===\n");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "requirements.txt"), requirements, ct);
            var (exit, log) = await PrepareAsync(dir, ct);
            if (exit != 0) return (null, log);
            await File.WriteAllTextAsync(ready, DateTimeOffset.UtcNow.ToString("O"), ct);
            return (VenvPython(dir), log);
        }
        finally { ToolLock.Release(); }
    }

    /// <summary>Runs a script with a given python (a tool environment's), from <paramref name="workDir"/>.</summary>
    public static Task<(int ExitCode, string Log)> RunWithAsync(string python, string args, string workDir, CancellationToken ct) =>
        RunAsync(python, args, workDir, RunTimeout, ct);

    // pytest's last line: "==== 5 passed, 1 failed, 2 skipped in 0.31s ====" (or "-q": "5 passed in 0.31s").
    private static readonly Regex SummaryLine = new(@"^=*\s*(?<body>(?:\d+ \w+(?:, )?)+) in [\d.]+s", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Count = new(@"(?<n>\d+) (?<what>passed|failed|errors?|skipped|xfailed|xpassed|deselected)", RegexOptions.Compiled);

    /// <summary>Counts from pytest's summary line; errors count as failures. Null when there is no summary.</summary>
    public static (int Passed, int Failed, int Skipped, int Total)? ParseSummary(string log)
    {
        var lines = SummaryLine.Matches(log);
        if (lines.Count == 0) return null;
        int passed = 0, failed = 0, skipped = 0;
        foreach (Match m in Count.Matches(lines[^1].Groups["body"].Value))
        {
            var n = int.Parse(m.Groups["n"].Value);
            switch (m.Groups["what"].Value)
            {
                case "passed": case "xpassed": passed += n; break;
                case "failed": case "error": case "errors": failed += n; break;
                case "skipped": case "xfailed": skipped += n; break;
            }
        }
        return (passed, failed, skipped, passed + failed + skipped);
    }

    private static async Task<(int ExitCode, string Log)> RunAsync(string file, string args, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PIP_NO_CACHE_DIR"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        using var proc = new Process { StartInfo = psi };
        var output = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        try
        {
            if (!proc.Start()) return (-1, $"{file} {args} failed to start.\n");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, $"{file} is not available on this server ({ex.Message}).\n");
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, output + $"=== timed out after {timeout.TotalSeconds:N0}s ===\n");
        }
        return (proc.ExitCode, output + $"=== exit {proc.ExitCode} ===\n");
    }
}
