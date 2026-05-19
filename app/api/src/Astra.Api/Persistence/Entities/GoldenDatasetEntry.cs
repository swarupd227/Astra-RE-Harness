namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Phase 6.0 — One curated "golden dataset" entry. A trap-laden snippet
/// of legacy code (Fortran or COBOL) paired with the claims the extract
/// pipeline is *expected* to produce when run against it. Used as a
/// regression measurement instrument: when prompts/models change, the
/// scorer replays every entry and surfaces any entry whose claim
/// coverage dropped.
///
/// Seeded from YAML files under <c>Llm/GoldenDataset/&lt;schemaId&gt;/&lt;entryId&gt;.yml</c>
/// (git-versioned, reproducible). The seeder is idempotent — re-seeding
/// adds new entries but does NOT overwrite existing rows, so Admin edits
/// through the UI persist across restarts.
/// </summary>
public sealed class GoldenDatasetEntry
{
    public Guid Id { get; set; }

    /// <summary>Human-readable id from the YAML file, e.g. "fortran-implicit-typing"
    /// or "cobol-rounded-off-by-one". Unique across the dataset.</summary>
    public string EntryId { get; set; } = "";

    /// <summary>"fortran-f77" or "cobol" — drives which extract prompt the scorer
    /// runs against this entry.</summary>
    public string SchemaId { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>Short trap category for filtering ("numeric/rounded",
    /// "control-flow/perform-thru", "io/file-status", …).</summary>
    public string TrapCategory { get; set; } = "";

    /// <summary>"easy" | "medium" | "hard" — drives demo storytelling
    /// (we lead with hard cases) and surfaces drift faster on the easy
    /// ones if a prompt regresses.</summary>
    public string Difficulty { get; set; } = "medium";

    /// <summary>Logical path the snippet is supposed to belong to
    /// (e.g. "fortran/snippets/implicit_typing.f"). Used only for
    /// display + audit; the source is stored inline below.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>The actual snippet content. Inline-stored so a YAML
    /// edit + re-seed makes the snippet self-contained for the scorer.</summary>
    public string SourceContent { get; set; } = "";

    /// <summary>"&lt;start&gt;-&lt;end&gt;" — citation hint that the extract
    /// pipeline can echo back.</summary>
    public string SourceLines { get; set; } = "";

    /// <summary>JSONB blob: array of expected-claim objects with shape
    /// <c>{kind: "invariant"|"section_contract"|"io_side_effect"|"edge_case"|"open_question",
    /// id: "INV-1", pattern: "regex"}</c>. The scorer iterates these
    /// and for each, scans the extract output for a claim of matching
    /// kind whose text/citation matches the pattern.</summary>
    public string ExpectedClaimsJson { get; set; } = "[]";

    /// <summary>Optional JSONB blob: array of canonical input vectors for
    /// the runtime-equivalence subset. Shape is entry-specific; the
    /// behavioural-equivalence harness consumes this. Empty array for
    /// extract-only entries.</summary>
    public string CanonicalInputsJson { get; set; } = "[]";

    /// <summary>Free-form admin notes, including why this trap matters
    /// and how it was verified. Shown in the demo UI as the "story".</summary>
    public string Notes { get; set; } = "";

    /// <summary>"seeded" (loaded from YAML, untouched) | "approved" (Admin
    /// verified the expected claims) | "draft" (Admin in-progress) |
    /// "deprecated" (kept for history; excluded from regression score).</summary>
    public string Status { get; set; } = "seeded";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// One execution of the scorer against one entry. Persisted so the
/// "score over time" chart can render without re-running everything.
/// </summary>
public sealed class GoldenDatasetRun
{
    public Guid Id { get; set; }
    public Guid EntryId { get; set; }

    /// <summary>The id of the LlmCall row that produced the extract claims
    /// scored here — gives the report card a click-through to the raw
    /// model output for the run that produced this score.</summary>
    public Guid? LlmCallId { get; set; }

    /// <summary>Prompt id this score was measured against ("fortran-extract",
    /// "cobol-extract"). Pinned at run time so we can chart score-per-prompt.</summary>
    public string PromptId { get; set; } = "";

    /// <summary>Prompt version ("v0.1") — combined with PromptId, this is the
    /// natural x-axis of the regression chart.</summary>
    public string PromptVersion { get; set; } = "";

    /// <summary>Model name reported by the provider ("claude-sonnet-4-5-..." or
    /// "mock"). Same prompt+version can score differently across models.</summary>
    public string ModelName { get; set; } = "";

    /// <summary>How many of the entry's expected claims were matched by the
    /// extract output.</summary>
    public int Matched { get; set; }

    /// <summary>Total expected claims for the entry at the time of the run.</summary>
    public int Total { get; set; }

    /// <summary>0.0–1.0 score. Computed as Matched / Total; we persist it so
    /// historical charts don't depend on the entry's current claim list.</summary>
    public double Score { get; set; }

    /// <summary>JSONB blob: per-claim {claim_id, kind, pattern, matched: bool,
    /// matched_against: "<claim text or null>"}. Lets the UI render the
    /// row-by-row diff without re-scoring.</summary>
    public string DetailJson { get; set; } = "[]";

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string? TriggeredBy { get; set; }
}
