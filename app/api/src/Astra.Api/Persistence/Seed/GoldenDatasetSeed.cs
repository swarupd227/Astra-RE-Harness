using System.Text.Json;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Phase 6.0 — Idempotent seeder for the golden dataset.
///
/// Reads <c>Llm/GoldenDataset/&lt;schemaId&gt;/&lt;entryId&gt;.json</c> files
/// (git-versioned, reproducible) and INSERTs any rows whose EntryId is
/// not already in the database. Existing rows are NOT touched — Admin
/// edits made through the UI win over the JSON source, which is the
/// behaviour the "Admin-configurable" requirement asks for.
///
/// The JSON format mirrors the GoldenDatasetEntry shape (camelCase):
/// {
///   "entryId":       "cobol-rounded-off-by-one",
///   "schemaId":      "cobol",
///   "title":         "...",
///   "trapCategory":  "numeric/rounded",
///   "difficulty":    "medium",
///   "sourcePath":    "cobol/snippets/avg_off_by_one.cbl",
///   "sourceLines":   "1-22",
///   "sourceContent": "<inlined source>",
///   "expectedClaims": [
///       { "kind": "invariant",  "id": "INV-RND-1", "pattern": "...regex..." },
///       { "kind": "edge_case",  "id": "EC-RND-1",  "pattern": "...regex..." }
///   ],
///   "canonicalInputs": [ ... entry-specific ... ],
///   "notes": "..."
/// }
/// </summary>
public sealed class GoldenDatasetSeed
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GoldenDatasetSeed> _log;

    public GoldenDatasetSeed(AppDbContext db, IConfiguration cfg, ILogger<GoldenDatasetSeed> log)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
    }

    public string BaseDir =>
        _cfg["GoldenDataset:Dir"]
        ?? Path.Combine(AppContext.BaseDirectory, "Llm", "GoldenDataset");

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var dir = BaseDir;
        if (!Directory.Exists(dir))
        {
            _log.LogInformation(
                "No golden-dataset directory at {Dir}; skipping seed", dir);
            return;
        }

        // Pull all existing EntryIds in one round-trip so the loop below
        // can decide "skip or insert" without hitting the DB per file.
        var existing = (await _db.GoldenDatasetEntries
            .Select(e => e.EntryId)
            .ToListAsync(ct)).ToHashSet();

        int inserted = 0, skipped = 0, errors = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var schemaDir in Directory.EnumerateDirectories(dir))
        {
            var schemaId = Path.GetFileName(schemaDir);
            foreach (var file in Directory.EnumerateFiles(schemaDir, "*.json"))
            {
                try
                {
                    var raw = await File.ReadAllTextAsync(file, ct);
                    var manifest = JsonSerializer.Deserialize<EntryManifest>(raw, JsonOpts)
                        ?? throw new InvalidOperationException($"Empty manifest in {file}");

                    if (string.IsNullOrWhiteSpace(manifest.EntryId))
                        throw new InvalidOperationException($"Missing entryId in {file}");

                    // The directory name is the canonical schemaId — the
                    // manifest's schemaId is required to match (defends
                    // against accidental moves).
                    if (!string.Equals(schemaId, manifest.SchemaId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            $"Manifest schemaId='{manifest.SchemaId}' does not match directory '{schemaId}' in {file}");

                    if (existing.Contains(manifest.EntryId))
                    {
                        skipped++;
                        continue;
                    }

                    var entry = new GoldenDatasetEntry
                    {
                        Id = Guid.NewGuid(),
                        EntryId = manifest.EntryId,
                        SchemaId = manifest.SchemaId,
                        Title = manifest.Title ?? "",
                        TrapCategory = manifest.TrapCategory ?? "uncategorised",
                        Difficulty = manifest.Difficulty ?? "medium",
                        SourcePath = manifest.SourcePath ?? "",
                        SourceContent = manifest.SourceContent ?? "",
                        SourceLines = manifest.SourceLines ?? "",
                        ExpectedClaimsJson = JsonSerializer.Serialize(
                            manifest.ExpectedClaims ?? Array.Empty<ExpectedClaimManifest>(), JsonOpts),
                        CanonicalInputsJson = manifest.CanonicalInputs is null
                            ? "[]"
                            : JsonSerializer.Serialize(manifest.CanonicalInputs, JsonOpts),
                        Notes = manifest.Notes ?? "",
                        Status = "seeded",
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    _db.GoldenDatasetEntries.Add(entry);
                    inserted++;
                }
                catch (Exception ex)
                {
                    errors++;
                    _log.LogError(ex, "Failed to load golden-dataset entry from {File}", file);
                }
            }
        }

        if (inserted > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "Golden dataset seed complete: {Inserted} new, {Skipped} already present, {Errors} errors",
            inserted, skipped, errors);
    }

    /// <summary>JSON manifest shape on disk. Mirrors the entity for the
    /// fields admins author by hand; runtime/derived fields (id, status,
    /// timestamps) are populated by the seeder.</summary>
    private sealed class EntryManifest
    {
        public string EntryId { get; set; } = "";
        public string SchemaId { get; set; } = "";
        public string? Title { get; set; }
        public string? TrapCategory { get; set; }
        public string? Difficulty { get; set; }
        public string? SourcePath { get; set; }
        public string? SourceLines { get; set; }
        public string? SourceContent { get; set; }
        public ExpectedClaimManifest[]? ExpectedClaims { get; set; }
        public object[]? CanonicalInputs { get; set; }
        public string? Notes { get; set; }
    }

    private sealed class ExpectedClaimManifest
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Pattern { get; set; } = "";
    }
}
