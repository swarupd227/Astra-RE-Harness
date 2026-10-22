using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Api.Artefacts;

public sealed record WaveInfo
{
    public string? Name { get; init; }
    /// <summary>ISO date, yyyy-MM-dd.</summary>
    public string? EndDate { get; init; }
}

public sealed record ProjectMetadata
{
    /// <summary>1 (most critical) to 3.</summary>
    public int? Tier { get; init; }
    /// <summary>The person or vendor who currently holds the knowledge.</summary>
    public string? KnowledgeHolder { get; init; }
    public WaveInfo? Wave { get; init; }
}

/// <summary>
/// Per-project configuration, stored as <c>corpora.settings_json</c>. Only optional
/// artefact kinds are listed: source code is always on. Reading is lenient (a bad or
/// missing value falls back to the default); writing goes through <see cref="Validate"/>.
/// </summary>
public sealed record ProjectSettings
{
    public IReadOnlyList<string> ArtefactKinds { get; init; } = Array.Empty<string>();
    public string? Pack { get; init; }
    public string? Banner { get; init; }
    public ProjectMetadata Metadata { get; init; } = new();
    public IReadOnlyDictionary<string, int> ExpectedCounts { get; init; } = new Dictionary<string, int>();
    public bool Demo { get; init; }

    public static ProjectSettings Default { get; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex PackId = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex CountKey = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.Compiled);

    private static readonly HashSet<string> TopLevel = new(StringComparer.Ordinal)
    {
        "artefactKinds", "pack", "banner", "metadata", "expectedCounts", "demo",
    };

    public const int MaxBanner = 200;
    public const int MaxHolder = 120;
    public const int MaxWaveName = 80;
    public const int MaxCountKeys = 20;
    public const int MaxCount = 1000;

    public bool Has(string kindId) =>
        ArtefactKinds.Any(k => string.Equals(k, kindId, StringComparison.OrdinalIgnoreCase));

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Lenient read of stored settings: null, empty or malformed JSON is the default.</summary>
    public static ProjectSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            return JsonSerializer.Deserialize<ProjectSettings>(json, Json) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>
    /// Strict validation of a full settings document (what the PUT endpoint receives).
    /// Returns the normalised settings, or every problem found — never both.
    /// </summary>
    public static (ProjectSettings? Settings, IReadOnlyList<string> Errors) Validate(JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
            return (null, new[] { "settings must be a JSON object" });

        foreach (var p in root.EnumerateObject())
            if (!TopLevel.Contains(p.Name)) errors.Add($"unknown field '{p.Name}'");

        var kinds = ReadKinds(root, errors);
        var pack = ReadString(root, "pack", 64, errors);
        if (pack is not null && !PackId.IsMatch(pack))
            errors.Add("pack must be lowercase letters, digits and hyphens (max 64)");
        var banner = ReadString(root, "banner", MaxBanner, errors);
        if (banner is not null && banner.Any(char.IsControl))
            errors.Add("banner must not contain control characters");
        var metadata = ReadMetadata(root, errors);
        var counts = ReadCounts(root, errors);
        var demo = false;
        if (root.TryGetProperty("demo", out var d))
        {
            if (d.ValueKind is JsonValueKind.True or JsonValueKind.False) demo = d.GetBoolean();
            else if (d.ValueKind != JsonValueKind.Null) errors.Add("demo must be true or false");
        }

        if (errors.Count > 0) return (null, errors);
        return (new ProjectSettings
        {
            ArtefactKinds = kinds,
            Pack = pack,
            Banner = banner,
            Metadata = metadata,
            ExpectedCounts = counts,
            Demo = demo,
        }, Array.Empty<string>());
    }

    private static IReadOnlyList<string> ReadKinds(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("artefactKinds", out var el) || el.ValueKind == JsonValueKind.Null)
            return Array.Empty<string>();
        if (el.ValueKind != JsonValueKind.Array)
        {
            errors.Add("artefactKinds must be an array of kind ids");
            return Array.Empty<string>();
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add("artefactKinds must contain only strings");
                continue;
            }
            var id = item.GetString()!.Trim().ToLowerInvariant();
            var kind = Artefacts.ArtefactKinds.Find(id);
            if (kind is null) errors.Add($"unknown artefact kind '{id}'");
            else if (!kind.Core) seen.Add(kind.Id); // code is always on; not stored
        }
        return seen.OrderBy(Artefacts.ArtefactKinds.OrderOf).ToArray();
    }

    private static string? ReadString(JsonElement parent, string name, int max, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{name} must be a string");
            return null;
        }
        var s = el.GetString()!.Trim();
        if (s.Length == 0) return null;
        if (s.Length > max)
        {
            errors.Add($"{name} must be at most {max} characters");
            return null;
        }
        return s;
    }

    private static ProjectMetadata ReadMetadata(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("metadata", out var m) || m.ValueKind == JsonValueKind.Null)
            return new ProjectMetadata();
        if (m.ValueKind != JsonValueKind.Object)
        {
            errors.Add("metadata must be an object");
            return new ProjectMetadata();
        }
        foreach (var p in m.EnumerateObject())
            if (p.Name is not ("tier" or "knowledgeHolder" or "wave"))
                errors.Add($"unknown field 'metadata.{p.Name}'");

        int? tier = null;
        if (m.TryGetProperty("tier", out var t) && t.ValueKind != JsonValueKind.Null)
        {
            if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) && n is >= 1 and <= 3) tier = n;
            else errors.Add("metadata.tier must be 1, 2 or 3");
        }

        var holder = ReadString(m, "knowledgeHolder", MaxHolder, errors);

        WaveInfo? wave = null;
        if (m.TryGetProperty("wave", out var w) && w.ValueKind != JsonValueKind.Null)
        {
            if (w.ValueKind != JsonValueKind.Object) errors.Add("metadata.wave must be an object");
            else
            {
                foreach (var p in w.EnumerateObject())
                    if (p.Name is not ("name" or "endDate"))
                        errors.Add($"unknown field 'metadata.wave.{p.Name}'");
                var name = ReadString(w, "name", MaxWaveName, errors);
                var end = ReadString(w, "endDate", 10, errors);
                if (end is not null && !DateOnly.TryParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    errors.Add("metadata.wave.endDate must be a date as yyyy-MM-dd");
                if (name is not null || end is not null) wave = new WaveInfo { Name = name, EndDate = end };
            }
        }
        return new ProjectMetadata { Tier = tier, KnowledgeHolder = holder, Wave = wave };
    }

    private static IReadOnlyDictionary<string, int> ReadCounts(JsonElement root, List<string> errors)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("expectedCounts", out var el) || el.ValueKind == JsonValueKind.Null) return result;
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add("expectedCounts must be an object of name to count");
            return result;
        }
        foreach (var p in el.EnumerateObject())
        {
            if (!CountKey.IsMatch(p.Name)) errors.Add($"expectedCounts key '{p.Name}' must be lowercase letters, digits and hyphens");
            else if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var n) || n < 0 || n > MaxCount)
                errors.Add($"expectedCounts['{p.Name}'] must be a whole number from 0 to {MaxCount}");
            else result[p.Name] = n;
        }
        if (result.Count > MaxCountKeys) errors.Add($"expectedCounts allows at most {MaxCountKeys} entries");
        return result;
    }
}
