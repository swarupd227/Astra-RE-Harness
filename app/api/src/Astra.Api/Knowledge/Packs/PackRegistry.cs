namespace Astra.Api.Knowledge;

/// <summary>
/// The packs that ship with the platform, read once at startup from <c>Llm/Packs/&lt;id&gt;/pack.json</c> (next to the
/// binary when published, under <c>src/</c> in dev-watch mode — the same places prompts are found). A pack that does
/// not load is reported in <see cref="Problems"/> and left out; it never stops the API from starting.
/// </summary>
public sealed class PackRegistry
{
    private readonly IReadOnlyDictionary<string, Pack> _packs;
    private readonly IReadOnlyDictionary<string, string> _directories;

    public IReadOnlyList<Pack> All { get; }
    public IReadOnlyList<string> Problems { get; }

    public PackRegistry(IEnumerable<Pack> packs, IReadOnlyDictionary<string, string>? directories = null, IReadOnlyList<string>? problems = null)
    {
        var ordered = packs.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        _packs = ordered.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _directories = directories ?? new Dictionary<string, string>();
        All = ordered;
        Problems = problems ?? Array.Empty<string>();
        Deliverables = new DeliverableRegistry(ordered);
    }

    public DeliverableRegistry Deliverables { get; }

    public Pack? Get(string? id) => id is not null && _packs.TryGetValue(id, out var p) ? p : null;

    /// <summary>Where a pack's own files live (<c>fixtures/</c>), when it was loaded from disk.</summary>
    public string? DirectoryOf(string id) => _directories.GetValueOrDefault(id);

    public static PackRegistry Load(string contentRoot)
    {
        var packs = new List<Pack>();
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var root in CandidateRoots(contentRoot))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var file = Path.Combine(dir, "pack.json");
                if (!File.Exists(file)) continue;
                var result = PackLoader.Parse(File.ReadAllText(file));
                if (result.Pack is not { } pack)
                {
                    problems.Add($"{Path.GetFileName(dir)}: {string.Join(" ", result.Errors)}");
                    continue;
                }
                if (packs.Any(p => p.Id == pack.Id)) continue;   // the first place it is found wins
                packs.Add(pack);
                directories[pack.Id] = dir;
            }
        }
        return new PackRegistry(packs, directories, problems);
    }

    private static IEnumerable<string> CandidateRoots(string contentRoot)
    {
        yield return Path.GetFullPath(Path.Combine(contentRoot, "Llm", "Packs"));
        yield return Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "Llm", "Packs"));
    }
}

/// <summary>
/// The deliverables of every loaded pack, by id and by the section kind they are stored under (<c>d-runbook</c>).
/// It stands in for the lists of kinds the older documentation code hard-wires: a new deliverable is data in a pack,
/// and nothing about it needs adding to those lists.
/// </summary>
public sealed class DeliverableRegistry
{
    private readonly IReadOnlyList<(Pack Pack, DeliverableDefinition Deliverable)> _all;

    public DeliverableRegistry(IEnumerable<Pack> packs)
    {
        _all = packs.SelectMany(p => p.Deliverables.Select(d => (p, d))).ToList();
    }

    public IReadOnlyList<DeliverableDefinition> All => _all.Select(x => x.Deliverable).ToList();

    public DeliverableDefinition? FindById(string? packId, string? deliverableId) =>
        _all.FirstOrDefault(x => x.Pack.Id == packId && x.Deliverable.Id == deliverableId).Deliverable;

    /// <summary>The deliverable a stored section of kind <c>d-…</c> belongs to, in the given pack.</summary>
    public DeliverableDefinition? FindBySectionKind(string? packId, string? sectionKind) =>
        DeliverableKinds.IsDeliverableSectionKind(sectionKind) ? FindById(packId, DeliverableKinds.DeliverableIdOf(sectionKind!)) : null;

    public bool IsDeliverableSectionKind(string? sectionKind) => DeliverableKinds.IsDeliverableSectionKind(sectionKind);
}
