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
    /// <summary>Spec-schema id (e.g. "fortran-f77", "cobol", "delphi", "cpp", "vb6").</summary>
    public const string Fortran = "fortran-f77";
    public const string Cobol = "cobol";
    public const string Delphi = "delphi";
    public const string Cpp = "cpp";
    public const string Vb6 = "vb6";

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

    public static readonly string[] CppExtensions =
    {
        ".cpp", ".cc", ".cxx", ".c++",
        ".hpp", ".hxx", ".h++", ".ipp",
        // ".h" is C/C++ ambiguous; we treat it as C++ because all seeded
        // corpora are C++. If a pure-C corpus arrives later, add a `.c`
        // branch and re-route .h based on the surrounding tree.
        ".h",
    };

    public static readonly string[] Vb6Extensions =
    {
        // Phase 10.0.a + 10.0.b — VB6 source extensions per ADR-035.
        // .bas standard module, .cls class module, .frm form (property
        // bag + code-behind), .ctl user control (same shape as .frm).
        ".bas", ".cls", ".frm", ".ctl",
    };

    /// <summary>Every extension we accept at ingest time.</summary>
    public static IEnumerable<string> AllowedExtensions =>
        FortranExtensions
            .Concat(CobolExtensions)
            .Concat(DelphiExtensions)
            .Concat(CppExtensions)
            .Concat(Vb6Extensions);

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
        if (CppExtensions.Contains(ext)) return Cpp;
        if (Vb6Extensions.Contains(ext)) return Vb6;
        return null;
    }

    /// <summary>Convenience: detect from a filename or relative path.</summary>
    public static string? FromFilename(string filename)
    {
        var ext = Path.GetExtension(filename ?? "");
        return string.IsNullOrEmpty(ext) ? null : FromExtension(ext);
    }

    /// <summary>
    /// Human-readable enumeration of every supported language with its top
    /// 2–3 extensions. Single source of truth for ingest error messages —
    /// adding a new language updates every "unsupported file type" string
    /// at once instead of going stale across IngestEndpoints / re-ingest.
    /// </summary>
    public static string SupportedLanguagesDescription() =>
        "Fortran (.f/.for/.f90), COBOL (.cob/.cbl/.cpy), Delphi (.pas/.dpr/.inc), " +
        "C++ (.cpp/.h/.hpp), or VB6 (.bas/.cls/.frm)";

    /// <summary>Just the language names — for "no source files found" copy.</summary>
    public static string SupportedLanguageNames() =>
        "Fortran, COBOL, Delphi, C++, or VB6";
}
