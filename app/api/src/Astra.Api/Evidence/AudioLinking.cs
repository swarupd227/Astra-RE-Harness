using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Api.Evidence;

/// <summary>
/// Decides which transcript a recording belongs to, so a citation of a moment in the transcript can play
/// the recording from there. Pure rules, no database:
/// <list type="number">
/// <item>a link the user set (<c>attachesTo</c> in the file's meta) wins, if that transcript still exists;</item>
/// <item>otherwise a transcript in the same folder with the same name (<c>kt/session1.mp3</c> and <c>kt/session1.vtt</c>),
/// preferring .vtt over .txt;</item>
/// <item>otherwise the only transcript anywhere with that name.</item>
/// </list>
/// Anything else is unlinked and says so; it is never guessed from how many files there are.
/// </summary>
public static class AudioLinking
{
    public const string MetaKey = "attachesTo";

    public static string StemOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
    }

    public static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash].ToLowerInvariant();
    }

    public static string? Resolve(string audioPath, string? explicitTarget, IReadOnlyCollection<string> transcriptPaths)
    {
        if (explicitTarget is not null)
        {
            var chosen = transcriptPaths.FirstOrDefault(t => string.Equals(t, explicitTarget, StringComparison.Ordinal));
            if (chosen is not null) return chosen;
        }

        var stem = StemOf(audioPath);
        var named = transcriptPaths.Where(t => StemOf(t) == stem).ToList();
        var sameFolder = named.Where(t => FolderOf(t) == FolderOf(audioPath)).OrderBy(Rank).ThenBy(t => t, StringComparer.Ordinal).ToList();
        if (sameFolder.Count > 0) return sameFolder[0];
        return named.Count == 1 ? named[0] : null;
    }

    private static int Rank(string transcriptPath) =>
        transcriptPath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ? 0
        : transcriptPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    /// <summary>The transcript the user linked a recording to, from the file's meta JSON.</summary>
    public static string? AttachesTo(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson)) return null;
        try
        {
            return JsonNode.Parse(metaJson) is JsonObject o && o[MetaKey] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
                ? s
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The meta JSON with the link set (or removed when <paramref name="target"/> is null), other keys untouched.</summary>
    public static string? WithAttachesTo(string? metaJson, string? target)
    {
        JsonObject o;
        try
        {
            o = !string.IsNullOrWhiteSpace(metaJson) && JsonNode.Parse(metaJson) is JsonObject existing ? existing : new JsonObject();
        }
        catch (JsonException)
        {
            o = new JsonObject();
        }
        if (target is null) o.Remove(MetaKey);
        else o[MetaKey] = target;
        return o.Count == 0 ? null : o.ToJsonString();
    }
}
