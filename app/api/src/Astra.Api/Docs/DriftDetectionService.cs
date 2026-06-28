using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.f — Drift detection.
///
/// After a new SourceVersion is committed for a corpus (initial ingest or
/// re-sync), any DocSection that was SIGNED against an older SourceVersion
/// is no longer known-current and must be re-confirmed by an SME.
/// This service transitions those rows to STALE so the review UI surfaces them.
///
/// Called by IngestPipeline at the end of both IngestAsync and ReingestAsync,
/// after the corpus's LatestVersionId is persisted.
/// </summary>
public sealed class DriftDetectionService
{
    private readonly AppDbContext _db;
    private readonly IAuditLogger _audit;
    private readonly ILogger<DriftDetectionService> _logger;

    public DriftDetectionService(
        AppDbContext db,
        IAuditLogger audit,
        ILogger<DriftDetectionService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Marks as STALE every SIGNED DocSection on the given corpus whose
    /// SourceVersionId differs from <paramref name="currentVersionId"/>.
    /// Returns the count of sections transitioned (0 on first ingest).
    /// </summary>
    public async Task<int> MarkStaleAsync(
        Guid corpusId,
        Guid currentVersionId,
        DevPersonaContext actor,
        CancellationToken ct)
    {
        var stale = await _db.DocSections
            .Where(s =>
                s.CorpusId == corpusId &&
                s.SourceVersionId != currentVersionId &&
                s.State == "SIGNED")
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        foreach (var s in stale)
        {
            s.State = "STALE";
            s.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "docs.drift_staled", "corpus", corpusId, actor,
            payload: new
            {
                currentVersionId,
                staleSections = stale.Count,
                kinds = stale.Select(s => s.SectionKind).Distinct().Order().ToArray(),
            },
            ct: ct);

        _logger.LogInformation(
            "Drift detection: corpus={Corpus} version={Version} staled={N} kinds={Kinds}",
            corpusId, currentVersionId, stale.Count,
            string.Join(',', stale.Select(s => s.SectionKind).Distinct().Order()));

        return stale.Count;
    }
}
