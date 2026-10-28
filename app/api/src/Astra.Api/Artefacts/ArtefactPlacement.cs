using Astra.Api.Ingest;

namespace Astra.Api.Artefacts;

/// <summary>
/// The outcome of deciding where one incoming file goes: which artefact kind it belongs
/// to, or why it cannot be taken. A refusal always carries a code (stable, for clients)
/// and a message (for people) — nothing is ever skipped without saying so.
/// </summary>
public sealed record Placement(
    bool Accepted,
    string Path,
    string? Kind,
    /// <summary>More than one enabled kind takes this extension; the first in registry order was chosen.</summary>
    bool Ambiguous,
    string? Code,
    string? Message)
{
    public static Placement Ok(string path, string kind, bool ambiguous = false) =>
        new(true, path, kind, ambiguous, null, null);

    public static Placement Refuse(string path, string code, string message) =>
        new(false, path, null, false, code, message);
}

/// <summary>
/// Pure placement rules for artefact files, so they can be tested without a database:
/// path hygiene, then kind resolution against what the project has switched on.
/// </summary>
public static class ArtefactPlacement
{
    public const int MaxPathLength = 1024;

    /// <summary>
    /// Normalises a client-supplied path to a relative, forward-slash path inside the
    /// project, or returns null with a reason. Paths become storage keys, so anything
    /// that could climb out of the project (a rooted path, a drive letter, a ".." segment)
    /// is refused rather than cleaned up.
    /// </summary>
    public static string? NormalisePath(string? raw, out string? problemCode)
    {
        problemCode = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            problemCode = "empty_path";
            return null;
        }

        var p = raw.Trim().Replace('\\', '/');
        if (p.Any(char.IsControl))
        {
            problemCode = "unsafe_path";
            return null;
        }
        if (p.StartsWith('/') || (p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0])))
        {
            problemCode = "unsafe_path";
            return null;
        }

        var segments = new List<string>();
        foreach (var s in p.Split('/'))
        {
            if (s.Length == 0 || s == ".") continue;
            if (s == "..")
            {
                problemCode = "unsafe_path";
                return null;
            }
            segments.Add(s);
        }
        if (segments.Count == 0)
        {
            problemCode = "empty_path";
            return null;
        }

        var normalised = string.Join('/', segments);
        if (normalised.Length > MaxPathLength)
        {
            problemCode = "path_too_long";
            return null;
        }
        return normalised;
    }

    /// <summary>
    /// Decides the kind for one file. <paramref name="explicitKind"/> (when given) must be a
    /// kind this project has switched on and must accept the file's extension; otherwise the
    /// kind is inferred from the extension among the enabled kinds.
    /// </summary>
    public static Placement Resolve(string? rawPath, ProjectSettings settings, string? explicitKind = null)
    {
        var path = NormalisePath(rawPath, out var problem);
        if (path is null)
        {
            var shown = rawPath ?? "";
            return Placement.Refuse(shown, problem!, problem switch
            {
                "empty_path" => "The file has no name.",
                "path_too_long" => $"The path is longer than {MaxPathLength} characters.",
                _ => "The path is absolute or climbs out of the project (\"..\"), so it was refused.",
            });
        }

        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0)
            return Placement.Refuse(path, "unsupported_extension", "The file has no extension, so its kind cannot be told.");

        if (!string.IsNullOrWhiteSpace(explicitKind))
        {
            var kind = ArtefactKinds.Find(explicitKind.Trim());
            if (kind is null)
                return Placement.Refuse(path, "unknown_kind", $"'{explicitKind}' is not a known artefact kind.");
            if (kind.Core)
                return CodeBelongsInResync(path);
            if (!settings.Has(kind.Id))
                return Placement.Refuse(path, "kind_not_enabled",
                    $"{kind.Label} is not switched on for this project. Enable it in the project settings first.");
            if (!kind.Extensions.Contains(ext))
                return Placement.Refuse(path, "extension_not_accepted_by_kind",
                    $"{kind.Label} takes {string.Join(", ", kind.Extensions)}, not {ext}.");
            return Placement.Ok(path, kind.Id);
        }

        var enabled = ArtefactKinds.Optional.Where(k => settings.Has(k.Id)).ToList();
        var candidates = enabled.Where(k => k.Extensions.Contains(ext)).ToList();
        if (candidates.Count == 1) return Placement.Ok(path, candidates[0].Id);
        if (candidates.Count > 1) return Placement.Ok(path, candidates[0].Id, ambiguous: true);

        if (SourceLanguageDetector.FromExtension(ext) is not null) return CodeBelongsInResync(path);

        var disabled = ArtefactKinds.Optional.Where(k => !settings.Has(k.Id) && k.Extensions.Contains(ext)).ToList();
        if (disabled.Count > 0)
            return Placement.Refuse(path, "kind_not_enabled",
                $"{ext} files belong to {string.Join(" or ", disabled.Select(k => k.Label))}, which " +
                (disabled.Count == 1 ? "is" : "are") + " not switched on for this project. Enable it in the project settings.");

        var accepted = enabled.SelectMany(k => k.Extensions).Distinct().OrderBy(e => e, StringComparer.Ordinal).ToList();
        return Placement.Refuse(path, "unsupported_extension",
            accepted.Count == 0
                ? $"{ext} is not an artefact type, and this project has no optional artefact kinds switched on."
                : $"{ext} is not accepted. This project takes {string.Join(", ", accepted)}.");
    }

    private static Placement CodeBelongsInResync(string path) =>
        Placement.Refuse(path, "code_belongs_in_resync",
            "Source code is added by re-syncing the project, not by attaching artefacts.");
}
