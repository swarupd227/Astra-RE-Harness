namespace Astra.Api.Work;

/// <summary>The one sentence a batch's single Confirm card says: how many of
/// what will run, how wide, where it stops for a person, and the cost ceiling.</summary>
public static class BatchSentence
{
    public static string Describe(WorkRunRequest request, WorkRunPreview preview)
    {
        var plan = preview.Plan;
        if (plan.Items.Count == 0)
        {
            var why = string.Join("; ", plan.Skipped.Take(3).Select(s => $"{s.Name}: {s.Reason}"));
            return $"queue \"{request.Label}\" (nothing for an agent to do{(why.Length > 0 ? $": {why}" : "")})";
        }

        var before = new List<string>();
        var after = new List<string>();
        var waits = plan.Count(WorkStages.Signoff);
        foreach (var kind in WorkStages.Machine)
        {
            var n = plan.Count(kind);
            if (n == 0) continue;
            var phrase = kind switch
            {
                WorkStages.Extract => $"draft {Count(n, "spec")}",
                WorkStages.Scaffold => $"generate {Count(n, "code package")}",
                WorkStages.Compile => $"compile {n}",
                _ => $"run {Count(n, "test pack")}",
            };
            (kind == WorkStages.Extract ? before : after).Add(phrase);
        }

        var steps = new List<string>();
        steps.AddRange(before);
        if (waits > 0) steps.Add($"wait for an SME to sign {(waits == 1 ? "it" : $"each of {waits}")}");
        steps.AddRange(after);

        var widths = WorkStages.Machine.Where(k => plan.Count(k) > 0)
            .Select(k => $"{Math.Min(preview.Caps[k], plan.Count(k))} {k}")
            .ToList();

        var sentence = $"queue \"{request.Label}\" ({Count(plan.RoutineCount, "routine")}): {string.Join(", then ", steps)}; " +
                       $"working in parallel ({string.Join(", ", widths)} at a time)";
        sentence += request.BudgetUsd is { } budget ? $", pausing at a ${budget:0.00} cost ceiling" : ", with no cost ceiling";
        if (waits > 0) sentence += "; agents never sign";
        if (plan.Skipped.Count > 0) sentence += $"; {Count(plan.Skipped.Count, "routine")} skipped";
        if (preview.Warnings.Count > 0) sentence += $"; note: {string.Join(" ", preview.Warnings)}";
        return sentence;
    }

    /// <summary>A routine-list run's name; when it takes only part of what matches, the name says so.</summary>
    public static string ListLabel(string title, int taken, int matching, string programme)
    {
        var how = matching > taken ? $"{taken} of {matching} routines" : Count(taken, "routine");
        return $"{title} · {how} · {programme}";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
