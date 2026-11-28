using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Validation;
using Astra.Api.Validation.PhpEquivalence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Astra.Api.Tests;

/// <summary>
/// Opt-in live check of seeded-database equivalence against real sidecars (never runs in CI): set ASTRA_EQ_E2E_DIR to a
/// folder of <c>&lt;unit&gt;.zip</c> (generated package) + <c>&lt;unit&gt;.spec.json</c> (signed spec) + optional
/// <c>&lt;unit&gt;.seeds.json</c> ({"*": {tables:{…}, session:{…}}} or per vector id), ASTRA_EQ_PHP_ROOT to the PHP
/// source tree, ASTRA_EQ_UNITS to "name=path/to/File.php,…", and ASTRA_EQ_PHP / ASTRA_EQ_MAVEN to the sidecars.
/// Runs the same vectors, schema translation, seeds, sidecar calls and comparer as the runner, and prints each verdict.
/// </summary>
public class EquivalenceLiveCheck
{
    private readonly ITestOutputHelper _out;
    public EquivalenceLiveCheck(ITestOutputHelper output) => _out = output;

    private sealed class Http : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(10) };
    }

    [Fact]
    public async Task Seeded_units_compare_against_real_sidecars()
    {
        var dir = Environment.GetEnvironmentVariable("ASTRA_EQ_E2E_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;   // opt-in only
        var phpRoot = Environment.GetEnvironmentVariable("ASTRA_EQ_PHP_ROOT")!;
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Validation:PhpEndpoint"] = Environment.GetEnvironmentVariable("ASTRA_EQ_PHP") ?? "http://php-eq:51060",
            ["Validation:MavenEndpoint"] = Environment.GetEnvironmentVariable("ASTRA_EQ_MAVEN") ?? "http://maven-eq:51053",
        }).Build();
        var php = new PhpClient(new Http(), cfg, NullLogger<PhpClient>.Instance);
        var maven = new MavenClient(new Http(), cfg, NullLogger<MavenClient>.Instance);
        var runner = new PhpEquivalenceRunner(null!, null!, php, maven, NullLogger.Instance);

        var phpFiles = new[] { "application", "public" }.Where(d => Directory.Exists(Path.Combine(phpRoot, d)))
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(phpRoot, d), "*.php", SearchOption.AllDirectories))
            .Select(f => new PhpClient.Source(Path.GetRelativePath(phpRoot, f).Replace('\\', '/'), File.ReadAllText(f))).ToList();

        foreach (var pair in (Environment.GetEnvironmentVariable("ASTRA_EQ_UNITS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (unit, path) = (pair.Split('=')[0], pair.Split('=')[1]);
            var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, unit + ".spec.json"))).RootElement;
            var vectors = PhpEquivalenceVectors.Build(spec);
            if (vectors.Count == 0) vectors.Add(new PhpEquivalenceVectors.EqVector("V1", "seeded", "called against seeded tables", new JsonObject(), null));

            var java = new List<MavenClient.JavaSource>();
            using (var zip = ZipFile.OpenRead(Path.Combine(dir, unit + ".zip")))
                foreach (var e in zip.Entries.Where(e => e.Length > 0))
                {
                    using var r = new StreamReader(e.Open());
                    java.Add(new MavenClient.JavaSource(e.FullName, r.ReadToEnd()));
                }
            var schemaSql = java.First(s => s.Path == "src/main/resources/schema.sql").Content;
            var statements = EquivalenceDatabase.Statements(schemaSql);
            var sqlite = EquivalenceDatabase.ToSqlite(statements);
            var tables = EquivalenceDatabase.Tables(statements);

            // seeds: "*" for every vector, or per vector id
            var seedsFile = Path.Combine(dir, unit + ".seeds.json");
            var seeds = File.Exists(seedsFile) ? (JsonObject)JsonNode.Parse(File.ReadAllText(seedsFile))! : new JsonObject();
            var t = new Dictionary<string, JsonObject>();
            var s = new Dictionary<string, JsonObject>();
            foreach (var v in vectors)
            {
                var one = seeds[v.Id] as JsonObject ?? seeds["*"] as JsonObject;
                if (one?["tables"] is JsonObject tt) t[v.Id] = (JsonObject)tt.DeepClone();
                if (one?["session"] is JsonObject ss) s[v.Id] = (JsonObject)ss.DeepClone();
            }
            var plan = new SeedPlan(t, s, "engineer", null, 0m, null);
            vectors = vectors.Select(v =>
            {
                if (!plan.Session.TryGetValue(v.Id, out var ses) || v.Superglobals?["session"] is not null) return v;
                var sg = (JsonObject?)v.Superglobals?.DeepClone() ?? new JsonObject();
                sg["session"] = ses.DeepClone();
                return v with { Superglobals = sg };
            }).ToList();

            var phpClass = spec.TryGetProperty("enclosing_type", out var et) && et.ValueKind == JsonValueKind.String ? et.GetString() : null;
            var target = JavaTargetLocator.Find(java.Select(j => (j.Path, j.Content)), unit, phpClass);
            _out.WriteLine($"===== {unit} ({path}) · {vectors.Count} vector(s) · tables {string.Join(",", tables)} · java {target?.ClassName}.{target?.MethodName} ({target?.Why})");
            if (target is null) { _out.WriteLine("  no Java counterpart"); continue; }

            var phpTask = runner.RunPhpAsync(phpFiles, unit, path, 0, phpClass, vectors, CancellationToken.None, v => PhpEquivalenceRunner.PhpDb(v, plan, sqlite, tables));
            var javaTask = runner.RunJavaAsync(java, target, vectors, CancellationToken.None, PhpEquivalenceRunner.JavaDb(statements, tables, schemaSql), plan);
            await Task.WhenAll(phpTask, javaTask);
            var (phpResp, phpError) = phpTask.Result;
            var javaRun = javaTask.Result;
            if (phpError is not null) { _out.WriteLine($"  PHP sidecar error: {phpError}"); continue; }
            if (javaRun.Fatal is not null && javaRun.Results.Count == 0) { _out.WriteLine($"  Java fatal: {javaRun.Fatal}\n{Tail(javaRun.Log, 3000)}"); continue; }
            var byId = (phpResp!["results"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToDictionary(r => r["id"]!.ToString());
            foreach (var v in vectors)
            {
                byId.TryGetValue(v.Id, out var p);
                javaRun.Results.TryGetValue(v.Id, out var j);
                var verdict = EquivalenceComparer.Compare(p, j);
                _out.WriteLine($"  {v.Id} [{verdict.Status}] {v.Label} · inputs {v.Named.ToJsonString()}");
                _out.WriteLine($"      {verdict.Reason}{(verdict.Diff is null ? "" : $"  [{verdict.Diff}]")}");
                foreach (var n in verdict.Notes.Take(5)) _out.WriteLine($"      note: {n}");
                _out.WriteLine($"      php db: {Short(p?["db"])} · java db: {Short(j?["db"])}");
                if (p?["exception"] is not null) _out.WriteLine($"      php exception: {Short(p["exception"])}");
                if (j?["exception"] is not null) _out.WriteLine($"      java exception: {Short(j["exception"])}");
            }
        }
    }

    private static string Short(JsonNode? n) { var s = n?.ToJsonString() ?? "null"; return s.Length <= 300 ? s : s[..300] + "…"; }
    private static string Tail(string s, int n) => s.Length <= n ? s : "…" + s[^n..];
}
