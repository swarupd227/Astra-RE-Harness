using Astra.Api.Artefacts;
using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

public class WorkbookAdapterTests
{
    private static byte[] Installer()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures", "office")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllBytes(Path.Combine(dir!.FullName, "Fixtures", "office", "vba-web", "VBA-Web - Installer.xlsm"));
    }

    private static AdapterResult Build(string path = "tools/Installer.xlsm") =>
        EvidenceAdapters.Build(new AdapterInput(path, ArtefactKinds.Workbook, null, Installer()));

    [Fact]
    public void A_workbook_is_its_own_kind_read_as_bytes_and_stating_facts()
    {
        Assert.Equal(ArtefactKinds.Workbook, ArtefactKinds.Find("workbook")!.Id);
        Assert.Contains(".xlsm", ArtefactKinds.Find("workbook")!.Extensions);
        Assert.True(EvidenceAdapters.NeedsBytes("a/Rates.xlsm"));
        Assert.True(EvidenceAdapters.NeedsBytes("Letter.docx"));        // unchanged
        Assert.False(EvidenceAdapters.NeedsBytes("call.mp3"));          // audio is never read for chunks
        Assert.True(EvidenceAdapters.ProducesFacts(ArtefactKinds.Workbook));
        Assert.IsType<WorkbookAdapter>(EvidenceAdapters.AdapterFor(ArtefactKinds.Workbook, ".xlsm"));
        Assert.True(ArtefactContentTypes.IsBinary(".xls"));
    }

    [Fact]
    public void Pieces_name_what_they_are()
    {
        var r = Build();
        Assert.Empty(r.Warnings);
        var locators = r.Chunks.Select(c => c.Locator).ToList();
        Assert.Contains("workbook", locators);
        Assert.Contains("sheet:Install VBA-Web", locators);
        Assert.Contains(locators, l => l.StartsWith("control:Install VBA-Web/", StringComparison.Ordinal));
        Assert.Contains(locators, l => l.StartsWith("name:", StringComparison.Ordinal));
        Assert.Equal(locators.Count, locators.Distinct().Count());

        var install = r.Chunks.Where(c => c.Locator.StartsWith("control:", StringComparison.Ordinal))
            .Single(c => c.Text!.Contains("runs Install.Install"));
        Assert.StartsWith("Form Button", install.Text);
        Assert.Contains("2 bound to VBA", r.Summary);
    }

    [Fact]
    public void Facts_join_buttons_to_the_macros_they_run_and_check_boxes_to_their_cells()
    {
        var facts = Build().Facts!;
        Assert.Contains(facts.Entities, e => e.Type == "workbook" && e.Name == "tools/Installer.xlsm");
        Assert.Contains(facts.Entities, e => e.Type == "sheet" && e.Name == "tools/Installer.xlsm/Install VBA-Web");

        var runs = facts.Links.Where(l => l.Relation == "runs").ToList();
        Assert.Equal(new[] { "tools/Installer.xlsm/Install.Browse", "tools/Installer.xlsm/Install.Install" },
            runs.Select(l => l.To.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(runs, l => Assert.Equal(("control", "macro", "high"), (l.From.Type, l.To.Type, l.Confidence)));

        // The macro says which uploaded source file holds it, so the graph can lead to the code.
        var macro = facts.Entities.Single(e => e.Type == "macro" && e.Name.EndsWith("Install.Install"));
        Assert.Equal("tools/Installer.xlsm/Install.cls", macro.Attrs!["codePath"]);
        Assert.Equal(true, macro.Attrs["found"]);
        Assert.Contains(facts.Links, l => l.Relation == "contains" && l.From.Type == "vba-module" && l.To.Name == macro.Name);

        // Thirteen check boxes each write a cell (to a named range where one sits on it).
        Assert.Equal(13, facts.Links.Count(l => l.Relation == "writes" && l.From.Type == "control"));

        // Every fact points at a piece of the same file.
        var locators = Build().Chunks.Select(c => c.Locator).ToHashSet();
        Assert.All(facts.Entities, e => Assert.Contains(e.Locator!, locators));
        Assert.All(facts.Links, l => Assert.Contains(l.Locator!, locators));
    }

    [Fact]
    public void Something_that_is_not_a_workbook_is_kept_with_a_note()
    {
        var r = EvidenceAdapters.Build(new AdapterInput("x.xlsx", ArtefactKinds.Workbook, null, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.NotEmpty(r.Warnings);
        Assert.True(r.Facts is null || r.Facts.Entities.Count <= 1);
    }
}
