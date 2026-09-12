using System.Text.Json;
using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

public class DocsEnvelopeTests
{
    [Fact]
    public void Parse_ReadsMarkdownAndMeta()
    {
        var env = DocEnvelope.Parse("""{"markdown":"# A\n\nbody","meta":{"title":"A","subsystems":["x","y"]}}""");

        Assert.Equal("# A\n\nbody\n", env.Markdown);
        Assert.Equal("A", env.MetaString("title"));
        Assert.Equal(new[] { "x", "y" }, env.MetaStrings("subsystems"));
    }

    [Fact]
    public void Parse_ThrowsWhenMarkdownIsMissingOrBlank()
    {
        Assert.Throws<InvalidOperationException>(() => DocEnvelope.Parse("""{"meta":{}}"""));
        Assert.Throws<InvalidOperationException>(() => DocEnvelope.Parse("""{"markdown":"   ","meta":{}}"""));
        Assert.Throws<InvalidOperationException>(() => DocEnvelope.Parse("[]"));
    }

    [Fact]
    public void Parse_MissingMeta_IsAnEmptyObject()
    {
        var env = DocEnvelope.Parse("""{"markdown":"# A"}""");
        Assert.Equal(JsonValueKind.Object, env.Meta.ValueKind);
        Assert.Null(env.MetaString("title"));
    }

    [Fact]
    public void ToPayloadJson_FlattensMeta_ExtrasWin_MarkdownIncluded()
    {
        var env = DocEnvelope.Parse("""{"markdown":"# A\n\ntext","meta":{"title":"A","ruleText":"IF x THEN y"}}""");
        var payload = env.ToPayloadJson(new Dictionary<string, object?> { ["title"] = "B", ["moduleName"] = "m" });

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        Assert.Equal("B", root.GetProperty("title").GetString());
        Assert.Equal("IF x THEN y", root.GetProperty("ruleText").GetString());
        Assert.Equal("m", root.GetProperty("moduleName").GetString());
        Assert.Equal("# A\n\ntext\n", root.GetProperty("markdown").GetString());
        Assert.False(root.TryGetProperty("meta", out _));
    }

    [Fact]
    public void ParseEntries_SkipsEntriesWithoutMarkdown_AndAcceptsBareArrays()
    {
        var (entries, skipped) = DocEnvelope.ParseEntries(
            """{"entries":[{"markdown":"### one","meta":{"name":"one"}},{"meta":{"name":"broken"}},{"markdown":"### two"}]}""");
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, skipped);
        Assert.Equal("one", entries[0].MetaString("name"));

        var (bare, _) = DocEnvelope.ParseEntries("""[{"markdown":"x"}]""");
        Assert.Single(bare);

        var (none, _) = DocEnvelope.ParseEntries("""{"other":1}""");
        Assert.Empty(none);
    }

    [Fact]
    public void EnsureTitle_Prepends_Relevels_OrAcceptsAnExistingTitle()
    {
        Assert.StartsWith("# lmder.f\n\nbody", DocMarkdown.EnsureTitle("body", "lmder.f"));
        Assert.StartsWith("# lmder.f\n", DocMarkdown.EnsureTitle("## lmder.f\n\nbody", "lmder.f"));
        Assert.StartsWith("# Other title\n", DocMarkdown.EnsureTitle("# Other title\n\nbody", "lmder.f"));
        Assert.StartsWith("### Rule\n\n", DocMarkdown.EnsureTitle("IF x THEN y.", "Rule", 3));
    }

    [Fact]
    public void ShiftHeadings_MovesTheShallowestToTheTarget()
    {
        var shifted = DocMarkdown.ShiftHeadings("# A\n\n## B\n\n```\n# not a heading\n```\n", 3);
        Assert.Contains("### A\n", shifted);
        Assert.Contains("#### B\n", shifted);
        Assert.Contains("# not a heading", shifted);
    }

    [Fact]
    public void MockDocWriter_ReturnsTheRequestsOwnPayload_WithZeroUsage()
    {
        var request = new DocWriteRequest(
            "p", "v", new[] { "system" }, "user", "emit_x", "d", new { }, "model", 10, "k",
            () => """{"markdown":"# x","meta":{"title":"x"}}""");
        var writer = new MockDocWriter();

        var result = writer.WriteAsync(request, CancellationToken.None).Result;

        Assert.True(writer.IsMock);
        Assert.Equal("mock", writer.ProviderName);
        Assert.Equal(0, result.Usage.InputTokens);
        Assert.Equal("x", DocEnvelope.Parse(result.ToolInputJson).MetaString("title"));
    }

    [Fact]
    public void RoutineSummaryMarkdown_RendersProse_NotTables()
    {
        var payload = """
            {"summary":"Scales a vector in place.","inputs":["the number of elements","the scaling constant"],
             "outputs":["the scaled vector"],"sideEffects":[],"preconditions":["N must be positive"],
             "edgeCases":["N <= 0 is a no-op"],"tier":"standard","citations":[{"lines":"1-30"}]}
            """;
        var md = RoutineSummaryMarkdown.Render(payload, "SSCAL", "blas/sscal.f", 1, 30);

        Assert.StartsWith("### SSCAL\n\nScales a vector in place.\n\n", md);
        Assert.Contains("Source: [blas/sscal.f:L1–L30]", md);
        Assert.Contains("**Inputs.** the number of elements; the scaling constant.", md);
        Assert.Contains("**Outputs.** the scaled vector.", md);
        Assert.Contains("**Side effects.** None;", md);
        Assert.Contains("#### Preconditions\n\n- N must be positive\n", md);
        Assert.Contains("#### Edge cases\n\n- N <= 0 is a no-op\n", md);
        Assert.DoesNotContain("| # |", md);
        Assert.DoesNotContain("> ", md);

        var index = new DocCitations.CorpusIndex(new[] { new DocCitations.RoutineSpan("blas/sscal.f", "SSCAL", 1, 30) });
        Assert.Empty(DocCitations.Check(md, index).Problems);
    }

    [Fact]
    public void ParseCritique_ClampsScores_AndDefaultsMissingAxes()
    {
        var critique = DocCriticPass.ParseCritique(
            """{"scores":{"accuracy":7,"completeness":0},"fixes":["Intro — vague — say what it does"],"summary":"ok"}""");

        Assert.Equal(5, critique.Scores.Accuracy);
        Assert.Equal(1, critique.Scores.Completeness);
        Assert.Equal(3, critique.Scores.Clarity);
        Assert.Equal(3, critique.Scores.Structure);
        Assert.Equal(12, critique.Scores.Total);
        Assert.Single(critique.Fixes);
        Assert.Equal("ok", critique.Summary);
    }

    [Fact]
    public void FrameEntry_NumbersRequirements_AndDropsAModelWrittenHeading()
    {
        var entry = DocEnvelope.Parse(
            """{"markdown":"### duplicate heading\n\nThe body.\n\n**Acceptance criteria**\n\n- one","meta":{"statement":"The system shall X"}}""");

        var (md, extras) = CatalogPipeline.FrameEntry("functional-requirement", entry, 7);

        Assert.StartsWith("### FR-007 — The system shall X\n\nThe body.", md);
        Assert.DoesNotContain("duplicate heading", md);
        Assert.Equal("FR-007", extras["reference"]);
    }

    [Fact]
    public void FrameEntry_BusinessRule_GetsAThirdLevelHeadingFromMeta()
    {
        var entry = DocEnvelope.Parse("""{"markdown":"IF a THEN b [x.f:L1–L2].","meta":{"title":"A before b","ruleText":"IF a THEN b"}}""");
        var (md, extras) = CatalogPipeline.FrameEntry("business-rule", entry, 1);

        Assert.StartsWith("### A before b\n\nIF a THEN b", md);
        Assert.Equal("A before b", extras["title"]);
    }

    [Fact]
    public void InjectDependencyDiagram_AppendsOnce()
    {
        var diagrams = new Dictionary<string, string> { ["lmder"] = "graph TD\n    A --> B" };
        var md = HierarchicalRollupPipeline.InjectDependencyDiagram("# lmder.f\n\nbody\n", "lmder", diagrams);

        Assert.Contains("## Call structure\n\n```mermaid\ngraph TD\n    A --> B\n```", md);
        Assert.Equal(md, HierarchicalRollupPipeline.InjectDependencyDiagram(md, "lmder", diagrams));
        Assert.Equal("# other\n", HierarchicalRollupPipeline.InjectDependencyDiagram("# other\n", "other", diagrams));
    }
}
