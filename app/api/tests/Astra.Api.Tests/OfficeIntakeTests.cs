using System.IO.Compression;
using System.Text;
using Astra.Api.Ingest;
using Astra.Api.Office;
using Xunit;

namespace Astra.Api.Tests;

public class OfficeIntakeTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures", "office")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Fixtures", "office", "vba-web", name);
    }

    [Fact]
    public void A_workbook_becomes_its_modules_named_under_it()
    {
        var tally = new IntakeTally();
        var files = OfficeIntake.Expand("clients/VBA-Web - Blank.xlsm", File.ReadAllBytes(Fixture("VBA-Web - Blank.xlsm")), tally);

        // Every module is a file under the workbook's own path, with the extension the VBA editor would export it with.
        Assert.All(files, f => Assert.StartsWith("clients/VBA-Web - Blank.xlsm/", f.RelativePath));
        Assert.Contains(files, f => f.RelativePath.EndsWith("/WebHelpers.bas"));
        Assert.Contains(files, f => f.RelativePath.EndsWith("/WebClient.cls"));
        // …and is VB the existing language detection routes to the VB parser.
        Assert.All(files, f => Assert.Equal(SourceLanguageDetector.Vb6, SourceLanguageDetector.FromFilename(f.RelativePath)));

        // The sheet and ThisWorkbook modules of the blank workbook hold no code, so they are not files.
        Assert.DoesNotContain(files, f => f.RelativePath.EndsWith("/ThisWorkbook.cls"));
        var note = Assert.Single(tally.Notes());
        Assert.Contains($"Read {files.Count} VBA modules from clients/VBA-Web - Blank.xlsm", note);
        Assert.Contains("with no code left out", note);

        // The source is the module's text, as the ground-truth test checks line for line.
        var helpers = files.Single(f => f.RelativePath.EndsWith("/WebHelpers.bas"));
        Assert.Contains("Public Function UrlEncode", helpers.Content);
    }

    [Fact]
    public void A_document_without_macros_adds_nothing_and_says_so()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open()))
            w.Write("<workbook/>");
        var tally = new IntakeTally();
        Assert.Empty(OfficeIntake.Expand("rates.xlsm", ms.ToArray(), tally));
        Assert.Contains(tally.Notes(), n => n == "rates.xlsm has no VBA project, so it adds no code.");

        var broken = new IntakeTally();
        Assert.Empty(OfficeIntake.Expand("broken.xls", Encoding.ASCII.GetBytes("not an office file at all"), broken));
        Assert.Contains(broken.Notes(), n => n.Contains("neither an Office zip"));
    }

    [Theory]
    [InlineData("Book.xlsm", true)]
    [InlineData("legacy/Rates.XLS", true)]
    [InlineData("Letter.docm", true)]
    [InlineData("Module1.bas", false)]
    [InlineData("data.xlsx", false)]                 // no macros by definition: not offered as code
    public void Office_documents_are_recognised_by_extension(string path, bool expected) =>
        Assert.Equal(expected, OfficeIntake.IsOfficeDocument(path));

    [Theory]
    [InlineData("Attribute VB_Name = \"Sheet1\"\nOption Explicit\n", false)]
    [InlineData("VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1  'True\nEND\nAttribute VB_Name = \"ThisWorkbook\"\n' a comment\n", false)]
    [InlineData("Attribute VB_Name = \"Sheet1\"\nPrivate Sub Worksheet_Change(ByVal Target As Range)\nEnd Sub\n", true)]
    public void A_module_with_only_attributes_has_no_code(string source, bool expected) =>
        Assert.Equal(expected, OfficeIntake.HasCode(source));

    [Fact]
    public void Intake_notes_come_before_the_skip_summary()
    {
        var t = new IntakeTally();
        t.Skip(IntakeTally.NotSource, "readme.md");
        t.Note("Read 3 VBA modules from a.xlsm.");
        Assert.Equal("Read 3 VBA modules from a.xlsm.", t.Notes()[0]);
        Assert.StartsWith("Skipped 1 file", t.Notes()[1]);
    }
}
