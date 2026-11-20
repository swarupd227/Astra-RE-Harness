using System.IO.Compression;
using System.Text;
using Astra.Api.Office;
using Xunit;
using Xunit.Abstractions;

namespace Astra.Api.Tests;

public class OfficeVbaTests
{
    private readonly ITestOutputHelper _out;
    public OfficeVbaTests(ITestOutputHelper output) => _out = output;

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures", "office")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Fixtures", "office");
    }

    // [MS-OVBA] 3.2.1 "No Compression": a compressed chunk that holds only literals.
    [Fact]
    public void Decompresses_the_spec_literal_only_example()
    {
        var bytes = new byte[]
        {
            0x01, 0x19, 0xB0,
            0x00, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f', (byte)'g', (byte)'h',
            0x00, (byte)'i', (byte)'j', (byte)'k', (byte)'l', (byte)'m', (byte)'n', (byte)'o', (byte)'p',
            0x00, (byte)'q', (byte)'r', (byte)'s', (byte)'t', (byte)'u', (byte)'v', (byte)'.',
        };
        Assert.Equal("abcdefghijklmnopqrstuv.", Encoding.ASCII.GetString(VbaCompression.Decompress(bytes)));
    }

    [Fact]
    public void A_copy_token_repeats_earlier_output()
    {
        // "abc" then a token copying 6 bytes from 3 back: flag 0b1000 (4th item is a token).
        // At 3 bytes decompressed the token uses 4 offset bits: offset 3 → 2 << 12, length 6 → 3.
        var token = (ushort)((2 << 12) | 3);
        var bytes = new byte[] { 0x01, 0x05, 0xB0, 0x08, (byte)'a', (byte)'b', (byte)'c', (byte)(token & 0xFF), (byte)(token >> 8) };
        Assert.Equal("abcabcabc", Encoding.ASCII.GetString(VbaCompression.Decompress(bytes)));
    }

    [Fact]
    public void Garbage_never_throws_and_says_why()
    {
        var r = OfficeVba.Read(Encoding.ASCII.GetBytes("this is not a workbook at all, just text padding the length out"), "x.xlsm");
        Assert.False(r.HasVba);
        Assert.Contains(r.Warnings, w => w.Contains("neither an Office zip"));

        var fakeOle = new byte[1024];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(fakeOle, 0);
        var r2 = OfficeVba.Read(fakeOle, "broken.xls");
        Assert.False(r2.HasVba);
        Assert.NotEmpty(r2.Warnings);
    }

    [Fact]
    public void A_workbook_without_macros_has_no_project()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open());
            w.Write("<workbook/>");
        }
        var r = OfficeVba.Read(ms.ToArray(), "plain.xlsx");
        Assert.Equal("ooxml", r.Container);
        Assert.False(r.HasVba);
        Assert.Empty(r.Warnings);
    }

    /// <summary>
    /// Ground truth that does not come from this reader: VBA-Web ships the same
    /// modules as text exported by the VBA editor. Every module in the workbook
    /// that has an export must match it line for line (the editor's export adds
    /// a class header and module attributes, so those are set aside).
    /// </summary>
    [Fact]
    public void Extracted_modules_match_the_editor_exports_line_for_line()
    {
        var dir = Path.Combine(FixtureDir(), "vba-web");
        var r = OfficeVba.Read(File.ReadAllBytes(Path.Combine(dir, "VBA-Web - Blank.xlsm")), "VBA-Web - Blank.xlsm");
        foreach (var w in r.Warnings) _out.WriteLine("warning: " + w);
        Assert.True(r.HasVba);
        Assert.Equal("ooxml", r.Container);
        foreach (var m in r.Project!.Modules) _out.WriteLine($"{m.Kind,-9} {m.Name,-24} {m.Source.Length,7:N0} chars");

        // The blank workbook ships the core modules only: the optional WebAsyncWrapper export
        // has no twin in it (and its Dictionary class has no export here), so compare the overlap.
        var compared = 0;
        foreach (var export in Directory.GetFiles(Path.Combine(dir, "src")))
        {
            var name = Path.GetFileNameWithoutExtension(export);
            var module = r.Project.Modules.SingleOrDefault(m => m.Name == name);
            if (module is null) continue;
            Assert.Equal(Path.GetFileName(export), module.ExportFileName);
            var expected = Normalise(File.ReadAllText(export, Encoding.Latin1));
            var actual = Normalise(module.Source);
            var firstDiff = Enumerable.Range(0, Math.Min(expected.Length, actual.Length)).FirstOrDefault(i => expected[i] != actual[i], -1);
            Assert.True(firstDiff < 0 && expected.Length == actual.Length,
                $"{name}: differs at line {firstDiff + 1} (expected {expected.Length} lines, got {actual.Length}):\n" +
                $"  expected: {(firstDiff >= 0 ? expected[firstDiff] : "")}\n  actual:   {(firstDiff >= 0 ? actual[firstDiff] : "")}");
            compared++;
        }
        Assert.Equal(5, compared);
        Assert.DoesNotContain(r.Project.Modules, m => m.Name == "WebAsyncWrapper");
        Assert.Contains(r.Project.Modules, m => m.Kind == VbaModuleKind.Document && m.Name == "ThisWorkbook");
    }

    private static string[] Normalise(string text) => text
        .Replace("\r\n", "\n").Split('\n')
        .Select(l => l.TrimEnd())
        .Where(l => !l.StartsWith("Attribute VB_", StringComparison.Ordinal))
        .SkipWhile(l => l is "VERSION 1.0 CLASS" or "BEGIN" or "END" || l.StartsWith("  MultiUse", StringComparison.Ordinal))
        .Reverse().SkipWhile(l => l.Length == 0).Reverse()
        .ToArray();

    /// <summary>
    /// Runs the reader over a local folder of third-party samples that are not
    /// committed (some carry no licence). Set ASTRA_VBA_SAMPLES to use it; it
    /// asserts only that nothing throws, and prints what it found.
    /// </summary>
    [Fact]
    public void Local_samples_report()
    {
        var root = Environment.GetEnvironmentVariable("ASTRA_VBA_SAMPLES");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        foreach (var f in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => OfficeVba.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            var r = OfficeVba.Read(File.ReadAllBytes(f), Path.GetFileName(f));
            var mods = r.Project?.Modules ?? Array.Empty<VbaModule>();
            var lines = mods.Sum(m => m.Source.Count(c => c == '\n'));
            _out.WriteLine($"{Path.GetFileName(f),-34} {r.Container,-6} modules={mods.Count,3} lines={lines,6:N0} " +
                           $"[{string.Join(", ", mods.GroupBy(m => m.Kind).Select(g => $"{g.Key}:{g.Count()}"))}]" +
                           (r.Warnings.Count > 0 ? $" warnings={r.Warnings.Count}: {r.Warnings[0]}" : ""));
            foreach (var form in mods.Where(m => m.Kind == VbaModuleKind.Form))
                _out.WriteLine($"    form {form.Name}: header {(form.FormHeader is null ? "missing" : form.FormHeader.Length + " chars")}");
            // ASTRA_VBA_DUMP: write each module out as the editor would export it, for parser work.
            var dump = Environment.GetEnvironmentVariable("ASTRA_VBA_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                var dir = Path.Combine(dump, Path.GetFileNameWithoutExtension(f));
                Directory.CreateDirectory(dir);
                foreach (var m in mods) File.WriteAllText(Path.Combine(dir, m.ExportFileName), m.Source, Encoding.Latin1);
            }
        }
    }
}
