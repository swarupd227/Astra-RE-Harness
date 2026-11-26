using System.Collections.Concurrent;
using System.Text.Json;
using Astra.Api.Artefacts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Evidence;

/// <summary>An entity as the API shows it. <see cref="Ref"/> (<c>job:TLR_EOD_POST</c>) is its stable handle: it names the thing, not a row.</summary>
public sealed record GraphNode(
    Guid Id,
    string Ref,
    string Type,
    string TypeLabel,
    string Name,
    bool Declared,
    JsonElement? Attrs,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Paths);

public sealed record GraphEdge(
    string Relation,
    string RelationLabel,
    string From,
    string To,
    string Confidence,
    string Origin,
    JsonElement? Attrs,
    IReadOnlyList<string> EvidenceIds);

public sealed record GraphTypeCount(string Type, string Label, int Count);

public sealed record EntityGraphDto(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges, IReadOnlyList<GraphTypeCount> Types);

public sealed record EntityPageDto(IReadOnlyList<GraphNode> Data, int Total, IReadOnlyList<GraphTypeCount> Types);

public sealed record EntityLinkDto(
    string Relation,
    string RelationLabel,
    string Direction,
    string Other,
    string OtherType,
    string OtherName,
    string Confidence,
    JsonElement? Attrs,
    IReadOnlyList<string> EvidenceIds);

public sealed record EntityDetailDto(GraphNode Entity, IReadOnlyList<EntityLinkDto> Links);

/// <summary>One route and one thing it triggers, with both sides' attributes and the link's evidence.</summary>
public sealed record RouteTrigger(
    string RouteName,
    JsonElement? RouteAttrs,
    string TargetType,
    string TargetName,
    JsonElement? LinkAttrs,
    IReadOnlyList<string> EvidenceIds);

/// <summary>
/// The entity graph of a source version: what its artefact files say exists (tables, procedures, jobs,
/// packages …) and how those things relate, each with the evidence that shows it. The graph is derived: it
/// is rebuilt from the per-file facts whenever a file is attached, replaced or removed and after a re-sync,
/// never patched, so it cannot drift from the files. Reads are open to every persona, like all evidence.
/// </summary>
public sealed class GraphService
{
    private const int InsertBatch = 1000;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AppDbContext _db;
    private readonly ILogger<GraphService> _logger;

    public GraphService(AppDbContext db, ILogger<GraphService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── building ────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces a version's graph with the merge of its files' facts. Never throws (a graph that could not
    /// be built is logged, and is built the next time it is read); a build that is already running for the
    /// version is waited for, not duplicated.
    /// </summary>
    public async Task RebuildAsync(Guid versionId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(versionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var files = await (
                from f in _db.FileFacts.AsNoTracking()
                join file in _db.SourceFiles.AsNoTracking() on f.SourceFileId equals file.Id
                where f.SourceVersionId == versionId
                select new { f.SourceFileId, file.RelativePath, f.FactsJson })
                .ToListAsync(ct);

            var parsed = files.ToDictionary(f => f.SourceFileId, f => FactSet.FromJson(f.FactsJson));

            // Only a file that states something needs its chunks (a C# estate has far more code chunks than facts).
            var stating = parsed.Where(kv => !kv.Value.IsEmpty).Select(kv => kv.Key).ToArray();
            var chunkRows = stating.Length == 0
                ? new List<(Guid SourceFileId, string Locator, string ShortId, string Kind, int? LineStart, int? LineEnd)>()
                : (await _db.EvidenceChunks.AsNoTracking()
                        .Where(c => c.SourceVersionId == versionId && stating.Contains(c.SourceFileId))
                        .Select(c => new { c.SourceFileId, c.Locator, c.ShortId, c.Kind, c.LineStart, c.LineEnd })
                        .ToListAsync(ct))
                    .Select(c => (c.SourceFileId, c.Locator, c.ShortId, c.Kind, c.LineStart, c.LineEnd))
                    .ToList();
            var chunks = chunkRows.GroupBy(c => c.SourceFileId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Locator, x => x.ShortId, StringComparer.Ordinal));
            // A fact read from code names the lines it was found on; the routine that contains them shows it.
            var spans = chunkRows
                .Where(c => c.Kind == ArtefactKinds.Code && c.LineStart is not null && c.LineEnd is not null)
                .GroupBy(c => c.SourceFileId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ChunkSpan>)g.Select(x => new ChunkSpan(x.LineStart!.Value, x.LineEnd!.Value, x.ShortId)).ToList());

            var merged = GraphMerger.Merge(files.Select(f => new FileFactSet(
                f.RelativePath,
                parsed[f.SourceFileId],
                chunks.GetValueOrDefault(f.SourceFileId) ?? new Dictionary<string, string>(),
                spans.GetValueOrDefault(f.SourceFileId))));
            merged = LinkingRules.Apply(merged, await NarrativeAsync(versionId, ct));

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.GraphLinks.Where(l => l.SourceVersionId == versionId).ExecuteDeleteAsync(ct);
            await _db.GraphEntities.Where(e => e.SourceVersionId == versionId).ExecuteDeleteAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var ids = new Dictionary<MergedEntity, Guid>();
            var entityRows = merged.Entities.Select(e =>
            {
                var id = Guid.NewGuid();
                ids[e] = id;
                return new GraphEntity
                {
                    Id = id,
                    SourceVersionId = versionId,
                    Type = e.Type,
                    Key = e.Key,
                    Name = e.Name,
                    Declared = e.Declared,
                    AttrsJson = e.Attrs.Count == 0 ? null : JsonSerializer.Serialize(e.Attrs),
                    EvidenceJson = e.EvidenceIds.Count == 0 ? null : JsonSerializer.Serialize(e.EvidenceIds),
                    PathsJson = e.Paths.Count == 0 ? null : JsonSerializer.Serialize(e.Paths),
                    CreatedAt = now,
                };
            }).ToList();
            await InsertAsync(entityRows, ct);

            var linkRows = merged.Links.Select(l => new GraphLink
            {
                Id = Guid.NewGuid(),
                SourceVersionId = versionId,
                Relation = l.Relation,
                FromEntityId = ids[l.From],
                ToEntityId = ids[l.To],
                Confidence = l.Confidence,
                Origin = l.Origin,
                AttrsJson = l.Attrs.Count == 0 ? null : JsonSerializer.Serialize(l.Attrs),
                EvidenceJson = l.EvidenceIds.Count == 0 ? null : JsonSerializer.Serialize(l.EvidenceIds),
            }).ToList();
            await InsertAsync(linkRows, ct);
            await tx.CommitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Building the entity graph for version {Version} failed; it is built when next read", versionId);
        }
        finally
        {
            gate.Release();
        }
    }

    // What people said or wrote, for the rules that find which things they name. A ticket export can be large, so
    // each piece is cut to its first characters and the number read is bounded.
    private const int NarrativeChunkLimit = 30_000;
    private const int NarrativeChunkChars = 4_000;

    private async Task<IReadOnlyList<NarrativeChunk>> NarrativeAsync(Guid versionId, CancellationToken ct)
    {
        var rows = await (
            from c in _db.EvidenceChunks.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on c.SourceFileId equals f.Id
            where c.SourceVersionId == versionId && c.Text != null
                  && (c.Kind == ArtefactKinds.Ticket || c.Kind == ArtefactKinds.Transcript || c.Kind == ArtefactKinds.Document)
            orderby f.RelativePath, c.Ordinal
            select new { c.ShortId, c.Kind, Text = c.Text!.Length > NarrativeChunkChars ? c.Text.Substring(0, NarrativeChunkChars) : c.Text })
            .Take(NarrativeChunkLimit)
            .ToListAsync(ct);
        return rows.Select(r => new NarrativeChunk(r.ShortId, r.Kind, r.Text!)).ToList();
    }

    /// <summary>Builds the graph if the version has facts but no graph yet (a re-sync that carried files forward, say).</summary>
    public async Task EnsureBuiltAsync(Guid versionId, CancellationToken ct)
    {
        try
        {
            if (!await _db.GraphEntities.AnyAsync(e => e.SourceVersionId == versionId, ct)
                && await _db.FileFacts.AnyAsync(f => f.SourceVersionId == versionId, ct))
            {
                await RebuildAsync(versionId, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking the entity graph for version {Version} failed", versionId);
        }
    }

    private async Task InsertAsync<T>(List<T> rows, CancellationToken ct) where T : class
    {
        foreach (var batch in rows.Chunk(InsertBatch))
        {
            _db.Set<T>().AddRange(batch);
            await _db.SaveChangesAsync(ct);
            foreach (var row in batch) _db.Entry(row).State = EntityState.Detached;
        }
    }

    // ── reading ─────────────────────────────────────────────────────────

    /// <summary>Everything, or only the given types, with the links among what is shown.</summary>
    public async Task<EntityGraphDto> GetGraphAsync(Guid versionId, IReadOnlyCollection<string>? types, CancellationToken ct)
    {
        var entities = await _db.GraphEntities.AsNoTracking().Where(e => e.SourceVersionId == versionId)
            .OrderBy(e => e.Type).ThenBy(e => e.Key).ToListAsync(ct);
        var facets = Facets(entities);

        var shown = types is { Count: > 0 } ? entities.Where(e => types.Contains(e.Type)).ToList() : entities;
        var refs = shown.ToDictionary(e => e.Id, e => $"{e.Type}:{e.Name}");

        var links = await _db.GraphLinks.AsNoTracking().Where(l => l.SourceVersionId == versionId)
            .OrderBy(l => l.Relation).ToListAsync(ct);
        var edges = links
            .Where(l => refs.ContainsKey(l.FromEntityId) && refs.ContainsKey(l.ToEntityId))
            .Select(l => new GraphEdge(
                l.Relation, RelationLabel(l.Relation), refs[l.FromEntityId], refs[l.ToEntityId], l.Confidence, l.Origin,
                ParseJson(l.AttrsJson), StringList(l.EvidenceJson)))
            .OrderBy(e => e.Relation, StringComparer.Ordinal).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();

        return new EntityGraphDto(shown.Select(ToNode).ToList(), edges, facets);
    }

    /// <summary>
    /// Every route the version serves with what it triggers (a class and the method named on the link, or a script
    /// and the routines its case calls) — what extraction needs to tell a handler which routes it answers.
    /// </summary>
    public async Task<IReadOnlyList<RouteTrigger>> RouteTriggersAsync(Guid versionId, CancellationToken ct)
    {
        var routes = await _db.GraphEntities.AsNoTracking()
            .Where(e => e.SourceVersionId == versionId && e.Type == EntityVocabulary.Route)
            .ToDictionaryAsync(e => e.Id, ct);
        if (routes.Count == 0) return Array.Empty<RouteTrigger>();
        var links = await _db.GraphLinks.AsNoTracking()
            .Where(l => l.SourceVersionId == versionId && l.Relation == EntityVocabulary.Triggers)
            .ToListAsync(ct);
        links = links.Where(l => routes.ContainsKey(l.FromEntityId)).ToList();
        var targetIds = links.Select(l => l.ToEntityId).ToHashSet();
        var targets = await _db.GraphEntities.AsNoTracking()
            .Where(e => targetIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);
        return links
            .Where(l => targets.ContainsKey(l.ToEntityId))
            .Select(l =>
            {
                var route = routes[l.FromEntityId];
                var target = targets[l.ToEntityId];
                return new RouteTrigger(route.Name, ParseJson(route.AttrsJson), target.Type, target.Name,
                    ParseJson(l.AttrsJson), StringList(l.EvidenceJson));
            })
            .OrderBy(t => t.RouteName, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<EntityPageDto> ListAsync(Guid versionId, string? type, string? query, int limit, int offset, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);

        var q = _db.GraphEntities.AsNoTracking().Where(e => e.SourceVersionId == versionId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var like = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(e => EF.Functions.ILike(e.Name, like, "\\") || EF.Functions.ILike(e.Type, like, "\\"));
        }

        var facets = await q.GroupBy(e => e.Type).Select(g => new { Type = g.Key, Count = g.Count() }).ToListAsync(ct);
        var filtered = string.IsNullOrWhiteSpace(type) ? q : q.Where(e => e.Type == type);
        var total = await filtered.CountAsync(ct);
        var rows = await filtered.OrderBy(e => e.Type).ThenBy(e => e.Key).Skip(offset).Take(limit).ToListAsync(ct);

        return new EntityPageDto(
            rows.Select(ToNode).ToList(),
            total,
            facets.OrderBy(f => f.Type, StringComparer.Ordinal).Select(f => new GraphTypeCount(f.Type, TypeLabel(f.Type), f.Count)).ToList());
    }

    /// <summary>One entity, with every link into and out of it.</summary>
    public async Task<EntityDetailDto?> GetAsync(Guid versionId, string type, string name, CancellationToken ct)
    {
        var key = GraphMerger.KeyOf(name);
        var entity = await _db.GraphEntities.AsNoTracking()
            .FirstOrDefaultAsync(e => e.SourceVersionId == versionId && e.Type == type && e.Key == key, ct);
        if (entity is null) return null;

        var outgoing = await _db.GraphLinks.AsNoTracking().Include(l => l.To)
            .Where(l => l.FromEntityId == entity.Id).ToListAsync(ct);
        var incoming = await _db.GraphLinks.AsNoTracking().Include(l => l.From)
            .Where(l => l.ToEntityId == entity.Id).ToListAsync(ct);

        var links = outgoing.Select(l => ToLink(l, "out", l.To!))
            .Concat(incoming.Select(l => ToLink(l, "in", l.From!)))
            .OrderBy(l => l.Relation, StringComparer.Ordinal).ThenBy(l => l.Direction, StringComparer.Ordinal).ThenBy(l => l.Other, StringComparer.Ordinal)
            .ToList();
        return new EntityDetailDto(ToNode(entity), links);
    }

    // ── shaping ─────────────────────────────────────────────────────────

    private static GraphNode ToNode(GraphEntity e) => new(
        e.Id, $"{e.Type}:{e.Name}", e.Type, TypeLabel(e.Type), e.Name, e.Declared,
        ParseJson(e.AttrsJson), StringList(e.EvidenceJson), StringList(e.PathsJson));

    private static EntityLinkDto ToLink(GraphLink l, string direction, GraphEntity other) => new(
        l.Relation, RelationLabel(l.Relation), direction, $"{other.Type}:{other.Name}", other.Type, other.Name,
        l.Confidence, ParseJson(l.AttrsJson), StringList(l.EvidenceJson));

    private static IReadOnlyList<GraphTypeCount> Facets(IEnumerable<GraphEntity> entities) =>
        entities.GroupBy(e => e.Type).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new GraphTypeCount(g.Key, TypeLabel(g.Key), g.Count())).ToList();

    private static string TypeLabel(string type) => EntityVocabulary.Types.GetValueOrDefault(type) ?? type;
    private static string RelationLabel(string relation) => EntityVocabulary.Relations.GetValueOrDefault(relation) ?? relation;

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    private static IReadOnlyList<string> StringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? new List<string>(); }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}
