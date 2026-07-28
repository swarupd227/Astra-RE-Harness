using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

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
    // Phase 14.0 — live registration (no code change or restart)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Registers or replaces an archetype in the live in-memory
    /// index immediately — used when an engineer approves a Phase 14.0
    /// <c>ArchetypeProposal</c>. No restart required.</summary>
    public LoadedArchetype RegisterLive(LoadedArchetype archetype)
    {
        _byKey[(archetype.Manifest.TargetStack, archetype.Manifest.Id)] = archetype;
        _log.LogInformation(
            "Registered live archetype {Target}/{Id} ({Files} files) from {Dir}",
            archetype.Manifest.TargetStack, archetype.Manifest.Id, archetype.Files.Count, archetype.ArchetypeDir);
        return archetype;
    }

    /// <summary>Called once at boot (after the filesystem walk in the
    /// constructor) to reload every PRODUCTION <c>ArchetypeProposal</c> from
    /// the database — so archetypes approved in a prior run survive a
    /// restart without ever having been written to disk.</summary>
    public async Task LoadFromDatabaseAsync(Persistence.AppDbContext db, CancellationToken ct)
    {
        var rows = await db.ArchetypeProposals.AsNoTracking()
            .Where(p => p.State == "PRODUCTION")
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                RegisterLive(Astra.Api.Llm.PatternAnalysis.ArchetypeAuthoringService.BuildLoadedArchetype(row));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to load archetype proposal {Id} from database", row.Id);
            }
        }
        if (rows.Count > 0)
            _log.LogInformation("Loaded {Count} live-authored archetype(s) from the database", rows.Count);
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
    /// Pick an archetype for the given (targetStack, subroutineName), scoped
    /// to archetypes compatible with <paramref name="sourceSchema"/>.
    ///
    /// Phase 15.0.a — a target stack like java-spring is shared across many
    /// source languages (unibasic, cobol, delphi, cpp, java, ...), each with
    /// its own archetype(s). Without the schema filter, a java-sourced
    /// routine whose name didn't match java-modernization's anyOf list would
    /// fall through to whichever java-spring archetype happened to be first
    /// (alphabetically canonical-abl-order-service — an OpenEdge archetype
    /// with nothing to do with Java). Filtering to compatibleSchemas FIRST,
    /// then name-matching/falling-back only within that filtered set, is
    /// what keeps the fallback meaningful instead of a coin-flip across
    /// unrelated languages.
    ///
    /// An archetype with an empty/missing <c>compatibleSchemas</c> is treated
    /// as compatible with every schema (legacy archetypes predating this
    /// field default to the old "any schema" behaviour rather than becoming
    /// silently unreachable).
    /// </summary>
    public LoadedArchetype? PickForSubroutine(string targetStack, string subroutineName, string? sourceSchema = null)
    {
        var byTarget = All().Where(a => string.Equals(a.Manifest.TargetStack, targetStack, StringComparison.OrdinalIgnoreCase)).ToArray();
        var candidates = string.IsNullOrWhiteSpace(sourceSchema)
            ? byTarget
            : byTarget.Where(a => IsCompatibleWithSchema(a, sourceSchema)).ToArray();
        // Exact subroutine match wins.
        var match = candidates.FirstOrDefault(a =>
            a.Manifest.Matches?.AnyOf?.Any(m =>
                string.Equals(m.SubroutineName, subroutineName, StringComparison.OrdinalIgnoreCase)) == true);
        return match ?? candidates.FirstOrDefault();
    }

    private static bool IsCompatibleWithSchema(LoadedArchetype archetype, string sourceSchema) =>
        archetype.Manifest.CompatibleSchemas.Count == 0
        || archetype.Manifest.CompatibleSchemas.Any(s => string.Equals(s, sourceSchema, StringComparison.OrdinalIgnoreCase));

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
