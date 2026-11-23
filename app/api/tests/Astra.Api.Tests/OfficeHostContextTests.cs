using Astra.Api.Llm;
using Astra.Api.Llm.Prompts;
using Astra.Api.Office;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The host-document block an extraction of VBA-in-Office sees, and the prompt it goes with.
/// </summary>
public class OfficeHostContextTests
{
    [Theory]
    [InlineData("Quotes/Rating.xlsm/Pricing.bas", "Quotes/Rating.xlsm", "Pricing.bas")]
    [InlineData("Legacy.XLS/frmQuote.frm", "Legacy.XLS", "frmQuote.frm")]
    [InlineData("Letter.docm/Merge.bas", "Letter.docm", "Merge.bas")]
    public void Office_paths_split_into_document_and_module(string path, string doc, string module)
    {
        var split = OfficeHostContext.SplitPath(path);
        Assert.Equal((doc, module), split!.Value);
    }

    [Theory]
    [InlineData("src/Pricing.bas")]
    [InlineData("Rating.xlsm")]
    [InlineData("a/Rating.xlsm/sub/Pricing.bas")]   // modules sit directly under the document
    public void Ordinary_source_has_no_host(string path) => Assert.Null(OfficeHostContext.SplitPath(path));

    private static WorkbookSurfaceResult Template()
    {
        var quote = new WorkbookSheet("Quote", "shQuote", "visible", "A1:D9", 12, 2,
            new[] { new WorkbookFormula("C9", "Premium*Term", null, false, new[] { "name:Premium" }) },
            new[] { new WorkbookValidation("B4", "list", "Male,Female", "Gender", null), new WorkbookValidation("B5", "whole", "18 to 80", "Age", null) },
            new[] { new WorkbookControl("Button 1", "form", "Button", "Get quote", "[0]!Pricing.CalcPremium", "Pricing", "CalcPremium", true, null, null, Array.Empty<string>()) },
            Array.Empty<WorkbookTable>(), new[] { "Rates" });
        var rates = new WorkbookSheet("Rates", "shRates", "veryHidden", "A1:B40", 80, 0,
            Array.Empty<WorkbookFormula>(), Array.Empty<WorkbookValidation>(), Array.Empty<WorkbookControl>(),
            new[] { new WorkbookTable("rates", "A1:B40", new[] { "age", "rate" }) }, Array.Empty<string>());
        var names = new[]
        {
            new WorkbookName("Gender", "Quote!$B$4", null, false, "Gender"),
            new WorkbookName("Age", "Quote!$B$5", null, false, "Age next birthday"),
            new WorkbookName("Premium", "Quote!$C$8", null, false, "Annual premium"),
        };
        var vba = new VbaProject("VBAProject", 1252, new[] { new VbaModule("Pricing", VbaModuleKind.Standard, "Sub CalcPremium()\nEnd Sub\n", null) });
        return new WorkbookSurfaceResult("Rating.xlsm", new[] { quote, rates }, names, Array.Empty<WorkbookEvent>(),
            new[] { new WorkbookCodeReach("Pricing", new[] { "Rates" }, new[] { "Age", "Premium" }, Array.Empty<string>()) },
            Array.Empty<WorkbookExternal>(), vba, Array.Empty<string>());
    }

    [Fact]
    public void The_block_names_what_starts_the_routine_and_the_workbook_vocabulary()
    {
        var text = OfficeHostContext.Build(Template(), "Pricing.bas", "CalcPremium");
        Assert.Contains("## Host document: Rating.xlsm", text);
        Assert.Contains("- the Button \"Get quote\" on sheet \"Quote\"", text);
        Assert.Contains("- named cells: Age, Premium", text);
        Assert.Contains("- sheets: \"Rates\"", text);
        // A named cell carries its label and the rule on it.
        Assert.Contains("- Age = Quote!$B$5 — \"Age next birthday\" — a whole 18 to 80", text);
        Assert.Contains("- Gender = Quote!$B$4 — \"Gender\" — one of Male,Female", text);
        Assert.Contains("- Premium = Quote!$C$8 — \"Annual premium\"", text);
        Assert.Contains("\"Rates\" (code name shRates), veryHidden", text);
        Assert.Contains("tables rates(age, rate)", text);
        // Another routine is not started by that button.
        Assert.DoesNotContain("What runs", OfficeHostContext.Build(Template(), "Pricing.bas", "Other"));
    }

    [Fact]
    public void Without_the_workbook_the_note_still_says_it_is_vba_in_a_document() =>
        Assert.Contains("VBA inside an Office document", OfficeHostContext.Minimal("Letter.docm"));

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Astra.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void The_office_prompt_is_found_and_ordinary_vb6_still_falls_back_to_its_own()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Astra.Api", "Llm", "Prompts"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Llm:PromptDir"] = Path.Combine(dir!.FullName, "src", "Astra.Api", "Llm", "Prompts") }).Build();
        var lib = new PromptLibrary(new Env(), cfg, NullLogger<PromptLibrary>.Instance);

        var office = lib.GetLatest("vb6", ExtractionPipeline.OfficeExtractTarget, "extract");
        Assert.NotNull(office);
        Assert.Contains("{{hostContext}}", office!.UserTemplate);

        var ordinary = lib.GetLatestWithFallback("vb6", "dotnet10", "extract", out var fellBackTo);
        Assert.NotEqual(ExtractionPipeline.OfficeExtractTarget, fellBackTo);
        Assert.DoesNotContain("{{hostContext}}", ordinary!.UserTemplate);
    }
}
