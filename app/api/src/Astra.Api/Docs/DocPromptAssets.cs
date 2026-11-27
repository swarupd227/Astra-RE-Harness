namespace Astra.Api.Docs;

/// <summary>
/// Loads the shared documentation prompt assets under
/// <c>Llm/Prompts/docs/</c>: the style guide (the cached system block every
/// writer, critic and reviser prompt starts with), the critic and revise
/// prompts, and one exemplar per document kind. Resolved the same way the
/// docs pipelines resolve <c>doc-*.md</c> — next to the binary when
/// published, under <c>src/</c> in dev-watch mode — and read once at startup.
/// </summary>
public sealed class DocPromptAssets
{
    public string StyleGuide { get; }
    public string CriticPrompt { get; }
    public string RevisePrompt { get; }

    private readonly IReadOnlyDictionary<string, string> _exemplars;
    private readonly IReadOnlyDictionary<string, string> _languages;

    private DocPromptAssets(
        string styleGuide, string criticPrompt, string revisePrompt,
        IReadOnlyDictionary<string, string> exemplars, IReadOnlyDictionary<string, string>? languages = null)
    {
        StyleGuide = styleGuide;
        CriticPrompt = criticPrompt;
        RevisePrompt = revisePrompt;
        _exemplars = exemplars;
        _languages = languages ?? new Dictionary<string, string>();
    }

    public static DocPromptAssets Load(string contentRoot)
    {
        var style = ReadPrompt(contentRoot, "docs", "style-guide.md");
        var critic = ReadPrompt(contentRoot, "docs", "critic.md");
        var revise = ReadPrompt(contentRoot, "docs", "revise.md");

        var exemplars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in new[] { "module", "overview", "business-rules", "routine-summary" })
        {
            var path = TryResolvePath(contentRoot, "docs", "exemplars", kind + ".md");
            if (path is not null)
                exemplars[kind] = StripFrontmatter(File.ReadAllText(path));
        }
        var guidancePath = TryResolvePath(contentRoot, "docs", "language-guidance.md");
        var languages = guidancePath is null
            ? new Dictionary<string, string>()
            : ParseLanguageGuidance(StripFrontmatter(File.ReadAllText(guidancePath)));
        return new DocPromptAssets(style, critic, revise, exemplars, languages);
    }

    /// <summary>
    /// The language-guidance file as language id → block. Each <c>## id, id</c> heading opens a block that serves
    /// every id it lists; <c>default</c> serves languages without their own.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseLanguageGuidance(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[]? ids = null;
        var body = new System.Text.StringBuilder();
        void Flush()
        {
            if (ids is null) return;
            var block = "# Source language\n\n" + body.ToString().Trim() + "\n";
            foreach (var id in ids) map[id] = block;
        }
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                ids = raw[3..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                body.Clear();
            }
            else if (ids is not null) body.AppendLine(raw);
        }
        Flush();
        return map;
    }

    /// <summary>The language-guidance file under a content root, parsed; empty when it does not ship.</summary>
    public static IReadOnlyDictionary<string, string> LoadLanguageGuidance(string contentRoot) =>
        TryResolvePath(contentRoot, "docs", "language-guidance.md") is { } p
            ? ParseLanguageGuidance(StripFrontmatter(File.ReadAllText(p)))
            : new Dictionary<string, string>();

    /// <summary>The guidance block for a source language (its own, else <c>default</c>), or null when none ships.</summary>
    public string? LanguageBlock(string? language) => LanguageBlock(_languages, language);

    /// <summary><see cref="LanguageBlock(string?)"/> over a given guidance map.</summary>
    public static string? LanguageBlock(IReadOnlyDictionary<string, string> _languages, string? language)
    {
        if (_languages.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(language) && _languages.TryGetValue(language.Trim(), out var own)) return own;
        if (!string.IsNullOrWhiteSpace(language))
        {
            // fortran-f77-like ids: try the family before the dash (php-8 → php).
            var family = language.Split('-', 2)[0];
            if (_languages.TryGetValue(family, out var fam)) return fam;
        }
        return _languages.TryGetValue("default", out var d)
            ? d.Replace("as named in the input", string.IsNullOrWhiteSpace(language) ? "not recorded" : language)
            : null;
    }

    /// <summary>Exemplar for a section kind, or null when none ships.
    /// "business-rule" (the section kind) maps onto the business-rules exemplar.</summary>
    public string? Exemplar(string kind)
    {
        var key = string.Equals(kind, "business-rule", StringComparison.OrdinalIgnoreCase) ? "business-rules" : kind;
        return _exemplars.TryGetValue(key, out var text) ? text : null;
    }

    /// <summary>
    /// System blocks for a writer call, in order: style guide, exemplar (when
    /// one exists for the kind), the kind prompt, then the source language's
    /// guidance (when a language is given). The writer marks the last block with
    /// cache_control so the whole prefix is cached — once per language.
    /// </summary>
    public IReadOnlyList<string> SystemBlocks(string kindPrompt, string? exemplarKind, string? language = null)
    {
        var blocks = new List<string>(4) { StyleGuide };
        if (exemplarKind is not null && Exemplar(exemplarKind) is { } exemplar)
            blocks.Add(exemplar);
        blocks.Add(kindPrompt);
        if (language is not null && LanguageBlock(language) is { } guidance)
            blocks.Add(guidance);
        return blocks;
    }

    /// <summary>A single system prompt (pipelines that send one string) with the language's guidance appended.</summary>
    public string WithLanguage(string systemPrompt, string? language) =>
        language is not null && LanguageBlock(language) is { } guidance ? systemPrompt.TrimEnd() + "\n\n" + guidance : systemPrompt;

    /// <summary>Read a prompt file under Llm/Prompts and strip its YAML frontmatter.</summary>
    public static string ReadPrompt(string contentRoot, params string[] segments) =>
        StripFrontmatter(File.ReadAllText(ResolvePath(contentRoot, segments)));

    public static string ResolvePath(string contentRoot, params string[] segments) =>
        TryResolvePath(contentRoot, segments)
        ?? throw new FileNotFoundException(
            $"Prompt asset {Path.Combine(segments)} not found under {Path.Combine(contentRoot, "Llm", "Prompts")}.");

    public static string? TryResolvePath(string contentRoot, params string[] segments)
    {
        var relative = Path.Combine(segments);
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Llm", "Prompts", relative),
            Path.Combine(contentRoot, "..", "..", "Llm", "Prompts", relative),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        return null;
    }

    public static string StripFrontmatter(string md)
    {
        if (!md.StartsWith("---")) return md;
        var endIdx = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIdx < 0) return md;
        var after = endIdx + 4;
        while (after < md.Length && (md[after] == '\n' || md[after] == '\r')) after++;
        return md[after..];
    }
}
