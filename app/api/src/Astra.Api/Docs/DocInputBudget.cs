namespace Astra.Api.Docs;

/// <summary>
/// Fits a writer's input under a token budget without chopping text. Each
/// item carries a core part (the routine summary) and an optional extra
/// (its source slice). When the total exceeds the budget, extras are
/// removed from the least important items first; when that is not enough,
/// whole items are dropped in the same order. Importance is tier first
/// (headline before standard), then size (larger routines are kept),
/// then input order (earlier kept). The caller says in the prompt what was
/// left out, so the model never sees a silently truncated summary.
/// Tokens are estimated at four characters each.
/// </summary>
public static class DocInputBudget
{
    public const int CharsPerToken = 4;

    public static int TokensToChars(int tokens) => checked(tokens * CharsPerToken);
    public static int EstimateTokens(int chars) => (chars + CharsPerToken - 1) / CharsPerToken;
    public static int EstimateTokens(string s) => EstimateTokens(s?.Length ?? 0);

    public static int TierRank(string? tier) => (tier ?? "").ToLowerInvariant() switch
    {
        "headline" => 2,
        "standard" => 1,
        _ => 0,
    };

    public sealed record Item<T>(T Value, string? Tier, int Size, int CoreChars, int ExtraChars = 0);

    public sealed record Placement<T>(T Value, bool Included, bool IncludeExtra);

    public sealed record Result<T>(IReadOnlyList<Placement<T>> Placements, long TotalChars, int Dropped, int ExtrasDropped)
    {
        public IEnumerable<Placement<T>> Kept => Placements.Where(p => p.Included);
        public IEnumerable<T> DroppedValues => Placements.Where(p => !p.Included).Select(p => p.Value);
        public bool Trimmed => Dropped > 0 || ExtrasDropped > 0;
    }

    /// <param name="items">Candidates in input order.</param>
    /// <param name="budgetTokens">Total budget for fixed + items.</param>
    /// <param name="fixedChars">Characters the message carries regardless
    /// (envelope, corpus name, overview text …).</param>
    public static Result<T> Fit<T>(IReadOnlyList<Item<T>> items, int budgetTokens, int fixedChars = 0)
    {
        var budgetChars = (long)TokensToChars(Math.Max(0, budgetTokens));
        var included = new bool[items.Count];
        var extra = new bool[items.Count];
        long total = fixedChars;
        for (var i = 0; i < items.Count; i++)
        {
            included[i] = true;
            extra[i] = items[i].ExtraChars > 0;
            total += items[i].CoreChars + items[i].ExtraChars;
        }

        // Least important first: lowest tier, then smallest, then latest.
        var order = Enumerable.Range(0, items.Count)
            .OrderBy(i => TierRank(items[i].Tier))
            .ThenBy(i => items[i].Size)
            .ThenByDescending(i => i)
            .ToList();

        var extrasDropped = 0;
        foreach (var i in order)
        {
            if (total <= budgetChars) break;
            if (!extra[i]) continue;
            extra[i] = false;
            total -= items[i].ExtraChars;
            extrasDropped++;
        }

        var dropped = 0;
        foreach (var i in order)
        {
            if (total <= budgetChars) break;
            if (!included[i]) continue;
            included[i] = false;
            total -= items[i].CoreChars + (extra[i] ? items[i].ExtraChars : 0);
            extra[i] = false;
            dropped++;
        }

        var placements = new List<Placement<T>>(items.Count);
        for (var i = 0; i < items.Count; i++)
            placements.Add(new Placement<T>(items[i].Value, included[i], included[i] && extra[i]));
        return new Result<T>(placements, total, dropped, extrasDropped);
    }
}
