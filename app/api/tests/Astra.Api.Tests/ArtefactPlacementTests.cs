using Astra.Api.Artefacts;
using Xunit;

namespace Astra.Api.Tests;

public class ArtefactPlacementTests
{
    private static ProjectSettings With(params string[] kinds) => new() { ArtefactKinds = kinds };

    // ── path hygiene ────────────────────────────────────────────────────

    [Theory]
    [InlineData("a/b.sql", "a/b.sql")]
    [InlineData("a\\b.sql", "a/b.sql")]
    [InlineData("./a//b.sql", "a/b.sql")]
    [InlineData("  a/b.sql  ", "a/b.sql")]
    [InlineData("a/./b/c.csv", "a/b/c.csv")]
    public void NormalisePath_CleansHarmlessVariation(string raw, string expected)
    {
        Assert.Equal(expected, ArtefactPlacement.NormalisePath(raw, out var problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("/etc/x.sql", "unsafe_path")]
    [InlineData("C:\\temp\\x.sql", "unsafe_path")]
    [InlineData("c:/x.sql", "unsafe_path")]
    [InlineData("../x.sql", "unsafe_path")]
    [InlineData("a/../../x.sql", "unsafe_path")]
    [InlineData("a/b/../c.sql", "unsafe_path")]
    [InlineData("", "empty_path")]
    [InlineData("   ", "empty_path")]
    [InlineData("./", "empty_path")]
    [InlineData("a/\u0001b.sql", "unsafe_path")]
    public void NormalisePath_RefusesAnythingThatCouldEscapeTheProject(string raw, string code)
    {
        Assert.Null(ArtefactPlacement.NormalisePath(raw, out var problem));
        Assert.Equal(code, problem);
    }

    [Fact]
    public void NormalisePath_RefusesAPathLongerThanTheLimit()
    {
        var tooLong = new string('a', ArtefactPlacement.MaxPathLength) + ".sql";
        Assert.Null(ArtefactPlacement.NormalisePath(tooLong, out var problem));
        Assert.Equal("path_too_long", problem);
    }

    // ── inferring the kind ──────────────────────────────────────────────

    [Theory]
    [InlineData("db/ddl.sql", "sql")]
    [InlineData("db/ddl.DDL", "sql")]
    [InlineData("tickets/export.csv", "ticket")]
    [InlineData("kt/session1.vtt", "transcript")]
    [InlineData("config/appsettings.PROD.json", "config")]
    [InlineData("docs/notes.docx", "document")]
    [InlineData("kt/session1.mp3", "audio")]
    [InlineData("batch/ssis/Pkg.dtsx", "etl")]
    public void Resolve_InfersTheKindFromTheExtension(string path, string kind)
    {
        var p = ArtefactPlacement.Resolve(path, With("sql", "job", "etl", "config", "transcript", "ticket", "document", "audio"));
        Assert.True(p.Accepted, p.Message);
        Assert.Equal(kind, p.Kind);
        Assert.False(p.Ambiguous);
    }

    [Fact]
    public void Resolve_XmlIsAmbiguousWhenSeveralKindsTakeIt_AndTheFirstInRegistryOrderWins()
    {
        var p = ArtefactPlacement.Resolve("batch/uc4/TLR_EOD.xml", With("document", "job"));
        Assert.True(p.Accepted);
        Assert.Equal("job", p.Kind);
        Assert.True(p.Ambiguous);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("document")]
    public void Resolve_XmlIsNotAmbiguousWhenOnlyOneEnabledKindTakesIt(string only)
    {
        var p = ArtefactPlacement.Resolve("x/y.xml", With(only));
        Assert.True(p.Accepted);
        Assert.Equal(only, p.Kind);
        Assert.False(p.Ambiguous);
    }

    [Fact]
    public void Resolve_ReturnsTheNormalisedPath()
    {
        var p = ArtefactPlacement.Resolve(".\\tickets\\\\export.csv", With("ticket"));
        Assert.Equal("tickets/export.csv", p.Path);
    }

    // ── refusals always say why ─────────────────────────────────────────

    [Fact]
    public void Resolve_AKindThatIsNotSwitchedOn_NamesTheKindAndThePlaceToEnableIt()
    {
        var p = ArtefactPlacement.Resolve("tickets/export.csv", With());
        Assert.False(p.Accepted);
        Assert.Equal("kind_not_enabled", p.Code);
        Assert.Contains("Tickets", p.Message);
        Assert.Contains("settings", p.Message);
    }

    [Fact]
    public void Resolve_SourceCodeIsDirectedToResync_EvenWhenNothingElseIsEnabled()
    {
        foreach (var path in new[] { "src/a.cs", "src/b.java", "src/c.f90", "src/d.cob" })
        {
            var p = ArtefactPlacement.Resolve(path, With("sql"));
            Assert.False(p.Accepted);
            Assert.Equal("code_belongs_in_resync", p.Code);
        }
    }

    [Theory]
    [InlineData("tool/setup.exe")]
    [InlineData("images/logo.png")]
    [InlineData("README")]
    public void Resolve_AnUnknownExtensionIsRefusedWithTheAcceptedList(string path)
    {
        var p = ArtefactPlacement.Resolve(path, With("sql", "ticket"));
        Assert.False(p.Accepted);
        Assert.Equal("unsupported_extension", p.Code);
    }

    [Fact]
    public void Resolve_UnsupportedExtensionMessage_ListsWhatTheProjectDoesTake()
    {
        var p = ArtefactPlacement.Resolve("a.exe", With("sql", "ticket"));
        Assert.Contains(".sql", p.Message);
        Assert.Contains(".csv", p.Message);
    }

    [Fact]
    public void Resolve_UnsupportedExtensionMessage_SaysSoWhenNoOptionalKindIsOn()
    {
        var p = ArtefactPlacement.Resolve("a.exe", With());
        Assert.Contains("no optional artefact kinds", p.Message);
    }

    [Fact]
    public void Resolve_AnUnsafePath_IsRefusedBeforeAnythingElse()
    {
        var p = ArtefactPlacement.Resolve("../../etc/passwd.sql", With("sql"));
        Assert.False(p.Accepted);
        Assert.Equal("unsafe_path", p.Code);
    }

    // ── an explicit kind ────────────────────────────────────────────────

    [Fact]
    public void Resolve_ExplicitKind_ResolvesTheAmbiguityWithoutFlaggingIt()
    {
        var p = ArtefactPlacement.Resolve("ui/Teller.oml.xml", With("job", "document"), "document");
        Assert.True(p.Accepted);
        Assert.Equal("document", p.Kind);
        Assert.False(p.Ambiguous);
    }

    [Theory]
    [InlineData("nope", "unknown_kind")]
    [InlineData("code", "code_belongs_in_resync")]
    [InlineData("sql", "kind_not_enabled")]
    public void Resolve_ExplicitKind_MustBeKnownOptionalAndEnabled(string explicitKind, string code)
    {
        var p = ArtefactPlacement.Resolve("db/x.sql", With("ticket"), explicitKind);
        Assert.False(p.Accepted);
        Assert.Equal(code, p.Code);
    }

    [Fact]
    public void Resolve_ExplicitKind_MustAcceptTheFilesExtension()
    {
        var p = ArtefactPlacement.Resolve("kt/notes.txt", With("ticket", "transcript"), "ticket");
        Assert.False(p.Accepted);
        Assert.Equal("extension_not_accepted_by_kind", p.Code);
        Assert.Contains(".csv", p.Message);
    }

    [Fact]
    public void Resolve_ExplicitKind_IsCaseInsensitive()
    {
        var p = ArtefactPlacement.Resolve("tickets/export.csv", With("ticket"), "  TICKET ");
        Assert.True(p.Accepted);
        Assert.Equal("ticket", p.Kind);
    }
}

public class ArtefactContentTypesTests
{
    [Theory]
    [InlineData(".docx")]
    [InlineData(".mp3")]
    [InlineData(".wav")]
    [InlineData(".m4a")]
    [InlineData(".MP3")]
    public void BinaryExtensions_AreStoredAsBytes(string ext) => Assert.True(ArtefactContentTypes.IsBinary(ext));

    [Theory]
    [InlineData(".sql")]
    [InlineData(".csv")]
    [InlineData(".vtt")]
    [InlineData(".json")]
    [InlineData(".xml")]
    [InlineData(".md")]
    public void TextExtensions_AreDecodedAsText(string ext) => Assert.False(ArtefactContentTypes.IsBinary(ext));

    [Fact]
    public void EveryRegisteredExtension_HasAContentType()
    {
        foreach (var ext in ArtefactKinds.Optional.SelectMany(k => k.Extensions))
            Assert.NotEqual("application/octet-stream", ArtefactContentTypes.For(ext));
    }

    [Fact]
    public void AKindMarkedAsText_AcceptsNoBinaryExtension()
    {
        foreach (var kind in ArtefactKinds.All.Where(k => k.IsText))
            Assert.DoesNotContain(kind.Extensions, ArtefactContentTypes.IsBinary);
    }

    [Fact]
    public void ABinaryExtension_BelongsToAKindThatIsNotMarkedAsText()
    {
        foreach (var kind in ArtefactKinds.All.Where(k => k.Extensions.Any(ArtefactContentTypes.IsBinary)))
            Assert.False(kind.IsText);
    }

    [Fact]
    public void UnknownExtension_IsOctetStream() =>
        Assert.Equal("application/octet-stream", ArtefactContentTypes.For(".exe"));
}
