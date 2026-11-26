using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Astra.Api.Validation.Quality;

/// <summary>A library a generated package declares, with the version it asks for.</summary>
public sealed record Dependency(string Ecosystem, string Name, string Version, string DeclaredIn)
{
    /// <summary>Package URL (purl), the identifier CycloneDX and vulnerability databases share.</summary>
    public string Purl => Ecosystem switch
    {
        "PyPI" => $"pkg:pypi/{Name.ToLowerInvariant()}@{Version}",
        "npm" => Name.StartsWith('@') ? $"pkg:npm/%40{Name[1..]}@{Version}" : $"pkg:npm/{Name}@{Version}",
        "Maven" => $"pkg:maven/{Name.Replace(':', '/')}@{Version}",
        "NuGet" => $"pkg:nuget/{Name}@{Version}",
        _ => $"pkg:generic/{Name}@{Version}",
    };
}

/// <summary>
/// The libraries a generated package declares (requirements.txt, package.json, pom.xml, *.csproj), a CycloneDX bill
/// of materials built from them, and a check of each against the OSV.dev vulnerability database (Q4 of the QUALITY
/// gate). Reading and the bill of materials are pure; only the OSV lookup touches the network, and a lookup that
/// fails is reported, never fatal. Versions are the declared ones: a range such as <c>^18.3.1</c> is checked at its
/// lower bound, and a Maven dependency whose version comes from a BOM is listed without a version and not checked.
/// </summary>
public static class Dependencies
{
    private static readonly Regex RequirementLine = new(@"^\s*([A-Za-z0-9][A-Za-z0-9._\-\[\]]*)\s*(==|>=|~=)\s*([0-9][^\s;,#]*)", RegexOptions.Compiled);
    private static readonly Regex NpmVersion = new(@"(\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?)", RegexOptions.Compiled);

    public static IReadOnlyList<Dependency> Read(IEnumerable<(string Path, string Content)> files)
    {
        var deps = new List<Dependency>();
        foreach (var (path, content) in files)
        {
            var p = path.Replace('\\', '/');
            var name = p[(p.LastIndexOf('/') + 1)..];
            if (p.Contains("/node_modules/", StringComparison.Ordinal)) continue;
            try
            {
                if (name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase)) deps.AddRange(ReadRequirements(content, p));
                else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) deps.AddRange(ReadPackageJson(content, p));
                else if (name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase)) deps.AddRange(ReadPom(content, p));
                else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) deps.AddRange(ReadCsproj(content, p));
            }
            catch (Exception) { /* a manifest that does not parse is simply not read; the build gates report it */ }
        }
        return deps.GroupBy(d => (d.Ecosystem, d.Name.ToLowerInvariant(), d.Version)).Select(g => g.First()).ToList();
    }

    internal static IEnumerable<Dependency> ReadRequirements(string content, string path)
    {
        foreach (var raw in content.Split('\n'))
        {
            var m = RequirementLine.Match(raw);
            if (!m.Success) continue;
            var pkg = Regex.Replace(m.Groups[1].Value, @"\[.*\]$", "");   // uvicorn[standard] → uvicorn
            yield return new("PyPI", pkg, m.Groups[3].Value, path);
        }
    }

    internal static IEnumerable<Dependency> ReadPackageJson(string content, string path)
    {
        var root = JsonNode.Parse(content)?.AsObject();
        if (root is null) yield break;
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (root[section] is not JsonObject obj) continue;
            foreach (var (pkg, v) in obj)
            {
                var spec = v?.GetValue<string>() ?? "";
                var m = NpmVersion.Match(spec);
                if (m.Success) yield return new("npm", pkg, m.Groups[1].Value, path);
            }
        }
    }

    internal static IEnumerable<Dependency> ReadPom(string content, string path)
    {
        var doc = XDocument.Parse(content);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var props = doc.Root?.Element(ns + "properties")?.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value.Trim())
                    ?? new Dictionary<string, string>();
        string Resolve(string v) => Regex.Replace(v, @"\$\{([^}]+)\}", m => props.TryGetValue(m.Groups[1].Value, out var x) ? x : m.Value);
        var parent = doc.Root?.Element(ns + "parent");
        if (parent is not null && parent.Element(ns + "version")?.Value is { Length: > 0 } pv)
            yield return new("Maven", $"{parent.Element(ns + "groupId")?.Value}:{parent.Element(ns + "artifactId")?.Value}", pv.Trim(), path);
        var deps = doc.Root?.Element(ns + "dependencies")?.Elements(ns + "dependency") ?? [];
        foreach (var d in deps)
        {
            var g = d.Element(ns + "groupId")?.Value.Trim();
            var a = d.Element(ns + "artifactId")?.Value.Trim();
            var v = Resolve(d.Element(ns + "version")?.Value.Trim() ?? "");
            if (string.IsNullOrEmpty(g) || string.IsNullOrEmpty(a)) continue;
            yield return new("Maven", $"{g}:{a}", v.Contains("${") ? "" : v, path);
        }
    }

    internal static IEnumerable<Dependency> ReadCsproj(string content, string path)
    {
        var doc = XDocument.Parse(content);
        foreach (var r in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var pkg = r.Attribute("Include")?.Value;
            var v = r.Attribute("Version")?.Value ?? r.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
            if (!string.IsNullOrEmpty(pkg)) yield return new("NuGet", pkg, v?.Trim() ?? "", path);
        }
    }

    /// <summary>A CycloneDX 1.5 JSON bill of materials for the package's declared libraries.</summary>
    public static string BuildSbom(string componentName, string version, IReadOnlyList<Dependency> deps, DateTimeOffset now) =>
        new JsonObject
        {
            ["bomFormat"] = "CycloneDX",
            ["specVersion"] = "1.5",
            ["version"] = 1,
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["tools"] = new JsonArray(new JsonObject { ["vendor"] = "Artizent", ["name"] = "Astra quality gate" }),
                ["component"] = new JsonObject { ["type"] = "application", ["name"] = componentName, ["version"] = version },
            },
            ["components"] = new JsonArray(deps.Select(d => (JsonNode)new JsonObject
            {
                ["type"] = "library",
                ["name"] = d.Name,
                ["version"] = d.Version,
                ["purl"] = d.Version.Length > 0 ? d.Purl : null,
                ["properties"] = new JsonArray(new JsonObject { ["name"] = "astra:declaredIn", ["value"] = d.DeclaredIn }),
            }).ToArray()),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Severity of a vulnerability for the gate: a known high or critical vulnerability blocks.</summary>
    public static string SeverityFor(string? osvSeverity) => (osvSeverity ?? "").ToUpperInvariant() switch
    {
        "CRITICAL" or "HIGH" => "critical",
        "MODERATE" or "MEDIUM" => "major",
        "LOW" => "minor",
        _ => "major",
    };

    /// <summary>
    /// Looks every versioned dependency up in OSV.dev (one batch query, then the details of each hit, at most 25).
    /// Returns the findings and whether the lookup ran.
    /// </summary>
    public static async Task<(IReadOnlyList<QualityFinding> Findings, bool Ran, string? Error)> CheckAsync(
        HttpClient http, IReadOnlyList<Dependency> deps, CancellationToken ct)
    {
        var checkable = deps.Where(d => d.Version.Length > 0).ToList();
        if (checkable.Count == 0) return ([], true, null);
        try
        {
            var query = new { queries = checkable.Select(d => new { package = new { name = d.Name, ecosystem = d.Ecosystem }, version = d.Version }).ToArray() };
            using var resp = await http.PostAsJsonAsync("https://api.osv.dev/v1/querybatch", query, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var hits = new List<(Dependency Dep, string Id)>();
            var i = 0;
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                if (i < checkable.Count && r.TryGetProperty("vulns", out var vulns))
                    foreach (var v in vulns.EnumerateArray())
                        if (v.TryGetProperty("id", out var id)) hits.Add((checkable[i], id.GetString()!));
                i++;
            }
            var findings = new List<QualityFinding>();
            foreach (var (dep, id) in hits.Take(25))
            {
                string summary = id, sev = "";
                try
                {
                    using var detail = JsonDocument.Parse(await http.GetStringAsync($"https://api.osv.dev/v1/vulns/{Uri.EscapeDataString(id)}", ct));
                    var d = detail.RootElement;
                    if (d.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String) summary = s.GetString()!;
                    if (d.TryGetProperty("database_specific", out var db) && db.TryGetProperty("severity", out var se) && se.ValueKind == JsonValueKind.String) sev = se.GetString()!;
                }
                catch (Exception) { /* the id alone is still a finding */ }
                findings.Add(new QualityFinding($"DEP-{id}", "security", SeverityFor(sev), dep.DeclaredIn, null,
                    $"{dep.Name} {dep.Version} has a known vulnerability: {summary} ({id}).",
                    "A library with a published vulnerability gives an attacker a documented way in; it is the most common route into a web service.",
                    $"Upgrade {dep.Name} to a version without {id} (see https://osv.dev/vulnerability/{id}).", Source: "tool"));
            }
            if (hits.Count > 25)
                findings.Add(new QualityFinding("DEP-MORE", "security", "major", checkable[0].DeclaredIn, null,
                    $"{hits.Count - 25} more known vulnerabilities in the declared libraries.", "See the full OSV report.", "Upgrade the libraries listed.", Source: "tool"));
            return (findings, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ([], false, ex.Message);
        }
    }
}
