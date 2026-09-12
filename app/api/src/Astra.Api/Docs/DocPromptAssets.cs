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

    private DocPromptAssets(
        string styleGuide, string criticPrompt, string revisePrompt,
        IReadOnlyDictionary<string, string> exemplars)
    {
        StyleGuide = styleGuide;
        CriticPrompt = criticPrompt;
        RevisePrompt = revisePrompt;
        _exemplars = exemplars;
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
        return new DocPromptAssets(style, critic, revise, exemplars);
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
    /// one exists for the kind), then the kind prompt. The writer marks the
    /// last block with cache_control so the whole prefix is cached.
    /// </summary>
    public IReadOnlyList<string> SystemBlocks(string kindPrompt, string? exemplarKind)
    {
        var blocks = new List<string>(3) { StyleGuide };
        if (exemplarKind is not null && Exemplar(exemplarKind) is { } exemplar)
            blocks.Add(exemplar);
        blocks.Add(kindPrompt);
        return blocks;
    }

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
