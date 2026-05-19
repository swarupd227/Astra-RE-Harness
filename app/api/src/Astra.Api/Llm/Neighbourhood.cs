namespace Astra.Api.Llm;

/// <summary>
/// Phase 7.0 — Structured cross-routine context attached to an
/// <see cref="ExtractionRequest"/>. Built by
/// <see cref="NeighbourhoodBuilder"/> from the parser's call-graph +
/// COMMON-block metadata that already lives on every
/// <see cref="Persistence.Entities.Subroutine"/> row, so the LLM can see
/// dependencies WITHOUT us stuffing the whole repo into a 200k context
/// or running blind RAG. The information is deterministic and free
/// (we already paid for it at parse time).
/// </summary>
public sealed record Neighbourhood(
    IReadOnlyList<RoutineRef> Callees,
    IReadOnlyList<RoutineRef> Callers,
    IReadOnlyList<CommonBlockRef> CommonBlocks,
    IReadOnlyList<RoutineRef> SiblingsInFile)
{
    public static Neighbourhood Empty { get; } = new(
        Array.Empty<RoutineRef>(),
        Array.Empty<RoutineRef>(),
        Array.Empty<CommonBlockRef>(),
        Array.Empty<RoutineRef>());

    public bool IsEmpty =>
        Callees.Count == 0
        && Callers.Count == 0
        && CommonBlocks.Count == 0
        && SiblingsInFile.Count == 0;
}

/// <summary>
/// One referenced routine in the neighbourhood. We give the LLM the
/// signature (declaration line, free to read) and a short summary —
/// either drawn from the routine's existing extracted spec (if it has
/// already been extracted) or left null. We deliberately do NOT pass
/// the callee's full source: that's what "stuff the repo" would do.
/// </summary>
public sealed record RoutineRef(
    string Name,
    string? Signature,
    string? SourcePath,
    /// <summary>"in_corpus" if we resolved this name to a Subroutine row;
    /// "external" if no match (likely a system call or intrinsic).</summary>
    string Resolution,
    /// <summary>Short prose summary of the callee's spec when one
    /// already exists; otherwise null. Pulled from Spec.summary.</summary>
    string? SpecSummary,
    /// <summary>Side-effect list excerpted from the callee's spec
    /// (file paths, REST endpoints, etc.) — what callers should know
    /// about. Empty when no spec is available.</summary>
    IReadOnlyList<string> KnownSideEffects);

/// <summary>
/// One COMMON block referenced by the current routine. We name the
/// block, point at the routine that declared it FIRST in the corpus
/// (the canonical layout source), and include that declaration's
/// signature so the model can read the variable order.
/// </summary>
public sealed record CommonBlockRef(
    string Name,
    string? DeclaringRoutine,
    string? DeclaringRoutineSignature,
    string? DeclaringRoutineSourcePath);
