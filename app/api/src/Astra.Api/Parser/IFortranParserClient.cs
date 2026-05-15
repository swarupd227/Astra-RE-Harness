namespace Astra.Api.Parser;

/// <summary>
/// Façade over the parser-sidecar gRPC client. Lets endpoints + the ingest
/// pipeline call the parser without depending on the generated proto types
/// directly, which keeps the call site readable and the surface narrow.
/// </summary>
public interface IFortranParserClient
{
    /// <summary>
    /// Parse a single Fortran source file. Always returns a result —
    /// parse errors surface as <see cref="ParseOutcome.Warnings"/> with
    /// <see cref="ParseOutcome.Subroutines"/> empty, so a malformed file
    /// can't break a multi-file corpus ingest.
    /// </summary>
    Task<ParseOutcome> ParseAsync(string filename, string content, string? form = null, CancellationToken ct = default);
}

public sealed record ParseOutcome(
    string Filename,
    int LineCount,
    IReadOnlyList<ParsedSubroutine> Subroutines,
    IReadOnlyList<string> Warnings);

public sealed record ParsedSubroutine(
    string Name,
    string Signature,
    int LineStart,
    int LineEnd,
    IReadOnlyList<string> CommonBlockRefs,
    IReadOnlyList<string> CalledSubroutines);
