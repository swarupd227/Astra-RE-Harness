using System.Text.Json;
using Astra.Api.Artefacts;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Evidence;

public sealed record InventoryFileKind(string Kind, string Label, int Files, int Chunks);

public sealed record InventoryLanguage(string Language, int Files, int Routines);

public sealed record InventoryCode(int Files, int Routines, IReadOnlyList<InventoryLanguage> Languages);

public sealed record InventoryThing(string Type, string Label, int Declared, int ReferencedOnly);

/// <param name="Candidates">The interface candidates the <c>interfaces</c> measure counts.</param>
public sealed record InventoryDto(
    Guid VersionId,
    IReadOnlyList<InventoryFileKind> Files,
    InventoryCode Code,
    IReadOnlyList<InventoryThing> Things,
    IReadOnlyList<InventoryMeasure> Measures,
    IReadOnlyList<InventoryFindingItem> Candidates,
    IReadOnlyList<InventoryFinding> Findings);

/// <summary>
/// What a project's evidence holds, in numbers a person can check: files and chunks by kind, routines and
/// languages in the code, things in the entity graph (declared, and only referenced), the counts the
/// project's settings expect next to what was found, and the gaps that follow from how the pieces join up.
/// Reads are open to every persona, like all evidence.
/// </summary>
public sealed class InventoryService
{
    private readonly AppDbContext _db;

    public InventoryService(AppDbContext db) { _db = db; }

    public async Task<InventoryDto> GetAsync(Guid corpusId, Guid versionId, CancellationToken ct)
    {
        var fileCounts = await _db.SourceFiles.AsNoTracking()
            .Where(f => f.SourceVersionId == versionId)
            .GroupBy(f => f.Kind).Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var chunkCounts = (await _db.EvidenceChunks.AsNoTracking()
                .Where(c => c.SourceVersionId == versionId)
                .GroupBy(c => c.Kind).Select(g => new { Kind = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Kind, x => x.Count);

        var files = fileCounts
            .OrderBy(f => ArtefactKinds.OrderOf(f.Kind))
            .Select(f => new InventoryFileKind(f.Kind, ArtefactKinds.Find(f.Kind)?.Label ?? f.Kind, f.Count, chunkCounts.GetValueOrDefault(f.Kind)))
            .ToList();

        var perLanguage = await (
            from s in _db.Subroutines.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            where f.SourceVersionId == versionId && f.Kind == ArtefactKinds.Code
            group s by s.SourceLanguage into g
            select new { Language = g.Key, Routines = g.Count(), Files = g.Select(x => x.SourceFileId).Distinct().Count() })
            .ToListAsync(ct);
        var languages = perLanguage.OrderBy(l => l.Language, StringComparer.Ordinal)
            .Select(l => new InventoryLanguage(l.Language, l.Files, l.Routines)).ToList();
        var routines = languages.Sum(l => l.Routines);
        var code = new InventoryCode(fileCounts.FirstOrDefault(f => f.Kind == ArtefactKinds.Code)?.Count ?? 0, routines, languages);

        var entityRows = await _db.GraphEntities.AsNoTracking().Where(e => e.SourceVersionId == versionId).ToListAsync(ct);
        var refs = entityRows.ToDictionary(e => e.Id, e => $"{e.Type}:{e.Name}");
        var entities = entityRows
            .Select(e => new InventoryEntity(refs[e.Id], e.Type, e.Name, e.Declared, StringList(e.EvidenceJson)))
            .ToList();
        var links = (await _db.GraphLinks.AsNoTracking().Where(l => l.SourceVersionId == versionId)
                .Select(l => new { l.Relation, l.FromEntityId, l.ToEntityId }).ToListAsync(ct))
            .Select(l => new InventoryLink(l.Relation, refs[l.FromEntityId], refs[l.ToEntityId]))
            .ToList();

        var things = entities.GroupBy(e => e.Type).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new InventoryThing(
                g.Key, EntityVocabulary.Types.GetValueOrDefault(g.Key) ?? g.Key, g.Count(e => e.Declared), g.Count(e => !e.Declared)))
            .ToList();

        var settingsJson = await _db.Corpora.AsNoTracking().Where(c => c.Id == corpusId).Select(c => c.SettingsJson).FirstOrDefaultAsync(ct);
        var expected = ProjectSettings.Parse(settingsJson).ExpectedCounts;

        return new InventoryDto(
            versionId,
            files,
            code,
            things,
            InventoryRules.Measures(entities, links, routines, expected),
            InventoryRules.InterfaceCandidates(entities, links).Select(e => new InventoryFindingItem(e.Ref, e.Name, e.EvidenceIds)).ToList(),
            InventoryRules.Findings(entities, links));
    }

    private static IReadOnlyList<string> StringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}
