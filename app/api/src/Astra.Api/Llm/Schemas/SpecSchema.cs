using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Llm.Schemas;

/// <summary>
/// Externalised per-language spec schema (Phase #3a).
///
/// Each source-language migration ships a JSON file under
/// <c>Llm/Schemas/&lt;language&gt;.schema.json</c> that declares:
///
///   - the claim taxonomy (kinds, id-prefixes, field shapes)
///   - which top-level fields live on the spec JSON
///   - compatible target stacks
///   - prompt hints the extraction layer uses to bias the LLM
///
/// The shape is intentionally generous about optional fields so a
/// customer can ship their own variant without lifting the C# layer.
/// </summary>
public sealed class SpecSchema
{
    [JsonPropertyName("$schema")] public string? SchemaUrl { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";

    public List<string> SupportedSourceExtensions { get; set; } = new();
    public List<string> CompatibleTargetStacks { get; set; } = new();

    public List<ClaimKindDefinition> ClaimKinds { get; set; } = new();
    public List<TopLevelFieldDefinition> TopLevelFields { get; set; } = new();

    public CitationShapeDefinition? CitationShape { get; set; }
    public PromptHintsDefinition? PromptHints { get; set; }

    public string? Owner { get; set; }
    public List<string>? CalibratedAgainst { get; set; }
    public string? Status { get; set; }
    public string? PlatformReadiness { get; set; }

    /// <summary>
    /// Phase 16.0. True when source and target are the same language (a
    /// modernization, not a cross-language migration) and the claim
    /// taxonomy already expresses "at this location, change X to Y"
    /// upgrade actions rather than idiom-translation gaps. Scaffold
    /// generation for these schemas anchors on the routine's OWN source
    /// file instead of an unrelated archetype — see
    /// <c>AnthropicScaffoldProvider.GenerateInPlaceAsync</c>.
    /// </summary>
    public bool InPlaceModernization { get; set; }
}

public sealed class ClaimKindDefinition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string IdPrefix { get; set; } = "";
    /// <summary>The persisted JSON field this kind lives under (e.g. "invariants").</summary>
    public string SpecJsonField { get; set; } = "";
    /// <summary>Which field on the individual claim object carries the human-readable text.</summary>
    public string TextField { get; set; } = "";
    public string? DisplayTone { get; set; }
    public string? Description { get; set; }
    public List<FieldDefinition> Fields { get; set; } = new();
}

public sealed class TopLevelFieldDefinition
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Required { get; set; }
}

public sealed class FieldDefinition
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Required { get; set; }
}

public sealed class CitationShapeDefinition
{
    public List<string> Fields { get; set; } = new();
    public Dictionary<string, List<string>>? Aliases { get; set; }
}

public sealed class PromptHintsDefinition
{
    public List<string>? PreferredClaimTypes { get; set; }
    public Dictionary<string, int>? MinimumClaimsPerKind { get; set; }
    public string? Emphasis { get; set; }
}

/// <summary>
/// Loads schemas from <c>Llm/Schemas/*.json</c> at startup. Singleton —
/// schemas don't change at runtime. Lookups are by id (string match).
/// </summary>
public sealed class SpecSchemaProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, SpecSchema> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SpecSchemaProvider> _log;

    public SpecSchemaProvider(IHostEnvironment env, IConfiguration cfg, ILogger<SpecSchemaProvider> log)
    {
        _log = log;
        var dir = cfg["Llm:SchemaDir"] ?? Path.Combine(AppContext.BaseDirectory, "Llm", "Schemas");
        if (!Directory.Exists(dir))
        {
            log.LogWarning("No spec-schema directory at {Dir}", dir);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*.schema.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var schema = JsonSerializer.Deserialize<SpecSchema>(text, JsonOpts)
                    ?? throw new InvalidOperationException("Empty schema deserialisation result.");
                if (string.IsNullOrWhiteSpace(schema.Id))
                {
                    log.LogWarning("Schema at {File} has no 'id' field; skipping", file);
                    continue;
                }
                _byId[schema.Id] = schema;
                log.LogInformation(
                    "Loaded spec schema {Id} ({Kinds} claim kinds) from {File}",
                    schema.Id, schema.ClaimKinds.Count, Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to load spec schema at {File}", file);
            }
        }
    }

    /// <summary>List every loaded schema, ordered by id.</summary>
    public IReadOnlyList<SpecSchema> All() =>
        _byId.Values.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Lookup by id; returns null if absent.</summary>
    public SpecSchema? GetById(string id) =>
        _byId.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Default schema for callers that don't carry an explicit id —
    /// today every demo runs against Fortran. Falls back to the first
    /// loaded schema by id-sort if Fortran isn't present.
    /// </summary>
    public SpecSchema Default()
    {
        if (_byId.TryGetValue("fortran-f77", out var f)) return f;
        return All().FirstOrDefault()
            ?? throw new InvalidOperationException("No spec schemas loaded.");
    }
}
