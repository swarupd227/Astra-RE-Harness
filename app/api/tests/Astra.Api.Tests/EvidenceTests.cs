using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

public class EvidenceIdsTests
{
    // Pinned from an independent SHA-1 of "<path>#<locator>": if the scheme ever changes, every
    // recorded citation stops resolving, so a change here must be deliberate.
    [Theory]
    [InlineData("sql", "db/ddl.sql", "L1-40", "SQL-f7f575c5")]
    [InlineData("code", "src/ACCT.cob", "L120-168", "SRC-788da6f2")]
    [InlineData("ticket", "tickets/export.csv", "row:INC-T-0412", "TKT-dc739d3d")]
    public void ShortId_IsPrefixPlusEightHexOfPathAndLocator(string kind, string path, string locator, string expected) =>
        Assert.Equal(expected, EvidenceIds.Base(kind, path, locator));

    [Fact]
    public void ShortId_IsStableAcrossCalls_AndDiffersByPathAndLocator()
    {
        Assert.Equal(EvidenceIds.Base("sql", "a.sql", "L1-40"), EvidenceIds.Base("sql", "a.sql", "L1-40"));
        Assert.NotEqual(EvidenceIds.Base("sql", "a.sql", "L1-40"), EvidenceIds.Base("sql", "b.sql", "L1-40"));
        Assert.NotEqual(EvidenceIds.Base("sql", "a.sql", "L1-40"), EvidenceIds.Base("sql", "a.sql", "L41-80"));
    }

    [Theory]
    [InlineData("code", "SRC")]
    [InlineData("sql", "SQL")]
    [InlineData("job", "JOB")]
    [InlineData("etl", "ETL")]
    [InlineData("config", "CFG")]
    [InlineData("transcript", "TRN")]
    [InlineData("ticket", "TKT")]
    [InlineData("document", "DOC")]
    [InlineData("audio", "AUD")]
    [InlineData("something-new", "EVD")]
    public void Prefix_NamesTheKind(string kind, string prefix) => Assert.Equal(prefix, EvidenceIds.Prefix(kind));

    [Fact]
    public void EveryRegisteredKind_HasItsOwnPrefix()
    {
        var kinds = Astra.Api.Artefacts.ArtefactKinds.All.Select(k => k.Id).ToList();
        var prefixes = kinds.Select(EvidenceIds.Prefix).ToList();
        Assert.DoesNotContain("EVD", prefixes);
        Assert.Equal(prefixes.Count, prefixes.Distinct().Count());
    }

    [Fact]
    public void Assign_GivesEveryItemAnId_InInputOrder()
    {
        var items = new List<(string, string, string)> { ("sql", "b.sql", "L1-40"), ("sql", "a.sql", "L1-40") };
        var ids = EvidenceIds.Assign(items);
        Assert.Equal(EvidenceIds.Base("sql", "b.sql", "L1-40"), ids[0]);
        Assert.Equal(EvidenceIds.Base("sql", "a.sql", "L1-40"), ids[1]);
    }

    [Fact]
    public void Assign_WhenAnotherKeyHoldsTheId_StepsToTheNextFreeSuffix()
    {
        var plain = EvidenceIds.Base("sql", "a.sql", "L1-40");
        var taken = new Dictionary<string, string>
        {
            [plain] = "somebody-else#L1-1",
            [plain + "-2"] = "another#L1-1",
        };
        var ids = EvidenceIds.Assign(new List<(string, string, string)> { ("sql", "a.sql", "L1-40") }, taken);
        Assert.Equal(plain + "-3", ids[0]);
    }

    [Fact]
    public void Assign_WhenTheItemAlreadyOwnsItsId_KeepsIt()
    {
        var plain = EvidenceIds.Base("sql", "a.sql", "L1-40");
        var taken = new Dictionary<string, string> { [plain] = EvidenceIds.KeyOf("a.sql", "L1-40") };
        var ids = EvidenceIds.Assign(new List<(string, string, string)> { ("sql", "a.sql", "L1-40") }, taken);
        Assert.Equal(plain, ids[0]);
    }

    [Fact]
    public void Assign_IsDeterministicWhateverTheInputOrder()
    {
        var a = ("sql", "a.sql", "L1-40");
        var b = ("sql", "b.sql", "L1-40");
        var one = EvidenceIds.Assign(new List<(string, string, string)> { a, b });
        var two = EvidenceIds.Assign(new List<(string, string, string)> { b, a });
        Assert.Equal(one[0], two[1]);
        Assert.Equal(one[1], two[0]);
    }

    /// <summary>Two locators in one file whose ids share all eight hex digits (found by search; the hash is fixed, so it is the same pair every run).</summary>
    private static (string First, string Second) FindCollidingLocators()
    {
        var seen = new Dictionary<string, string>();
        for (var i = 1; i < 2_000_000; i++)
        {
            var locator = $"L1-{i}";
            var h = EvidenceIds.Hash8("a.sql", locator);
            if (seen.TryGetValue(h, out var other)) return (other, locator);
            seen[h] = locator;
        }
        throw new InvalidOperationException("No collision found.");
    }

    [Fact]
    public void Assign_TwoItemsWithTheSameHash_GetThePlainAndASuffixedId_InTheSameWayWhateverTheOrder()
    {
        var (x, y) = FindCollidingLocators();
        var one = EvidenceIds.Assign(new List<(string, string, string)> { ("sql", "a.sql", x), ("sql", "a.sql", y) });
        var two = EvidenceIds.Assign(new List<(string, string, string)> { ("sql", "a.sql", y), ("sql", "a.sql", x) });

        Assert.NotEqual(one[0], one[1]);
        Assert.Equal(one[0], two[1]);
        Assert.Equal(one[1], two[0]);
        Assert.Equal(1, one.Count(id => id.EndsWith("-2", StringComparison.Ordinal)));
        Assert.All(one, id => Assert.True(EvidenceIds.IsShortId(id), id));
    }

    [Theory]
    [InlineData("SQL-f7f575c5", "SQL-f7f575c5")]
    [InlineData("[SQL-f7f575c5]", "SQL-f7f575c5")]
    [InlineData(" sql-F7F575C5 ", "SQL-f7f575c5")]
    [InlineData("`SRC-788da6f2`", "SRC-788da6f2")]
    [InlineData("SQL-f7f575c5-2", "SQL-f7f575c5-2")]
    public void Normalise_AcceptsWhatPeopleAndModelsWrite(string raw, string expected) =>
        Assert.Equal(expected, EvidenceIds.Normalise(raw));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("SQL")]
    [InlineData("SQL-xyz")]
    [InlineData("SQL-f7f575c")]
    [InlineData("SQL-f7f575c55")]
    [InlineData("SQLX-f7f575c5")]
    [InlineData("12-f7f575c5")]
    public void Normalise_RefusesAnythingNotShapedLikeAnId(string? raw) => Assert.Null(EvidenceIds.Normalise(raw));
}

public class EvidenceLocatorTests
{
    [Theory]
    [InlineData("L120-168", 120, 168)]
    [InlineData("L7", 7, 7)]
    [InlineData("L1-1", 1, 1)]
    public void TryParseLines_ReadsLineRanges(string locator, int start, int end)
    {
        Assert.True(EvidenceLocator.TryParseLines(locator, out var s, out var e));
        Assert.Equal((start, end), (s, e));
    }

    [Theory]
    [InlineData("")]
    [InlineData("L")]
    [InlineData("L0")]
    [InlineData("L0-5")]
    [InlineData("L9-3")]
    [InlineData("La-b")]
    [InlineData("L1-")]
    [InlineData("L-5")]
    [InlineData("L1-2-3")]
    [InlineData("row:INC-1")]
    [InlineData("l5")]
    public void TryParseLines_RefusesAnythingElse(string locator) =>
        Assert.False(EvidenceLocator.TryParseLines(locator, out _, out _));

    [Theory]
    [InlineData("L10-20", LocatorKind.Lines)]
    [InlineData("t=00:14:32-00:15:40", LocatorKind.Time)]
    [InlineData("row:INC-T-0412", LocatorKind.Row)]
    [InlineData("xpath:/JOBP/Tasks/Task[3]", LocatorKind.XPath)]
    [InlineData("h=Restart", LocatorKind.Heading)]
    [InlineData("json:$.Ofac.TimeoutSeconds", LocatorKind.JsonPath)]
    [InlineData("whatever", LocatorKind.Other)]
    [InlineData("", LocatorKind.Other)]
    public void Classify_NamesTheLocatorFamily(string locator, LocatorKind kind) =>
        Assert.Equal(kind, EvidenceLocator.Classify(locator));

    [Fact]
    public void Lines_FormatsTheCanonicalLocator() => Assert.Equal("L5-9", EvidenceLocator.Lines(5, 9));
}

public class EvidenceAdapterTests
{
    private static AdapterInput Text(string path, string kind, string text) => new(path, kind, text, null);

    private static string Numbered(int n) => string.Join('\n', Enumerable.Range(1, n).Select(i => $"line {i}")) + "\n";

    [Fact]
    public void LineWindows_CutAFileIntoWholeLineWindows_WithExactLocators()
    {
        var r = new LineWindowAdapter().Build(Text("db/ddl.sql", "sql", Numbered(100)));
        Assert.Equal(new[] { "L1-40", "L41-80", "L81-100" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal(new[] { 1, 41, 81 }, r.Chunks.Select(c => c.LineStart!.Value));
        Assert.Equal(new[] { 40, 80, 100 }, r.Chunks.Select(c => c.LineEnd!.Value));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void LineWindows_TextIsTheWindowsOwnLines_WithoutATrailingNewline()
    {
        var r = new LineWindowAdapter().Build(Text("a.sql", "sql", "one\ntwo\nthree\n"));
        var chunk = Assert.Single(r.Chunks);
        Assert.Equal("one\ntwo\nthree", chunk.Text);
        Assert.Equal("L1-3", chunk.Locator);
    }

    [Fact]
    public void LineWindows_LabelNamesTheFileAndTheLines()
    {
        var r = new LineWindowAdapter().Build(Text("deep/dir/ddl.sql", "sql", Numbered(50)));
        Assert.Equal("ddl.sql · lines 1–40", r.Chunks[0].Label);
        Assert.Equal("ddl.sql · lines 41–50", r.Chunks[1].Label);
    }

    [Fact]
    public void LineWindows_SkipWindowsThatAreOnlyBlank_ButKeepTheLineNumbers()
    {
        var text = Numbered(40) + string.Concat(Enumerable.Repeat("\n", 40)) + "tail\n";
        var r = new LineWindowAdapter().Build(Text("a.sql", "sql", text));
        Assert.Equal(new[] { "L1-40", "L81-81" }, r.Chunks.Select(c => c.Locator));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    public void LineWindows_AnEmptyFileHasNoChunks(string text) =>
        Assert.Empty(new LineWindowAdapter().Build(Text("a.sql", "sql", text)).Chunks);

    [Fact]
    public void LineWindows_StopAtTheCap_AndSayWhy()
    {
        var text = Numbered(LineWindowAdapter.WindowLines * (LineWindowAdapter.MaxChunksPerFile + 5));
        var r = new LineWindowAdapter().Build(Text("big.sql", "sql", text));
        Assert.Equal(LineWindowAdapter.MaxChunksPerFile, r.Chunks.Count);
        Assert.Contains("only the first 2,000 windows", Assert.Single(r.Warnings));
    }

    [Fact]
    public void LineWindows_CutAVeryLongWindow_AndSayWhy()
    {
        var r = new LineWindowAdapter().Build(Text("min.json", "config", new string('x', LineWindowAdapter.MaxChunkChars + 500)));
        Assert.Equal(LineWindowAdapter.MaxChunkChars, Assert.Single(r.Chunks).Text!.Length);
        Assert.Contains("cut to", Assert.Single(r.Warnings));
    }

    [Fact]
    public void Registry_BinaryFilesHaveNoChunksYet()
    {
        var r = EvidenceAdapters.Build(new AdapterInput("kt/session.mp3", "audio", null, new byte[] { 1, 2, 3 }));
        Assert.Empty(r.Chunks);
    }

    [Fact]
    public void Registry_TextFilesGetTheLineWindows()
    {
        var r = EvidenceAdapters.Build(Text("kt/s.vtt", "transcript", "WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nHello\n"));
        Assert.Single(r.Chunks);
    }

    [Theory]
    [InlineData("code", "structured")]
    [InlineData("sql", "structured")]
    [InlineData("config", "structured")]
    [InlineData("ticket", "narrative")]
    [InlineData("transcript", "narrative")]
    [InlineData("audio", "narrative")]
    public void TrustTier_FollowsTheKind(string kind, string tier) => Assert.Equal(tier, EvidenceAdapters.TrustTierOf(kind));
}

public class CodeEvidenceTests
{
    private static readonly Guid Sub = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void ARoutineIsAChunkOfItsLines_WithNoTextOfItsOwn()
    {
        var d = CodeEvidence.ToDraft(new RoutineSpan(Sub, "src/ACCT.cob", "sha256:abc", "ACCT-MAINT", 120, 168, "cobol"));
        Assert.Equal("L120-168", d.Locator);
        Assert.Equal("ACCT-MAINT", d.Label);
        Assert.Null(d.Text);
        Assert.Equal((120, 168), (d.LineStart, d.LineEnd));
        Assert.Contains("\"routine\":\"ACCT-MAINT\"", d.StructuredJson);
        Assert.Contains("\"language\":\"cobol\"", d.StructuredJson);
        Assert.Contains(Sub.ToString(), d.StructuredJson);
    }

    [Fact]
    public void TheHash_ChangesWithTheFileOrTheSpan_AndFitsTheColumn()
    {
        var a = CodeEvidence.TextHash("sha256:abc", "L1-10");
        Assert.Equal(a, CodeEvidence.TextHash("sha256:abc", "L1-10"));
        Assert.NotEqual(a, CodeEvidence.TextHash("sha256:abd", "L1-10"));
        Assert.NotEqual(a, CodeEvidence.TextHash("sha256:abc", "L1-11"));
        Assert.True(a.Length <= 80);
        Assert.StartsWith("span:", a);
    }

    [Fact]
    public void ATextHash_FitsTheColumnToo() => Assert.True(EvidenceAdapters.TextHash("hello").Length <= 80);
}
