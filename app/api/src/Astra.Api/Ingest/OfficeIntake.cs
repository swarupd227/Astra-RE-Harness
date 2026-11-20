using System.Text.RegularExpressions;
using Astra.Api.Office;

namespace Astra.Api.Ingest;

/// <summary>
/// An Office document with macros (<c>.xlsm</c>, <c>.xls</c>, <c>.docm</c> …) is not source code, but the
/// VBA inside it is. At intake each such document becomes its modules, read statically by
/// <see cref="OfficeVba"/> and named under the document — <c>Quotes/Rating.xlsm/Pricing.bas</c> — so
/// every routine says which workbook it came from, and from there they are parsed like any other VB
/// source. The document itself is not kept with the code; attach it as evidence to cite its sheets.
/// Nothing in the document is run.
/// </summary>
public static class OfficeIntake
{
    /// <summary>A workbook is read up to the VBA reader's own limit, above the per-file limit for source text.</summary>
    public const long MaxDocumentBytes = OfficeVba.MaxInputBytes;

    public static bool IsOfficeDocument(string path) =>
        OfficeVba.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// The VBA modules of one document as source files. A document module (<c>ThisWorkbook</c>, a sheet)
    /// that holds no code is left out — every workbook has one per sheet and they would only add empty
    /// files. What was read, and anything that could not be, goes on <paramref name="tally"/>.
    /// </summary>
    public static List<IngestPipeline.IncomingFile> Expand(string relativePath, byte[] bytes, IntakeTally tally)
    {
        var result = new List<IngestPipeline.IncomingFile>();
        var name = Path.GetFileName(relativePath);
        // A spreadsheet is kept whole as well, so its sheets, inputs and buttons can be cited.
        if (Artefacts.ArtefactKinds.WorkbookExtensions.Contains(Path.GetExtension(relativePath).ToLowerInvariant()))
            tally.Keep(new Artefacts.IncomingArtefact(relativePath, bytes, Artefacts.ArtefactKinds.Workbook));
        var read = OfficeVba.Read(bytes, name);
        foreach (var w in read.Warnings) tally.Note(w);
        if (!read.HasVba)
        {
            tally.Note($"{relativePath} has no VBA project, so it adds no code.");
            return result;
        }

        var empty = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in read.Project!.Modules)
        {
            if (!HasCode(m.Source)) { empty++; continue; }
            var file = SafeFileName(m.ExportFileName);
            for (var n = 2; !used.Add(file); n++) file = $"{Path.GetFileNameWithoutExtension(SafeFileName(m.ExportFileName))}_{n}{Path.GetExtension(file)}";
            result.Add(new IngestPipeline.IncomingFile($"{relativePath}/{file}", m.Source));
        }

        var kinds = string.Join(", ", result
            .GroupBy(f => Path.GetExtension(f.RelativePath))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Count()} {g.Key switch { ".bas" => "standard", ".frm" => "form", _ => "class or document" }}"));
        tally.Note($"Read {Count(result.Count, "VBA module")} from {relativePath}" +
                   (result.Count > 0 ? $" ({kinds})" : "") +
                   (empty > 0 ? $"; {Count(empty, "module")} with no code left out." : "."));
        return result;
    }

    /// <summary>True when a module has more than its attributes and <c>Option</c> lines.</summary>
    public static bool HasCode(string source)
    {
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('\'')) continue;
            if (line.StartsWith("Attribute ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Option ", StringComparison.OrdinalIgnoreCase)) continue;
            if (line is "VERSION 1.0 CLASS" or "BEGIN" or "END" || line.StartsWith("MultiUse", StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }
        return false;
    }

    // Module names are VBA identifiers, but the file name becomes a storage key: keep it to a safe set.
    private static string SafeFileName(string name) => Regex.Replace(name, @"[^\p{L}\p{Nd}_.\-]", "_");

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
