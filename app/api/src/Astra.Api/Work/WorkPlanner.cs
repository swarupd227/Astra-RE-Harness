namespace Astra.Api.Work;

/// <summary>Stage kinds of the per-routine pipeline a work run walks.</summary>
public static class WorkStages
{
    public const string Extract = "extract";
    /// <summary>The human wait state: a spec reviewed and signed by an SME. No
    /// worker ever leases it and no agent ever completes it.</summary>
    public const string Signoff = "signoff";
    public const string Scaffold = "scaffold";
    public const string Compile = "compile";
    public const string TestPack = "test-pack";

    public static readonly IReadOnlyList<string> Order = new[] { Extract, Signoff, Scaffold, Compile, TestPack };
    public static readonly IReadOnlyList<string> Machine = new[] { Extract, Scaffold, Compile, TestPack };

    public static bool IsHuman(string kind) => kind == Signoff;

    public static int IndexOf(string kind)
    {
        for (var i = 0; i < Order.Count; i++)
            if (Order[i] == kind) return i;
        return -1;
    }

    /// <summary>Past participle used in progress sentences ("34 of 80 extracted").</summary>
    public static string Done(string kind) => kind switch
    {
        Extract => "extracted",
        Signoff => "signed",
        Scaffold => "generated",
        Compile => "compiled",
        TestPack => "test packs passed",
        _ => kind,
    };

    /// <summary>Default per-stage concurrency (model-bound stages wider than the
    /// build gates, which are CPU and memory heavy).</summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultCaps = new Dictionary<string, int>
    {
        [Extract] = 6,
        [Scaffold] = 4,
        [Compile] = 2,
        [TestPack] = 2,
    };

    /// <summary>Requested stage names in pipeline order, unknown ones dropped.
    /// Asking for a machine stage on both sides of the sign-off keeps the wait
    /// between them: it can never be left out to get unsigned code generated.</summary>
    public static IReadOnlyList<string> Normalise(IEnumerable<string>? requested)
    {
        var set = (requested ?? Order)
            .Select(s => s.Trim().ToLowerInvariant().Replace('_', '-'))
            .ToHashSet();
        if (set.Count == 0) set = Order.ToHashSet();
        if (set.Contains(Extract) && (set.Contains(Scaffold) || set.Contains(Compile) || set.Contains(TestPack)))
            set.Add(Signoff);
        return Order.Where(set.Contains).ToList();
    }
}

public sealed record RoutineSnapshot(Guid Id, string Name, string State, int WaveNumber);

public sealed record PlannedItem(Guid Id, string Kind, Guid SubjectId, string SubjectName, Guid[] DependsOn, int Priority);

public sealed record SkippedRoutine(Guid Id, string Name, string Reason);

public sealed record PlanResult(IReadOnlyList<PlannedItem> Items, IReadOnlyList<SkippedRoutine> Skipped)
{
    public int RoutineCount => Items.Select(i => i.SubjectId).Distinct().Count();
    public int Count(string kind) => Items.Count(i => i.Kind == kind);
}

/// <summary>
/// Pure expansion of routines into the per-routine item chain
/// <c>extract → signoff → scaffold → compile → test-pack</c>. Each routine
/// enters at the stage its current state calls for (a SIGNED routine starts at
/// scaffold, an IN_REVIEW one at the sign-off wait) and keeps only the stages
/// the run asked for. Lower wave numbers get lower priorities, so leaves are
/// leased first.
/// </summary>
public static class WorkPlanner
{
    public static PlanResult Expand(IEnumerable<RoutineSnapshot> routines, IReadOnlyList<string> stages, Func<Guid>? newId = null)
    {
        newId ??= Guid.NewGuid;
        var items = new List<PlannedItem>();
        var skipped = new List<SkippedRoutine>();

        foreach (var r in routines)
        {
            var entry = EntryStage(r.State);
            if (entry is null)
            {
                skipped.Add(new(r.Id, r.Name, BusyOrDoneReason(r.State)));
                continue;
            }

            var entryIdx = WorkStages.IndexOf(entry);
            var chain = stages.Where(s => WorkStages.IndexOf(s) >= entryIdx).ToList();
            if (chain.Count == 0)
            {
                skipped.Add(new(r.Id, r.Name, $"already past the requested stages ({Words(r.State)})"));
                continue;
            }
            if (!CanStartAt(r.State, chain[0]))
            {
                skipped.Add(new(r.Id, r.Name, $"{Words(r.State)}, so not ready for {chain[0]}"));
                continue;
            }

            Guid? prev = null;
            foreach (var kind in chain)
            {
                var id = newId();
                items.Add(new PlannedItem(id, kind, r.Id, r.Name,
                    prev is { } p ? new[] { p } : Array.Empty<Guid>(),
                    r.WaveNumber * 10 + WorkStages.IndexOf(kind)));
                prev = id;
            }
        }
        return new PlanResult(items, skipped);
    }

    /// <summary>The first stage a routine in this state still needs; null when it is busy or finished.</summary>
    public static string? EntryStage(string state) => state switch
    {
        "PARSED" => WorkStages.Extract,
        "DRAFT" => WorkStages.Signoff,       // a spec exists: route it, wait for the SME
        "IN_REVIEW" => WorkStages.Signoff,
        "SIGNED" => WorkStages.Scaffold,
        "SCAFFOLDED" => WorkStages.Compile,
        _ => null,                           // EXTRACTING / SCAFFOLDING (busy), COMMITTED (done)
    };

    /// <summary>Whether the routine's state already provides everything the stage needs.</summary>
    private static bool CanStartAt(string state, string stage) => state switch
    {
        "PARSED" => stage == WorkStages.Extract,
        "DRAFT" or "IN_REVIEW" => stage == WorkStages.Signoff,
        "SIGNED" => stage == WorkStages.Scaffold,
        "SCAFFOLDED" => stage is WorkStages.Compile or WorkStages.TestPack,
        _ => false,
    };

    private static string Words(string state) => state.ToLowerInvariant().Replace('_', ' ');

    private static string BusyOrDoneReason(string state) => state switch
    {
        "EXTRACTING" => "a spec is being drafted right now",
        "SCAFFOLDING" => "code is being generated right now",
        "COMMITTED" => "already committed",
        _ => $"state {Words(state)}",
    };
}
