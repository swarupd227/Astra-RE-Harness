using Astra.Api.Llm.Archetypes;

namespace Astra.Api.Work;

public sealed record ScaffoldTarget(string TargetStack, string? ArchetypeId, string? ErrorCode, string? Error)
{
    public bool Ok => ErrorCode is null;
}

/// <summary>
/// Which target stack and archetype a routine is generated with: the requested
/// one, else the project's own target, else its language's default target, else
/// the platform's preferred stack; refused when no production archetype covers
/// the pair. One rule for the single-routine tool and for wave runs.
/// </summary>
public static class ScaffoldTargetResolver
{
    public static ScaffoldTarget Resolve(ArchetypeRegistry archetypes, string sourceLanguage, string routineName, string? requested,
        string? projectTarget = null)
    {
        bool Speaks(IReadOnlyCollection<string> schemas) => string.IsNullOrEmpty(sourceLanguage)
            || schemas.Any(s => string.Equals(s, sourceLanguage, StringComparison.OrdinalIgnoreCase));

        var all = archetypes.All();
        string chosen;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            chosen = requested.Trim();
        }
        else
        {
            var compatible = all
                .Where(a => Speaks(a.Manifest.CompatibleSchemas))
                .Select(a => a.Manifest.TargetStack)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            chosen = Endpoints.ScaffoldEndpoints.PreferredStack(compatible, projectTarget, sourceLanguage);
        }

        var forTarget = all
            .Where(a => string.Equals(a.Manifest.TargetStack, chosen, StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Manifest.CompatibleSchemas.Count == 0 || Speaks(a.Manifest.CompatibleSchemas))
            .ToArray();
        if (forTarget.Length == 0)
        {
            var available = all.Where(a => Speaks(a.Manifest.CompatibleSchemas))
                .Select(a => a.Manifest.TargetStack).Distinct().ToArray();
            return new(chosen, null, "scaffold.no_compatible_archetype",
                $"No archetype for {sourceLanguage} → {chosen}. Available targets for this language: {string.Join(", ", available)}.");
        }
        var match = archetypes.PickForSubroutine(chosen, routineName, sourceLanguage) ?? forTarget[0];
        if (!(match.Manifest.Status ?? "").StartsWith("production", StringComparison.OrdinalIgnoreCase))
            return new(chosen, match.Manifest.Id, "scaffold.target_gated",
                $"Archetype '{match.Manifest.Id}' for {chosen} is {match.Manifest.Status} — not self-service yet.");
        return new(chosen, match.Manifest.Id, null, null);
    }
}
