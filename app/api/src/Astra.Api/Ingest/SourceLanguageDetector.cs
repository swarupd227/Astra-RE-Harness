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
    public const string Csharp = "csharp";
    public const string Vbnet = "vbnet";
    public const string Openedge = "openedge";
    public const string Java = "java";
    public const string Php = "php";

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

    public static readonly string[] CsharpExtensions =
    {
        // Phase 12.0 — C# source extensions.
        // .cs is the primary unit; .csx is C# script (used in some legacy
        // build-automation files). .cshtml (Razor MVC views) is excluded —
        // the parser extracts code-behind logic, not the template markup.
        ".cs", ".csx",
    };

    public static readonly string[] VbnetExtensions =
    {
        // Phase 12.0 — VB.NET source extensions.
        // .vb covers all VB.NET source; .vbhtml (Razor) excluded for the
        // same reason as .cshtml above.
        ".vb",
    };

    public static readonly string[] OpenedgeExtensions =
    {
        // Phase 13.0 — Progress OpenEdge ABL source extensions.
        // .p procedure/program, .w SmartWindow, .i include. OO `.cls` is
        // NOT listed — it collides with the VB6 class-module extension and
        // needs content-based disambiguation (deferred). Legacy Progress
        // apps are overwhelmingly procedural .p/.w.
        ".p", ".w", ".i",
    };

    public static readonly string[] JavaExtensions =
    {
        // Phase 14.0 — Java source. .java covers all Java. Kotlin/Scala/Groovy
        // are separate JVM languages and excluded.
        ".java",
    };

    public static readonly string[] PhpExtensions =
    {
        // Phase 15.0 — PHP source (Magento / PHP 8). .php covers code files;
        // .phtml (Magento view templates) is excluded — the parser reads code,
        // not template markup.
        ".php",
    };

    /// <summary>Every extension we accept at ingest time.</summary>
    public static IEnumerable<string> AllowedExtensions =>
        FortranExtensions
            .Concat(CobolExtensions)
            .Concat(DelphiExtensions)
            .Concat(CppExtensions)
            .Concat(Vb6Extensions)
            .Concat(CsharpExtensions)
            .Concat(VbnetExtensions)
            .Concat(OpenedgeExtensions)
            .Concat(JavaExtensions)
            .Concat(PhpExtensions);

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
        if (CsharpExtensions.Contains(ext)) return Csharp;
        if (VbnetExtensions.Contains(ext)) return Vbnet;
        if (OpenedgeExtensions.Contains(ext)) return Openedge;
        if (JavaExtensions.Contains(ext)) return Java;
        if (PhpExtensions.Contains(ext)) return Php;
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
        "C++ (.cpp/.h/.hpp), VB6 (.bas/.cls/.frm), C# (.cs), VB.NET (.vb), " +
        "OpenEdge ABL (.p/.w/.i), Java (.java), or PHP (.php)";

    /// <summary>Just the language names — for "no source files found" copy.</summary>
    public static string SupportedLanguageNames() =>
        "Fortran, COBOL, Delphi, C++, VB6, C#, VB.NET, OpenEdge ABL, Java, or PHP";
}
