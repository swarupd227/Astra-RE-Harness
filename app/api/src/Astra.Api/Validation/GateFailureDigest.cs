using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation;

/// <summary>
/// What a failed gate log says, in a shape an agent can narrate and a
/// regeneration can act on. Deterministic: every sentence comes from the
/// log itself (file, line, code, message) plus a small table of what the
/// common compiler and test failures usually mean. No model involved.
/// </summary>
public sealed record GateFailureDigest(
    string Stage,
    IReadOnlyList<GateFailureDigest.CompileError> Errors,
    IReadOnlyList<GateFailureDigest.TestFailure> FailedTests,
    int ErrorCount,
    int FailedTestCount,
    string? RunnerProblem)
{
    public sealed record CompileError(string? File, int? Line, string Code, string Message);
    public sealed record TestFailure(string Name, string? Message);

    // dotnet: `src/Foo.cs(42,13): error CS0103: The name 'x' does not exist [proj.csproj]`
    //   — as MSBuild prints it, indented and prefixed with the project
    //   number (`     1>…`) in a multi-project build, and repeated once
    //   more in the summary block (the dedup folds that).
    // ng/tsc: `src/app/foo.ts:12:5 - error TS2304: Cannot find name 'x'.`
    private static readonly Regex DotnetError = new(
        @"^\s*(?:\d+>)?(?<file>[^\s(]+)\((?<line>\d+),\d+\):\s*error\s+(?<code>[A-Z]+\d+):\s*(?<msg>.*?)(\s+\[[^\]]+\])?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex TsError = new(
        @"^\s*(?:\d+>)?(?<file>[^\s:]+):(?<line>\d+):\d+\s+-\s+error\s+(?<code>TS\d+):\s*(?<msg>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    // The validators build in a throw-away directory; the reader wants the
    // path inside the package, not the temp root.
    private static readonly Regex TempRoot = new(
        @"^(/tmp/astra-[a-z]+-[0-9a-f]+/|[A-Za-z]:\\[^\\]*\\astra-[a-z]+-[0-9a-f]+\\)",
        RegexOptions.Compiled);
    private static readonly Regex BareError = new(
        @"^\s*error\s+(?<code>[A-Z]+\d+):\s*(?<msg>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    // xunit console: `  Failed Demo.Tests.FormatterTests.Rejects_empty [12 ms]` then `Assert.Equal() Failure …`
    private static readonly Regex XunitFailed = new(
        @"^\s*Failed\s+(?<name>[A-Za-z_][\w.`+]*)(\s+\[\d+\s*m?s\])?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex JestFailed = new(
        @"^\s*●\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex RunnerProblemLine = new(
        @"^(Runner error:|=== .* failed to start|.*did not finish within .*s\.?|.*No \.NET SDKs were found.*|.*command not found.*)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static GateFailureDigest Parse(string stage, string? log)
    {
        log ??= "";
        var errors = new List<CompileError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DotnetError.Matches(log))
            Add(errors, seen, new(Relative(m.Groups["file"].Value), int.Parse(m.Groups["line"].Value), m.Groups["code"].Value, m.Groups["msg"].Value));
        foreach (Match m in TsError.Matches(log))
            Add(errors, seen, new(Relative(m.Groups["file"].Value), int.Parse(m.Groups["line"].Value), m.Groups["code"].Value, m.Groups["msg"].Value));
        if (errors.Count == 0)
            foreach (Match m in BareError.Matches(log))
                Add(errors, seen, new(null, null, m.Groups["code"].Value, m.Groups["msg"].Value));

        var tests = new List<TestFailure>();
        var lines = log.Split('\n');
        foreach (Match m in XunitFailed.Matches(log))
        {
            var name = m.Groups["name"].Value;
            var msg = NextMessageLine(lines, m.Index, log);
            if (tests.All(t => t.Name != name)) tests.Add(new(name, msg));
        }
        if (tests.Count == 0)
            foreach (Match m in JestFailed.Matches(log))
            {
                var name = m.Groups["name"].Value.Trim();
                if (name.Length > 0 && tests.All(t => t.Name != name)) tests.Add(new(name, null));
            }

        var runner = RunnerProblemLine.Match(log);
        return new GateFailureDigest(
            Stage: stage,
            Errors: errors,
            FailedTests: tests,
            ErrorCount: errors.Count,
            FailedTestCount: tests.Count,
            RunnerProblem: runner.Success ? runner.Value.Trim() : null);
    }

    /// <summary>One or two plain sentences for the thread, then the first
    /// few errors verbatim so the reader can trust them.</summary>
    public string ToMarkdown(string routineName, string? gateSummary = null)
    {
        var sb = new StringBuilder();
        var gate = Stage.Equals("COMPILE", StringComparison.OrdinalIgnoreCase) ? "compile" : Stage.Equals("TEST_PACK", StringComparison.OrdinalIgnoreCase) || Stage.Equals("TEST-PACK", StringComparison.OrdinalIgnoreCase) ? "test-pack" : Stage.ToLowerInvariant();

        if (RunnerProblem is not null && Errors.Count == 0 && FailedTests.Count == 0)
        {
            sb.Append($"The {gate} gate for `{routineName}` could not run: {RunnerProblem} That is the platform, not the generated code — run it again once the runner is back.");
            return sb.ToString();
        }

        if (Errors.Count > 0)
        {
            var first = Errors[0];
            sb.Append($"The {gate} gate for `{routineName}` failed with {Errors.Count} error{(Errors.Count == 1 ? "" : "s")}. ");
            sb.Append($"The first is {Where(first)}: `{first.Code}` — {first.Message.TrimEnd('.')}. ");
            var hint = Hint(first.Code, first.Message);
            if (hint is not null) sb.Append(hint).Append(' ');
            sb.Append("I can regenerate the code with these errors in hand and run the gate again.");
            sb.AppendLine().AppendLine();
            foreach (var e in Errors.Take(5))
                sb.AppendLine($"- {Where(e)} `{e.Code}` {e.Message}");
            if (Errors.Count > 5) sb.AppendLine($"- … {Errors.Count - 5} more in the log");
            return sb.ToString().TrimEnd();
        }

        if (FailedTests.Count > 0)
        {
            sb.Append($"The test pack for `{routineName}` has {FailedTests.Count} failing test{(FailedTests.Count == 1 ? "" : "s")}. ");
            var first = FailedTests[0];
            sb.Append($"The first is `{ShortName(first.Name)}`");
            if (!string.IsNullOrWhiteSpace(first.Message)) sb.Append($": {first.Message.Trim().TrimEnd('.')}");
            sb.Append(". Each test is a claim from the signed spec, so a failure means the generated code and the spec disagree — the fix is in the code unless the SME says the claim is wrong. I can regenerate with the failures in hand and run the pack again.");
            sb.AppendLine().AppendLine();
            foreach (var t in FailedTests.Take(5))
                sb.AppendLine($"- `{ShortName(t.Name)}`{(string.IsNullOrWhiteSpace(t.Message) ? "" : $" — {t.Message!.Trim()}")}");
            if (FailedTests.Count > 5) sb.AppendLine($"- … {FailedTests.Count - 5} more in the log");
            return sb.ToString().TrimEnd();
        }

        sb.Append($"The {gate} gate for `{routineName}` failed");
        if (!string.IsNullOrWhiteSpace(gateSummary)) sb.Append($": {gateSummary.TrimEnd('.')}");
        sb.Append(". The log has no error line I recognise — open it to see what the runner printed.");
        return sb.ToString();
    }

    /// <summary>The same facts, phrased for the model that regenerates the
    /// code: what failed, where, and what to change.</summary>
    public string ToRepairHint()
    {
        var sb = new StringBuilder();
        if (Errors.Count > 0)
        {
            sb.AppendLine($"The previous attempt failed the {Stage} gate with {Errors.Count} compiler error(s). Fix every one of them; keep the file layout and the tests unchanged unless an error is in a test.");
            foreach (var e in Errors.Take(20)) sb.AppendLine($"- {Where(e)} {e.Code}: {e.Message}");
        }
        if (FailedTests.Count > 0)
        {
            sb.AppendLine($"The previous attempt had {FailedTests.Count} failing test(s). The tests encode the signed spec's claims: change the implementation so they pass; do not weaken the tests.");
            foreach (var t in FailedTests.Take(20)) sb.AppendLine($"- {t.Name}{(string.IsNullOrWhiteSpace(t.Message) ? "" : $": {t.Message!.Trim()}")}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Relative(string file) => TempRoot.Replace(file, "").Replace('\\', '/');

    private static void Add(List<CompileError> list, HashSet<string> seen, CompileError e)
    {
        var key = $"{e.File}|{e.Line}|{e.Code}|{e.Message}";
        if (seen.Add(key)) list.Add(e);
    }

    private static string Where(CompileError e) =>
        e.File is null ? "(no file)" : e.Line is null ? $"`{e.File}`" : $"`{e.File}:{e.Line}`";

    private static string ShortName(string full)
    {
        var i = full.LastIndexOf('.');
        return i > 0 && i < full.Length - 1 ? full[(i + 1)..] : full;
    }

    private static string? NextMessageLine(string[] lines, int matchIndex, string log)
    {
        var lineNo = log[..matchIndex].Count(c => c == '\n');
        for (var i = lineNo + 1; i < Math.Min(lines.Length, lineNo + 6); i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("Stack Trace", StringComparison.OrdinalIgnoreCase) || l.StartsWith("at ", StringComparison.Ordinal)) break;
            if (l.StartsWith("Error Message", StringComparison.OrdinalIgnoreCase)) continue;
            return l.Length > 200 ? l[..200] + "…" : l;
        }
        return null;
    }

    /// <summary>What the common failures usually mean, in one sentence.</summary>
    public static string? Hint(string code, string message) => code switch
    {
        "CS0246" or "CS0234" => "A type or namespace is missing: usually a `using`, a project reference or a NuGet package the archetype does not carry.",
        "CS0103" => "A name is used before it is declared — typically a helper the generated code assumed the reference had.",
        "CS1061" => "The code calls a member the type does not have; the generated code and the reference type drifted apart.",
        "CS0029" or "CS0266" => "A type mismatch: the generated code passes or returns the wrong type for the spec's contract.",
        "CS0117" => "The code refers to a static member that does not exist on that type.",
        "CS1002" or "CS1513" or "CS1026" => "A syntax slip (missing `;`, `}` or `)`), usually from a truncated file.",
        "CS8956" or "CS0116" or "CS8802" => "A statement or declaration landed outside any type or namespace — the file's structure was disturbed before the first namespace line.",
        "CS0104" => "Two namespaces define the same name; the generated code needs a qualified name or an alias.",
        "NU1101" or "NU1102" => "A NuGet package in the project file does not exist at that version.",
        "TS2304" or "TS2307" => "An import is missing or its module cannot be found.",
        "TS2339" => "A property is used that the type does not declare.",
        _ => null,
    };
}
