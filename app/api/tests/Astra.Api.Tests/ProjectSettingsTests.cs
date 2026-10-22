using System.Text.Json;
using Astra.Api.Artefacts;
using Xunit;

namespace Astra.Api.Tests;

public class ProjectSettingsTests
{
    private static (ProjectSettings? Settings, IReadOnlyList<string> Errors) Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ProjectSettings.Validate(doc.RootElement.Clone());
    }

    [Fact]
    public void Parse_NullEmptyOrMalformed_IsTheDefault()
    {
        Assert.Same(ProjectSettings.Default, ProjectSettings.Parse(null));
        Assert.Same(ProjectSettings.Default, ProjectSettings.Parse("  "));
        Assert.Same(ProjectSettings.Default, ProjectSettings.Parse("{not json"));
        Assert.Empty(ProjectSettings.Default.ArtefactKinds);
        Assert.False(ProjectSettings.Default.Demo);
    }

    [Fact]
    public void Validate_EmptyObject_IsValidAndDefault()
    {
        var (s, errors) = Validate("{}");
        Assert.Empty(errors);
        Assert.NotNull(s);
        Assert.Empty(s!.ArtefactKinds);
        Assert.Null(s.Banner);
    }

    [Fact]
    public void Validate_KindsAreNormalised_DeduplicatedAndInRegistryOrder()
    {
        var (s, errors) = Validate("""{"artefactKinds":["Ticket"," sql ","transcript","sql"]}""");
        Assert.Empty(errors);
        Assert.Equal(new[] { "sql", "transcript", "ticket" }, s!.ArtefactKinds);
    }

    [Fact]
    public void Validate_CodeIsAlwaysOn_SoItIsNotStored()
    {
        var (s, errors) = Validate("""{"artefactKinds":["code","sql"]}""");
        Assert.Empty(errors);
        Assert.Equal(new[] { "sql" }, s!.ArtefactKinds);
    }

    [Fact]
    public void Validate_UnknownKind_IsRejectedByName()
    {
        var (s, errors) = Validate("""{"artefactKinds":["sql","wizardry"]}""");
        Assert.Null(s);
        Assert.Contains(errors, e => e.Contains("wizardry"));
    }

    [Fact]
    public void Validate_UnknownTopLevelAndNestedFields_AreRejected_SoTyposAreCaught()
    {
        var (_, errors) = Validate("""{"bannr":"x","metadata":{"teir":1}}""");
        Assert.Contains(errors, e => e.Contains("bannr"));
        Assert.Contains(errors, e => e.Contains("metadata.teir"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("1.5")]
    [InlineData("\"2\"")]
    public void Validate_TierOutsideOneToThree_IsRejected(string tier)
    {
        var (s, errors) = Validate("{\"metadata\":{\"tier\":" + tier + "}}");
        Assert.Null(s);
        Assert.Contains(errors, e => e.Contains("tier"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    public void Validate_TierOneToThree_IsAccepted(string tier)
    {
        var (s, errors) = Validate("{\"metadata\":{\"tier\":" + tier + "}}");
        Assert.Empty(errors);
        Assert.Equal(int.Parse(tier), s!.Metadata.Tier);
    }

    [Fact]
    public void Validate_Banner_IsTrimmed_EmptyBecomesNull_AndLengthCapped()
    {
        Assert.Equal("Synthetic data for demonstration",
            Validate("""{"banner":"  Synthetic data for demonstration  "}""").Settings!.Banner);
        Assert.Null(Validate("""{"banner":"   "}""").Settings!.Banner);

        var tooLong = new string('x', ProjectSettings.MaxBanner + 1);
        var (s, errors) = Validate("{\"banner\":\"" + tooLong + "\"}");
        Assert.Null(s);
        Assert.Contains(errors, e => e.Contains("banner"));
    }

    [Fact]
    public void Validate_BannerWithControlCharacters_IsRejected()
    {
        var (s, errors) = Validate("{\"banner\":\"bell\\u0007\"}");
        Assert.Null(s);
        Assert.Contains(errors, e => e.Contains("control"));
    }

    [Theory]
    [InlineData("2027-03-31", true)]
    [InlineData("31/03/2027", false)]
    [InlineData("2027-13-01", false)]
    [InlineData("tomorrow", false)]
    public void Validate_WaveEndDate_MustBeIsoDate(string date, bool ok)
    {
        var (s, errors) = Validate("{\"metadata\":{\"wave\":{\"name\":\"Wave 1\",\"endDate\":\"" + date + "\"}}}");
        Assert.Equal(ok, s is not null);
        if (!ok) Assert.Contains(errors, e => e.Contains("endDate"));
    }

    [Theory]
    [InlineData("tier-one", true)]
    [InlineData("v1", true)]
    [InlineData("Bad Pack", false)]
    [InlineData("-lead", false)]
    public void Validate_PackId_IsALowercaseSlug(string pack, bool ok)
    {
        var (s, _) = Validate("{\"pack\":\"" + pack + "\"}");
        Assert.Equal(ok, s is not null);
    }

    [Fact]
    public void Validate_ExpectedCounts_AreBoundedWholeNumbers()
    {
        var good = Validate("""{"expectedCounts":{"interfaces":8,"jobs":6}}""");
        Assert.Empty(good.Errors);
        Assert.Equal(8, good.Settings!.ExpectedCounts["interfaces"]);

        Assert.NotNull(Validate("""{"expectedCounts":{"interfaces":-1}}""").Errors.FirstOrDefault());
        Assert.NotNull(Validate("""{"expectedCounts":{"interfaces":1001}}""").Errors.FirstOrDefault());
        Assert.NotNull(Validate("""{"expectedCounts":{"interfaces":2.5}}""").Errors.FirstOrDefault());
        Assert.NotNull(Validate("""{"expectedCounts":{"Bad Key":1}}""").Errors.FirstOrDefault());
    }

    [Fact]
    public void Validate_TooManyExpectedCountKeys_IsRejected()
    {
        var entries = string.Join(",", Enumerable.Range(0, ProjectSettings.MaxCountKeys + 1).Select(i => $"\"k{i}\":1"));
        var (s, errors) = Validate("{\"expectedCounts\":{" + entries + "}}");
        Assert.Null(s);
        Assert.Contains(errors, e => e.Contains("at most"));
    }

    [Fact]
    public void Validate_NonObjectRoot_IsRejected()
    {
        var (s, errors) = Validate("[1,2]");
        Assert.Null(s);
        Assert.Single(errors);
    }

    [Fact]
    public void Validate_CollectsEveryProblem_NotJustTheFirst()
    {
        var (s, errors) = Validate("""{"artefactKinds":["nope"],"metadata":{"tier":9},"demo":"yes"}""");
        Assert.Null(s);
        Assert.True(errors.Count >= 3, string.Join(" | ", errors));
    }

    [Fact]
    public void RoundTrip_ThroughJson_PreservesEverything()
    {
        var (s, errors) = Validate("""
            {"artefactKinds":["sql","ticket"],"pack":"knowledge-transfer","banner":"Synthetic",
             "metadata":{"tier":1,"knowledgeHolder":"A Holder","wave":{"name":"Wave 1","endDate":"2027-03-31"}},
             "expectedCounts":{"interfaces":8},"demo":true}
            """);
        Assert.Empty(errors);

        var back = ProjectSettings.Parse(s!.ToJson());
        Assert.Equal(s.ArtefactKinds, back.ArtefactKinds);
        Assert.Equal("knowledge-transfer", back.Pack);
        Assert.Equal("Synthetic", back.Banner);
        Assert.Equal(1, back.Metadata.Tier);
        Assert.Equal("A Holder", back.Metadata.KnowledgeHolder);
        Assert.Equal("2027-03-31", back.Metadata.Wave!.EndDate);
        Assert.Equal(8, back.ExpectedCounts["interfaces"]);
        Assert.True(back.Demo);
        Assert.True(back.Has("TICKET"));
        Assert.False(back.Has("audio"));
    }

    [Fact]
    public void ToJson_UsesCamelCase_SoTheFrontendTypeMatches()
    {
        var json = ProjectSettings.Default.ToJson();
        Assert.Contains("\"artefactKinds\"", json);
        Assert.Contains("\"expectedCounts\"", json);
        Assert.DoesNotContain("ArtefactKinds", json);
    }
}
