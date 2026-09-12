using System.Text.RegularExpressions;

namespace Astra.Api.Docs;

/// <summary>
/// Filler phrases the style guide bans. The list here is the enforced one;
/// <c>Llm/Prompts/docs/style-guide.md</c> mirrors it for the model. Matching
/// is case-insensitive on prose only — fenced code and inline code are
/// stripped first so an identifier such as <c>ROBUST</c> does not count.
/// </summary>
public static class DocBannedPhrases
{
    public static readonly IReadOnlyList<string> Phrases = new[]
    {
        "it is worth noting", "it's worth noting", "it should be noted", "it is important to note", "please note that",
        "in conclusion", "in summary", "to summarize", "overall,",
        "plays a crucial role", "plays a vital role", "plays a key role", "plays an important role",
        "robust", "seamless", "seamlessly",
        "leverage", "leverages", "leveraging",
        "delve", "delves", "delving",
        "cutting-edge", "state-of-the-art", "best-in-class",
        "a wide range of", "a variety of", "comprehensive", "holistic",
        "as mentioned above", "as previously mentioned", "as we can see",
        "essentially", "basically", "simply put", "in order to",
        "utilize", "utilizes", "utilizing", "utilized",
        "facilitate", "facilitates", "aforementioned",
        "we will", "let's", "let us",
    };

    private static readonly Lazy<(string Phrase, Regex Regex)[]> Patterns = new(() =>
        Phrases.Select(p => (p, new Regex(
            @"(?<![a-z0-9])" + Regex.Escape(p.ToLowerInvariant()) + @"(?![a-z0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant))).ToArray());

    public sealed record Hit(string Phrase, int Count);

    public static IReadOnlyList<Hit> Find(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<Hit>();
        var text = DocMarkdown.StripCode(markdown).ToLowerInvariant().Replace('’', '\'');
        var hits = new List<Hit>();
        foreach (var (phrase, regex) in Patterns.Value)
        {
            var n = regex.Matches(text).Count;
            if (n > 0) hits.Add(new Hit(phrase, n));
        }
        return hits;
    }
}
