using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation.PhpEquivalence;

/// <summary>
/// EQUIVALENCE for a PHP routine and the java-spring package generated from it. Vectors come from the signed spec
/// (<see cref="PhpEquivalenceVectors"/>); the PHP source runs in the php sidecar, the Java method (found by
/// <see cref="JavaTargetLocator"/>) runs in the maven sidecar through a JUnit harness added for this run only, and
/// <see cref="EquivalenceComparer"/> decides each vector. Routines that need a database, uploaded files or the network
/// are skipped with the reason, as are routines that read request superglobals when the spec names no parameters.
/// </summary>
public sealed class PhpEquivalenceRunner
{
    public const string HarnessClass = "AstraEquivalenceHarnessTest";
    private const string Marker = "@@ASTRA-EQ@@ ";
    private const int MaxSiblingFiles = 150;
    private const int MaxSiblingBytes = 4 * 1024 * 1024;

    public sealed record Outcome(string Status, string? ErrorCode, string Summary, object Metrics, string Transcript);

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly PhpClient _php;
    private readonly MavenClient _maven;
    private readonly ILogger _log;

    public PhpEquivalenceRunner(AppDbContext db, IBlobClient blob, PhpClient php, MavenClient maven, ILogger log)
    {
        _db = db;
        _blob = blob;
        _php = php;
        _maven = maven;
        _log = log;
    }

    public async Task<Outcome> RunAsync(Scaffold scaffold, IReadOnlyList<JsonObject>? engineerVectors, CancellationToken ct)
    {
        var sub = scaffold.Spec?.Subroutine ?? throw new InvalidOperationException("Scaffold has no routine.");
        var routine = sub.Name;
        var t = new StringBuilder();
        t.AppendLine($"=== Per-unit equivalence: PHP `{routine}` against the {scaffold.TargetPlatform} package ===");

        Outcome Skip(string code, string why, object? extra = null)
        {
            t.AppendLine().AppendLine($"=== SKIPPED === {why}");
            return new Outcome("SKIPPED", code, why, new { mode = "per-vector", sourceLanguage = "php", routine, skipped = why, detail = extra }, t.ToString());
        }

        var target = (scaffold.TargetPlatform ?? "").ToLowerInvariant();
        var backendPrefix = target == "angular-java" ? "backend/" : target == "java-spring" ? "" : null;
        if (backendPrefix is null)
            return Skip("equivalence.target_not_java",
                $"PHP equivalence compares the PHP code unit with a Java package; this one targets {scaffold.TargetPlatform}.");

        using var specDoc = JsonDocument.Parse(scaffold.Spec!.SpecJson.RootElement.GetRawText());
        var spec = specDoc.RootElement;

        // ── the PHP source ──
        var file = await _db.SourceFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == sub.SourceFileId, ct)
            ?? throw new InvalidOperationException("The routine's source file is missing.");
        var content = await _blob.GetTextAsync(file.BlobUri, ct);
        var routineSource = PhpRoutineTraits.Slice(content, sub.LineStart, sub.LineEnd);
        var traits = PhpRoutineTraits.Analyze(routineSource, spec);
        t.AppendLine($"routine: {file.RelativePath}:{sub.LineStart}-{sub.LineEnd}");
        t.AppendLine($"superglobals read: {(traits.Superglobals.Count == 0 ? "none" : string.Join(", ", traits.Superglobals.Select(s => "$" + s)))}");
        if (traits.TouchesDatabase)
            return Skip("equivalence.needs_database",
                $"`{routine}` reads or writes a database ({traits.DatabaseEvidence}); a PHP run here has no database to give it. " +
                "The HTTP parity replay (the legacy app and the Spring service against one database) is where it is compared.");
        if (traits.Unsuppliable.Count > 0)
            return Skip("equivalence.needs_outside_world", $"`{routine}` {traits.Unsuppliable[0]}.");

        // ── vectors from the signed spec ──
        var vectors = PhpEquivalenceVectors.Build(spec, engineerVectors);
        t.AppendLine($"vectors: {vectors.Count} ({string.Join(", ", vectors.GroupBy(v => v.Source).Select(g => $"{g.Count()} from {g.Key}"))})");
        if (vectors.Count == 0)
            return Skip("equivalence.no_vectors",
                $"The signed spec of `{routine}` gives no inputs to try: no generatorHints (examples or inputs) on an invariant or edge case, " +
                "no request parameters on an HTTP contract, no typed inputs.");
        if (traits.ReadsRequest && vectors.All(v => v.Superglobals is null))
            return Skip("equivalence.needs_request",
                $"`{routine}` reads {string.Join(", ", traits.Superglobals.Where(s => s is "_GET" or "_POST" or "_REQUEST" or "_COOKIE").Select(s => "$" + s))}, " +
                "but the signed spec names no request parameters to supply (add an HTTP contract or superglobal inputs).");

        // ── the Java method ──
        var manifestText = await _blob.GetTextAsync(scaffold.PackageBlobUri, ct);
        using var manifest = JsonDocument.Parse(manifestText);
        var javaSources = new List<MavenClient.JavaSource>();
        foreach (var f in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = f.GetProperty("path").GetString() ?? "";
            if (!path.StartsWith(backendPrefix, StringComparison.Ordinal)) continue;
            javaSources.Add(new MavenClient.JavaSource(path[backendPrefix.Length..], f.GetProperty("content").GetString() ?? ""));
        }
        var phpClass = spec.TryGetProperty("enclosing_type", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString() : null;
        phpClass ??= sub.CommonBlockRefs?.RootElement is { ValueKind: JsonValueKind.Array } cb && cb.GetArrayLength() > 0 && cb[0].ValueKind == JsonValueKind.String ? cb[0].GetString() : null;
        var ranked = JavaTargetLocator.Rank(javaSources.Select(s => (s.Path, s.Content)), routine, phpClass);
        var javaTarget = ranked.Count > 0 && ranked[0].Score >= 7 ? ranked[0] : null;
        if (javaTarget is null)
            return Skip("equivalence.no_java_counterpart",
                $"No method in the generated package corresponds to `{routine}` (same name, or an annotation naming it).",
                new { candidates = ranked.Take(5) });
        t.AppendLine($"java target: {javaTarget.ClassName}.{javaTarget.MethodName}({javaTarget.ParamCount} params) — {javaTarget.Why}");
        if (!javaSources.Any(s => s.Path == "pom.xml"))
            return Skip("equivalence.no_pom", "The generated package has no pom.xml, so the Java side cannot be built.");

        // ── run both sides at once ──
        var phpFiles = await PhpFilesAsync(file, content, ct);
        var phpTask = RunPhpAsync(phpFiles, routine, file.RelativePath, sub.LineStart, phpClass, vectors, ct);
        var javaTask = RunJavaAsync(javaSources, javaTarget, vectors, ct);
        await Task.WhenAll(phpTask, javaTask);
        var (phpResp, phpError) = phpTask.Result;
        var java = javaTask.Result;

        t.AppendLine().AppendLine("=== php sidecar ===");
        if (phpError is not null) t.AppendLine(phpError);
        else t.AppendLine($"php {phpResp!["php"]} ({phpResp["sapi"]}) · {phpFiles.Count} file(s) loaded as declarations · {phpResp["durationMs"]} ms · routine {phpResp["routine"]?.ToJsonString() ?? phpResp["loadError"]?.ToString()}");
        t.AppendLine().AppendLine("=== maven sidecar (JUnit equivalence harness) ===");
        t.AppendLine(java.Log);

        if (phpError is not null)
            return new Outcome("ERRORED", "equivalence.php_sidecar_failed", $"The PHP sidecar did not run: {phpError}",
                new { mode = "per-vector", sourceLanguage = "php", routine }, t.ToString());
        if (java.Fatal is not null && java.Results.Count == 0)
            return new Outcome("ERRORED", "equivalence.java_harness_failed", $"The Java side did not run: {java.Fatal}",
                new { mode = "per-vector", sourceLanguage = "php", routine, javaTarget }, t.ToString());

        // ── per-vector verdicts ──
        var phpById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var r in (phpResp!["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if (r["id"]?.ToString() is { } id) phpById[id] = r;
        var rows = new List<object>();
        int passed = 0, failed = 0, skipped = 0, errored = 0;
        t.AppendLine().AppendLine("=== vector by vector ===");
        foreach (var v in vectors)
        {
            phpById.TryGetValue(v.Id, out var p);
            java.Results.TryGetValue(v.Id, out var j);
            var verdict = EquivalenceComparer.Compare(p, j);
            switch (verdict.Status)
            {
                case EquivalenceComparer.Passed: passed++; break;
                case EquivalenceComparer.Failed: failed++; break;
                case EquivalenceComparer.Skipped: skipped++; break;
                default: errored++; break;
            }
            t.AppendLine($"{v.Id} [{verdict.Status}] {v.Source} · {v.Label} · inputs {v.Named.ToJsonString()}" +
                         (v.Superglobals is null ? "" : $" · request {v.Superglobals.ToJsonString()}"));
            t.AppendLine($"    {verdict.Reason}{(verdict.Diff is null ? "" : $"  [{verdict.Diff}]")}");
            foreach (var n in verdict.Notes) t.AppendLine($"    note: {n}");
            rows.Add(new
            {
                id = v.Id,
                source = v.Source,
                label = v.Label,
                inputs = JsonSerializer.Deserialize<JsonElement>(v.Named.ToJsonString()),
                request = v.Superglobals is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(v.Superglobals.ToJsonString()),
                status = verdict.Status,
                reason = verdict.Reason,
                diff = verdict.Diff,
                notes = verdict.Notes,
                php = p is null ? (JsonElement?)null : Pick(p, "returnValue", "output", "responseCode", "headers", "exception", "byRef", "sessionChanged", "exited"),
                java = j is null ? (JsonElement?)null : Pick(j, "returnValue", "exception", "returnType"),
            });
        }

        string status, summary;
        string? errorCode = null;
        var counts = $"{passed} passed, {failed} failed, {skipped} skipped" + (errored > 0 ? $", {errored} errored" : "");
        var against = $"{javaTarget.ClassName[(javaTarget.ClassName.LastIndexOf('.') + 1)..]}.{javaTarget.MethodName}";
        if (failed > 0)
        {
            status = "FAILED";
            errorCode = "equivalence.outputs_differ";
            summary = $"PHP `{routine}` and Java {against} differ on {failed} of {vectors.Count} vectors from the signed spec ({counts}).";
        }
        else if (passed > 0)
        {
            status = "PASSED";
            summary = $"PHP `{routine}` and Java {against} agree on {passed} of {vectors.Count} vectors from the signed spec ({counts}).";
        }
        else if (errored > 0)
        {
            status = "ERRORED";
            errorCode = "equivalence.runner_errors";
            summary = $"No vector could be compared for `{routine}` ({counts}).";
        }
        else
        {
            status = "SKIPPED";
            errorCode = "equivalence.all_vectors_skipped";
            summary = $"None of the {vectors.Count} vectors could be run fairly on both sides for `{routine}` ({counts}); see each vector's reason.";
        }
        t.AppendLine().AppendLine($"=== verdict === {status} · {counts}");

        var metrics = new
        {
            mode = "per-vector",
            sourceLanguage = "php",
            routine,
            javaTarget = new { className = javaTarget.ClassName, method = javaTarget.MethodName, why = javaTarget.Why },
            php = new { version = phpResp["php"]?.ToString(), sapi = phpResp["sapi"]?.ToString(), files = phpFiles.Count, durationMs = phpResp["durationMs"]?.GetValue<int>() },
            java = new { compileMs = java.CompileMs, testMs = java.TestMs },
            vectorCount = vectors.Count,
            passed, failed, skipped, errored,
            vectors = rows,
        };
        return new Outcome(status, errorCode, summary, metrics, t.ToString());
    }

    private static JsonElement Pick(JsonObject o, params string[] keys)
    {
        var x = new JsonObject();
        foreach (var k in keys)
            if (o[k] is { } v)
            {
                if (k == "output" && v.ToString() is { Length: > 2000 } s) x[k] = s[..2000] + "…";
                else x[k] = v.DeepClone();
            }
        return JsonSerializer.Deserialize<JsonElement>(x.ToJsonString());
    }

    /// <summary>The routine's file first, then PHP files of the same version nearest to it (for classes it uses).</summary>
    private async Task<List<PhpClient.Source>> PhpFilesAsync(SourceFile file, string content, CancellationToken ct)
    {
        var files = new List<PhpClient.Source> { new(file.RelativePath, content) };
        var dir = file.RelativePath.Contains('/') ? file.RelativePath[..file.RelativePath.LastIndexOf('/')] : "";
        var siblings = await _db.SourceFiles.AsNoTracking()
            .Where(f => f.SourceVersionId == file.SourceVersionId && f.Id != file.Id && f.RelativePath.EndsWith(".php"))
            .Select(f => new { f.RelativePath, f.BlobUri, f.SizeBytes })
            .ToListAsync(ct);
        long bytes = content.Length;
        foreach (var s in siblings
                     .OrderBy(s => s.RelativePath.StartsWith(dir + "/") || dir.Length == 0 ? 0 : 1)
                     .ThenBy(s => s.RelativePath.Count(c => c == '/'))
                     .ThenBy(s => s.RelativePath, StringComparer.Ordinal)
                     .Take(MaxSiblingFiles))
        {
            if (bytes + (s.SizeBytes ?? 0) > MaxSiblingBytes) continue;
            try
            {
                var text = await _blob.GetTextAsync(s.BlobUri, ct);
                bytes += text.Length;
                files.Add(new PhpClient.Source(s.RelativePath, text));
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Skipping {Path} for the PHP equivalence run", s.RelativePath);
            }
        }
        return files;
    }

    private async Task<(JsonObject? Response, string? Error)> RunPhpAsync(
        List<PhpClient.Source> files, string routine, string path, int line, string? phpClass,
        List<PhpEquivalenceVectors.EqVector> vectors, CancellationToken ct)
    {
        if (await _php.HealthAsync(ct) is null)
            return (null, $"PHP sidecar unreachable at {_php.Endpoint} (Validation:PhpEndpoint).");
        try
        {
            var shortClass = phpClass is null ? null : phpClass[(phpClass.LastIndexOf('\\') + 1)..];
            var req = new PhpClient.RunRequest(files,
                new PhpClient.Entry(routine, path, line > 0 ? line : null, shortClass),
                vectors.Select(v => new PhpClient.Vector(v.Id, (JsonObject)v.Named.DeepClone(), null,
                    (JsonObject?)v.Superglobals?.DeepClone())).ToList());
            return (await _php.RunAsync(req, ct), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    public sealed record JavaRun(Dictionary<string, JsonObject> Results, string? Fatal, string Log, int CompileMs, int TestMs);

    private async Task<JavaRun> RunJavaAsync(List<MavenClient.JavaSource> sources, JavaTargetLocator.Target target,
        List<PhpEquivalenceVectors.EqVector> vectors, CancellationToken ct)
    {
        if (!await _maven.PingAsync(ct))
            return new JavaRun(new(), "maven sidecar unreachable (Validation:MavenEndpoint).", "", 0, 0);
        var request = new JsonObject
        {
            ["className"] = target.ClassName,
            ["methodName"] = target.MethodName,
            ["paramCount"] = target.ParamCount,
            ["vectors"] = new JsonArray(vectors.Select(v => (JsonNode)new JsonObject { ["id"] = v.Id, ["named"] = v.JavaNamed() }).ToArray()),
        };
        var all = sources
            .Where(s => !s.Path.EndsWith($"{HarnessClass}.java", StringComparison.Ordinal))
            .Select(s => s.Path == "pom.xml" ? s with { Content = EnsureJUnit(s.Content) } : s)
            .ToList();
        all.Add(new MavenClient.JavaSource($"src/test/java/astra/equivalence/{HarnessClass}.java", HarnessSource()));
        all.Add(new MavenClient.JavaSource("src/test/resources/astra-equivalence/request.json", request.ToJsonString()));
        var args = new[]
        {
            $"-Dtest={HarnessClass}", "-Dsurefire.failIfNoSpecifiedTests=false", "-DfailIfNoTests=false",
            "-Djacoco.skip=true", "-Dcheckstyle.skip=true", "-Dpmd.skip=true", "-Dspotbugs.skip=true",
        };
        MavenClient.CompileAndTestResponse resp;
        try
        {
            resp = await _maven.CompileAndTestAsync(new MavenClient.CompileAndTestRequest(all, args, TimeoutMs: 240_000), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JavaRun(new(), $"maven sidecar call failed: {ex.Message}", "", 0, 0);
        }
        var log = new StringBuilder();
        log.AppendLine($"compile exit {resp.Compile.ExitCode} · {resp.Compile.DurationMs} ms");
        if (resp.Compile.ExitCode != 0) log.AppendLine(Tail(resp.Compile.Log, 6000));
        if (resp.Test is null)
            return new JavaRun(new(), $"the package did not compile with the harness ({resp.SkippedTestReason ?? "no test run"})", log.ToString(), resp.Compile.DurationMs, 0);
        log.AppendLine($"test exit {resp.Test.ExitCode} · {resp.Test.DurationMs} ms · tests {resp.Test.Tests} · timedOut {resp.Test.TimedOut}");
        var (results, fatal) = ParseHarness(resp.Test.Stdout + "\n" + resp.Test.Stderr);
        if (results.Count == 0) log.AppendLine(Tail(resp.Test.Stdout, 6000));
        return new JavaRun(results, fatal ?? (results.Count == 0 ? "the harness printed no results" : null), log.ToString(), resp.Compile.DurationMs, resp.Test.DurationMs);
    }

    public static (Dictionary<string, JsonObject> Results, string? Fatal) ParseHarness(string stdout)
    {
        var results = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string? fatal = null;
        foreach (var raw in stdout.Split('\n'))
        {
            var idx = raw.IndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            JsonObject? o;
            try { o = JsonNode.Parse(raw[(idx + Marker.Length)..].Trim()) as JsonObject; }
            catch { continue; }
            if (o is null) continue;
            if (o["fatal"] is { } f) { fatal = f.ToString(); continue; }
            if (o["id"]?.ToString() is { } id) results[id] = o;
        }
        return (results, fatal);
    }

    /// <summary>The harness needs JUnit 5; a package whose pom has neither JUnit nor Spring's test starter gets it.</summary>
    public static string EnsureJUnit(string pom)
    {
        if (pom.Contains("junit-jupiter", StringComparison.Ordinal) || pom.Contains("spring-boot-starter-test", StringComparison.Ordinal))
            return pom;
        const string dep = "<dependency><groupId>org.junit.jupiter</groupId><artifactId>junit-jupiter</artifactId><version>5.10.2</version><scope>test</scope></dependency>";
        var at = pom.LastIndexOf("</dependencies>", StringComparison.Ordinal);
        if (at >= 0) return pom.Insert(at, dep);
        at = pom.LastIndexOf("</project>", StringComparison.Ordinal);
        return at < 0 ? pom : pom.Insert(at, $"<dependencies>{dep}</dependencies>");
    }

    private static string? _harness;

    public static string HarnessSource()
    {
        if (_harness is not null) return _harness;
        var asm = typeof(PhpEquivalenceRunner).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith($"{HarnessClass}.java", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return _harness = r.ReadToEnd();
    }

    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
}
