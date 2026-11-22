namespace Astra.Api.Knowledge;

/// <summary>
/// Options for knowledge capture, bound from the <c>Knowledge</c> configuration section. Which provider answers is
/// not decided here: every call goes through the documentation writer seam (<c>Docs:Generator:Provider</c>), so a
/// mock or recorded run needs no key and a live run uses the same key and rate limiter as the other pipelines.
/// </summary>
public sealed class KnowledgeOptions
{
    /// <summary>Model that writes statements. Empty means the standard documentation tier (<c>Docs:Generator:SonnetModel</c>).</summary>
    public string? WriterModel { get; set; }

    /// <summary>Model that checks whether the cited evidence says what a statement says (a small model; the writer never grades its own work).</summary>
    public string SupportModel { get; set; } = "claude-haiku-4-5-20251001";

    public int WriterMaxTokens { get; set; } = 8192;

    /// <summary>The most pieces of evidence handed to a writer for one slot.</summary>
    public int MaxEvidenceChunks { get; set; } = 40;

    /// <summary>Characters of each piece of evidence shown to a writer.</summary>
    public int MaxChunkChars { get; set; } = 3000;

    /// <summary>Attempts to write one slot: the first, plus one retry that rewrites only what the gate threw away.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Model that cuts narrative evidence into claims. Empty means the standard documentation tier.</summary>
    public string? ClaimsModel { get; set; }

    /// <summary>The most pieces of narrative evidence read for claims in one run (the rest are said so, not silently skipped).</summary>
    public int MaxNarrativeChunks { get; set; } = 300;

    /// <summary>Tickets that mention one of these, and name nothing in the system, are routine requests and stay out of known errors.</summary>
    public List<string> RoutineKeywords { get; set; } = new() { "password", "printer", "access request", "new starter", "leaver" };

    /// <summary>Incidents that make a cluster worth a known-error article.</summary>
    public int MinClusterSize { get; set; } = 3;
}
