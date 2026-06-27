using System.Text.Json;

namespace Astra.Api.Persistence.Entities;

/// <summary>
/// One unit of transition documentation (per ADR-038). Lifecycle parallels
/// Spec: DRAFT after generation; IN_REVIEW once an SME opens it; SIGNED
/// after sign-off; STALE when source citations move after sign; SUPERSEDED
/// when a newer SourceVersion has its own current section row.
/// </summary>
public sealed class DocSection
{
    public Guid Id { get; set; }

    /// <summary>Owning corpus. All section kinds belong to exactly one corpus.</summary>
    public Guid CorpusId { get; set; }

    /// <summary>
    /// SourceVersion the section was generated against. Drift detection
    /// compares this against the corpus's current SourceVersion to decide
    /// whether SIGNED sections need re-confirmation.
    /// </summary>
    public Guid SourceVersionId { get; set; }

    /// <summary>
    /// Section kind from doc.schema.json sectionKinds[].id: overview,
    /// entry-point, module, routine-summary, data-dictionary, glossary,
    /// interface, business-rule, diagram.
    /// </summary>
    public string SectionKind { get; set; } = "";

    /// <summary>
    /// Scope this section attaches to. corpus | module | subroutine
    /// (mirrors sectionKinds[].scope). Drives whether SubroutineId /
    /// ModuleName is required and which review surface lists it.
    /// </summary>
    public string Scope { get; set; } = "corpus";

    /// <summary>
    /// When Scope = subroutine, the subroutine this section is about.
    /// Null for corpus-scope and module-scope sections.
    /// </summary>
    public Guid? SubroutineId { get; set; }

    /// <summary>
    /// When Scope = module, the module identifier as the parser emits it
    /// (Fortran MODULE name, COBOL program-id, VB6 .bas/.cls name, etc.).
    /// Null for corpus-scope and subroutine-scope sections.
    /// </summary>
    public string? ModuleName { get; set; }

    /// <summary>DRAFT | IN_REVIEW | SIGNED | STALE | SUPERSEDED.</summary>
    public string State { get; set; } = "DRAFT";

    /// <summary>
    /// Canonical section payload conforming to doc.schema.json. Shape
    /// depends on SectionKind (overview vs glossary vs diagram, etc.).
    /// </summary>
    public JsonDocument PayloadJson { get; set; } = JsonDocument.Parse("{}");

    /// <summary>
    /// Render of PayloadJson as the markdown that ships into the
    /// mkdocs site + the git mirror. Cached so the site generator
    /// doesn't re-render per request. Re-rendered when PayloadJson moves.
    /// </summary>
    public string? RenderedMarkdown { get; set; }

    /// <summary>
    /// LLM generation provenance. Null for hand-authored sections
    /// (an SME can author a section from scratch — same lifecycle).
    /// </summary>
    public Guid? LlmCallId { get; set; }

    /// <summary>
    /// Batch generation run this section came from. Null for hand-authored.
    /// </summary>
    public Guid? GenerationRunId { get; set; }

    /// <summary>
    /// Signing FK. Set when State = SIGNED.
    /// </summary>
    public Guid? SignatureId { get; set; }

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When a newer SourceVersion has its own current section row that
    /// supersedes this one, points at the successor. Carries the audit
    /// chain ('what did this descend from?').
    /// </summary>
    public Guid? PreviousSectionId { get; set; }

    public Corpus? Corpus { get; set; }
    public Subroutine? Subroutine { get; set; }
    public LlmCall? LlmCall { get; set; }
}
