using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Evidence;

/// <summary>
/// The kinds of thing the entity graph holds and the ways they relate. A type or relation not
/// listed is still stored (a later adapter may add one); the lists say what the platform itself
/// produces and gives each a plain label for screens and prompts.
/// </summary>
public static class EntityVocabulary
{
    public const string Table = "table";
    public const string View = "view";
    public const string Procedure = "procedure";
    public const string Function = "function";
    public const string Job = "job";
    public const string Package = "package";
    public const string File = "file";
    public const string Script = "script";
    public const string ConfigKey = "config-key";

    public const string Reads = "reads";
    public const string Writes = "writes";
    public const string Calls = "calls";
    public const string Runs = "runs";
    public const string Precedes = "precedes";
    public const string Contains = "contains";
    public const string Triggers = "triggers";
    public const string Configures = "configures";
    public const string References = "references";

    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>
    {
        [Table] = "Table",
        [View] = "View",
        [Procedure] = "Stored procedure",
        [Function] = "Function",
        [Job] = "Batch job",
        [Package] = "Integration package",
        [File] = "File",
        [Script] = "Script",
        [ConfigKey] = "Configuration key",
    };

    public static readonly IReadOnlyDictionary<string, string> Relations = new Dictionary<string, string>
    {
        [Reads] = "reads",
        [Writes] = "writes",
        [Calls] = "calls",
        [Runs] = "runs",
        [Precedes] = "runs before",
        [Contains] = "contains",
        [Triggers] = "triggers",
        [Configures] = "configures",
        [References] = "references",
    };

    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";

    public static int ConfidenceRank(string? c) => c switch { High => 3, Medium => 2, Low => 1, _ => 0 };
}

/// <summary>A thing a file says exists: a table, a job, a package … <see cref="Locator"/> is the chunk of the same file that shows it.</summary>
public sealed record EntityFact(
    string Type,
    string Name,
    string? Locator = null,
    IReadOnlyDictionary<string, object?>? Attrs = null);

public sealed record EntityRefFact(string Type, string Name);

/// <summary>A relation between two things, as one file states it. Either end may be defined in another file.</summary>
public sealed record LinkFact(
    string Relation,
    EntityRefFact From,
    EntityRefFact To,
    string? Locator = null,
    string Confidence = EntityVocabulary.High,
    IReadOnlyDictionary<string, object?>? Attrs = null);

/// <summary>
/// What one file contributes to the entity graph. Adapters produce these next to their chunks and
/// each fact names the chunk that proves it; the graph is the merge of every file's facts. Facts are
/// stored per file, so replacing or removing a file changes the graph by rebuilding it, never by
/// patching it.
/// </summary>
public sealed record FactSet(IReadOnlyList<EntityFact> Entities, IReadOnlyList<LinkFact> Links)
{
    public static readonly FactSet Empty = new(Array.Empty<EntityFact>(), Array.Empty<LinkFact>());

    public bool IsEmpty => Entities.Count == 0 && Links.Count == 0;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static FactSet FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try
        {
            return JsonSerializer.Deserialize<FactSet>(json, Options) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}

/// <summary>Small helpers for building the attribute bags facts carry.</summary>
public static class Attrs
{
    /// <summary>The pairs whose value is not null, empty text or an empty list.</summary>
    public static Dictionary<string, object?> Of(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (value is null) continue;
            if (value is string s && string.IsNullOrWhiteSpace(s)) continue;
            if (value is System.Collections.ICollection { Count: 0 }) continue;
            d[key] = value;
        }
        return d;
    }
}
