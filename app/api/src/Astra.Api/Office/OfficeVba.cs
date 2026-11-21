using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Astra.Api.Office;

public enum VbaModuleKind { Standard, Class, Document, Form }

/// <param name="FormHeader">For a UserForm, the designer's text header (<c>\x03VBFrame</c>):
/// the form's own properties. Its controls live in a binary stream not read yet.</param>
public sealed record VbaModule(string Name, VbaModuleKind Kind, string Source, string? FormHeader)
{
    /// <summary>For a UserForm, its controls as the designer stores them; null when they could not be read.</summary>
    public UserFormLayout? Layout { get; init; }

    /// <summary>The name the VBA editor would export it under.</summary>
    public string ExportFileName => Name + Kind switch
    {
        VbaModuleKind.Standard => ".bas",
        VbaModuleKind.Form => ".frm",
        _ => ".cls",
    };
}

public sealed record VbaProject(string? Name, int CodePage, IReadOnlyList<VbaModule> Modules);

/// <param name="Container">"ooxml" (a zip such as .xlsm/.docm), "ole" (.xls/.doc, or a bare vbaProject.bin) or "unknown".</param>
/// <param name="Project">Null when the file holds no VBA project.</param>
/// <param name="Warnings">Plain sentences about what could not be read.</param>
public sealed record OfficeVbaResult(string Container, VbaProject? Project, IReadOnlyList<string> Warnings)
{
    public bool HasVba => Project is { Modules.Count: > 0 };
}

/// <summary>
/// Gets the VBA source out of an Office document without running anything:
/// open the container, find the VBA project's compound file, read its
/// <c>dir</c> stream for the module list ([MS-OVBA] 2.3.4.2) and decompress each
/// module's source. Never throws — a file it cannot read comes back with a
/// warning. Macros are never executed and no Office installation is involved.
/// </summary>
public static class OfficeVba
{
    /// <summary>Uploads bigger than this are not opened.</summary>
    public const long MaxInputBytes = 100L << 20;
    /// <summary>A zip entry claiming more than this is not inflated (zip bomb guard).</summary>
    public const long MaxProjectBytes = 64L << 20;

    public static readonly string[] Extensions =
    {
        ".xlsm", ".xltm", ".xlam", ".xls", ".xla", ".xlt",
        ".docm", ".dotm", ".doc", ".dot",
        ".pptm", ".potm", ".ppam",
    };

    static OfficeVba() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static OfficeVbaResult Read(byte[] file, string fileName)
    {
        var warnings = new List<string>();
        if (file.LongLength > MaxInputBytes)
            return new("unknown", null, new[] { $"{fileName} is larger than {MaxInputBytes >> 20} MB and was not opened." });
        try
        {
            if (file.Length >= 4 && file[0] == 0x50 && file[1] == 0x4B)
                return ReadOoxml(file, fileName, warnings);
            if (CompoundFile.LooksLikeCompoundFile(file))
                return new("ole", ReadProject(new CompoundFile(file), fileName, warnings), warnings);
            return new("unknown", null, new[] { $"{fileName} is neither an Office zip nor an OLE compound file." });
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or ArgumentOutOfRangeException or IOException)
        {
            warnings.Add($"{fileName} could not be read: {ex.Message}.");
            return new("unknown", null, warnings);
        }
    }

    private static OfficeVbaResult ReadOoxml(byte[] file, string fileName, List<string> warnings)
    {
        using var zip = new ZipArchive(new MemoryStream(file), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new("ooxml", null, warnings);
        if (entry.Length > MaxProjectBytes)
        {
            warnings.Add($"{fileName}: the VBA project claims {entry.Length:N0} bytes, over the {MaxProjectBytes >> 20} MB limit; not read.");
            return new("ooxml", null, warnings);
        }
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new("ooxml", ReadProject(new CompoundFile(ms.ToArray()), fileName, warnings), warnings);
    }

    /// <summary>The VBA storage sits at a different path in each host's compound file.</summary>
    private static readonly string[][] VbaStoragePaths =
    {
        new[] { "VBA" },                          // vbaProject.bin inside an .xlsm/.docm
        new[] { "_VBA_PROJECT_CUR", "VBA" },      // legacy Excel .xls
        new[] { "Macros", "VBA" },                // legacy Word .doc
        new[] { "_VBA_PROJECT", "VBA" },          // legacy PowerPoint
    };

    private static VbaProject? ReadProject(CompoundFile cf, string fileName, List<string> warnings)
    {
        CompoundFile.Entry? vba = null;
        string[]? at = null;
        foreach (var p in VbaStoragePaths)
        {
            vba = cf.Find(p);
            if (vba is { IsStorage: true }) { at = p; break; }
        }
        if (vba is null || at is null) return null;

        var dirEntry = cf.Child(vba, "dir");
        if (dirEntry is null) { warnings.Add($"{fileName}: VBA storage has no dir stream."); return null; }
        var dir = VbaCompression.Decompress(cf.ReadStream(dirEntry));
        var (codePage, projectName, declared) = ParseDir(dir, warnings);
        var enc = EncodingFor(codePage, warnings);

        // The PROJECT stream (a sibling of VBA) says which modules are documents, classes and forms.
        var parent = at.Length == 1 ? cf.Root : cf.Find(at[..^1])!;
        var kinds = ReadProjectKinds(cf, parent, enc);

        var modules = new List<VbaModule>();
        foreach (var m in declared)
        {
            var name = m.UnicodeName ?? m.Name ?? "";
            var streamName = m.UnicodeStreamName ?? m.StreamName ?? name;
            var stream = cf.Child(vba, streamName);
            if (stream is null) { warnings.Add($"{fileName}: module {name} is listed but its stream is missing."); continue; }
            var bytes = cf.ReadStream(stream);
            if (m.Offset >= bytes.Length)
            {
                warnings.Add($"{fileName}: module {name} has no source text (compiled code only) — it cannot be read.");
                continue;
            }
            string source;
            try { source = enc.GetString(VbaCompression.Decompress(bytes, (int)m.Offset)); }
            catch (InvalidDataException ex)
            {
                warnings.Add($"{fileName}: module {name} source could not be decompressed ({ex.Message}).");
                continue;
            }
            var kind = kinds.TryGetValue(name, out var k) ? k : m.Procedural ? VbaModuleKind.Standard : VbaModuleKind.Class;
            string? header = null;
            UserFormLayout? layout = null;
            if (kind == VbaModuleKind.Form)
            {
                var designer = cf.Child(parent, name);
                var frame = designer is null ? null : cf.Child(designer, "\u0003VBFrame");
                if (frame is not null) header = enc.GetString(cf.ReadStream(frame));
                if (designer is { IsStorage: true }) layout = UserForms.Read(cf, designer, name, warnings);
            }
            modules.Add(new VbaModule(name, kind, source, header) { Layout = layout });
        }
        return new VbaProject(projectName, codePage, modules);
    }

    private sealed class DeclaredModule
    {
        public string? Name, UnicodeName, StreamName, UnicodeStreamName;
        public uint Offset;
        public bool Procedural;
    }

    /// <summary>
    /// Walks the dir stream's records (id, size, data). One record breaks the
    /// pattern: PROJECTVERSION (0x0009) says size 4 but carries 6 bytes.
    /// </summary>
    private static (int CodePage, string? ProjectName, List<DeclaredModule> Modules) ParseDir(byte[] d, List<string> warnings)
    {
        var codePage = 1252;
        string? projectName = null;
        var modules = new List<DeclaredModule>();
        DeclaredModule? cur = null;
        var latin = Encoding.Latin1;
        var p = 0;
        while (p + 6 <= d.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p, 2));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 2, 4));
            p += 6;
            if (id == 0x0009) { p += 6; continue; }
            if (size > d.Length - p) { warnings.Add("the project's module list is cut off; later modules may be missing."); break; }
            var data = d.AsSpan(p, (int)size);
            p += (int)size;
            switch (id)
            {
                case 0x0003 when size >= 2: codePage = BinaryPrimitives.ReadUInt16LittleEndian(data); break;
                case 0x0004: projectName = latin.GetString(data); break;
                case 0x0019: cur = new DeclaredModule { Name = latin.GetString(data) }; break;
                case 0x0047 when cur is not null: cur.UnicodeName = Encoding.Unicode.GetString(data); break;
                case 0x001A when cur is not null: cur.StreamName = latin.GetString(data); break;
                case 0x0032 when cur is not null: cur.UnicodeStreamName = Encoding.Unicode.GetString(data); break;
                case 0x0031 when cur is not null && size >= 4: cur.Offset = BinaryPrimitives.ReadUInt32LittleEndian(data); break;
                case 0x0021 when cur is not null: cur.Procedural = true; break;
                case 0x002B when cur is not null: modules.Add(cur); cur = null; break;
            }
        }
        // MBCS names were read as Latin-1 to survive any code page; the Unicode records win where present.
        return (codePage, projectName, modules);
    }

    private static Dictionary<string, VbaModuleKind> ReadProjectKinds(CompoundFile cf, CompoundFile.Entry parent, Encoding enc)
    {
        var map = new Dictionary<string, VbaModuleKind>(StringComparer.OrdinalIgnoreCase);
        var project = cf.Child(parent, "PROJECT");
        if (project is null) return map;
        foreach (var raw in enc.GetString(cf.ReadStream(project)).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];
            var kind = key switch
            {
                "Module" => VbaModuleKind.Standard,
                "Class" => VbaModuleKind.Class,
                "BaseClass" => VbaModuleKind.Form,
                "Document" => VbaModuleKind.Document,
                _ => (VbaModuleKind?)null,
            };
            if (kind is null) continue;
            var slash = value.IndexOf('/');           // Document=ThisWorkbook/&H00000000
            map[slash >= 0 ? value[..slash] : value] = kind.Value;
        }
        return map;
    }

    private static Encoding EncodingFor(int codePage, List<string> warnings)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            warnings.Add($"code page {codePage} is not available; source read as Windows-1252.");
            return Encoding.GetEncoding(1252);
        }
    }
}
