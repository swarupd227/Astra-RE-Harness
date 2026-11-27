using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// Documentation prompts are language-neutral and each call carries its own source language's guidance — PHP docs
/// were once written by a model told it was reading "a Fortran codebase" and asked for COMMON blocks and XERBLA.
/// </summary>
public class DocLanguageGuidanceTests
{
    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Astra.Api", "Llm", "Prompts"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Astra.Api");
    }

    private static string Summary(string root) =>
        DocPromptAssets.ReadPrompt(root, "fortran-f77", "doc-summary.v1.md");

    [Fact]
    public void A_php_summary_call_carries_php_guidance_and_no_fortran_framing()
    {
        var root = ApiRoot();
        var assets = DocPromptAssets.Load(root);
        var blocks = assets.SystemBlocks(Summary(root), "routine-summary", "php");
        var all = string.Join("\n", blocks);

        Assert.Contains("Source language: PHP.", blocks[^1]);                     // the guidance is the last, cached block
        Assert.Contains("superglobals", blocks[^1]);
        Assert.Contains("$_SESSION", blocks[^1]);
        Assert.DoesNotContain("Fortran codebase", all);
        Assert.DoesNotContain("COMMON block", blocks[^1]);
    }

    [Fact]
    public void Fortran_keeps_its_own_guidance()
    {
        var root = ApiRoot();
        var assets = DocPromptAssets.Load(root);
        var block = assets.SystemBlocks(Summary(root), "routine-summary", "fortran-f77")[^1];
        Assert.Contains("Source language: Fortran", block);
        Assert.Contains("COMMON blocks", block);
        Assert.Contains("XERBLA", block);
    }

    [Theory]
    [InlineData("vb6", "Visual Basic")]
    [InlineData("csharp", "C# / .NET")]
    [InlineData("cobol", "COBOL")]
    [InlineData("openedge", "MultiValue / 4GL")]
    [InlineData("rust", "Source language: rust")]       // no block of its own: the default names it
    public void Every_language_gets_a_block(string language, string expected)
    {
        var assets = DocPromptAssets.Load(ApiRoot());
        Assert.Contains(expected, assets.LanguageBlock(language));
    }

    [Fact]
    public void No_language_means_no_extra_block_and_no_doc_prompt_says_fortran_codebase()
    {
        var root = ApiRoot();
        var assets = DocPromptAssets.Load(root);
        Assert.Equal(assets.SystemBlocks("p", null).Count, assets.SystemBlocks("p", null, null).Count);

        foreach (var file in Directory.GetFiles(Path.Combine(root, "Llm", "Prompts", "fortran-f77"), "doc-*.md"))
            Assert.DoesNotContain("Fortran codebase", File.ReadAllText(file));
    }
}
