using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <param name="Gates">
/// Latest result of each of the four validation gates for this routine's
/// scaffold (PASSED / FAILED / ERRORED / RUNNING, or NOT_RUN when the gate
/// has never run). Null when the routine has no scaffold yet.
/// </param>
public sealed record FlowBoardRoutine(
    Guid Id, string Name, string FilePath, int LineStart, int LineEnd, string? ScaffoldState,
    IReadOnlyDictionary<string, string>? Gates = null);

/// <param name="GateTally">
/// For stages whose routines have scaffolds (built / verified / committed):
/// per gate, how many of ALL the stage's routines — not just the returned
/// ones — sit at each result. Null for earlier stages.
/// </param>
public sealed record FlowBoardColumn(
    string Key, string Label, int Total, IReadOnlyList<FlowBoardRoutine> Routines, bool HasMore,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? GateTally = null);

public sealed record FlowBoardResult(Guid CorpusId, string CorpusName, int TotalRoutines, IReadOnlyList<FlowBoardColumn> Columns);

/// <summary>
/// Read model for the Routine flow board (WS2): where every routine in a
/// corpus's latest source version currently sits in the pipeline, from
/// parsed through committed. Served by the page's endpoint and by the
/// copilot's <c>get_flow_board</c> tool — both go through
/// <see cref="BuildAsync"/> so a card in a thread can never disagree with
/// the page it opens.
///
/// The first six columns track <see cref="Subroutine.State"/> / <see
/// cref="Spec.State"/> the same way <c>MigrationPlanEndpoints.ClassifyState</c>
/// does. The last two need one more join that helper doesn't do: telling
/// "built" from "verified" means reading the scaffold's own latest
/// <see cref="ValidationRun"/> per gate — the same rule
/// ValidationReportPage's <c>computeOverall</c> uses on the frontend (every
/// one of the four gates has a latest run, and it's PASSED). Bulk-loaded in
/// four queries (subroutines, their latest specs, latest scaffolds, latest
/// per-gate runs) so this stays O(1) round trips regardless of corpus size,
/// not O(n) per routine.
/// </summary>
public static class FlowBoardEndpoints
{
    private static readonly string[] GateStages = { "COMPILE", "TEST_PACK", "EQUIVALENCE", "FALSIFYING" };

    // A physical board can't show thousands of cards either — cap what's
    // returned per column and let the count + "hasMore" speak for the rest.
    // Routines beyond the cap are still counted, just not returned.
    public const int PageCap = 100;

    public static readonly string[] ColumnKeys =
        { "parsed", "extracting", "draft", "in_review", "signed", "built", "verified", "committed" };

    public static IEndpointRouteBuilder MapFlowBoardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/corpora/{id:guid}/flow-board", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var board = await BuildAsync(db, id, PageCap, ct);
            return board is null
                ? Results.NotFound(new { error = new { code = "corpus.not_found" } })
                : Results.Ok(board);
        });

        return app;
    }

    /// <summary>Null when the corpus doesn't exist.</summary>
    public static async Task<FlowBoardResult?> BuildAsync(AppDbContext db, Guid corpusId, int perColumnCap, CancellationToken ct)
    {
        var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == corpusId, ct);
        if (corpus is null) return null;

        if (corpus.LatestVersionId is not { } versionId)
        {
            return new FlowBoardResult(
                corpusId,
                corpus.Name,
                0,
                ColumnKeys.Select(k => new FlowBoardColumn(k, Label(k), 0, Array.Empty<FlowBoardRoutine>(), false)).ToList());
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

        var buckets = ColumnKeys.ToDictionary(k => k, _ => new List<FlowBoardRoutine>());
        var counts = ColumnKeys.ToDictionary(k => k, _ => 0);
        var tallies = new Dictionary<string, Dictionary<string, Dictionary<string, int>>>();

        foreach (var x in subs)
        {
            var spec = latestSpecBySub.GetValueOrDefault(x.Id);
            var scaffold = spec is null ? null : latestScaffoldBySpec.GetValueOrDefault(spec.Id);
            var statusByStage = scaffold is null ? null : latestStatusByScaffold.GetValueOrDefault(scaffold.Id);
            var column = ClassifyColumn(x.State, spec?.State, scaffold?.State, statusByStage);
            counts[column]++;

            var gates = scaffold is null ? null : GateMap(statusByStage);
            if (gates is not null)
            {
                if (!tallies.TryGetValue(column, out var byStage)) tallies[column] = byStage = new();
                Tally(byStage, gates);
            }

            var list = buckets[column];
            if (list.Count < perColumnCap)
                list.Add(new FlowBoardRoutine(x.Id, x.Name, x.FilePath, x.LineStart, x.LineEnd, scaffold?.State, gates));
        }

        var columns = ColumnKeys
            .Select(k => new FlowBoardColumn(
                k, Label(k), counts[k], buckets[k], counts[k] > buckets[k].Count,
                tallies.TryGetValue(k, out var t)
                    ? t.ToDictionary(e => e.Key, e => (IReadOnlyDictionary<string, int>)e.Value)
                    : null))
            .ToList();
        return new FlowBoardResult(corpusId, corpus.Name, subs.Count, columns);
    }

    public const string NotRun = "NOT_RUN";

    /// <summary>
    /// All four gates in order, each with its latest result — or NOT_RUN when
    /// it never ran, so "hasn't run" is stated by the data, not inferred by a
    /// reader (or a model) from a missing key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GateMap(IReadOnlyDictionary<string, string>? statusByStage) =>
        GateStages.ToDictionary(
            g => g,
            g => statusByStage is not null && statusByStage.TryGetValue(g, out var s) ? s : NotRun);

    /// <summary>Adds one routine's gate results to a stage's running per-gate counts.</summary>
    public static void Tally(Dictionary<string, Dictionary<string, int>> byStage, IReadOnlyDictionary<string, string> gates)
    {
        foreach (var (stage, status) in gates)
        {
            if (!byStage.TryGetValue(stage, out var counts)) byStage[stage] = counts = new();
            counts[status] = counts.GetValueOrDefault(status) + 1;
        }
    }

    public static string Label(string key) => key switch
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
    public static string ClassifyColumn(
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
