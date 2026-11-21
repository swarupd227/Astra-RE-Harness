using System.Text.RegularExpressions;

namespace Astra.Api.Knowledge;

public enum IdentifierKind { Quoted, Name, Code, Url, Path, Setting }

/// <param name="Text">The identifier as the statement wrote it.</param>
/// <param name="Parts">For a quoted span, the identifier-like words inside it: when the span as a whole is not in the evidence (a command with arguments), each of these still has to be.</param>
public sealed record Identifier(string Text, IdentifierKind Kind, IReadOnlyList<string> Parts);

/// <summary>
/// Finds, in the text of a statement, the things that can be checked by looking them up verbatim: backticked
/// spans, names with an underscore (<c>TLR_EOD_POST</c>, <c>usp_PostStage</c>), compound names
/// (<c>OfacScreeningClient</c>), error and ticket codes (<c>error 2627</c>, <c>INC-T-0412</c>), settings
/// (<c>Ofac:TimeoutSeconds</c>), URLs and file paths. A plain word is never an identifier, so ordinary prose
/// is never held against the evidence; a name that is not in what the statement cites is a name the writer
/// made up, or took from somewhere it did not cite.
/// </summary>
public static class IdentifierExtractor
{
    private static readonly Regex Backticked = new(@"`([^`\n]+)`", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://[^\s)\]>""'`]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WindowsPath = new(@"(?<![\w])(?:[A-Za-z]:\\|\\\\)[^\s""'`<>|]+", RegexOptions.Compiled);
    private static readonly Regex PosixPath = new(@"(?<![\w:/.\-])(?:/[\w.\-]+){2,}/?|(?<![\w:/.\-])[\w.\-]+(?:/[\w.\-]+)+\.\w{1,6}(?![\w])", RegexOptions.Compiled);

    private static readonly Regex Setting = new(@"(?<![\w:])[A-Za-z][A-Za-z0-9_]*(?::[A-Za-z][A-Za-z0-9_]*)+(?![\w:])", RegexOptions.Compiled);
    private static readonly Regex Snake = new(@"(?<![\w])[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+(?![\w])", RegexOptions.Compiled);
    private static readonly Regex Compound = new(@"(?<![\w])[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+(?![\w])", RegexOptions.Compiled);
    private static readonly Regex TicketCode = new(@"(?<![\w-])[A-Z]{2,5}(?:-[A-Z])?-\d{2,6}(?![\w-])", RegexOptions.Compiled);
    private static readonly Regex ErrorNumber = new(
        @"\b(?:error|errno|sqlcode|sqlstate|exception)\s*(?:code|number|no\.?|#|:)?\s*(?<n>\d{3,6})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Product and platform names that are written in compound form and are not in anyone's evidence by that spelling.
    private static readonly HashSet<string> CommonCompounds = new(StringComparer.OrdinalIgnoreCase)
    {
        "PowerShell", "JavaScript", "TypeScript", "GitHub", "ServiceNow", "OneDrive", "SharePoint", "WebSphere",
        "PostgreSQL", "MySQL", "SQLite", "DevOps", "LinkedIn", "YouTube", "OpenAI", "iPhone", "macOS", "NuGet",
        "RabbitMQ", "ActiveDirectory", "SqlServer",
    };

    public static IReadOnlyList<Identifier> Extract(string text)
    {
        var found = new List<Identifier>();
        if (string.IsNullOrWhiteSpace(text)) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rest = text;

        void Add(string value, IdentifierKind kind, IReadOnlyList<string>? parts = null)
        {
            var v = value.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '\'', '"');
            if (v.Length == 0 || !seen.Add(v)) return;
            found.Add(new Identifier(v, kind, parts ?? Array.Empty<string>()));
        }

        // Backticked spans first, and out of the way, so the words inside are not found a second time.
        rest = Backticked.Replace(rest, m =>
        {
            var span = m.Groups[1].Value.Trim();
            if (span.Length > 0) Add(span, IdentifierKind.Quoted, PartsOf(span));
            return " ";
        });

        rest = Url.Replace(rest, m => { Add(m.Value, IdentifierKind.Url); return " "; });
        rest = WindowsPath.Replace(rest, m => { Add(m.Value, IdentifierKind.Path); return " "; });
        rest = PosixPath.Replace(rest, m => { Add(m.Value, IdentifierKind.Path); return " "; });

        foreach (Match m in ErrorNumber.Matches(rest)) Add(m.Groups["n"].Value, IdentifierKind.Code);
        foreach (Match m in TicketCode.Matches(rest)) Add(m.Value, IdentifierKind.Code);
        // A setting is one identifier, not also the compound name that ends it.
        rest = Setting.Replace(rest, m => { Add(m.Value, IdentifierKind.Setting); return " "; });
        foreach (Match m in Snake.Matches(rest)) Add(m.Value, IdentifierKind.Name);
        foreach (Match m in Compound.Matches(rest))
            if (!CommonCompounds.Contains(m.Value)) Add(m.Value, IdentifierKind.Name);
        return found;
    }

    /// <summary>The identifier-like words inside a quoted span (a command with its arguments): names, settings, paths, numbers of four digits or more.</summary>
    private static IReadOnlyList<string> PartsOf(string span)
    {
        var parts = new List<string>();
        foreach (var raw in Regex.Split(span, @"[\s=,;()]+"))
        {
            var w = raw.Trim().TrimStart('@').Trim('\'', '"', '.');
            if (w.Length < 4) continue;
            var identifierLike = w.Contains('_') || w.Contains(':') || w.Contains('/') || w.Contains('\\')
                || (w.Any(char.IsUpper) && w.Any(char.IsLower) && w.Skip(1).Any(char.IsUpper))
                || w.All(char.IsDigit);
            if (identifierLike && !parts.Contains(w, StringComparer.OrdinalIgnoreCase)) parts.Add(w);
        }
        return parts;
    }

    // ── looking one up ──────────────────────────────────────────────────

    /// <summary>Case, whitespace and the direction and doubling of slashes do not matter: <c>\\dw-stage\teller\</c> is <c>//dw-stage/teller/</c> is <c>/dw-stage/teller/</c>.</summary>
    public static string Normalise(string s)
    {
        var t = s.ToLowerInvariant().Replace('\\', '/');
        t = Regex.Replace(t, @"/{2,}", "/");
        return Regex.Replace(t, @"\s+", " ");
    }

    /// <summary>True when the identifier is in the (already normalised) text: the whole of it, or — for a quoted span — every identifier-like part of it.</summary>
    public static bool IsIn(Identifier id, IReadOnlyList<string> normalisedTexts)
    {
        if (normalisedTexts.Any(t => t.Contains(Normalise(id.Text), StringComparison.Ordinal))) return true;
        if (id.Kind == IdentifierKind.Quoted && id.Parts.Count > 0)
            return id.Parts.All(p => normalisedTexts.Any(t => t.Contains(Normalise(p), StringComparison.Ordinal)));
        return false;
    }
}
