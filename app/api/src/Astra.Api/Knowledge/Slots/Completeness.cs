namespace Astra.Api.Knowledge;

/// <summary>One version of what was written for a slot, and whether a person approved it.</summary>
/// <param name="Statuses">The status of every statement in that version (<c>supported</c>, <c>unverified</c>, <c>conflict</c>).</param>
public sealed record SlotVersion(bool Approved, IReadOnlyList<string> Statuses);

/// <summary>Everything written for one slot, version by version.</summary>
public sealed record SlotEvidence(string SlotKey, IReadOnlyList<SlotVersion> Versions);

/// <param name="Derived">For each deliverable whose slots are one per thing found (jobs, interfaces, known errors): the names of the things the project turned out to have.</param>
/// <param name="ExpectedCounts">The counts the project's settings say there should be, by count key. They set denominators only; they are never shown to a writer.</param>
public sealed record CompletenessInput(
    Pack Pack,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Derived,
    IReadOnlyDictionary<string, int> ExpectedCounts,
    IReadOnlyList<SlotEvidence> Slots);

public static class SlotState
{
    public const string ApprovedSupported = "approved-supported";
    public const string ApprovedUnverified = "approved-unverified";
    public const string NotApproved = "not-approved";
    public const string Missing = "missing";
}

public sealed record SlotScore(string SlotKey, string Label, double Credit, string State);

/// <param name="Denominator">How many slots the deliverable has (fixed ones, plus as many as the project expects of the things it has one per).</param>
/// <param name="Earned">Credit earned, never more than the denominator.</param>
/// <param name="Applicable">False when the deliverable has no slots to fill (a project with no batch jobs has no job sheets to write); it then takes no part in the score.</param>
public sealed record DeliverableScore(
    string Id,
    string Label,
    double Weight,
    int Denominator,
    double Earned,
    double Percent,
    bool Applicable,
    IReadOnlyList<SlotScore> Slots);

/// <param name="Percent">Completeness: how much of what a successor needs to know is written down, approved and backed by evidence.</param>
/// <param name="DependencyPercent">The knowledge dependency that remains: 100 minus completeness.</param>
public sealed record CompletenessResult(
    string PackId,
    double Percent,
    double DependencyPercent,
    string DependencyBand,
    IReadOnlyList<DeliverableScore> Deliverables);

/// <summary>
/// How complete a project's transition knowledge is, as a number a person can check by hand. Pure: a pack, the
/// things the project has, the counts it expects and what has been approved in, a score out; no clock, no model,
/// no database. The rules:
/// <list type="bullet">
/// <item>A slot earns its best credit over every approved version of it — approving a later version can never lower
/// the score. A version earns full credit when every statement in it is supported, partial credit when some are only
/// unverified, and nothing when it is not approved or a conflict is still open in it.</item>
/// <item>A deliverable's share is its earned credit over its slots. For the slots that exist once per thing found, the
/// project's expected count is the denominator when it gives one (a thing nobody found still counts against it), and
/// credit beyond that denominator is not counted.</item>
/// <item>The whole is the weighted mean of the deliverables that have slots to fill.</item>
/// </list>
/// </summary>
public static class Completeness
{
    public static CompletenessResult Compute(CompletenessInput input)
    {
        var pack = input.Pack;
        var byKey = input.Slots
            .GroupBy(s => s.SlotKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.SelectMany(s => s.Versions).ToList(), StringComparer.Ordinal);

        var scores = new List<DeliverableScore>();
        foreach (var d in pack.Deliverables)
        {
            var slots = new List<SlotScore>();
            double earned = 0;
            var denominator = 0;

            foreach (var s in d.StaticSlots)
            {
                var score = ScoreSlot(s.Key, s.Label, byKey.GetValueOrDefault(s.Key), pack.Credit);
                slots.Add(score);
                earned += score.Credit;
                denominator++;
            }

            if (d.EntitySlots is { } e)
            {
                var names = (input.Derived.GetValueOrDefault(d.Id) ?? Array.Empty<string>())
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
                var expected = input.ExpectedCounts.TryGetValue(e.CountKey, out var x) ? Math.Max(0, x) : names.Count;

                double entityEarned = 0;
                foreach (var name in names)
                {
                    var key = e.KeyPrefix + name;
                    var score = ScoreSlot(key, name, byKey.GetValueOrDefault(key), pack.Credit);
                    slots.Add(score);
                    entityEarned += score.Credit;
                }
                // Expected but not found: they count against the deliverable, and are shown so the gap can be seen.
                for (var i = names.Count; i < expected; i++)
                    slots.Add(new SlotScore($"{e.KeyPrefix}(not found {i - names.Count + 1})", e.Label, pack.Credit.Otherwise, SlotState.Missing));

                earned += Math.Min(entityEarned, expected);
                denominator += expected;
            }

            var applicable = denominator > 0;
            scores.Add(new DeliverableScore(
                d.Id, d.Label, d.Weight, denominator, earned,
                applicable ? earned / denominator * 100 : 0,
                applicable, slots));
        }

        var weight = scores.Where(s => s.Applicable).Sum(s => s.Weight);
        var total = weight > 0 ? scores.Where(s => s.Applicable).Sum(s => s.Weight * (s.Earned / s.Denominator)) / weight * 100 : 0;
        var dependency = 100 - total;
        return new CompletenessResult(pack.Id, total, dependency, pack.Bands.BandOf(dependency), scores);
    }

    private static SlotScore ScoreSlot(string key, string label, List<SlotVersion>? versions, CreditRules credit)
    {
        if (versions is null || versions.Count == 0)
            return new SlotScore(key, label, credit.Otherwise, SlotState.Missing);

        var best = new SlotScore(key, label, credit.Otherwise, SlotState.NotApproved);
        foreach (var v in versions)
        {
            var (c, state) = CreditOf(v, credit);
            if (c > best.Credit || (c == best.Credit && Rank(state) > Rank(best.State))) best = best with { Credit = c, State = state };
        }
        return best;
    }

    private static (double Credit, string State) CreditOf(SlotVersion v, CreditRules credit)
    {
        if (!v.Approved || v.Statuses.Count == 0) return (credit.Otherwise, SlotState.NotApproved);
        if (v.Statuses.Any(s => s == StatementStatus.Conflict)) return (credit.Otherwise, SlotState.NotApproved);
        return v.Statuses.All(s => s == StatementStatus.Supported)
            ? (credit.ApprovedAllSupported, SlotState.ApprovedSupported)
            : (credit.ApprovedWithUnverified, SlotState.ApprovedUnverified);
    }

    private static int Rank(string state) => state switch
    {
        SlotState.ApprovedSupported => 3,
        SlotState.ApprovedUnverified => 2,
        SlotState.NotApproved => 1,
        _ => 0,
    };
}
