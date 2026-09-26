using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Read model for the Routine flow board (WS2): where every routine in a
/// corpus's latest source version currently sits in the pipeline, from
/// parsed through committed.
///
/// The first six columns track <see cref="Subroutine.State"/> / <see
/// cref="Spec.State"/> the same way <c>MigrationPlanEndpoints.ClassifyState</c>
/// does. The last two need one more join that helper doesn't do: telling
/// "built" from "verified" means reading the scaffold's own latest
/// <see cref="ValidationRun"/> per gate — the same rule
/// ValidationReportPage's <c>computeOverall</c> uses on the frontend (every
/// one of the four gates has a run, and it's PASSED). Bulk-loaded in four
/// queries (subroutines, their latest specs, latest scaffolds, latest
/// per-gate runs) so this stays O(1) round trips regardless of corpus size,
/// not O(n) per routine.
/// </summary>
public static class FlowBoardEndpoints
{
    private static readonly string[] GateStages = { "COMPILE", "TEST_PACK", "EQUIVALENCE", "FALSIFYING" };

    // A physical board can't show thousands of cards either — cap what's
    // rendered per column and let the count + "hasMore" speak for the rest.
    // Routines beyond the cap are still counted, just not returned.
    private const int PerColumnCap = 100;

    private static readonly string[] ColumnKeys =
        { "parsed", "extracting", "draft", "in_review", "signed", "built", "verified", "committed" };

    public static IEndpointRouteBuilder MapFlowBoardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/corpora/{id:guid}/flow-board", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
            if (corpus is null)
                return Results.NotFound(new { error = new { code = "corpus.not_found" } });

            if (corpus.LatestVersionId is not { } versionId)
            {
                return Results.Ok(new
                {
                    corpusId = id,
                    corpusName = corpus.Name,
                    totalRoutines = 0,
                    columns = ColumnKeys.Select(EmptyColumn).ToList(),
                });
            }

            var subs = await (
                from s in db.Subroutines.AsNoTracking()
                join f in db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
                where f.SourceVersionId == versionId
                orderby f.RelativePath, s.LineStart
                select new { s.Id, s.Name, s.State, s.LineStart, s.LineEnd, FilePath = f.RelativePath })
                .ToListAsync(ct);

            var subIds = subs.Select(x => x.Id).ToHashSet();
            var specs = subIds.Count == 0
                ? new List<Spec>()
                : await db.Specs.AsNoTracking()
                    .Where(sp => subIds.Contains(sp.SubroutineId))
                    .OrderByDescending(sp => sp.UpdatedAt)
                    .ToListAsync(ct);
            var latestSpecBySub = specs.GroupBy(sp => sp.SubroutineId).ToDictionary(g => g.Key, g => g.First());

            var specIds = specs.Select(sp => sp.Id).ToHashSet();
            var scaffolds = specIds.Count == 0
                ? new List<Scaffold>()
                : await db.Scaffolds.AsNoTracking()
                    .Where(sc => specIds.Contains(sc.SpecId))
                    .OrderByDescending(sc => sc.GeneratedAt)
                    .ToListAsync(ct);
            var latestScaffoldBySpec = scaffolds.GroupBy(sc => sc.SpecId).ToDictionary(g => g.Key, g => g.First());

            var scaffoldIds = latestScaffoldBySpec.Values.Select(sc => sc.Id).ToHashSet();
            var runs = scaffoldIds.Count == 0
                ? new List<ValidationRun>()
                : await db.ValidationRuns.AsNoTracking()
                    .Where(r => scaffoldIds.Contains(r.ScaffoldId))
                    .OrderByDescending(r => r.StartedAt)
                    .ToListAsync(ct);
            var latestStatusByScaffold = runs
                .GroupBy(r => r.ScaffoldId)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(r => r.Stage).ToDictionary(gg => gg.Key, gg => gg.First().Status));

            var buckets = ColumnKeys.ToDictionary(k => k, _ => new List<object>());
            var counts = ColumnKeys.ToDictionary(k => k, _ => 0);

            foreach (var x in subs)
            {
                var spec = latestSpecBySub.GetValueOrDefault(x.Id);
                var scaffold = spec is null ? null : latestScaffoldBySpec.GetValueOrDefault(spec.Id);
                var statusByStage = scaffold is null ? null : latestStatusByScaffold.GetValueOrDefault(scaffold.Id);
                var column = ClassifyColumn(x.State, spec?.State, scaffold?.State, statusByStage);
                counts[column]++;
                var list = buckets[column];
                if (list.Count < PerColumnCap)
                {
                    list.Add(new
                    {
                        id = x.Id,
                        name = x.Name,
                        filePath = x.FilePath,
                        lineStart = x.LineStart,
                        lineEnd = x.LineEnd,
                        scaffoldState = scaffold?.State,
                    });
                }
            }

            var columns = ColumnKeys.Select(key => (object)new
            {
                key,
                label = Label(key),
                total = counts[key],
                routines = buckets[key],
                hasMore = counts[key] > buckets[key].Count,
            }).ToList();

            return Results.Ok(new
            {
                corpusId = id,
                corpusName = corpus.Name,
                totalRoutines = subs.Count,
                columns,
            });
        });

        return app;
    }

    private static string Label(string key) => key switch
    {
        "parsed" => "Parsed",
        "extracting" => "Extracting",
        "draft" => "Draft",
        "in_review" => "In review",
        "signed" => "Signed",
        "built" => "Built",
        "verified" => "Verified",
        "committed" => "Committed",
        _ => key,
    };

    private static object EmptyColumn(string key) =>
        new { key, label = Label(key), total = 0, routines = Array.Empty<object>(), hasMore = false };

    /// <summary>
    /// One routine, one column. Mirrors <c>MigrationPlanEndpoints.ClassifyState</c>
    /// for the shared part (spec state drives draft/in_review/signed once a
    /// spec exists; a scaffold's own state drives committed), then extends it:
    /// a SIGNED spec with no scaffold row yet also covers the brief
    /// "SCAFFOLDING" window (the row is only inserted once generation
    /// succeeds — see ScaffoldPipeline — so there is nothing further to
    /// distinguish there); a scaffold that exists but isn't committed is
    /// "verified" only once every one of the four gates has a latest run
    /// that PASSED, else "built" (this covers a FAILED scaffold too — it
    /// will never have all four green).
    /// </summary>
    private static string ClassifyColumn(
        string subState,
        string? specState,
        string? scaffoldState,
        IReadOnlyDictionary<string, string>? statusByStage)
    {
        if (specState is null)
            return subState == "EXTRACTING" ? "extracting" : "parsed";
        if (specState == "DRAFT") return "draft";
        if (specState == "IN_REVIEW") return "in_review";
        // specState == "SIGNED" from here (or an unrecognised future value,
        // treated the same since SIGNED is the last confirmed step before one).
        if (scaffoldState is null) return "signed";
        if (scaffoldState == "COMMITTED") return "committed";
        if (statusByStage is not null && GateStages.All(st => statusByStage.TryGetValue(st, out var status) && status == "PASSED"))
            return "verified";
        return "built";
    }
}
