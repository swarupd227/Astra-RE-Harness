namespace Astra.Api.Ingest;

/// <summary>
/// Single source of truth for "what language is this file?".
///
/// Phase 5.2 ships Fortran F77 (default) and COBOL. Phase 7 adds
/// RPG / PL/I via the same surface — drop a new arm in
/// <see cref="FromExtension"/> + extend <see cref="AllowedExtensions"/>.
/// </summary>
public static class SourceLanguageDetector
{
    /// <summary>Spec-schema id (e.g. "fortran-f77", "cobol", "delphi").</summary>
    public const string Fortran = "fortran-f77";
    public const string Cobol = "cobol";
    public const string Delphi = "delphi";

    public static readonly string[] FortranExtensions =
    {
        ".f", ".f77", ".for", ".fpp", ".ftn",
        ".f90", ".f95", ".f03", ".f08", ".f15", ".f18",
    };

    public static readonly string[] CobolExtensions =
    {
        ".cob", ".cbl", ".cpy",
    };

    public static readonly string[] DelphiExtensions =
    {
        ".pas", ".dpr", ".dpk", ".inc",
    };

    /// <summary>Every extension we accept at ingest time.</summary>
    public static IEnumerable<string> AllowedExtensions =>
        FortranExtensions.Concat(CobolExtensions).Concat(DelphiExtensions);

    /// <summary>
    /// Map a file extension (case-insensitive, with leading dot) to a
    /// source-language id. Returns null for unknown extensions so the
    /// caller can reject ingestion or surface a warning.
    /// </summary>
    public static string? FromExtension(string extension)
    {
        var ext = extension?.ToLowerInvariant() ?? "";
        if (FortranExtensions.Contains(ext)) return Fortran;
        if (CobolExtensions.Contains(ext)) return Cobol;
        if (DelphiExtensions.Contains(ext)) return Delphi;
        return null;
    }

    /// <summary>Convenience: detect from a filename or relative path.</summary>
    public static string? FromFilename(string filename)
    {
        var ext = Path.GetExtension(filename ?? "");
        return string.IsNullOrEmpty(ext) ? null : FromExtension(ext);
    }
}
