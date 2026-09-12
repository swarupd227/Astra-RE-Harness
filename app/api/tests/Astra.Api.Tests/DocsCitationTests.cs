using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

public class DocsCitationTests
{
    private static DocCitations.CorpusIndex Index() => new(new[]
    {
        new DocCitations.RoutineSpan("blas/dgemm.f", "DGEMM", 1, 380),
        new DocCitations.RoutineSpan("minpack/lmder.f", "LMDER", 1, 452),
        new DocCitations.RoutineSpan("minpack/enorm.f", "ENORM", 1, 108),
        // Same file name as minpack/enorm.f in another directory: a bare
        // "enorm.f" must be ambiguous, a trailing sub-path must still resolve.
        new DocCitations.RoutineSpan("src/util/enorm.f", "ENORM2", 10, 50),
    });

    [Fact]
    public void Parse_AcceptsEnDash_Hyphen_AndSingleLine()
    {
        var cites = DocCitations.Parse(
            "Alpha [blas/dgemm.f:L187–L214] beta [minpack/lmder.f:L42-60] gamma [minpack/enorm.f:L40].");

        Assert.Equal(3, cites.Count);
        Assert.Equal(("blas/dgemm.f", 187, 214), (cites[0].Path, cites[0].Start, cites[0].End));
        Assert.Equal(("minpack/lmder.f", 42, 60), (cites[1].Path, cites[1].Start, cites[1].End));
        Assert.Equal(("minpack/enorm.f", 40, 40), (cites[2].Path, cites[2].Start, cites[2].End));
    }

    [Fact]
    public void Resolve_InsideARoutine_Succeeds()
    {
        var r = Index().Resolve(new DocCitations.Citation("blas/dgemm.f", 187, 214, "[blas/dgemm.f:L187–L214]"));
        Assert.True(r.Ok);
        Assert.Equal("DGEMM", r.Routine!.Name);
    }

    [Fact]
    public void Resolve_OutsideEveryRoutine_Fails()
    {
        var r = Index().Resolve(new DocCitations.Citation("blas/dgemm.f", 400, 410, "[blas/dgemm.f:L400–L410]"));
        Assert.False(r.Ok);
        Assert.Contains("outside every routine", r.Problem);
    }

    [Fact]
    public void Resolve_UnknownPath_Fails()
    {
        var r = Index().Resolve(new DocCitations.Citation("lapack/dgetrf.f", 1, 10, "[lapack/dgetrf.f:L1–L10]"));
        Assert.False(r.Ok);
        Assert.Contains("unknown path", r.Problem);
    }

    [Fact]
    public void Resolve_InvalidRange_Fails()
    {
        var r = Index().Resolve(new DocCitations.Citation("blas/dgemm.f", 50, 20, "[blas/dgemm.f:L50–L20]"));
        Assert.False(r.Ok);
        Assert.Contains("invalid line range", r.Problem);
    }

    [Fact]
    public void ResolvePath_AcceptsUniqueFileName_UniqueSuffix_AndRejectsAmbiguous()
    {
        var index = Index();
        Assert.Equal("blas/dgemm.f", index.ResolvePath("dgemm.f"));
        Assert.Equal("src/util/enorm.f", index.ResolvePath("util/enorm.f"));
        Assert.Equal("minpack/enorm.f", index.ResolvePath(@".\minpack\enorm.f"));
        Assert.Null(index.ResolvePath("enorm.f"));
    }

    [Fact]
    public void Check_ReportsEachUnresolvedCitationOnce()
    {
        var md = "[blas/dgemm.f:L1–L10] ok. [nope.f:L1–L2] bad. [nope.f:L1–L2] bad again. [blas/dgemm.f:L500] bad.";
        var result = DocCitations.Check(md, Index());

        Assert.Equal(4, result.Total);
        Assert.Equal(2, result.Problems.Count);
        Assert.Contains(result.Problems, p => p.StartsWith("[nope.f:L1–L2]"));
        Assert.Contains(result.Problems, p => p.StartsWith("[blas/dgemm.f:L500]"));
    }

    [Fact]
    public void Format_ProducesTheCanonicalForm_ThatParseReads()
    {
        var text = DocCitations.Format("minpack/enorm.f", 20, 58) + " " + DocCitations.Format("minpack/enorm.f", 40, 40);
        Assert.Equal("[minpack/enorm.f:L20–L58] [minpack/enorm.f:L40]", text);
        var cites = DocCitations.Parse(text);
        Assert.Equal(2, cites.Count);
        Assert.All(cites, c => Assert.True(Index().Resolve(c).Ok));
    }

    [Fact]
    public void RunChecks_FailsCitationsCheck_WhenARequiredCitationIsMissingOrUnresolved()
    {
        var index = Index();
        var noCitations = DocCriticPass.RunChecks("# T\n\nprose\n", index, Array.Empty<string>(), isCatalog: false, requireCitations: true);
        Assert.False(noCitations.Single(c => c.Name == "citations").Passed);

        var bad = DocCriticPass.RunChecks("# T\n\nprose [ghost.f:L1–L3]\n", index, Array.Empty<string>(), isCatalog: false, requireCitations: true);
        Assert.False(bad.Single(c => c.Name == "citations").Passed);

        var good = DocCriticPass.RunChecks("# T\n\nprose [blas/dgemm.f:L10–L20]\n", index, Array.Empty<string>(), isCatalog: false, requireCitations: true);
        Assert.True(good.Single(c => c.Name == "citations").Passed);
    }
}
