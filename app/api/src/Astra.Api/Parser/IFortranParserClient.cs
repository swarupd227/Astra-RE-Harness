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

    /// <summary>
    /// Liveness + identity probe. Returns the sidecar's service name and
    /// version (e.g. "astra-parser 0.2.0") so /health/ready can prove
    /// which build is actually serving — a TCP connect cannot tell a
    /// stale container from a fresh one.
    /// </summary>
    Task<ParserPing> PingAsync(CancellationToken ct = default);

    /// <summary>
    /// Parse a whole source version in one call. The sidecar puts the files
    /// on disk so cross-file references resolve — a C++ routine defined in
    /// a .cpp whose class lives in a header is invisible to a per-file
    /// parse. Results keep the input order. Against a sidecar that predates
    /// the RPC the client parses file by file and says so in the warnings.
    /// </summary>
    Task<CorpusParseOutcome> ParseCorpusAsync(
        IReadOnlyList<CorpusFile> files,
        CancellationToken ct = default,
        Action<int, int>? onProgress = null);

    /// <summary>
    /// Facts the files state for the entity graph (routes served, tables read and written, settings read,
    /// includes), one FactSet JSON per file, for the languages the sidecar reads facts from (PHP). A sidecar
    /// that predates the call, or a client that does not implement it, answers no facts and says so.
    /// </summary>
    Task<FactsOutcome> ExtractFactsAsync(IReadOnlyList<CorpusFile> files, CancellationToken ct = default) =>
        Task.FromResult(new FactsOutcome(
            files.Select(f => new FileFactsJson(f.Filename, "")).ToList(),
            new[] { "This parser client does not read facts." }));
}

/// <param name="FactsJson">The FactSet JSON for the file; empty when the sidecar reads no facts from it.</param>
public sealed record FileFactsJson(string Filename, string FactsJson);

public sealed record FactsOutcome(IReadOnlyList<FileFactsJson> Files, IReadOnlyList<string> Warnings);

public sealed record ParserPing(string Service, string Version);

public sealed record CorpusFile(string Filename, string Content);

public sealed record CorpusParseOutcome(
    IReadOnlyList<ParseOutcome> Results,
    IReadOnlyList<string> Warnings,
    bool CrossFileResolved);

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
