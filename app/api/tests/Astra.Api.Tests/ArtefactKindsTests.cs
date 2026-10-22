using Astra.Api.Artefacts;
using Xunit;

namespace Astra.Api.Tests;

public class ArtefactKindsTests
{
    [Fact]
    public void Ids_AreUnique_LowercaseAndShortEnoughForTheColumn()
    {
        var ids = ArtefactKinds.All.Select(k => k.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.Equal(id, id.ToLowerInvariant());
            Assert.InRange(id.Length, 1, 32); // source_files.kind is varchar(32)
        });
    }

    [Fact]
    public void Code_IsTheOnlyCoreKind_AndComesFirst()
    {
        Assert.Equal(ArtefactKinds.Code, ArtefactKinds.All[0].Id);
        Assert.Single(ArtefactKinds.All, k => k.Core);
        Assert.DoesNotContain(ArtefactKinds.Optional, k => k.Id == ArtefactKinds.Code);
    }

    [Fact]
    public void Extensions_AreLowercase_DotPrefixed_AndNonEmpty()
    {
        foreach (var kind in ArtefactKinds.All)
        {
            Assert.NotEmpty(kind.Extensions);
            Assert.All(kind.Extensions, e =>
            {
                Assert.StartsWith(".", e);
                Assert.Equal(e, e.ToLowerInvariant());
            });
        }
    }

    [Fact]
    public void CodeKind_CoversTheExistingLanguageDetector()
    {
        var code = ArtefactKinds.Find(ArtefactKinds.Code)!;
        foreach (var ext in new[] { ".f", ".cob", ".pas", ".cpp", ".cs", ".java", ".vb" })
            Assert.Contains(ext, code.Extensions);
    }

    [Fact]
    public void TrustTiers_StructuredForParsedFacts_NarrativeForWhatPeopleWrote()
    {
        foreach (var id in new[] { "code", "sql", "job", "etl", "config" })
            Assert.Equal(TrustTier.Structured, ArtefactKinds.Find(id)!.Trust);
        foreach (var id in new[] { "transcript", "ticket", "document", "audio" })
            Assert.Equal(TrustTier.Narrative, ArtefactKinds.Find(id)!.Trust);
    }

    [Fact]
    public void Audio_IsBinary_WithALargerCapThanText()
    {
        var audio = ArtefactKinds.Find("audio")!;
        Assert.False(audio.IsText);
        Assert.True(audio.MaxBytes > ArtefactKinds.Find("sql")!.MaxBytes);
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndNullSafe()
    {
        Assert.NotNull(ArtefactKinds.Find("SQL"));
        Assert.Null(ArtefactKinds.Find("nope"));
        Assert.Null(ArtefactKinds.Find(null));
        Assert.False(ArtefactKinds.IsKnown(""));
    }

    [Fact]
    public void OrderOf_FollowsRegistryOrder_AndUnknownSortsLast()
    {
        Assert.True(ArtefactKinds.OrderOf("sql") < ArtefactKinds.OrderOf("audio"));
        Assert.Equal(int.MaxValue, ArtefactKinds.OrderOf("nope"));
    }
}
