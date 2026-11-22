using System.Diagnostics;
using System.Text.Json;
using Astra.Api.Docs;
using Astra.Api.Evidence;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Knowledge;

public sealed record ClaimCounts(int Claims, int Supported, int Unverified, int Conflict, int Dropped, int Clusters, int Candidates, int Routine);

/// <param name="Json">The stored document (<c>claims/v1</c>).</param>
/// <param name="Calls">Every model call made, so the caller can record and price them like any other.</param>
public sealed record ClaimRun(string Json, ClaimCounts Counts, IReadOnlyList<DocCallRecord> Calls, int ElapsedMs, IReadOnlyList<string> Notes);

/// <summary>
/// Reads the narrative evidence of a source version (what people said and wrote) into claims, checks each against
/// the entity graph, groups tickets into clusters, and keeps the result for the version. Only the reading of narrative
/// text is a model call; matching, verdicts and clusters are rules. Runs are measured (calls and time) and nothing
/// here waits on a request: the capture run (K9) calls this from a background run.
/// </summary>
public sealed class ClaimService
{
    public const string Schema = "claims/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly EvidenceService _evidence;
    private readonly GraphService _graph;
    private readonly ClaimExtractor _extractor;
    private readonly KnowledgeOptions _options;

    public ClaimService(AppDbContext db, EvidenceService evidence, GraphService graph, ClaimExtractor extractor, IOptions<KnowledgeOptions> options)
    {
        _db = db;
        _evidence = evidence;
        _graph = graph;
        _extractor = extractor;
        _options = options.Value;
    }

    public async Task<ClaimRun> RunAsync(Guid versionId, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var notes = new List<string>();
        await _evidence.EnsureIndexedAsync(versionId, ct);
        await _graph.EnsureBuiltAsync(versionId, ct);

        var narrative = await LoadNarrativeAsync(versionId, ct);
        var tickets = narrative.Where(c => c.Kind == "ticket").ToList();
        var clustering = TicketClusterer.Cluster(tickets.Select(AsTicket).ToList(), _options.RoutineKeywords, _options.MinClusterSize);

        // Routine requests say nothing about the system; the rest of the tickets, every transcript turn and every note are read.
        var routine = clustering.Routine.ToHashSet(StringComparer.Ordinal);
        var toRead = narrative.Where(c => !routine.Contains(c.ShortId)).ToList();
        if (toRead.Count > _options.MaxNarrativeChunks)
        {
            notes.Add($"{toRead.Count} pieces of narrative evidence were found; the first {_options.MaxNarrativeChunks} were read.");
            toRead = toRead.Take(_options.MaxNarrativeChunks).ToList();
        }

        var extraction = await _extractor.ExtractAsync(toRead, ct);
        notes.AddRange(extraction.Notes);

        var graph = await _graph.GetGraphAsync(versionId, null, ct);
        var existing = await _db.EvidenceChunks.AsNoTracking().Where(c => c.SourceVersionId == versionId).Select(c => c.ShortId).ToListAsync(ct);
        var verdicts = ClaimLinker.Link(extraction.Claims, graph, new IdSetEvidence(existing.ToHashSet(StringComparer.Ordinal)));

        var byClaim = verdicts.ToDictionary(v => v.ClaimId, StringComparer.Ordinal);
        var counts = new ClaimCounts(
            extraction.Claims.Count,
            verdicts.Count(v => v.Status == StatementStatus.Supported),
            verdicts.Count(v => v.Status == StatementStatus.Unverified),
            verdicts.Count(v => v.Status == StatementStatus.Conflict),
            extraction.Dropped.Count,
            clustering.Clusters.Count,
            clustering.Clusters.Count(c => c.Candidate),
            clustering.Routine.Count);

        var document = new
        {
            schema = Schema,
            claims = extraction.Claims.Select(c => new
            {
                id = c.Id,
                text = c.Text,
                evidence = c.EvidenceId,
                at = c.At,
                assertions = c.Assertions,
                status = byClaim[c.Id].Status,
                reason = byClaim[c.Id].Reason,
                structuredEvidence = byClaim[c.Id].StructuredEvidenceIds,
                entities = byClaim[c.Id].Entities,
            }).ToList(),
            clusters = clustering.Clusters,
            routine = clustering.Routine,
            dropped = extraction.Dropped,
            notes,
            counts,
        };
        var json = JsonSerializer.Serialize(document, Json);

        var elapsed = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
        var row = await _db.ClaimSets.FirstOrDefaultAsync(r => r.SourceVersionId == versionId, ct);
        if (row is null) _db.ClaimSets.Add(row = new ClaimSetRow { SourceVersionId = versionId });
        row.Json = json;
        row.ModelCalls = extraction.Calls.Count;
        row.ElapsedMs = elapsed;
        row.BuiltAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return new ClaimRun(json, counts, extraction.Calls, elapsed, notes);
    }

    /// <summary>The stored claims of a version with when and how they were made, or null when none were read yet.</summary>
    public async Task<(string Json, int ModelCalls, int ElapsedMs, DateTimeOffset BuiltAt)?> GetAsync(Guid versionId, CancellationToken ct)
    {
        var row = await _db.ClaimSets.AsNoTracking().FirstOrDefaultAsync(r => r.SourceVersionId == versionId, ct);
        return row is null ? null : (row.Json, row.ModelCalls, row.ElapsedMs, row.BuiltAt);
    }

    /// <summary>Ticket clusters of a version, worked out now (no model, nothing stored).</summary>
    public async Task<TicketClustering> ClustersAsync(Guid versionId, CancellationToken ct)
    {
        await _evidence.EnsureIndexedAsync(versionId, ct);
        var tickets = (await LoadNarrativeAsync(versionId, ct)).Where(c => c.Kind == "ticket").ToList();
        return TicketClusterer.Cluster(tickets.Select(AsTicket).ToList(), _options.RoutineKeywords, _options.MinClusterSize);
    }

    /// <summary>A ticket chunk's label is <c>INC-T-0412 · EOD post fails</c>; what people call the ticket is the part before the dot.</summary>
    private static TicketText AsTicket(GateChunk c) =>
        new(c.ShortId, c.Label.Split(" · ")[0].Trim(), c.Text);

    private async Task<List<GateChunk>> LoadNarrativeAsync(Guid versionId, CancellationToken ct)
    {
        var rows = await (
            from c in _db.EvidenceChunks.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on c.SourceFileId equals f.Id
            where c.SourceVersionId == versionId && c.TrustTier == "narrative" && c.Text != null
            orderby f.RelativePath, c.Ordinal
            select new { c.ShortId, c.Kind, c.Label, c.Text, c.Locator, f.RelativePath })
            .ToListAsync(ct);
        return rows.Select(r => new GateChunk(r.ShortId, r.Kind, "narrative", r.Label, r.Text ?? "", $"{r.RelativePath} {r.Locator}")).ToList();
    }
}
