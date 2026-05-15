using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Api.Llm.Archetypes;

/// <summary>
/// Externalised scaffold archetype registry (Phase #3c).
///
/// Each archetype lives at
/// <c>Llm/Archetypes/&lt;targetStack&gt;/&lt;archetypeId&gt;/</c>, with:
///   - <c>archetype.json</c>: metadata + ordered file list + claim refs
///   - the actual template files at their final scaffold paths
///
/// At boot we walk that tree and index by <c>(targetStack, id)</c>.
/// Adding a new target stack or archetype variant is a directory drop —
/// no code change. This is the layer the <see cref="MockScaffoldProvider"/>
/// reads from at scaffold-generation time, replacing the old
/// <c>CanonicalScaffold</c> static class.
/// </summary>
public sealed class ArchetypeRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ConcurrentDictionary<(string Target, string Id), LoadedArchetype> _byKey = new();
    private readonly ILogger<ArchetypeRegistry> _log;

    public ArchetypeRegistry(IHostEnvironment env, IConfiguration cfg, ILogger<ArchetypeRegistry> log)
    {
        _log = log;
        var dir = cfg["Llm:ArchetypeDir"] ?? Path.Combine(AppContext.BaseDirectory, "Llm", "Archetypes");
        if (!Directory.Exists(dir))
        {
            log.LogWarning("No archetype directory at {Dir}; ArchetypeRegistry will be empty", dir);
            return;
        }
        foreach (var targetDir in Directory.EnumerateDirectories(dir))
        {
            var targetStack = Path.GetFileName(targetDir);
            foreach (var archetypeDir in Directory.EnumerateDirectories(targetDir))
            {
                var manifestPath = Path.Combine(archetypeDir, "archetype.json");
                if (!File.Exists(manifestPath))
                {
                    log.LogWarning("Skipping {Dir} — no archetype.json", archetypeDir);
                    continue;
                }
                try
                {
                    var loaded = Load(manifestPath, archetypeDir, targetStack);
                    _byKey[(targetStack, loaded.Manifest.Id)] = loaded;
                    log.LogInformation(
                        "Loaded archetype {Target}/{Id} ({Files} files) from {Dir}",
                        targetStack, loaded.Manifest.Id, loaded.Files.Count, archetypeDir);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to load archetype at {Dir}", archetypeDir);
                }
            }
        }
        if (_byKey.IsEmpty) log.LogWarning("ArchetypeRegistry loaded zero archetypes from {Dir}", dir);
    }

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    public IReadOnlyList<LoadedArchetype> All() => _byKey.Values
        .OrderBy(a => a.Manifest.TargetStack)
        .ThenBy(a => a.Manifest.Id)
        .ToArray();

    public LoadedArchetype? Get(string targetStack, string id) =>
        _byKey.TryGetValue((targetStack, id), out var a) ? a : null;

    /// <summary>
    /// Pick an archetype for the given (targetStack, subroutineName).
    /// Prefers the most-specific match in <c>archetype.json.matches.anyOf</c>;
    /// falls back to the first archetype registered for the target stack
    /// (today every target stack has exactly one archetype, so the fallback
    /// is always the right answer — when we ship multiple archetypes per
    /// stack the matcher tightens).
    /// </summary>
    public LoadedArchetype? PickForSubroutine(string targetStack, string subroutineName)
    {
        var byTarget = All().Where(a => string.Equals(a.Manifest.TargetStack, targetStack, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Exact subroutine match wins.
        var match = byTarget.FirstOrDefault(a =>
            a.Manifest.Matches?.AnyOf?.Any(m =>
                string.Equals(m.SubroutineName, subroutineName, StringComparison.OrdinalIgnoreCase)) == true);
        return match ?? byTarget.FirstOrDefault();
    }

    // ────────────────────────────────────────────────────────────────────
    // Loader
    // ────────────────────────────────────────────────────────────────────

    private static LoadedArchetype Load(string manifestPath, string archetypeDir, string targetStack)
    {
        var manifestText = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<ArchetypeManifest>(manifestText, JsonOpts)
            ?? throw new InvalidOperationException("Empty archetype manifest.");
        if (string.IsNullOrWhiteSpace(manifest.Id))
            throw new InvalidOperationException($"Archetype at {archetypeDir} missing 'id'.");

        // Cross-check: filesystem layout's target dir must equal the manifest's
        // targetStack (catch a copy-paste error before it ships).
        if (!string.IsNullOrWhiteSpace(manifest.TargetStack)
            && !string.Equals(manifest.TargetStack, targetStack, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Archetype {archetypeDir}: manifest.targetStack='{manifest.TargetStack}' but dir says '{targetStack}'.");
        }
        manifest.TargetStack = targetStack;

        var files = new List<LoadedFile>(manifest.Files.Count);
        foreach (var spec in manifest.Files)
        {
            var abs = Path.Combine(archetypeDir, spec.Path);
            if (!File.Exists(abs))
                throw new FileNotFoundException(
                    $"Archetype {manifest.Id}: declared file {spec.Path} missing on disk.", abs);
            var content = File.ReadAllText(abs);
            files.Add(new LoadedFile
            {
                Path = spec.Path.Replace('\\', '/'),
                Language = spec.Language,
                Content = content,
                DerivedFromClaimIds = spec.DerivedFromClaimIds ?? Array.Empty<string>(),
            });
        }
        return new LoadedArchetype { Manifest = manifest, ArchetypeDir = archetypeDir, Files = files };
    }

    // ────────────────────────────────────────────────────────────────────
    // Models
    // ────────────────────────────────────────────────────────────────────

    public sealed class ArchetypeManifest
    {
        [JsonPropertyName("$schema")] public string? SchemaUrl { get; set; }
        public string Id { get; set; } = "";
        public string TargetStack { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> CompatibleSchemas { get; set; } = new();
        public MatchRule? Matches { get; set; }
        public string? Owner { get; set; }
        public string? Status { get; set; }
        public string? PlatformReadiness { get; set; }
        public List<FileSpec> Files { get; set; } = new();
    }

    public sealed class MatchRule
    {
        public List<MatchClause>? AnyOf { get; set; }
        public string? Fallback { get; set; }
    }

    public sealed class MatchClause
    {
        public string? SubroutineName { get; set; }
    }

    public sealed class FileSpec
    {
        public string Path { get; set; } = "";
        public string Language { get; set; } = "";
        public string[]? DerivedFromClaimIds { get; set; }
    }

    public sealed class LoadedFile
    {
        public string Path { get; init; } = "";
        public string Language { get; init; } = "";
        public string Content { get; init; } = "";
        public string[] DerivedFromClaimIds { get; init; } = Array.Empty<string>();

        public int LineCount => Content.Count(c => c == '\n') + 1;
        public int TodoCount
        {
            get
            {
                int n = 0, idx = 0;
                while ((idx = Content.IndexOf("TODO", idx, StringComparison.Ordinal)) >= 0) { n++; idx += 4; }
                return n;
            }
        }
    }

    public sealed class LoadedArchetype
    {
        public ArchetypeManifest Manifest { get; init; } = new();
        public string ArchetypeDir { get; init; } = "";
        public List<LoadedFile> Files { get; init; } = new();
    }
}
