namespace Astra.Api.Llm.Dependency;

/// <summary>
/// A feature slice from the call graph: the seed routines and every routine they call, directly or through others.
/// Pure: breadth-first over the call edges, each routine once, so a cycle ends the walk instead of looping.
/// </summary>
public static class CallClosure
{
    public static HashSet<Guid> Of(IEnumerable<Guid> seeds, IEnumerable<(Guid From, Guid To)> calls)
    {
        var next = new Dictionary<Guid, List<Guid>>();
        foreach (var (from, to) in calls)
        {
            if (!next.TryGetValue(from, out var list)) next[from] = list = new List<Guid>();
            list.Add(to);
        }
        var seen = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var s in seeds)
            if (seen.Add(s)) queue.Enqueue(s);
        while (queue.Count > 0)
        {
            var x = queue.Dequeue();
            if (!next.TryGetValue(x, out var callees)) continue;
            foreach (var y in callees)
                if (seen.Add(y)) queue.Enqueue(y);
        }
        return seen;
    }
}
