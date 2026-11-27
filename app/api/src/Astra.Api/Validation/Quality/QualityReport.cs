using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Astra.Api.Validation.Quality;

/// <summary>
/// The quality report for one routine's generated code: what was checked, with which tool and version, when, and what
/// came out — built only from the stored gate runs, so every line can be traced back to a <c>ValidationRun</c>. A run
/// older than the package (the code was regenerated after it) is listed as history, never as proof for the current code.
/// Pure: the same records always give the same report.
/// </summary>
public sealed record QualityReport(
    string Schema,
    DateTimeOffset GeneratedAt,
    string PlatformBuild,
    QualityReport.SubjectInfo Subject,
    QualityReport.SpecInfo Spec,
    QualityReport.VerdictInfo Verdict,
    IReadOnlyList<QualityReport.GateInfo> Gates,
    IReadOnlyList<QualityReport.CheckInfo> Checks,
    QualityReport.FindingsInfo Findings,
    QualityReport.DependencyInfo Dependencies,
    QualityReport.TestInfo Tests,
    IReadOnlyList<QualityReport.HistoryEntry> History)
{
    public const string SchemaId = "quality-report/v1";

    public sealed record SubjectInfo(
        string Routine, string? SourceLanguage, string? SourceFile, string? Programme, Guid ScaffoldId, Guid SpecId,
        string TargetStack, DateTimeOffset PackageGeneratedAt, int FileCount, int TotalLines, int TodoCount, string? GitCommit);

    public sealed record SpecInfo(string State, int ClaimCount, string? SignedBy, DateTimeOffset? SignedAt, string? SpecHash);

    /// <summary>PASSED, FAILED or INCOMPLETE (a required gate has not run on this version of the code).</summary>
    public sealed record VerdictInfo(string Status, string Reason);

    public sealed record GateInfo(
        string Stage, string Title, bool Required, string Status, string Summary, Guid? RunId,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, long? DurationMs);

    /// <summary>One check: status is ran, failed-to-run, not-run, not-applicable (the stack has no such code) or
    /// not-available (the platform does not have this check yet).</summary>
    public sealed record CheckInfo(
        string Id, string Name, string Kind, string Version, string Covers, string Status, int? Findings, long? DurationMs, string? Note);

    public sealed record FindingsInfo(
        int Total, int Blocking, IReadOnlyList<string> BlockOn, IReadOnlyDictionary<string, int> BySeverity,
        IReadOnlyDictionary<string, int> ByCategory, IReadOnlyDictionary<string, int> BySource, bool Truncated,
        IReadOnlyList<FindingLine> Listed);

    public sealed record FindingLine(
        string Rule, string Severity, string Category, string Source, string File, int? Line, string Message, string? Fix, bool Blocking);

    public sealed record DependencyInfo(int? Components, IReadOnlyList<string> Ecosystems, int Vulnerable, bool SbomAvailable);

    public sealed record TestInfo(int? Total, int? Passed, int? Failed, int? Skipped, double? LineCoveragePercent, string? Runner);

    public sealed record HistoryEntry(string Stage, string Status, string Summary, Guid RunId, DateTimeOffset StartedAt);
}

/// <summary>A programme's quality at a glance: one row per routine with generated code (its latest package).</summary>
public sealed record QualityRollup(
    string Schema, DateTimeOffset GeneratedAt, string PlatformBuild, Guid CorpusId, string Programme,
    int Routines, int Passed, int Failed, int Incomplete, IReadOnlyList<QualityRollup.Row> Rows)
{
    public const string SchemaId = "quality-rollup/v1";

    public sealed record Row(
        string Routine, Guid ScaffoldId, string TargetStack, string Verdict, string Reason,
        IReadOnlyDictionary<string, string> Gates, int Blocking, int Findings, int VulnerableLibraries, int? TestsPassed, int? TestsTotal);

    public static QualityRollup From(Guid corpusId, string programme, IReadOnlyList<QualityReport> reports, string build, DateTimeOffset now)
    {
        var rows = reports.Select(r => new Row(r.Subject.Routine, r.Subject.ScaffoldId, r.Subject.TargetStack, r.Verdict.Status, r.Verdict.Reason,
                r.Gates.ToDictionary(g => g.Stage, g => g.Status), r.Findings.Blocking, r.Findings.Total, r.Dependencies.Vulnerable,
                r.Tests.Passed, r.Tests.Total))
            .OrderBy(x => x.Verdict switch { "FAILED" => 0, "INCOMPLETE" => 1, _ => 2 }).ThenBy(x => x.Routine, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new(SchemaId, now, build, corpusId, programme, rows.Count,
            rows.Count(x => x.Verdict == "PASSED"), rows.Count(x => x.Verdict == "FAILED"), rows.Count(x => x.Verdict == "INCOMPLETE"), rows);
    }
}

/// <summary>What the report is built from: the package, its spec and signature, and every gate run on it.</summary>
public sealed record QualityReportInput(
    Guid ScaffoldId, Guid SpecId, string Routine, string? SourceLanguage, string? SourceFile, string? Programme,
    string TargetStack, DateTimeOffset PackageGeneratedAt, int FileCount, int TotalLines, int TodoCount, string? GitCommit,
    string SpecState, int ClaimCount, string? SignedBy, DateTimeOffset? SignedAt, string? SpecHash,
    IReadOnlyList<QualityReportInput.Run> Runs, string PlatformBuild, DateTimeOffset Now)
{
    public sealed record Run(Guid Id, string Stage, string Status, string Summary, string? MetricsJson, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt);
}

public static class QualityReportBuilder
{
    /// <summary>The gates in report order; the first three are what "green" means (compile, tests, quality).</summary>
    private static readonly (string Stage, string Title, bool Required)[] GateOrder =
    [
        ("COMPILE", "Build (compile)", true),
        ("TEST_PACK", "Claim test pack", true),
        (QualityValidator.Stage, "Quality (security, standards, maintainability)", true),
        ("EQUIVALENCE", "Equivalence with the legacy routine", false),
        ("FALSIFYING", "Property tests (falsifying)", false),
    ];

    /// <summary>The checks the platform can run, with the versions baked into its images (API image: ruff, bandit;
    /// Java build service: SpotBugs + FindSecBugs, PMD, Checkstyle, JaCoCo). Keep in step with the Dockerfiles.</summary>
    private sealed record CheckDef(string Id, string Name, string Kind, string Version, string Covers, Func<string, bool> AppliesTo, bool Available = true);

    private static bool IsJava(string s) => s.StartsWith("java", StringComparison.OrdinalIgnoreCase) || s.Equals("angular-java", StringComparison.OrdinalIgnoreCase);
    private static bool IsPython(string s) => s.Equals("react-python", StringComparison.OrdinalIgnoreCase);
    private static bool HasTypeScript(string s) => s.StartsWith("angular-", StringComparison.OrdinalIgnoreCase) || IsPython(s);
    private static bool IsDotnet(string s) => s.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) || s.Equals("angular-dotnet8", StringComparison.OrdinalIgnoreCase);

    private static readonly CheckDef[] Catalogue =
    [
        new("rules", "Astra rule scan", "rules", "", "Secrets, injection-prone queries, unsafe calls, open CORS, disabled TLS checks, coding standards", _ => true),
        new("ai-review", "AI code reviewer", "ai", "", "Judgement against the signed spec: logic, edge cases, error handling (advisory; each finding checked against the code)", _ => true),
        new("spotbugs", "SpotBugs + FindSecBugs", "tool", "4.8.6 + 1.13.0", "Bugs and security weaknesses in the compiled Java", IsJava),
        new("pmd", "PMD", "tool", "7.7.0", "Error-prone Java, best practice, size and complexity", IsJava),
        new("checkstyle", "Checkstyle", "tool", "10.20.1", "Java conventions", IsJava),
        new("ruff", "ruff", "tool", "0.8.4", "Python bugs, undefined names, complexity", IsPython),
        new("bandit", "bandit", "tool", "1.8.0", "Python security", IsPython),
        new("eslint", "ESLint", "tool", "", "TypeScript lint", HasTypeScript, Available: false),
        new("dotnet-analyzers", ".NET analyzers", "tool", "", "C# code analysis", IsDotnet, Available: false),
        new("dependencies", "Known-vulnerability check (OSV.dev)", "dependencies", "OSV.dev API", "Every declared library and version against published advisories", _ => true),
        new("sbom", "Software bill of materials", "sbom", "CycloneDX 1.5", "The libraries the package declares, with versions", _ => true),
    ];

    private const int MaxListedFindings = 60;

    public static QualityReport Build(QualityReportInput i)
    {
        // A run counts for this version of the code only if it started after the package was (re)generated.
        bool Current(QualityReportInput.Run r) => r.StartedAt >= i.PackageGeneratedAt;
        var latest = i.Runs.Where(Current).GroupBy(r => r.Stage)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());

        var gates = GateOrder.Select(g =>
        {
            latest.TryGetValue(g.Stage, out var r);
            return new QualityReport.GateInfo(g.Stage, g.Title, g.Required, r?.Status ?? "NOT_RUN",
                r?.Summary ?? (i.Runs.Any(x => x.Stage == g.Stage) ? "Not run on this version of the code (earlier runs are in the history)" : "Not run"),
                r?.Id, r?.StartedAt, r?.CompletedAt,
                r?.CompletedAt is { } c ? (long)(c - r.StartedAt).TotalMilliseconds : null);
        }).ToList();

        latest.TryGetValue(QualityValidator.Stage, out var quality);
        latest.TryGetValue("TEST_PACK", out var testPack);
        using var qm = Parse(quality?.MetricsJson);
        using var tm = Parse(testPack?.MetricsJson);
        var q = qm?.RootElement;
        var t = tm?.RootElement;

        var findings = ReadFindings(q);
        var checks = BuildChecks(i.TargetStack, quality, q, findings);
        var blockOn = q is { } qe && qe.TryGetProperty("blockOn", out var bo) && bo.ValueKind == JsonValueKind.Array
            ? bo.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : QualityRules.BlockingByDefault.ToList();
        var findingsInfo = new QualityReport.FindingsInfo(
            Total: Int(q, "total") ?? findings.Count,
            Blocking: Int(q, "blocking") ?? findings.Count(f => f.Blocking),
            BlockOn: blockOn,
            BySeverity: QualityRules.Severities.ToDictionary(s => s, s => findings.Count(f => f.Severity == s)),
            ByCategory: new[] { "security", "standards", "maintainability" }.ToDictionary(c => c, c => findings.Count(f => f.Category == c)),
            BySource: new[] { "rule", "tool", "ai" }.ToDictionary(s => s, s => findings.Count(f => f.Source == s)),
            Truncated: Bool(q, "truncated") || findings.Count > MaxListedFindings,
            Listed: findings.Take(MaxListedFindings).ToList());

        var depFindings = findings.Count(f => f.Rule.StartsWith("DEP-", StringComparison.Ordinal));
        var sbom = q is { } q2 && q2.TryGetProperty("sbom", out var sb) && sb.ValueKind == JsonValueKind.Object ? sb : (JsonElement?)null;
        var deps = new QualityReport.DependencyInfo(
            Components: sbom is { } s1 ? Int(s1, "components") : null,
            Ecosystems: sbom is { } s2 && s2.TryGetProperty("ecosystems", out var eco) && eco.ValueKind == JsonValueKind.Array
                ? eco.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
            Vulnerable: depFindings,
            SbomAvailable: q is { } q3 && q3.TryGetProperty("sbomUri", out var su) && su.ValueKind == JsonValueKind.String);

        double? coverage = t is { } t1 && t1.TryGetProperty("coverage", out var cov) && cov.ValueKind == JsonValueKind.Object
            && cov.TryGetProperty("percent", out var pct) && pct.ValueKind == JsonValueKind.Number ? pct.GetDouble() : null;
        var tests = new QualityReport.TestInfo(Int(t, "total"), Int(t, "passed"), Int(t, "failed"), Int(t, "skipped"), coverage, Str(t, "runner"));

        var history = i.Runs.Where(r => !Current(r)).OrderByDescending(r => r.StartedAt).Take(30)
            .Select(r => new QualityReport.HistoryEntry(r.Stage, r.Status, r.Summary, r.Id, r.StartedAt)).ToList();

        return new QualityReport(QualityReport.SchemaId, i.Now, i.PlatformBuild,
            new(i.Routine, i.SourceLanguage, i.SourceFile, i.Programme, i.ScaffoldId, i.SpecId, i.TargetStack, i.PackageGeneratedAt,
                i.FileCount, i.TotalLines, i.TodoCount, i.GitCommit),
            new(i.SpecState, i.ClaimCount, i.SignedBy, i.SignedAt, i.SpecHash),
            Verdict(gates, i.SpecState), gates, checks, findingsInfo, deps, tests, history);
    }

    private static QualityReport.VerdictInfo Verdict(IReadOnlyList<QualityReport.GateInfo> gates, string specState)
    {
        var failed = gates.Where(g => g.Status is "FAILED" or "ERRORED").ToList();
        if (failed.Count > 0)
            return new("FAILED", $"{Join(failed.Select(g => g.Title.Split(" (")[0].ToLowerInvariant()))} did not pass on this version of the code.");
        var missing = gates.Where(g => g.Required && g.Status != "PASSED").ToList();
        if (missing.Count > 0)
            return new("INCOMPLETE", $"{Join(missing.Select(g => g.Title.Split(" (")[0].ToLowerInvariant()))} {(missing.Count == 1 ? "has" : "have")} not run on this version of the code.");
        var extra = gates.Where(g => !g.Required && g.Status == "PASSED").Select(g => g.Title.Split(" (")[0].ToLowerInvariant()).ToList();
        var signed = specState == "SIGNED" ? " against the signed spec" : " (the spec is not signed)";
        return new("PASSED", $"The code builds, passes its claim test pack{signed} and has no blocking quality findings"
            + (extra.Count > 0 ? $"; {Join(extra)} also passed." : "."));
    }

    private static string Join(IEnumerable<string> parts)
    {
        var l = parts.ToList();
        var s = l.Count <= 1 ? string.Concat(l) : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1];
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static List<QualityReport.CheckInfo> BuildChecks(string stack, QualityReportInput.Run? quality, JsonElement? q, IReadOnlyList<QualityReport.FindingLine> findings)
    {
        var tools = q is { } qe && qe.TryGetProperty("tools", out var te) && te.ValueKind == JsonValueKind.Object ? te : (JsonElement?)null;
        var ran = quality is not null && quality.Status != "ERRORED";
        var list = new List<QualityReport.CheckInfo>();
        foreach (var d in Catalogue)
        {
            var version = d.Id == "rules" ? $"{QualityRules.RuleCount} rules · set {QualityRules.RuleSetFingerprint}" : d.Version;
            if (!d.AppliesTo(stack)) { list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, "not-applicable", null, null, $"{stack} has no code this check reads")); continue; }
            if (!d.Available) { list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, "not-available", null, null, "Not part of the platform yet")); continue; }
            if (quality is null) { list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, "not-run", null, null, "The quality gate has not run on this version of the code")); continue; }
            if (!ran) { list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, "failed-to-run", null, null, quality.Summary)); continue; }
            switch (d.Id)
            {
                case "rules":
                    list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, "ran", findings.Count(f => f.Source == "rule"), null, null));
                    break;
                case "ai-review":
                    var ai = q is { } qa && qa.TryGetProperty("aiReview", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
                    if (ai is not { } r)
                        list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, "not-run", null, null, "The reviewer was not asked on this run (the fix loop checks without it)"));
                    else
                    {
                        var status = Str(r, "status");
                        var cost = r.TryGetProperty("costUsd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : (decimal?)null;
                        var note = $"{Int(r, "proposed") ?? 0} proposed, {Int(r, "kept") ?? 0} kept after checking against the code"
                            + (cost is { } cc ? $" · ${cc.ToString("0.000", CultureInfo.InvariantCulture)}" : "");
                        list.Add(new(d.Id, d.Name, d.Kind, Str(r, "model") ?? "", d.Covers, status == "reviewed" ? "ran" : "failed-to-run",
                            status == "reviewed" ? Int(r, "kept") : null, null, status == "reviewed" ? note : Str(r, "note") ?? status));
                    }
                    break;
                case "sbom":
                    var sbomOk = q is { } qs && qs.TryGetProperty("sbomUri", out var su) && su.ValueKind == JsonValueKind.String;
                    var comps = q is { } qc && qc.TryGetProperty("sbom", out var sb) && sb.ValueKind == JsonValueKind.Object ? Int(sb, "components") : null;
                    list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, sbomOk ? "ran" : "not-run", comps, null,
                        sbomOk ? $"{comps} components recorded" : "The package declares no libraries the platform reads"));
                    break;
                default:
                    if (tools is { } tl && tl.TryGetProperty(d.Id, out var run) && run.ValueKind == JsonValueKind.Object)
                    {
                        var ok = run.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                        list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, ok ? "ran" : "failed-to-run", Int(run, "count"),
                            run.TryGetProperty("durationMs", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt64() : null,
                            ok ? null : Trim(Str(run, "error"))));
                    }
                    else
                    {
                        // A tool group that could not start (java-tools / python-tools) says why.
                        var group = IsJava(stack) ? "java-tools" : IsPython(stack) ? "python-tools" : null;
                        string? why = group is not null && tools is { } tg && tg.TryGetProperty(group, out var gr) ? Trim(Str(gr, "error")) : null;
                        list.Add(new(d.Id, d.Name, d.Kind, version, d.Covers, why is null ? "not-run" : "failed-to-run", null, null,
                            why ?? (d.Id == "dependencies" ? "The package declares no libraries" : "Not reported by this run")));
                    }
                    break;
            }
        }
        return list;
    }

    private static List<QualityReport.FindingLine> ReadFindings(JsonElement? q)
    {
        if (q is not { } qe || !qe.TryGetProperty("findings", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var blockOn = qe.TryGetProperty("blockOn", out var bo) && bo.ValueKind == JsonValueKind.Array
            ? bo.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : QualityRules.BlockingByDefault;
        return arr.EnumerateArray().Select(f =>
        {
            var source = Str(f, "source") ?? "rule";
            var sev = Str(f, "severity") ?? "info";
            return new QualityReport.FindingLine(Str(f, "rule") ?? "", sev, Str(f, "category") ?? "", source, Str(f, "file") ?? "",
                Int(f, "line"), Str(f, "message") ?? "", Str(f, "fix"), source != "ai" && blockOn.Contains(sev));
        })
        .OrderByDescending(f => f.Blocking)
        .ThenBy(f => Array.IndexOf(QualityRules.Severities, f.Severity))
        .ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line ?? 0)
        .ToList();
    }

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); } catch (JsonException) { return null; }
    }

    private static int? Int(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    private static string? Trim(string? s) => s is null ? null : s.Length > 300 ? s[..300] + "…" : s;
}

/// <summary>The report as a printable HTML page (rendered to PDF by weasyprint). Every value is escaped.</summary>
public static class QualityReportHtml
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string When(DateTimeOffset? t) => t is { } v ? v.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "—";
    private static string Dur(long? ms) => ms is not { } m ? "" : m < 1000 ? $"{m} ms" : m < 120_000 ? $"{m / 1000.0:0.#} s" : $"{m / 60000.0:0.#} min";

    private static string Badge(string status) => status switch
    {
        "PASSED" or "ran" => $"<span class=\"b ok\">{E(Label(status))}</span>",
        "FAILED" or "ERRORED" or "failed-to-run" => $"<span class=\"b bad\">{E(Label(status))}</span>",
        "INCOMPLETE" or "NOT_RUN" or "not-run" => $"<span class=\"b warn\">{E(Label(status))}</span>",
        _ => $"<span class=\"b mute\">{E(Label(status))}</span>",
    };

    private static string Label(string status) => status switch
    {
        "PASSED" => "Passed", "FAILED" => "Failed", "ERRORED" => "Errored", "INCOMPLETE" => "Incomplete", "NOT_RUN" => "Not run",
        "ran" => "Ran", "failed-to-run" => "Could not run", "not-run" => "Not run", "not-applicable" => "Not applicable",
        "not-available" => "Not available yet", "RUNNING" => "Running", _ => status,
    };

    public static string Render(QualityReport r)
    {
        var s = r.Subject;
        var h = new StringBuilder();
        h.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Quality report — ").Append(E(s.Routine)).Append("</title><style>")
         .Append(Css).Append("</style></head><body>");

        h.Append("<header><div class=\"brand\">Astra · Quality report</div><h1>").Append(E(s.Routine)).Append("</h1><div class=\"sub\">")
         .Append(E(s.SourceLanguage ?? "")).Append(" → ").Append(E(s.TargetStack))
         .Append(s.Programme is null ? "" : " · " + E(s.Programme)).Append("</div></header>");

        h.Append("<section class=\"verdict v-").Append(E(r.Verdict.Status.ToLowerInvariant())).Append("\"><div class=\"vs\">")
         .Append(E(Label(r.Verdict.Status))).Append("</div><p>").Append(E(r.Verdict.Reason)).Append("</p></section>");

        h.Append("<h2>1. What was checked</h2><table class=\"kv\">");
        Row(h, "Routine", s.Routine + (s.SourceFile is null ? "" : $"  ({s.SourceFile})"));
        Row(h, "Generated code", $"{s.FileCount} files · {s.TotalLines:N0} lines · {s.TodoCount} TODOs · generated {When(s.PackageGeneratedAt)}");
        Row(h, "Package id", s.ScaffoldId.ToString());
        if (s.GitCommit is not null) Row(h, "Commit", s.GitCommit);
        Row(h, "Spec", $"{r.Spec.State} · {r.Spec.ClaimCount} claims" + (r.Spec.SignedBy is null ? "" : $" · signed by {r.Spec.SignedBy} on {When(r.Spec.SignedAt)}"));
        if (r.Spec.SpecHash is not null) Row(h, "Signed spec hash", r.Spec.SpecHash);
        Row(h, "Platform build", r.PlatformBuild);
        Row(h, "Report generated", When(r.GeneratedAt));
        h.Append("</table>");

        h.Append("<h2>2. Gates</h2><table><thead><tr><th>Gate</th><th>Result</th><th>Detail</th><th>Ran</th><th>Took</th></tr></thead><tbody>");
        foreach (var g in r.Gates)
            h.Append("<tr><td>").Append(E(g.Title)).Append(g.Required ? "" : " <span class=\"opt\">optional</span>").Append("</td><td>").Append(Badge(g.Status))
             .Append("</td><td>").Append(E(g.Summary)).Append("</td><td class=\"nw\">").Append(When(g.StartedAt)).Append("</td><td class=\"nw\">")
             .Append(Dur(g.DurationMs)).Append("</td></tr>");
        h.Append("</tbody></table>");

        h.Append("<h2>3. Checks inside the quality gate</h2><table><thead><tr><th>Check</th><th>Version</th><th>Covers</th><th>Result</th><th>Findings</th><th>Note</th></tr></thead><tbody>");
        foreach (var c in r.Checks)
            h.Append("<tr").Append(c.Status is "not-applicable" ? " class=\"dim\"" : "").Append("><td>").Append(E(c.Name)).Append("</td><td class=\"mono\">").Append(E(c.Version))
             .Append("</td><td>").Append(E(c.Covers)).Append("</td><td>").Append(Badge(c.Status)).Append("</td><td class=\"num\">")
             .Append(c.Findings?.ToString(CultureInfo.InvariantCulture) ?? "").Append("</td><td>").Append(E(c.Note)).Append("</td></tr>");
        h.Append("</tbody></table>");

        var f = r.Findings;
        h.Append("<h2>4. Findings</h2><p>").Append(f.Total).Append(" findings, ").Append(f.Blocking).Append(" blocking (")
         .Append(E(string.Join(" and ", f.BlockOn))).Append(" from the rules and tools fail the gate; AI findings are advisory).</p><table class=\"counts\"><tr>");
        foreach (var (k, v) in f.BySeverity) h.Append("<td><b>").Append(v).Append("</b><br>").Append(E(k)).Append("</td>");
        foreach (var (k, v) in f.ByCategory) h.Append("<td><b>").Append(v).Append("</b><br>").Append(E(k)).Append("</td>");
        foreach (var (k, v) in f.BySource) h.Append("<td><b>").Append(v).Append("</b><br>from ").Append(E(k == "rule" ? "rules" : k == "tool" ? "tools" : "AI")).Append("</td>");
        h.Append("</tr></table>");
        if (f.Listed.Count > 0)
        {
            h.Append("<table class=\"findings\"><thead><tr><th>Severity</th><th>Rule</th><th>Where</th><th>Finding and fix</th></tr></thead><tbody>");
            foreach (var x in f.Listed)
                h.Append("<tr><td>").Append(x.Blocking ? "<span class=\"b bad\">" : "<span class=\"b mute\">").Append(E(x.Severity)).Append("</span><div class=\"src\">")
                 .Append(E(x.Source == "ai" ? "AI (advisory)" : x.Source)).Append(" · ").Append(E(x.Category)).Append("</div></td><td class=\"mono\">").Append(E(x.Rule))
                 .Append("</td><td class=\"mono\">").Append(E(x.File)).Append(x.Line is { } n ? $":{n}" : "").Append("</td><td>").Append(E(x.Message))
                 .Append(x.Fix is null ? "" : "<div class=\"fix\">Fix: " + E(x.Fix) + "</div>").Append("</td></tr>");
            h.Append("</tbody></table>");
            if (f.Truncated) h.Append("<p class=\"note\">Only the first ").Append(f.Listed.Count).Append(" findings are listed; the JSON report and the validation record hold the rest.</p>");
        }

        var d = r.Dependencies;
        h.Append("<h2>5. Libraries</h2><table class=\"kv\">");
        Row(h, "Declared libraries", d.Components is { } comp ? $"{comp} ({string.Join(", ", d.Ecosystems)})" : "None read");
        Row(h, "With a known vulnerability", d.Vulnerable.ToString(CultureInfo.InvariantCulture));
        Row(h, "Bill of materials", d.SbomAvailable ? "CycloneDX 1.5, stored with the quality run (GET /api/v1/scaffolds/{id}/sbom)" : "Not produced");
        h.Append("</table>");

        var t = r.Tests;
        h.Append("<h2>6. Tests</h2><table class=\"kv\">");
        var packGate = r.Gates.FirstOrDefault(g => g.Stage == "TEST_PACK");
        Row(h, "Claim test pack", t.Total is { } tt ? $"{t.Passed} of {tt} passed · {t.Failed} failed · {t.Skipped ?? 0} skipped"
            : packGate is { Status: not "NOT_RUN" } pg ? $"{Label(pg.Status)}: {pg.Summary} (no test counts recorded)" : "Not run on this version");
        if (t.Runner is not null) Row(h, "Runner", t.Runner);
        if (t.LineCoveragePercent is { } pc) Row(h, "Line coverage (JaCoCo)", $"{pc:0.#}%");
        Row(h, "Spec claims the pack is written from", r.Spec.ClaimCount.ToString(CultureInfo.InvariantCulture));
        h.Append("</table>");

        if (r.History.Count > 0)
        {
            h.Append("<h2>7. Earlier runs (previous versions of the code)</h2><table><thead><tr><th>Ran</th><th>Gate</th><th>Result</th><th>Detail</th></tr></thead><tbody>");
            foreach (var x in r.History)
                h.Append("<tr><td class=\"nw\">").Append(When(x.StartedAt)).Append("</td><td>").Append(E(x.Stage)).Append("</td><td>").Append(Badge(x.Status))
                 .Append("</td><td>").Append(E(x.Summary)).Append("</td></tr>");
            h.Append("</tbody></table>");
        }

        h.Append("<footer>Built from the stored gate runs of package ").Append(E(s.ScaffoldId.ToString())).Append(" · ").Append(E(QualityReport.SchemaId))
         .Append(" · every gate row names its validation run id in the JSON report.</footer></body></html>");
        return h.ToString();
    }

    public static string Render(QualityRollup r)
    {
        var h = new StringBuilder();
        h.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Quality report — ").Append(E(r.Programme)).Append("</title><style>")
         .Append(Css).Append("</style></head><body>");
        h.Append("<header><div class=\"brand\">Astra · Programme quality report</div><h1>").Append(E(r.Programme)).Append("</h1><div class=\"sub\">")
         .Append(r.Routines).Append(" routines with generated code · ").Append(r.Passed).Append(" passed · ").Append(r.Failed).Append(" failed · ")
         .Append(r.Incomplete).Append(" incomplete</div></header>");
        h.Append("<h2>Routines</h2><table><thead><tr><th>Routine</th><th>Target</th><th>Verdict</th><th>Build</th><th>Tests</th><th>Quality</th><th>Blocking</th><th>Findings</th><th>Vulnerable libraries</th></tr></thead><tbody>");
        foreach (var x in r.Rows)
            h.Append("<tr><td>").Append(E(x.Routine)).Append("<div class=\"src\">").Append(E(x.Reason)).Append("</div></td><td class=\"mono\">").Append(E(x.TargetStack))
             .Append("</td><td>").Append(Badge(x.Verdict)).Append("</td><td>").Append(Badge(x.Gates.GetValueOrDefault("COMPILE", "NOT_RUN")))
             .Append("</td><td>").Append(Badge(x.Gates.GetValueOrDefault("TEST_PACK", "NOT_RUN")))
             .Append(x.TestsTotal is { } tt ? $"<div class=\"src\">{x.TestsPassed}/{tt}</div>" : "")
             .Append("</td><td>").Append(Badge(x.Gates.GetValueOrDefault(QualityValidator.Stage, "NOT_RUN")))
             .Append("</td><td class=\"num\">").Append(x.Blocking).Append("</td><td class=\"num\">").Append(x.Findings)
             .Append("</td><td class=\"num\">").Append(x.VulnerableLibraries).Append("</td></tr>");
        h.Append("</tbody></table>");
        h.Append("<p class=\"note\">Each row is the routine's latest package; its own quality report (GET /api/v1/scaffolds/{id}/quality-report.pdf) lists every check, tool version and finding.</p>");
        h.Append("<footer>Platform build ").Append(E(r.PlatformBuild)).Append(" · generated ").Append(When(r.GeneratedAt)).Append(" · ")
         .Append(E(QualityRollup.SchemaId)).Append("</footer></body></html>");
        return h.ToString();
    }

    private static void Row(StringBuilder h, string k, string v) =>
        h.Append("<tr><th>").Append(E(k)).Append("</th><td>").Append(E(v)).Append("</td></tr>");

    private const string Css = """
        @page { size: A4; margin: 16mm 14mm 18mm; @bottom-right { content: "Page " counter(page) " of " counter(pages); font-family: "DejaVu Sans", Arial, sans-serif; font-size: 8pt; color: #6b6b6b; } }
        body { font-family: "DejaVu Sans", Arial, sans-serif; font-size: 9pt; color: #17171a; line-height: 1.4; }
        header { background: #121214; color: #f2f2f2; padding: 12px 16px; border-radius: 6px; }
        header .brand { color: #FFDD00; font-size: 8pt; letter-spacing: .08em; text-transform: uppercase; }
        header h1 { margin: 4px 0 2px; font-size: 18pt; }
        header .sub { color: #b9b9be; }
        h2 { font-size: 11.5pt; margin: 18px 0 6px; }
        .verdict { margin-top: 12px; padding: 10px 14px; border-radius: 6px; border: 1px solid #d0d0d4; }
        .verdict .vs { font-size: 14pt; font-weight: bold; }
        .verdict p { margin: 4px 0 0; }
        .v-passed { background: #eaf6ee; border-color: #6fbf8a; }
        .v-failed { background: #fbecec; border-color: #d77a7a; }
        .v-incomplete { background: #fdf6e0; border-color: #d9b44a; }
        table { width: 100%; border-collapse: collapse; margin: 4px 0; }
        th, td { text-align: left; vertical-align: top; padding: 4px 6px; border-bottom: 1px solid #e3e3e6; }
        thead th { background: #f1f1f3; font-size: 8pt; text-transform: uppercase; letter-spacing: .03em; color: #4a4a50; }
        table.kv th { width: 30%; color: #4a4a50; font-weight: normal; }
        table.counts td { text-align: center; border: 1px solid #e3e3e6; font-size: 8pt; color: #4a4a50; }
        table.counts td b { font-size: 12pt; color: #17171a; }
        .b { display: inline-block; padding: 1px 6px; border-radius: 8px; font-size: 7.5pt; font-weight: bold; white-space: nowrap; }
        .ok { background: #d6efdd; color: #1e6b37; } .bad { background: #f6d5d5; color: #8a1f1f; }
        .warn { background: #f8ecc4; color: #7a5a00; } .mute { background: #ececef; color: #4a4a50; }
        .mono { font-family: "DejaVu Sans Mono", monospace; font-size: 7.5pt; word-break: break-all; }
        .num { text-align: right; } .nw { white-space: nowrap; font-size: 8pt; }
        .dim td { color: #8a8a90; } .opt { font-size: 7pt; color: #8a8a90; }
        .src { font-size: 7pt; color: #6b6b6b; margin-top: 2px; } .fix { color: #3c3c42; margin-top: 2px; font-size: 8pt; }
        .note { color: #6b6b6b; font-size: 8pt; }
        tr { page-break-inside: avoid; }
        footer { margin-top: 18px; font-size: 7.5pt; color: #6b6b6b; border-top: 1px solid #e3e3e6; padding-top: 6px; }
        """;
}
