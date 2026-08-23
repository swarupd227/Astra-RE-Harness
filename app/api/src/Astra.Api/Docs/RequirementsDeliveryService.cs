using System.Text.Json;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// Turns the requirements pack from a document into a tracker.
///
/// Each functional requirement already names the routines it derives
/// from, and the rest of the pipeline already knows what happened to
/// those routines — whether a spec exists, whether it was signed,
/// whether code was scaffolded, whether validation passed, and which
/// migration wave they sit in. Nothing joined those facts, so the
/// pack could not answer the question a delivery manager actually
/// asks: "is FR-014 built yet, and did it pass?"
///
/// This is that join. It adds no new extraction and calls no model —
/// every fact here is already stored.
/// </summary>
public sealed class RequirementsDeliveryService
{
    private readonly AppDbContext _db;

    public RequirementsDeliveryService(AppDbContext db) => _db = db;

    /// <summary>Lifecycle of one routine a requirement derives from.</summary>
    public sealed record RoutineStatus(
        string Name,
        Guid? SubroutineId,
        /// <summary>unresolved | parsed | specified | signed | built | verified | failed</summary>
        string Status,
        int? Wave);

    public sealed record RequirementDelivery(
        string Reference,
        string Statement,
        string Capability,
        string Priority,
        string Status,
        IReadOnlyList<int> Waves,
        int RoutinesNamed,
        int RoutinesResolved,
        IReadOnlyList<RoutineStatus> Routines);

    public sealed record DeliveryReport(
        Guid CorpusId,
        string CorpusName,
        int RequirementCount,
        IReadOnlyDictionary<string, int> StatusCounts,
        int RequirementsWithoutTraceableRoutine,
        int RoutineNamesUnresolved,
        IReadOnlyList<RequirementDelivery> Requirements);

    // Weakest-link ordering: a requirement is only as delivered as its
    // least-delivered routine, so "built" means every routine is built.
    private static readonly string[] Ladder =
        { "unresolved", "parsed", "specified", "signed", "built", "failed", "verified" };

    private static int Rank(string s) => Array.IndexOf(Ladder, s) is var i && i >= 0 ? i : 0;

    public async Task<DeliveryReport?> BuildAsync(Guid corpusId, CancellationToken ct)
    {
        var corpus = await _db.Corpora.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == corpusId, ct);
        if (corpus is null) return null;

        var reqSections = await _db.DocSections.AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SectionKind == "functional-requirement"
                     && s.State != "REJECTED")
            .OrderBy(s => s.CreatedAt).ThenBy(s => s.Id)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);
        if (reqSections.Count == 0)
            return new DeliveryReport(corpusId, corpus.Name, 0,
                new Dictionary<string, int>(), 0, 0, Array.Empty<RequirementDelivery>());

        // ── Routine lifecycle, one pass over the corpus ──
        var versionId = corpus.LatestVersionId;
        var subs = await _db.Subroutines.AsNoTracking()
            .Where(s => s.SourceFile!.SourceVersionId == versionId)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);

        var subIds = subs.Select(s => s.Id).ToList();
        var specs = await _db.Specs.AsNoTracking()
            .Where(sp => subIds.Contains(sp.SubroutineId))
            .OrderByDescending(sp => sp.UpdatedAt)
            .Select(sp => new { sp.Id, sp.SubroutineId, sp.State })
            .ToListAsync(ct);
        var latestSpec = specs.GroupBy(sp => sp.SubroutineId)
            .ToDictionary(g => g.Key, g => g.First());

        var specIds = specs.Select(sp => sp.Id).ToList();
        var scaffolds = await _db.Scaffolds.AsNoTracking()
            .Where(sc => specIds.Contains(sc.SpecId))
            .OrderByDescending(sc => sc.GeneratedAt)
            .Select(sc => new { sc.Id, sc.SpecId, sc.State })
            .ToListAsync(ct);
        var latestScaffold = scaffolds.GroupBy(sc => sc.SpecId)
            .ToDictionary(g => g.Key, g => g.First());

        var scaffoldIds = scaffolds.Select(sc => sc.Id).ToList();
        var runs = await _db.ValidationRuns.AsNoTracking()
            .Where(r => scaffoldIds.Contains(r.ScaffoldId))
            .Select(r => new { r.ScaffoldId, r.Status })
            .ToListAsync(ct);
        var runsByScaffold = runs.GroupBy(r => r.ScaffoldId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Status).ToList());

        // ── Wave membership from the current plan ──
        var plan = await _db.MigrationPlans.AsNoTracking()
            .Where(p => p.CorpusId == corpusId && p.Status != "archived")
            .OrderByDescending(p => p.Status == "approved").ThenByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var waveByRoutine = new Dictionary<Guid, int>();
        if (plan is not null)
        {
            var waves = await _db.MigrationWaves.AsNoTracking()
                .Where(w => w.MigrationPlanId == plan.Id)
                .Select(w => new { w.WaveNumber, w.PlannedRoutineIdsJson })
                .ToListAsync(ct);
            foreach (var w in waves)
                foreach (var rid in ParseGuids(w.PlannedRoutineIdsJson))
                    waveByRoutine[rid] = w.WaveNumber;
        }

        // ── Name → routine. Requirements cite routines by name, and the
        // C# parser stores them qualified ("ProductDao.findProgramById")
        // while a requirement may cite either form. Same two-way match as
        // the dependency graph, and ambiguous bare names are left
        // unresolved rather than guessed.
        var byFull = subs.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byBare = subs.GroupBy(s => Bare(s.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        string StatusOf(Guid subId)
        {
            if (!latestSpec.TryGetValue(subId, out var sp)) return "parsed";
            if (!latestScaffold.TryGetValue(sp.Id, out var sc))
                return sp.State == "SIGNED" ? "signed" : "specified";
            if (runsByScaffold.TryGetValue(sc.Id, out var statuses) && statuses.Count > 0)
            {
                if (statuses.Any(s => s is "FAILED" or "ERRORED")) return "failed";
                if (statuses.Any(s => s == "PASSED")) return "verified";
            }
            return "built";
        }

        var results = new List<RequirementDelivery>();
        var unresolvedNames = 0;
        var noTrace = 0;

        for (var i = 0; i < reqSections.Count; i++)
        {
            var root = reqSections[i].RootElement;
            var named = ReadStringArray(root, "sourceRoutines");
            var routines = new List<RoutineStatus>();

            foreach (var name in named)
            {
                if (!byFull.TryGetValue(name, out var hit))
                    byBare.TryGetValue(Bare(name), out hit);
                if (hit is null)
                {
                    // Requirements often cite a type or table alongside the
                    // routines — real, but not something with a lifecycle.
                    unresolvedNames++;
                    routines.Add(new RoutineStatus(name, null, "unresolved", null));
                    continue;
                }
                routines.Add(new RoutineStatus(
                    hit.Name, hit.Id, StatusOf(hit.Id),
                    waveByRoutine.TryGetValue(hit.Id, out var w) ? w : null));
            }

            var resolved = routines.Where(r => r.SubroutineId is not null).ToList();
            if (resolved.Count == 0) noTrace++;

            // Weakest link across the routines that actually resolved.
            var status = resolved.Count == 0
                ? "untraceable"
                : resolved.OrderBy(r => Rank(r.Status)).First().Status;

            results.Add(new RequirementDelivery(
                Reference: $"FR-{i + 1:000}",
                Statement: ReadString(root, "statement"),
                Capability: ReadString(root, "capability"),
                Priority: ReadString(root, "priority"),
                Status: status,
                Waves: resolved.Where(r => r.Wave is not null)
                               .Select(r => r.Wave!.Value).Distinct().OrderBy(x => x).ToList(),
                RoutinesNamed: named.Count,
                RoutinesResolved: resolved.Count,
                Routines: routines));
        }

        var counts = results.GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        return new DeliveryReport(
            corpusId, corpus.Name, results.Count, counts, noTrace, unresolvedNames, results);
    }

    private static string Bare(string name)
    {
        var i = name.LastIndexOf('.');
        return i >= 0 && i < name.Length - 1 ? name[(i + 1)..] : name;
    }

    private static IEnumerable<Guid> ParseGuids(string json)
    {
        List<Guid> ids = new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ids;
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g))
                    ids.Add(g);
        }
        catch { }
        return ids;
    }

    private static string ReadString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static List<string> ReadStringArray(JsonElement el, string prop)
    {
        var list = new List<string>();
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }
}
