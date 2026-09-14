using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Persistence.Entities;

namespace Astra.Api.Llm;

/// <summary>
/// WS3 Mode A — faithful 1:1 conversion. Instead of substituting one routine
/// into a canonical archetype package (which re-shapes the code into a "REST
/// resource" or an "SMTP wrapper"), the routine's whole source UNIT is
/// converted into one target file: same types, same routine names, same
/// order, same call graph — only the syntax and casing become idiomatic.
/// The unit's signed specs ride along as guardrails (their claims must hold
/// and are cited), so every claim still becomes a named test and the four
/// gates run unchanged.
///
/// The mode is expressed as the target stack <see cref="Stack"/>: that single
/// naming choice reuses the prompt-family fallback, the validators' dotnet
/// branch, the (spec, target) uniqueness of scaffolds, and the target picker
/// in the UI without touching any of them.
/// </summary>
public static class FaithfulConversion
{
    public const string Stack = "dotnet10-faithful";
    public const string Mode = "faithful-1to1";
    public const string PromptKind = "faithful-transform";
    public const string ToolName = "emit_package";

    /// <summary>The archetype file that only shows the model the target
    /// shape; it is never carried into a generated package.</summary>
    public const string ExemplarPath = "src/Unit.cs";

    /// <summary>One non-streaming call has to return the whole unit; past
    /// this many source lines the output cap is hit and the package would be
    /// cut off mid-file, so the provider refuses up front with a reason.</summary>
    public const int MaxUnitLines = 1200;

    /// <summary>Every placeholder the faithful prompt may use — the test
    /// suite checks the prompt on disk against this list so a typo can never
    /// leave a literal <c>{{name}}</c> in what the model reads.</summary>
    public static readonly string[] PromptVariables =
    {
        "unitName", "unitPath", "className", "namespace", "anchorRoutine",
        "routineCount", "signedCount", "unitRoutinesJson", "unitSpecsJson",
        "unitSourceText", "rtlMappingTable", "exemplarSource", "provenanceSource",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsFaithful(string? targetStack) =>
        targetStack is not null && targetStack.StartsWith(Stack, StringComparison.OrdinalIgnoreCase);

    public static bool IsExemplar(string path) =>
        string.Equals(path.Replace('\\', '/'), ExemplarPath, StringComparison.OrdinalIgnoreCase);

    // ────────────────────────────────────────────────────────────────────
    // Unit context — what the pipeline gathers, what the provider narrates
    // ────────────────────────────────────────────────────────────────────

    public sealed record UnitRoutine(
        Guid Id,
        string Name,
        string Signature,
        int LineStart,
        int LineEnd,
        IReadOnlyList<string> Calls,
        /// <summary>SIGNED, or the current draft's state, or "none".</summary>
        string SpecState,
        Guid? SpecId);

    public sealed record UnitSpec(string Routine, string SpecJson);

    public sealed record UnitContext(
        string UnitName,
        string UnitPath,
        string ClassName,
        string Namespace,
        IReadOnlyList<UnitRoutine> Routines,
        IReadOnlyList<UnitSpec> SignedSpecs)
    {
        public int SignedCount => SignedSpecs.Count;

        /// <summary>Inventory the model must convert completely, in source order.</summary>
        public string RoutinesJson() => JsonSerializer.Serialize(
            Routines.Select(r => new
            {
                name = r.Name,
                signature = r.Signature,
                lines = $"{r.LineStart}-{r.LineEnd}",
                calls = r.Calls.Count == 0 ? null : r.Calls,
                spec = r.SpecState,
            }),
            JsonOpts);

        /// <summary>The signed specs, each labelled with its routine.</summary>
        public string SpecsJson()
        {
            var items = new List<object>(SignedSpecs.Count);
            foreach (var s in SignedSpecs)
            {
                JsonElement spec;
                try { using var doc = JsonDocument.Parse(s.SpecJson); spec = doc.RootElement.Clone(); }
                catch (JsonException) { continue; }
                items.Add(new { routine = s.Routine, spec });
            }
            return JsonSerializer.Serialize(items, JsonOpts);
        }

        /// <summary>Union of every claim id across the unit's signed specs —
        /// the closed vocabulary for <c>[SpecClaim]</c> citations.</summary>
        public HashSet<string> ClaimIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in SignedSpecs) ids.UnionWith(ClaimIdsOf(s.SpecJson));
            return ids;
        }
    }

    /// <summary>
    /// Gather the unit around one routine: every routine of the same source
    /// file in line order, and for each the SIGNED spec if one exists (the
    /// newest, when a routine was signed more than once), else the state of
    /// its newest live draft so the prompt can say which routines are
    /// converted from source alone.
    /// </summary>
    public static UnitContext Build(string unitPath, IEnumerable<Subroutine> routines, IEnumerable<Spec> specs)
    {
        var byRoutine = specs
            .Where(s => !string.Equals(s.State, "SUPERSEDED", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.SubroutineId)
            .ToDictionary(g => g.Key, g =>
                g.OrderByDescending(s => string.Equals(s.State, "SIGNED", StringComparison.OrdinalIgnoreCase))
                 .ThenByDescending(s => s.CreatedAt)
                 .First());

        var unitName = UnitNameOf(unitPath);
        var list = new List<UnitRoutine>();
        var signed = new List<UnitSpec>();
        foreach (var r in routines.OrderBy(r => r.LineStart).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            byRoutine.TryGetValue(r.Id, out var spec);
            var state = spec is null ? "none" : spec.State.ToUpperInvariant();
            list.Add(new UnitRoutine(r.Id, r.Name, r.Signature, r.LineStart, r.LineEnd, CallsOf(r.CalledSubroutines), state, spec?.Id));
            if (spec is not null && state == "SIGNED")
                signed.Add(new UnitSpec(r.Name, spec.SpecJson.RootElement.GetRawText()));
        }
        return new UnitContext(unitName, unitPath, ClassNameFor(unitName), NamespaceFor(unitName), list, signed);
    }

    public static string UnitNameOf(string sourcePath)
    {
        var p = (sourcePath ?? "").Replace('\\', '/');
        var last = p.LastIndexOf('/');
        var file = last >= 0 ? p[(last + 1)..] : p;
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    public static string ClassNameFor(string unitName) => IdentifierOf(unitName);

    public static string NamespaceFor(string unitName) => $"Faithful.{IdentifierOf(unitName)}";

    /// <summary>A C# identifier from a unit name: invalid characters become
    /// underscores, a leading digit gets one, and nothing becomes "Unit".</summary>
    public static string IdentifierOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unit";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>The parser records callees as a JSON array of names, or of
    /// objects carrying a <c>name</c>; either way, distinct names in order.</summary>
    public static IReadOnlyList<string> CallsOf(JsonDocument? calledSubroutines)
    {
        if (calledSubroutines is null || calledSubroutines.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var el in calledSubroutines.RootElement.EnumerateArray())
        {
            string? n = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Object when el.TryGetProperty("name", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(n) && seen.Add(n)) list.Add(n);
        }
        return list;
    }

    /// <summary>Every <c>id</c> across a spec's claim sections.</summary>
    public static HashSet<string> ClaimIdsOf(string specJson)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(specJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return ids;
            foreach (var section in doc.RootElement.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in section.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                        && idEl.GetString() is { Length: > 0 } id)
                        ids.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // An unparseable spec contributes no ids; citations then fall
            // through the filter, which is the honest outcome.
        }
        return ids;
    }

    // ────────────────────────────────────────────────────────────────────
    // Prompt + tool
    // ────────────────────────────────────────────────────────────────────

    public static Dictionary<string, string?> PromptVariablesFor(
        ScaffoldRequest request, UnitContext unit, string exemplarSource, string provenanceSource, string? rtlMappingTable) =>
        new()
        {
            ["unitName"] = unit.UnitName,
            ["unitPath"] = unit.UnitPath,
            ["className"] = unit.ClassName,
            ["namespace"] = unit.Namespace,
            ["anchorRoutine"] = request.SubroutineName,
            ["routineCount"] = unit.Routines.Count.ToString(),
            ["signedCount"] = unit.SignedCount.ToString(),
            ["unitRoutinesJson"] = unit.RoutinesJson(),
            ["unitSpecsJson"] = unit.SpecsJson(),
            ["unitSourceText"] = request.OriginalSourceText,
            ["rtlMappingTable"] = rtlMappingTable ?? "(no curated RTL mapping table is registered for this source language)",
            ["exemplarSource"] = exemplarSource,
            ["provenanceSource"] = provenanceSource,
        };

    /// <summary>The forced tool the model answers with. Its input is
    /// serialised by the API, so file contents full of quotes and
    /// backslashes can never arrive as malformed JSON.</summary>
    public static Dictionary<string, object?> EmitPackageTool() => new()
    {
        ["name"] = ToolName,
        ["description"] = "Return the converted unit as a package: one entry per file, each with its full content. " +
                          "Emit the converted unit file first, then any stub files; never emit .csproj or test files.",
        ["input_schema"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["files"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["language"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["content"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["derivedFromClaimIds"] = new Dictionary<string, object?>
                            {
                                ["type"] = "array",
                                ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                            },
                        },
                        ["required"] = new[] { "path", "content" },
                    },
                },
            },
            ["required"] = new[] { "files" },
        },
    };

    // ────────────────────────────────────────────────────────────────────
    // Package assembly
    // ────────────────────────────────────────────────────────────────────

    public sealed record PackageFile(string Path, string Language, string Content, string[] DerivedFromClaimIds);

    /// <summary>Files from the tool call's input: paths normalised, language
    /// defaulting to C#, citations filtered to ids that exist in the unit's
    /// signed specs. Empty on anything unparseable.</summary>
    public static List<PackageFile> ParseToolFiles(string? toolInputJson, HashSet<string> validClaimIds)
    {
        var result = new List<PackageFile>();
        if (string.IsNullOrWhiteSpace(toolInputJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(toolInputJson);
            if (!doc.RootElement.TryGetProperty("files", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var path = ReadString(item, "path").Replace('\\', '/').TrimStart('/');
                if (path.Length == 0 || path.Contains("..") || !seen.Add(path)) continue;
                var content = ReadString(item, "content");
                if (content.Length == 0) continue;
                var language = ReadString(item, "language");
                if (language.Length == 0) language = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "csharp" : "text";
                var claims = item.TryGetProperty("derivedFromClaimIds", out var c) && c.ValueKind == JsonValueKind.Array
                    ? c.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0 && validClaimIds.Contains(s)).Distinct().ToArray()
                    : Array.Empty<string>();
                result.Add(new PackageFile(path, language, content, claims));
            }
        }
        catch (JsonException)
        {
            // Keep whatever parsed before the failure.
        }
        return result;
    }

    /// <summary>
    /// The model writes source; the archetype supplies the build shell (the
    /// two project files, the provenance attributes, the test placeholder the
    /// test-pack generator anchors on). Archetype files are carried unless
    /// the model produced the same path or the file is the shape exemplar.
    /// </summary>
    public static List<PackageFile> MergeWithArchetype(
        IReadOnlyList<PackageFile> generated, ArchetypeRegistry.LoadedArchetype archetype)
    {
        var result = new List<PackageFile>(generated);
        var have = new HashSet<string>(generated.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var f in archetype.Files)
        {
            var path = f.Path.Replace('\\', '/');
            if (IsExemplar(path) || have.Contains(path)) continue;
            result.Add(new PackageFile(path, f.Language, f.Content, Array.Empty<string>()));
            have.Add(path);
        }
        return result;
    }

    private static string ReadString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
}
