using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Shared helpers: build adapter input and read a draft's structured part.</summary>
internal static class AdapterTestKit
{
    public static AdapterInput Text(string path, string kind, string text) => new(path, kind, text, null);

    public static AdapterResult Build(string path, string kind, string text) =>
        EvidenceAdapters.Build(Text(path, kind, text));

    public static JsonElement Structured(EvidenceChunkDraft d) => JsonDocument.Parse(d.StructuredJson!).RootElement;

    public static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";
}

public class AdapterRegistryTests
{
    [Theory]
    [InlineData("config", ".json", typeof(ConfigAdapter))]
    [InlineData("ticket", ".csv", typeof(TicketAdapter))]
    [InlineData("transcript", ".vtt", typeof(TranscriptAdapter))]
    [InlineData("transcript", ".txt", typeof(TranscriptAdapter))]
    [InlineData("document", ".docx", typeof(DocxAdapter))]
    [InlineData("document", ".md", typeof(MarkdownAdapter))]
    [InlineData("document", ".xml", typeof(XmlElementAdapter))]
    [InlineData("job", ".xml", typeof(XmlElementAdapter))]
    [InlineData("etl", ".dtsx", typeof(XmlElementAdapter))]
    [InlineData("sql", ".sql", typeof(LineWindowAdapter))]
    public void EachKindGetsItsOwnAdapter(string kind, string ext, Type expected) =>
        Assert.IsType(expected, EvidenceAdapters.AdapterFor(kind, ext));

    [Fact]
    public void ARecordingHasNoAdapter_ItIsCitedThroughItsTranscript() =>
        Assert.Null(EvidenceAdapters.AdapterFor("audio", ".mp3"));

    [Theory]
    [InlineData("docs/a.docx", true)]
    [InlineData("docs/A.DOCX", true)]
    [InlineData("docs/a.md", false)]
    [InlineData("kt/s.mp3", false)]
    public void OnlyAWordFileIsReadAsBytes(string path, bool bytes) => Assert.Equal(bytes, EvidenceAdapters.NeedsBytes(path));

    [Fact]
    public void RecordingsAreNeverIndexed() => Assert.False(EvidenceAdapters.Indexes("audio"));

    [Fact]
    public void ATicketExportMayBeFarLargerThanAnyOtherKind() =>
        Assert.True(EvidenceAdapters.MaxChunksFor("ticket") > 5 * EvidenceAdapters.MaxChunksFor("transcript"));

    [Fact]
    public void RepeatedLocators_AreMadeUnique_InFileOrder()
    {
        var drafts = new[]
        {
            new EvidenceChunkDraft("h=Notes", "a", "1"),
            new EvidenceChunkDraft("h=Other", "b", "2"),
            new EvidenceChunkDraft("h=Notes", "c", "3"),
            new EvidenceChunkDraft("h=Notes", "d", "4"),
        };
        Assert.Equal(new[] { "h=Notes", "h=Other", "h=Notes~2", "h=Notes~3" }, EvidenceAdapters.EnsureUniqueLocators(drafts).Select(d => d.Locator));
    }

    [Fact]
    public void ALocatorAlreadyEndingInTilde2_IsNotConfusedByTheSuffix()
    {
        var drafts = new[]
        {
            new EvidenceChunkDraft("h=A", "a", "1"),
            new EvidenceChunkDraft("h=A", "b", "2"),
            new EvidenceChunkDraft("h=A~2", "c", "3"),
        };
        var locators = EvidenceAdapters.EnsureUniqueLocators(drafts).Select(d => d.Locator).ToList();
        Assert.Equal(3, locators.Distinct().Count());
        Assert.Equal("h=A", locators[0]);
        Assert.Equal("h=A~2", locators[1]);
    }

    [Fact]
    public void ALocatorLongerThanTheColumn_IsBounded_Deterministically_AndStaysUnique()
    {
        var a = "xpath:/" + string.Concat(Enumerable.Repeat("Level/", 120)) + "A";
        var b = "xpath:/" + string.Concat(Enumerable.Repeat("Level/", 120)) + "B";
        var first = EvidenceAdapters.BoundLocator(a);
        Assert.True(first.Length <= EvidenceAdapters.MaxLocatorChars);
        Assert.Equal(first, EvidenceAdapters.BoundLocator(a));
        Assert.NotEqual(first, EvidenceAdapters.BoundLocator(b));
        Assert.Equal("h=short", EvidenceAdapters.BoundLocator("h=short"));
    }

    [Fact]
    public void Finish_DropsEmptyChunks_ClipsLongOnes_AndCaps()
    {
        var long1 = new string('x', LineWindowAdapter.MaxChunkChars + 10);
        var r = EvidenceAdapters.Finish(new AdapterResult(new[]
        {
            new EvidenceChunkDraft("a", "a", "  \n "),
            new EvidenceChunkDraft("b", "b", long1),
            new EvidenceChunkDraft("c", "c", "ok"),
            new EvidenceChunkDraft("d", "d", "ok"),
        }, Array.Empty<string>()), maxChunks: 2);

        Assert.Equal(new[] { "b", "c" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal(LineWindowAdapter.MaxChunkChars, r.Chunks[0].Text!.Length);
        Assert.Contains(r.Warnings, w => w.Contains("cut to"));
        Assert.Contains(r.Warnings, w => w.Contains("Only the first 2 of 3"));
    }

    [Fact]
    public void AnAdapterIsNeverGivenNothingToRead()
    {
        Assert.Empty(EvidenceAdapters.Build(new AdapterInput("docs/a.docx", "document", null, null)).Chunks);
        Assert.Empty(EvidenceAdapters.Build(new AdapterInput("a.json", "config", null, null)).Chunks);
    }
}

public class ConfigAdapterTests
{
    private static AdapterResult Build(string json, string path = "config/appsettings.PROD.json") =>
        AdapterTestKit.Build(path, "config", json);

    private static readonly string Sample = AdapterTestKit.Lines(
        "{",
        "  \"Ofac\": {",
        "    \"BaseUrl\": \"https://ofac.example/screen\",",
        "    \"TimeoutSeconds\": 15",
        "  },",
        "  \"Servers\": [\"a\", \"b\"]",
        "}");

    [Fact]
    public void EachSettingIsAChunk_LocatedByItsJsonPath_AndNamedAsTheApplicationReadsIt()
    {
        var r = Build(Sample);
        Assert.Equal(new[] { "json:$.Ofac.BaseUrl", "json:$.Ofac.TimeoutSeconds", "json:$.Servers" }, r.Chunks.Select(c => c.Locator));
        var timeout = r.Chunks[1];
        Assert.Equal("Ofac:TimeoutSeconds", timeout.Label);
        Assert.Equal("Ofac:TimeoutSeconds = 15", timeout.Text);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ASettingKeepsTheLinesItSitsOn()
    {
        var r = Build(Sample);
        Assert.Equal((3, 3), (r.Chunks[0].LineStart, r.Chunks[0].LineEnd));
        Assert.Equal((4, 4), (r.Chunks[1].LineStart, r.Chunks[1].LineEnd));
        Assert.Equal((6, 6), (r.Chunks[2].LineStart, r.Chunks[2].LineEnd));
    }

    [Fact]
    public void StructuredPart_CarriesKeyPathValueAndType()
    {
        var s = AdapterTestKit.Structured(Build(Sample).Chunks[1]);
        Assert.Equal("Ofac:TimeoutSeconds", s.GetProperty("key").GetString());
        Assert.Equal("$.Ofac.TimeoutSeconds", s.GetProperty("path").GetString());
        Assert.Equal("15", s.GetProperty("value").GetString());
        Assert.Equal("number", s.GetProperty("type").GetString());
        Assert.False(s.GetProperty("masked").GetBoolean());
    }

    [Fact]
    public void AShortListOfPlainValuesStaysTogether()
    {
        var list = Build(Sample).Chunks[2];
        Assert.Equal("Servers = [a, b]", list.Text);
        Assert.Equal("array", AdapterTestKit.Structured(list).GetProperty("type").GetString());
    }

    [Fact]
    public void ALongList_IsOneChunkPerElement()
    {
        var items = string.Join(", ", Enumerable.Range(1, ConfigAdapter.MaxInlineArray + 1).Select(i => $"\"v{i}\""));
        var r = Build($"{{ \"List\": [{items}] }}");
        Assert.Equal(ConfigAdapter.MaxInlineArray + 1, r.Chunks.Count);
        Assert.Equal("json:$.List[0]", r.Chunks[0].Locator);
        Assert.Equal("List:12", r.Chunks[^1].Label);
    }

    [Fact]
    public void ObjectsInAnArray_AreAddressedByIndex()
    {
        var r = Build("{ \"Queues\": [ { \"Name\": \"gl\" }, { \"Name\": \"audit\" } ] }");
        Assert.Equal(new[] { "json:$.Queues[0].Name", "json:$.Queues[1].Name" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("Queues:1:Name", r.Chunks[1].Label);
    }

    [Fact]
    public void AKeyThatIsNotAnIdentifier_UsesTheBracketForm()
    {
        var r = Build("{ \"Connection Strings\": { \"Default\": \"x\" }, \"it's\": 1 }");
        Assert.Equal("json:$['Connection Strings'].Default", r.Chunks[0].Locator);
        Assert.Equal("json:$['it\\'s']", r.Chunks[1].Locator);
    }

    [Fact]
    public void ASecretLookingValue_NeverEntersAChunk()
    {
        var r = Build("""
            { "Db": { "Password": "hunter2", "Conn": "Server=x;Password=hunter2;Timeout=5",
                      "ApiKey": "${API_KEY}", "Shared": "<<SECRET>>", "Token": "abc", "TokenTimeout": "30s", "Secret": "" } }
            """);
        string TextOf(string key) => r.Chunks.Single(c => c.Label == key).Text!;

        Assert.Equal("Db:Password = <<MASKED>>", TextOf("Db:Password"));
        Assert.Equal("Db:Conn = Server=x;Password=<<MASKED>>;Timeout=5", TextOf("Db:Conn"));
        Assert.Equal("Db:Token = <<MASKED>>", TextOf("Db:Token"));
        Assert.DoesNotContain(r.Chunks, c => c.Text!.Contains("hunter2") || (c.StructuredJson ?? "").Contains("hunter2"));
        Assert.True(AdapterTestKit.Structured(r.Chunks.Single(c => c.Label == "Db:Password")).GetProperty("masked").GetBoolean());
    }

    [Fact]
    public void APlaceholder_IsKept_BecauseItIsNotASecret()
    {
        var r = Build("""{ "Db": { "ApiKey": "${API_KEY}", "Password": "<<SECRET>>", "Secret": "" , "TokenTimeout": "30s" } }""");
        Assert.Equal("Db:ApiKey = ${API_KEY}", r.Chunks.Single(c => c.Label == "Db:ApiKey").Text);
        Assert.Equal("Db:Password = <<SECRET>>", r.Chunks.Single(c => c.Label == "Db:Password").Text);
        Assert.Equal("Db:Secret = \"\"", r.Chunks.Single(c => c.Label == "Db:Secret").Text);
        Assert.Equal("Db:TokenTimeout = 30s", r.Chunks.Single(c => c.Label == "Db:TokenTimeout").Text);
    }

    [Theory]
    [InlineData("    \"Password\": \"hunter2\",", "    \"Password\": \"<<MASKED>>\",")]
    [InlineData("\"ApiKey\":\"abc123\"", "\"ApiKey\":\"<<MASKED>>\"")]
    [InlineData("  \"Connection\": \"Server=x;Password=hunter2;Timeout=5\"", "  \"Connection\": \"Server=x;Password=<<MASKED>>;Timeout=5\"")]
    [InlineData("  \"Connection\": \"Server=x;Pwd=hunter2\"", "  \"Connection\": \"Server=x;Pwd=<<MASKED>>\"")]
    [InlineData("    \"Password\": \"<<SECRET>>\"", "    \"Password\": \"<<SECRET>>\"")]
    [InlineData("    \"Password\": \"${DB_PASS}\"", "    \"Password\": \"${DB_PASS}\"")]
    [InlineData("    \"Password\": \"\"", "    \"Password\": \"\"")]
    [InlineData("    \"TimeoutSeconds\": 15", "    \"TimeoutSeconds\": 15")]
    [InlineData("    \"TokenTimeout\": \"30s\"", "    \"TokenTimeout\": \"30s\"")]
    [InlineData("  \"Reconciliation\": { \"Password\": \"p;q\", \"Path\": \"x\" }", "  \"Reconciliation\": { \"Password\": \"<<MASKED>>\", \"Path\": \"x\" }")]
    public void TheLinesTheViewerShows_HideASecretTheSameWayTheChunkDoes(string line, string expected) =>
        Assert.Equal(expected, ConfigAdapter.RedactLine(line));

    [Fact]
    public void ABackslashPath_RoundTrips()
    {
        var r = Build("""{ "Reconciliation": { "VaultFilePath": "\\\\vault-prd\\export\\" } }""");
        Assert.Equal(@"Reconciliation:VaultFilePath = \\vault-prd\export\", r.Chunks.Single().Text);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreTolerated()
    {
        var r = Build("{\n  // timeout\n  \"A\": 1,\n  /* more */ \"B\": 2,\n}\n");
        Assert.Equal(new[] { "json:$.A", "json:$.B" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal(3, r.Chunks[0].LineStart);
    }

    [Fact]
    public void ABom_IsIgnored() => Assert.Single(Build("\uFEFF{ \"A\": 1 }").Chunks);

    [Fact]
    public void AnEmptyObject_HasNothingToCite() => Assert.Empty(Build("{ \"A\": {}, \"B\": [] }").Chunks);

    [Fact]
    public void ADuplicateKey_StaysCitable()
    {
        var r = Build("{ \"A\": 1, \"A\": 2 }");
        Assert.Equal(new[] { "json:$.A", "json:$.A~2" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void InvalidJson_FallsBackToLineWindows_AndSaysSo()
    {
        var r = Build("{ \"A\": \n");
        Assert.Single(r.Chunks);
        Assert.StartsWith("L", r.Chunks[0].Locator);
        Assert.Contains("not valid JSON", r.Warnings[0]);
    }

    [Fact]
    public void TooDeep_FallsBackToo()
    {
        var deep = string.Concat(Enumerable.Repeat("{\"a\":", ConfigAdapter.MaxDepth + 5)) + "1" + string.Concat(Enumerable.Repeat("}", ConfigAdapter.MaxDepth + 5));
        Assert.Contains("not valid JSON", Build(deep).Warnings[0]);
    }

    [Fact]
    public void ARootScalar_IsOneChunk() => Assert.Equal("json:$", Build("5").Chunks.Single().Locator);
}

public class CsvReaderTests
{
    [Fact]
    public void QuotedValues_MayHoldDelimitersQuotesAndLineBreaks_AndKeepTheirLines()
    {
        var text = "a,b\n1,\"x, y\"\n2,\"he said \"\"hi\"\"\"\n3,\"two\nlines\"\n4,z\n";
        var records = CsvReader.Read(text, ',', out var open);
        Assert.False(open);
        Assert.Equal(new[] { "1", "x, y" }, records[1].Fields);
        Assert.Equal("he said \"hi\"", records[2].Fields[1]);
        Assert.Equal("two\nlines", records[3].Fields[1]);
        Assert.Equal((4, 5), (records[3].StartLine, records[3].EndLine));
        Assert.Equal((6, 6), (records[4].StartLine, records[4].EndLine));
    }

    [Fact]
    public void WindowsAndOldMacLineEndings_AreLineEndings()
    {
        var records = CsvReader.Read("a,b\r\n1,2\r3,4\r\n", ',', out _);
        Assert.Equal(3, records.Count);
        Assert.Equal(new[] { "3", "4" }, records[2].Fields);
    }

    [Fact]
    public void BlankLines_AreNotRecords_ButCountAsLines()
    {
        var records = CsvReader.Read("a\n\n\nb\n", ',', out _);
        Assert.Equal(new[] { "a", "b" }, records.Select(r => r.Fields[0]));
        Assert.Equal(4, records[1].StartLine);
    }

    [Fact]
    public void AnUnclosedQuote_IsReported_AndTheRestIsOneValue()
    {
        var records = CsvReader.Read("a,b\n1,\"never closed\nstill going\n", ',', out var open);
        Assert.True(open);
        Assert.Equal("never closed\nstill going", records[1].Fields[1]);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3\n", ';')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("a|b|c\n", '|')]
    [InlineData("a,b,c\n", ',')]
    [InlineData("just one column\nsecond\n", ',')]
    public void TheDelimiter_IsSniffedFromTheHeader(string text, char expected) =>
        Assert.Equal(expected, CsvReader.SniffDelimiter(text));

    [Fact]
    public void ADelimiterInsideQuotes_DoesNotCount() =>
        Assert.Equal(',', CsvReader.SniffDelimiter("\"a;b;c;d\",e,f\n"));

    [Fact]
    public void AMidFieldQuote_IsLiteral()
    {
        var records = CsvReader.Read("a,b\n5\" pipe,x\n", ',', out _);
        Assert.Equal("5\" pipe", records[1].Fields[0]);
    }
}

public class TicketAdapterTests
{
    private static readonly string Export = string.Join('\n', new[]
    {
        "number,opened_at,priority,state,short_description,description,close_notes",
        "INC-T-0412,2026-03-02 02:11,2,Closed,TLR_EOD_POST fails with 2627,\"Violation of UNIQUE KEY constraint 'UQ_TLR_STAGE_TXN_REF'.",
        "Cannot insert duplicate key (TXN-77).\",Deleted the duplicate staged row and restarted from step 3",
        "INC-T-0413,2026-03-03 09:00,4,Open,Receipt printer jams,,",
    }) + "\n";

    private static AdapterResult Build(string csv, string path = "tickets/export.csv") => AdapterTestKit.Build(path, "ticket", csv);

    [Fact]
    public void EachRowIsAChunk_LocatedByItsTicketNumber()
    {
        var r = Build(Export);
        Assert.Equal(new[] { "row:INC-T-0412", "row:INC-T-0413" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("INC-T-0412 · TLR_EOD_POST fails with 2627", r.Chunks[0].Label);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void TheTextIsTheRowAsColumnLines_SoNamesAndCodesCanBeFoundVerbatim()
    {
        var text = Build(Export).Chunks[0].Text!;
        Assert.Contains("number: INC-T-0412", text);
        Assert.Contains("priority: 2", text);
        Assert.Contains("UQ_TLR_STAGE_TXN_REF", text);
        Assert.Contains("Cannot insert duplicate key (TXN-77).", text);
        Assert.DoesNotContain("close_notes: \n", text);
    }

    [Fact]
    public void AnEmptyValue_IsLeftOut()
    {
        var text = "\n" + Build(Export).Chunks[1].Text!;
        Assert.DoesNotContain("\ndescription:", text);
        Assert.DoesNotContain("\nclose_notes:", text);
        Assert.Contains("\nshort_description: Receipt printer jams", text);
    }

    [Fact]
    public void ARowKeepsEveryLineItOccupies()
    {
        var r = Build(Export);
        Assert.Equal((2, 3), (r.Chunks[0].LineStart, r.Chunks[0].LineEnd));
        Assert.Equal((4, 4), (r.Chunks[1].LineStart, r.Chunks[1].LineEnd));
    }

    [Fact]
    public void StructuredPart_HasTheFactsClusteringNeeds_AndEveryField()
    {
        var s = AdapterTestKit.Structured(Build(Export).Chunks[0]);
        Assert.Equal("INC-T-0412", s.GetProperty("id").GetString());
        Assert.Equal("TLR_EOD_POST fails with 2627", s.GetProperty("title").GetString());
        Assert.Equal("2", s.GetProperty("priority").GetString());
        Assert.Equal("Closed", s.GetProperty("state").GetString());
        Assert.Equal("2026-03-02 02:11", s.GetProperty("openedAt").GetString());
        // A list in column order (an object would lose its order once stored as jsonb).
        var fields = s.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "number", "opened_at", "priority", "state", "short_description", "description", "close_notes" }, fields);
        Assert.Equal("INC-T-0412", s.GetProperty("fields")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void ColumnsAreRecognisedByTheirUsualNames()
    {
        var r = Build("Ticket ID,Summary,Severity,Status,Created\nT-9,Printer jam,High,Open,2026-01-01\n");
        var s = AdapterTestKit.Structured(r.Chunks.Single());
        Assert.Equal("row:T-9", r.Chunks.Single().Locator);
        Assert.Equal("Printer jam", s.GetProperty("title").GetString());
        Assert.Equal("High", s.GetProperty("priority").GetString());
        Assert.Equal("Open", s.GetProperty("state").GetString());
        Assert.Equal("2026-01-01", s.GetProperty("openedAt").GetString());
    }

    [Fact]
    public void WithoutATicketNumberColumn_RowsAreCitedByPosition_AndTheFileSaysSo()
    {
        var r = Build("title,state\nA,Open\nB,Closed\n");
        Assert.Equal(new[] { "row:1", "row:2" }, r.Chunks.Select(c => c.Locator));
        Assert.Contains("ticket number", r.Warnings[0]);
    }

    [Fact]
    public void ARepeatedNumber_StaysCitable()
    {
        var r = Build("number,title\nINC-1,A\nINC-1,B\n");
        Assert.Equal(new[] { "row:INC-1", "row:INC-1~2" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void ANumberWithSpaces_BecomesALocatorWithoutThem() =>
        Assert.Equal("row:INC_T_1", Build("number,title\nINC_T 1,A\n").Chunks.Single().Locator.Replace(" ", "_"));

    [Fact]
    public void RaggedRows_AreKept_AndCountedInOneWarning()
    {
        var r = Build("number,title,state\nINC-1,A\nINC-2,B,Open,extra\nINC-3,C,Open\n");
        Assert.Equal(3, r.Chunks.Count);
        Assert.Contains("2 rows have a different number of columns", r.Warnings.Single());
        Assert.Contains("column_4: extra", r.Chunks[1].Text);
    }

    [Fact]
    public void BlankAndDuplicateHeaders_AreMadeUsable()
    {
        var r = Build("number,,state,state\nINC-1,x,a,b\n");
        var keys = AdapterTestKit.Structured(r.Chunks.Single()).GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "number", "column_2", "state", "state_2" }, keys);
    }

    [Fact]
    public void ARowWithNothingInIt_IsSkipped() =>
        Assert.Single(Build("number,title\nINC-1,A\n,\n,,\n").Chunks);

    [Fact]
    public void AHeaderOnly_HasNoTickets_AndSaysSo()
    {
        var r = Build("number,title\n");
        Assert.Empty(r.Chunks);
        Assert.Contains("no tickets", r.Warnings.Single());
    }

    [Fact]
    public void ASemicolonExport_IsRead()
    {
        var r = Build("number;title\nINC-1;A, with comma\n");
        Assert.Equal("INC-1 · A, with comma", r.Chunks.Single().Label);
    }

    [Fact]
    public void TheLabel_IsTheNumberAlone_WhenThereIsNoTitle() =>
        Assert.Equal("INC-1", Build("number,state\nINC-1,Open\n").Chunks.Single().Label);

    [Fact]
    public void AnUnclosedQuote_IsMentioned() =>
        Assert.Contains(Build("number,title\nINC-1,\"oops\n").Warnings, w => w.Contains("never closed"));
}

public class TranscriptAdapterTests
{
    private static AdapterResult Build(string text, string path = "kt/session1.vtt") => AdapterTestKit.Build(path, "transcript", text);

    private static readonly string Vtt = AdapterTestKit.Lines(
        "WEBVTT",
        "",
        "1",
        "00:00:05.000 --> 00:00:10.000",
        "<v Morgan Lee>How does the nightly chain restart?</v>",
        "",
        "2",
        "00:00:11.000 --> 00:00:20.500",
        "<v Dale Henry>It restarts from the top.</v>",
        "",
        "3",
        "00:00:21.000 --> 00:00:30.000",
        "<v Dale Henry>Then you check the log.</v>");

    [Fact]
    public void ASpeakersConsecutiveCues_AreOneTurn_LocatedByTime()
    {
        var r = Build(Vtt);
        Assert.Equal(new[] { "t=00:00:05-00:00:10", "t=00:00:11-00:00:30" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("Dale Henry: It restarts from the top. Then you check the log.", r.Chunks[1].Text);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ATurnKeepsTheLinesOfItsCues()
    {
        var r = Build(Vtt);
        Assert.Equal((3, 5), (r.Chunks[0].LineStart, r.Chunks[0].LineEnd));
        Assert.Equal((7, 13), (r.Chunks[1].LineStart, r.Chunks[1].LineEnd));
    }

    [Fact]
    public void StructuredPart_HasSpeakerAndTimes()
    {
        var s = AdapterTestKit.Structured(Build(Vtt).Chunks[1]);
        Assert.Equal("Dale Henry", s.GetProperty("speaker").GetString());
        Assert.Equal(11, s.GetProperty("startSeconds").GetDouble());
        Assert.Equal(30, s.GetProperty("endSeconds").GetDouble());
        Assert.Equal("00:00:11", s.GetProperty("start").GetString());
        Assert.Equal(2, s.GetProperty("cues").GetInt32());
    }

    [Fact]
    public void TheLabelNamesTheSpeaker_TheMoment_AndTheWords()
    {
        Assert.Equal("Morgan Lee · 0:05 · How does the nightly chain restart?", Build(Vtt).Chunks[0].Label);
    }

    [Fact]
    public void ThePlantedMoment_IsLocatedToTheSecond()
    {
        var r = Build(AdapterTestKit.Lines(
            "WEBVTT", "",
            "00:14:32.000 --> 00:15:40.000",
            "<v Dale Henry>Delete the staged duplicate and rerun from step three.</v>"));
        Assert.Equal("t=00:14:32-00:15:40", r.Chunks.Single().Locator);
    }

    [Fact]
    public void AnEndWithAFraction_IsRoundedUp_SoTheMomentIsWhollyInside()
    {
        var r = Build(AdapterTestKit.Lines("WEBVTT", "", "00:00:01.200 --> 00:00:04.100", "<v A B>Hi</v>"));
        Assert.Equal("t=00:00:01-00:00:05", r.Chunks.Single().Locator);
    }

    [Fact]
    public void ADifferentSpeaker_StartsANewTurn_AndSoDoesALongPause()
    {
        var r = Build(AdapterTestKit.Lines(
            "WEBVTT", "",
            "00:00:00.000 --> 00:00:05.000", "<v Dale Henry>One.</v>", "",
            "00:00:20.000 --> 00:00:25.000", "<v Dale Henry>Two after a pause.</v>"));
        Assert.Equal(2, r.Chunks.Count);
    }

    [Fact]
    public void ALongMonologue_IsSplitIntoTurnsNoLongerThanAboutAMinuteAndAHalf()
    {
        var cues = Enumerable.Range(0, 10).Select(i => $"00:{i * 10 / 60:00}:{i * 10 % 60:00}.000 --> 00:{(i * 10 + 10) / 60:00}:{(i * 10 + 10) % 60:00}.000\n<v Dale Henry>Sentence {i}.</v>\n");
        var r = Build("WEBVTT\n\n" + string.Join('\n', cues));
        Assert.True(r.Chunks.Count >= 2);
        Assert.All(r.Chunks, c => Assert.True(AdapterTestKit.Structured(c).GetProperty("endSeconds").GetDouble() - AdapterTestKit.Structured(c).GetProperty("startSeconds").GetDouble() <= TranscriptAdapter.MaxTurnSeconds));
    }

    [Fact]
    public void TheHeader_NoteBlocks_CueIds_AndSettings_AreNotText()
    {
        var r = Build(AdapterTestKit.Lines(
            "WEBVTT - KT session", "Kind: captions", "",
            "NOTE this is a comment", "that spans two lines", "",
            "intro", "00:00:01.000 --> 00:00:03.000 align:start position:0%", "<v Dale Henry>Hello.</v>"));
        Assert.Equal("Dale Henry: Hello.", r.Chunks.Single().Text);
    }

    [Fact]
    public void ShortTimestamps_AndSrtCommas_AreRead()
    {
        var r = Build(AdapterTestKit.Lines("1", "00:05,500 --> 00:09,000", "Hello there.", "", "2", "01:02:03,000 --> 01:02:05,000", "Later."));
        Assert.Equal(new[] { "t=00:00:05-00:00:09", "t=01:02:03-01:02:05" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void MarkupAndEntities_AreCleanedOut()
    {
        var r = Build(AdapterTestKit.Lines("WEBVTT", "", "00:00:01.000 --> 00:00:03.000", "<v Dale Henry><i>R&amp;D</i> is <c.loud>on</c> <00:00:02.000>fire</v>"));
        Assert.Equal("Dale Henry: R&D is on fire", r.Chunks.Single().Text);
    }

    [Fact]
    public void TwoVoicesInOneCue_AreTwoTurns()
    {
        var r = Build(AdapterTestKit.Lines("WEBVTT", "", "00:00:01.000 --> 00:00:03.000", "<v Dale Henry>Yes.</v> <v Morgan Lee>Right.</v>"));
        Assert.Equal(new[] { "Dale Henry: Yes.", "Morgan Lee: Right." }, r.Chunks.Select(c => c.Text));
    }

    [Fact]
    public void ANamePrefix_IsASpeaker_WhenItRecurs()
    {
        var r = Build(AdapterTestKit.Lines(
            "WEBVTT", "",
            "00:00:01.000 --> 00:00:03.000", "Dale Henry: First.", "",
            "00:00:10.000 --> 00:00:12.000", "Morgan Lee: Question?", "",
            "00:00:20.000 --> 00:00:22.000", "Dale Henry: Second.", "",
            "00:00:30.000 --> 00:00:32.000", "Morgan Lee: Thanks."));
        Assert.Equal(new[] { "Dale Henry", "Morgan Lee", "Dale Henry", "Morgan Lee" }, r.Chunks.Select(c => AdapterTestKit.Structured(c).GetProperty("speaker").GetString()));
        Assert.Equal("Dale Henry: First.", r.Chunks[0].Text);
    }

    [Fact]
    public void AOneOffPrefix_IsJustSomethingSaid()
    {
        var r = Build(AdapterTestKit.Lines(
            "WEBVTT", "",
            "00:00:01.000 --> 00:00:03.000", "Right.", "",
            "00:00:10.000 --> 00:00:12.000", "Right again.", "",
            "00:00:20.000 --> 00:00:22.000", "Note: the restart point matters.", "",
            "00:00:30.000 --> 00:00:32.000", "Done."));
        Assert.DoesNotContain(r.Chunks, c => c.StructuredJson!.Contains("\"speaker\""));
        Assert.Contains(r.Chunks, c => c.Text!.Contains("Note: the restart point matters."));
    }

    [Fact]
    public void ACueWithAnUnreadableTiming_IsLeftOut_AndCounted()
    {
        var r = Build(AdapterTestKit.Lines("WEBVTT", "", "00:00:01.000 --> later", "bad", "", "00:00:05.000 --> 00:00:06.000", "good"));
        Assert.Equal("good", r.Chunks.Single().Text);
        Assert.Contains("timing that could not be read", r.Warnings.Single());
    }

    [Fact]
    public void BracketedTimestamps_InPlainText_AreRead_AndAContinuationLineJoinsItsCue()
    {
        var r = Build(AdapterTestKit.Lines(
            "Interview with Dale",
            "[00:00:05] Morgan Lee: How does it restart?",
            "[00:00:11] Dale Henry: From the top.",
            "and then the log.",
            "[00:00:30] Morgan Lee: Thanks."), "kt/notes.txt");
        Assert.Equal(new[] { "t=00:00:05-00:00:11", "t=00:00:11-00:00:30", "t=00:00:30-00:00:33" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("Dale Henry: From the top. and then the log.", r.Chunks[1].Text);
        Assert.Equal((3, 4), (r.Chunks[1].LineStart, r.Chunks[1].LineEnd));
        Assert.True(AdapterTestKit.Structured(r.Chunks[2]).GetProperty("endEstimated").GetBoolean());
    }

    [Fact]
    public void AnEndOfPlainText_IsEstimated_NotInvented()
    {
        var r = Build("[00:00:10] Dale Henry: A short remark.\n", "kt/notes.txt");
        Assert.True(AdapterTestKit.Structured(r.Chunks.Single()).GetProperty("endEstimated").GetBoolean());
    }

    [Theory]
    [InlineData("00:14:32 Dale Henry: Delete the duplicate.")]
    [InlineData("[14:32] Dale Henry: Delete the duplicate.")]
    [InlineData("(00:14:32) - Dale Henry: Delete the duplicate.")]
    [InlineData("Dale Henry (00:14:32): Delete the duplicate.")]
    [InlineData("Dale Henry [14:32] Delete the duplicate.")]
    public void TheCommonPlainTextShapes_AreAllRead(string line)
    {
        var r = Build(line + "\n", "kt/notes.txt");
        var chunk = Assert.Single(r.Chunks);
        Assert.StartsWith("t=00:14:32-", chunk.Locator);
        Assert.Contains("Delete the duplicate.", chunk.Text);
    }

    [Fact]
    public void ASpeakerAndTimeOnOneLine_ThenTheWordsBelow_IsRead()
    {
        var r = Build(AdapterTestKit.Lines("Dale Henry  14:32", "Delete the duplicate", "and rerun.", "", "Morgan Lee  15:10", "Understood."), "kt/otter.txt");
        Assert.Equal(new[] { "Dale Henry: Delete the duplicate and rerun.", "Morgan Lee: Understood." }, r.Chunks.Select(c => c.Text));
    }

    [Fact]
    public void ATranscriptWithNoTiming_IsCutIntoLineWindows_AndSaysItCannotPlay()
    {
        var r = Build("Dale said this.\nMorgan said that.\n", "kt/notes.txt");
        Assert.Equal("L1-2", r.Chunks.Single().Locator);
        Assert.Contains("No timestamps", r.Warnings[0]);
    }

    [Fact]
    public void TwoTurnsStartingInTheSameSecond_StayCitable()
    {
        var r = Build(AdapterTestKit.Lines(
            "WEBVTT", "",
            "00:00:01.100 --> 00:00:01.400", "<v A One>x</v>", "",
            "00:00:01.500 --> 00:00:01.800", "<v B Two>y</v>"));
        Assert.Equal(new[] { "t=00:00:01-00:00:02", "t=00:00:01-00:00:02~2" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void TimesAreFormattedForLocatorsAndForPeople()
    {
        Assert.Equal("00:14:32", TranscriptAdapter.Hms(872.9));
        Assert.Equal("01:02:03", TranscriptAdapter.Hms(3723));
        Assert.Equal("14:32", TranscriptAdapter.Display(872.9));
        Assert.Equal("1:02:03", TranscriptAdapter.Display(3723));
        Assert.Equal("0:05", TranscriptAdapter.Display(5));
    }

    [Theory]
    [InlineData("t=00:14:32-00:15:40", 872, 940)]
    [InlineData("t=00:14:32", 872, 872)]
    [InlineData("t=00:14:32-00:15:40~2", 872, 940)]
    public void ATimeLocator_CanBeReadBack(string locator, double start, double end)
    {
        Assert.True(TranscriptAdapter.TryParseLocator(locator, out var s, out var e));
        Assert.Equal((start, end), (s, e));
    }

    [Theory]
    [InlineData("L1-4")]
    [InlineData("row:INC-1")]
    [InlineData("t=nonsense")]
    [InlineData(null)]
    public void ANonTimeLocator_IsNotATime(string? locator) => Assert.False(TranscriptAdapter.TryParseLocator(locator, out _, out _));

    [Theory]
    [InlineData("14:32", 872)]
    [InlineData("00:14:32.500", 872.5)]
    [InlineData("1:02:03", 3723)]
    [InlineData("00:05,250", 5.25)]
    public void Times_AreParsed(string s, double seconds)
    {
        Assert.True(TranscriptAdapter.TryTime(s, out var got));
        Assert.Equal(seconds, got, 3);
    }

    [Theory]
    [InlineData("14:75")]
    [InlineData("nope")]
    [InlineData("")]
    public void NotATime_IsRejected(string s) => Assert.False(TranscriptAdapter.TryTime(s, out _));
}

public class MarkdownAdapterTests
{
    private static AdapterResult Build(string text, string path = "docs/notes.md") => AdapterTestKit.Build(path, "document", text);

    private static readonly string Notes = AdapterTestKit.Lines(
        "Text before any heading.",
        "",
        "# Overview",
        "The platform does X.",
        "",
        "## Users",
        "Tellers.",
        "",
        "# Restart",
        "Restart from the failed step.",
        "",
        "```bash",
        "# not a heading",
        "echo hi",
        "```");

    [Fact]
    public void EachHeadingWithItsBody_IsAChunk_AndTheTextBeforeTheFirstIsThePreamble()
    {
        var r = Build(Notes);
        Assert.Equal(new[] { "h=Preamble", "h=Overview", "h=Users", "h=Restart" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("# Overview\nThe platform does X.", r.Chunks[1].Text);
    }

    [Fact]
    public void ASectionKeepsItsLines_WithoutTheBlankOnesAtItsEdges()
    {
        var r = Build(Notes);
        Assert.Equal((1, 1), (r.Chunks[0].LineStart, r.Chunks[0].LineEnd));
        Assert.Equal((3, 4), (r.Chunks[1].LineStart, r.Chunks[1].LineEnd));
        Assert.Equal((6, 7), (r.Chunks[2].LineStart, r.Chunks[2].LineEnd));
        Assert.Equal((9, 15), (r.Chunks[3].LineStart, r.Chunks[3].LineEnd));
    }

    [Fact]
    public void ANestedHeading_KnowsItsPath()
    {
        var r = Build(Notes);
        Assert.Equal("Overview › Users", r.Chunks[2].Label);
        var path = AdapterTestKit.Structured(r.Chunks[2]).GetProperty("path").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "Overview", "Users" }, path);
        Assert.Equal(2, AdapterTestKit.Structured(r.Chunks[2]).GetProperty("level").GetInt32());
    }

    [Fact]
    public void ALaterTopLevelHeading_StartsAFreshPath()
    {
        var path = AdapterTestKit.Structured(Build(Notes).Chunks[3]).GetProperty("path").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "Restart" }, path);
    }

    [Fact]
    public void AHashInsideACodeFence_IsNotAHeading() =>
        Assert.Contains("# not a heading", Build(Notes).Chunks[3].Text);

    [Fact]
    public void AHeadingWithNothingUnderIt_HasNothingToCite()
    {
        var r = Build("# Batch\n## TLR_EOD_LOCK\nLocks drawers.\n");
        Assert.Equal(new[] { "h=TLR_EOD_LOCK" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void InlineMarkup_DoesNotReachTheLocator()
    {
        var r = Build("# **Restart** from [the step](http://x) `now`\nText.\n");
        Assert.Equal("h=Restart from the step now", r.Chunks.Single().Locator);
    }

    [Fact]
    public void ARepeatedHeading_StaysCitable()
    {
        var r = Build("# Notes\nA\n# Notes\nB\n");
        Assert.Equal(new[] { "h=Notes", "h=Notes~2" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void ALongSection_IsSplitIntoParts_ThatAreLabelledAsSuch()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("word", 60));
        var body = string.Join("\n\n", Enumerable.Repeat(paragraph, 40));
        var r = Build("# Long\n" + body + "\n");
        Assert.True(r.Chunks.Count >= 3);
        Assert.All(r.Chunks, c => Assert.True(c.Text!.Length <= MarkdownAdapter.SectionChars + paragraph.Length));
        Assert.Contains("part 1 of", r.Chunks[0].Label);
        Assert.StartsWith("# Long", r.Chunks[0].Text);
        Assert.DoesNotContain("# Long", r.Chunks[1].Text);
        Assert.Equal(r.Chunks.Count, r.Chunks.Select(c => c.Locator).Distinct().Count());
    }

    [Fact]
    public void AFileWithNoHeadings_IsOnePreamble() =>
        Assert.Equal("h=Preamble", Build("Just notes.\nMore.\n").Chunks.Single().Locator);

    [Fact]
    public void AnEmptyFile_HasNoChunks() => Assert.Empty(Build("\n\n").Chunks);
}

public class DocxAdapterTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string P(string text, string? style = null, int? list = null) =>
        "<w:p>"
        + (style is null && list is null ? "" : "<w:pPr>"
            + (style is null ? "" : $"<w:pStyle w:val=\"{style}\"/>")
            + (list is null ? "" : $"<w:numPr><w:ilvl w:val=\"{list}\"/><w:numId w:val=\"1\"/></w:numPr>")
            + "</w:pPr>")
        + $"<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static string Row(params string[] cells) =>
        "<w:tr>" + string.Concat(cells.Select(c => $"<w:tc>{P(c)}</w:tc>")) + "</w:tr>";

    private static string Style(string id, string name, string? outline = null) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/>"
        + (outline is null ? "" : $"<w:pPr><w:outlineLvl w:val=\"{outline}\"/></w:pPr>") + "</w:style>";

    private static byte[] Docx(string body, string? styles = null, string? documentXml = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("word/document.xml", documentXml ?? $"<w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>");
            if (styles is not null) Add("word/styles.xml", $"<w:styles xmlns:w=\"{W}\">{styles}</w:styles>");
        }
        return ms.ToArray();
    }

    private static AdapterResult Build(byte[] bytes, string path = "docs/Notes.docx") =>
        EvidenceAdapters.Build(new AdapterInput(path, "document", null, bytes));

    private static readonly string StyleSheet = Style("Heading1", "heading 1") + Style("Heading2", "heading 2") + Style("Title", "Title");

    [Fact]
    public void Headings_FoundByStyle_CutTheDocumentIntoSections()
    {
        var r = Build(Docx(
            P("Overview", "Heading1") + P("Teller Lite takes ") + P("payments.") +
            P("Users", "Heading2") + P("Tellers", list: 0) + P("Supervisors", list: 1) +
            P("Restart", "Heading1") + P("Restart from the failed step."), StyleSheet));

        Assert.Equal(new[] { "h=Overview", "h=Users", "h=Restart" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("Overview\nTeller Lite takes\npayments.", r.Chunks[0].Text);
        Assert.Equal("Users\n- Tellers\n  - Supervisors", r.Chunks[1].Text);
        Assert.Equal("Overview › Users", r.Chunks[1].Label);
    }

    [Fact]
    public void AWordFile_HasNoLines()
    {
        var r = Build(Docx(P("Overview", "Heading1") + P("Text."), StyleSheet));
        Assert.All(r.Chunks, c => Assert.Null(c.LineStart));
    }

    [Fact]
    public void ATable_BecomesOneLinePerRow()
    {
        var r = Build(Docx(
            P("Batch", "Heading1") +
            "<w:tbl>" + Row("Job", "When") + Row("TLR_EOD", "20:30") + Row("", "") + "</w:tbl>", StyleSheet));
        Assert.Equal("Batch\nJob | When\nTLR_EOD | 20:30", r.Chunks.Single().Text);
    }

    [Fact]
    public void TextBeforeTheFirstHeading_IsThePreamble_AndTitleIsATopHeading()
    {
        var r = Build(Docx(P("Before.") + P("Teller Ops Notes", "Title") + P("Draft 2019.") + P("Overview", "Heading1") + P("Body."), StyleSheet));
        Assert.Equal(new[] { "h=Preamble", "h=Teller Ops Notes", "h=Overview" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void WithoutAStyleSheet_TheStyleIdIsTriedByName()
    {
        var r = Build(Docx(P("Overview", "Heading1") + P("Body.")));
        Assert.Equal("h=Overview", r.Chunks.Single().Locator);
    }

    [Fact]
    public void AnOutlineLevel_MakesAnyStyleAHeading()
    {
        var r = Build(Docx(P("Known issues", "Custom") + P("Drawer rounding."), Style("Custom", "Section Heading", outline: "0")));
        Assert.Equal("h=Known issues", r.Chunks.Single().Locator);
    }

    [Fact]
    public void AStyleBasedOnAHeading_IsAHeading()
    {
        var based = "<w:style w:type=\"paragraph\" w:styleId=\"Mine\"><w:name w:val=\"Mine\"/><w:basedOn w:val=\"Heading2\"/></w:style>";
        var r = Build(Docx(P("Mine here", "Mine") + P("Body."), StyleSheet + based));
        Assert.Equal("h=Mine here", r.Chunks.Single().Locator);
        Assert.Equal(2, AdapterTestKit.Structured(r.Chunks.Single()).GetProperty("level").GetInt32());
    }

    [Fact]
    public void ContentInsideAContentControl_IsRead()
    {
        var sdt = $"<w:sdt><w:sdtContent>{P("Overview", "Heading1")}{P("Inside.")}</w:sdtContent></w:sdt>";
        Assert.Contains("Inside.", Build(Docx(sdt, StyleSheet)).Chunks.Single().Text);
    }

    [Fact]
    public void TabsBreaksAndDeletedText_AreHandled()
    {
        var para = "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t></w:r><w:del><w:r><w:delText>gone</w:delText></w:r></w:del></w:p>";
        var text = Build(Docx(P("H", "Heading1") + para, StyleSheet)).Chunks.Single().Text!;
        Assert.Contains("a\tb\nc", text);
        Assert.DoesNotContain("gone", text);
    }

    [Fact]
    public void ANonZipFile_IsReported_NotThrown()
    {
        var r = Build(Encoding.UTF8.GetBytes("this is not a zip"));
        Assert.Empty(r.Chunks);
        Assert.Contains("not a readable Word document", r.Warnings.Single());
    }

    [Fact]
    public void AZipWithoutADocumentPart_IsReported()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            zip.CreateEntry("hello.txt");
        var r = Build(ms.ToArray());
        Assert.Empty(r.Chunks);
        Assert.Contains("word/document.xml", r.Warnings.Single());
    }

    [Fact]
    public void ADocumentTypeDeclaration_IsRefused()
    {
        var evil = $"<!DOCTYPE x [<!ENTITY e \"boom\">]><w:document xmlns:w=\"{W}\"><w:body>{P("A", "Heading1")}{P("&e;")}</w:body></w:document>";
        var r = Build(Docx("", documentXml: evil));
        Assert.Empty(r.Chunks);
        Assert.Contains("could not be read", r.Warnings.Single());
    }

    [Fact]
    public void AnEmptyDocument_HasNoChunks() => Assert.Empty(Build(Docx("", StyleSheet)).Chunks);
}

public class XmlElementAdapterTests
{
    private static AdapterResult Build(string xml, string kind = "document", string path = "src/web/Teller.oml.xml") =>
        AdapterTestKit.Build(path, kind, xml);

    private static string Screens(int count, int widgets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Module Name=\"Teller\">");
        sb.AppendLine("  <Screens>");
        for (var s = 1; s <= count; s++)
        {
            sb.AppendLine($"    <Screen Name=\"S{s}\">");
            for (var w = 1; w <= widgets; w++) sb.AppendLine($"      <Widget Id=\"w{w}\" Type=\"Input\"/>");
            sb.AppendLine("    </Screen>");
        }
        sb.AppendLine("  </Screens>");
        sb.AppendLine("</Module>");
        return sb.ToString().Replace("\r\n", "\n");
    }

    [Fact]
    public void ASmallFile_IsOneChunk_LocatedByItsRoot()
    {
        var r = Build(AdapterTestKit.Lines("<JOBP Name=\"TLR_EOD\">", "  <Tasks>", "    <Task Name=\"A\"/>", "    <Task Name=\"B\"/>", "  </Tasks>", "</JOBP>"), "job", "batch/uc4/TLR_EOD.xml");
        var chunk = Assert.Single(r.Chunks);
        Assert.Equal("xpath:/JOBP", chunk.Locator);
        Assert.Equal("JOBP TLR_EOD", chunk.Label);
        Assert.Equal((1, 6), (chunk.LineStart, chunk.LineEnd));
    }

    [Fact]
    public void ALargeFile_IsOpenedUp_ChildrenBecomeChunks_AndContainersKeepTheirOpeningTag()
    {
        var r = Build(Screens(count: 3, widgets: 30));
        Assert.Equal(new[]
        {
            "xpath:/Module", "xpath:/Module/Screens",
            "xpath:/Module/Screens/Screen[1]", "xpath:/Module/Screens/Screen[2]", "xpath:/Module/Screens/Screen[3]",
        }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("Module Teller (opening)", r.Chunks[0].Label);
        Assert.Equal("Screen S2", r.Chunks[3].Label);
        Assert.Equal("<Module Name=\"Teller\">", r.Chunks[0].Text);
    }

    [Fact]
    public void AnElementKeepsItsOwnLinesAndText()
    {
        var xml = Screens(3, 30);
        var r = Build(xml);
        var second = r.Chunks[3];
        var lines = xml.Split('\n');
        Assert.Equal(string.Join('\n', lines[(second.LineStart!.Value - 1)..second.LineEnd!.Value]), second.Text);
        Assert.Equal(second.LineEnd - second.LineStart, 31);
    }

    [Fact]
    public void StructuredPart_HasTheElementItsPathAndItsAttributes()
    {
        var s = AdapterTestKit.Structured(Build(Screens(3, 30)).Chunks[3]);
        Assert.Equal("Screen", s.GetProperty("element").GetString());
        Assert.Equal("/Module/Screens/Screen[2]", s.GetProperty("xpath").GetString());
        Assert.Equal("S2", s.GetProperty("attributes").GetProperty("Name").GetString());
    }

    [Fact]
    public void ARunOfTinySiblings_IsOneChunkLocatedByARange()
    {
        var sb = new StringBuilder("<Root>\n  <Params>\n");
        for (var i = 1; i <= 100; i++) sb.AppendLine($"    <Param Name=\"p{i}\"/>");
        sb.AppendLine("  </Params>\n</Root>");
        var r = Build(sb.ToString().Replace("\r\n", "\n"));

        Assert.Equal(new[]
        {
            "xpath:/Root", "xpath:/Root/Params",
            "xpath:/Root/Params/Param[1..60]", "xpath:/Root/Params/Param[61..100]",
        }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("Param ×60", r.Chunks[2].Label);
        Assert.Equal("Param ×40", r.Chunks[3].Label);
    }

    [Fact]
    public void SameNamedSiblings_AreNumbered_OnlyWhenThereIsMoreThanOne()
    {
        var r = Build(Screens(count: 1, widgets: 60));
        Assert.Contains("xpath:/Module/Screens/Screen", r.Chunks.Select(c => c.Locator));
        Assert.DoesNotContain(r.Chunks, c => c.Locator.Contains("Screen["));
    }

    [Fact]
    public void ANamespace_DoesNotReachThePath()
    {
        var r = Build("<d:Doc xmlns:d=\"urn:x\" Name=\"n\">\n<d:Item/>\n</d:Doc>\n");
        Assert.Equal("xpath:/Doc", r.Chunks.Single().Locator);
        Assert.DoesNotContain("xmlns", r.Chunks.Single().StructuredJson!);
    }

    [Fact]
    public void ADeclarationOrCommentBeforeTheRoot_IsFine() =>
        Assert.Equal("xpath:/A", Build("<?xml version=\"1.0\"?>\n<!-- c -->\n<A/>\n").Chunks.Single().Locator);

    [Fact]
    public void ADocumentTypeDeclaration_IsRefused_AndTheFileFallsBackToLines()
    {
        var r = Build("<!DOCTYPE foo [<!ENTITY x \"y\">]>\n<foo>&x;</foo>\n");
        Assert.StartsWith("L", Assert.Single(r.Chunks).Locator);
        Assert.Contains("not well-formed", r.Warnings[0]);
    }

    [Fact]
    public void BrokenXml_FallsBackToLineWindows_AndSaysSo()
    {
        var r = Build("<a>\n<b>\n</a>\n");
        Assert.StartsWith("L", Assert.Single(r.Chunks).Locator);
        Assert.Contains("not well-formed", r.Warnings[0]);
    }

    [Fact]
    public void MinifiedXml_IsReIndented_ChunksCarryNoLines_AndTheFileSaysSo()
    {
        var sb = new StringBuilder("<Root>");
        for (var i = 1; i <= 400; i++) sb.Append($"<Item Name=\"item-{i}\"><Value>{new string('x', 10)}</Value></Item>");
        sb.Append("</Root>");
        var r = Build(sb.ToString());

        Assert.True(r.Chunks.Count > 3);
        Assert.All(r.Chunks, c =>
        {
            Assert.Null(c.LineStart);
            Assert.True(c.Text!.Length < 3000);
        });
        Assert.Contains("squeezed", r.Warnings.Single());
    }

    [Fact]
    public void ADeeplyNestedElement_GetsALocatorThatFitsTheColumn()
    {
        var depth = 150;
        var open = string.Concat(Enumerable.Range(0, depth).Select(i => $"{new string(' ', i)}<LongElementNameNumber{i}>\n"));
        var close = string.Concat(Enumerable.Range(0, depth).Reverse().Select(i => $"{new string(' ', i)}</LongElementNameNumber{i}>\n"));
        var r = Build(open + close);
        Assert.All(r.Chunks, c => Assert.True(c.Locator.Length <= EvidenceAdapters.MaxLocatorChars));
    }

    [Fact]
    public void EtlPackagesAndJobExports_GetTheSameTreatment()
    {
        Assert.Equal("xpath:/Executable", Build("<Executable Name=\"Import\"/>\n", "etl", "batch/ssis/DrawerRecon.dtsx").Chunks.Single().Locator);
        Assert.Equal("xpath:/JOB", Build("<JOB Name=\"X\"/>\n", "job", "batch/uc4/X.xml").Chunks.Single().Locator);
    }
}

public class AudioLinkingTests
{
    private static readonly string[] Transcripts = { "kt/session1.vtt", "kt/session2.vtt", "interviews/session1.txt" };

    [Fact]
    public void ARecordingPlaysWithTheTranscriptOfTheSameNameInTheSameFolder() =>
        Assert.Equal("kt/session1.vtt", AudioLinking.Resolve("kt/session1.mp3", null, Transcripts));

    [Fact]
    public void TheNameMatchIgnoresCase() =>
        Assert.Equal("kt/session1.vtt", AudioLinking.Resolve("KT/Session1.MP3", null, Transcripts));

    [Fact]
    public void AVttIsPreferredOverATxtInTheSameFolder() =>
        Assert.Equal("kt/a.vtt", AudioLinking.Resolve("kt/a.mp3", null, new[] { "kt/a.txt", "kt/a.vtt" }));

    [Fact]
    public void AUniqueNameElsewhere_IsEnough() =>
        Assert.Equal("interviews/only.vtt", AudioLinking.Resolve("audio/only.wav", null, new[] { "interviews/only.vtt", "kt/other.vtt" }));

    [Fact]
    public void AnAmbiguousNameElsewhere_IsNotGuessed() =>
        Assert.Null(AudioLinking.Resolve("audio/session1.wav", null, Transcripts));

    [Fact]
    public void NoMatchingName_IsUnlinked_EvenWithOnlyOneTranscript() =>
        Assert.Null(AudioLinking.Resolve("kt/recording.mp3", null, new[] { "kt/transcript.vtt" }));

    [Fact]
    public void AnExplicitLink_Wins()
    {
        Assert.Equal("kt/session2.vtt", AudioLinking.Resolve("kt/session1.mp3", "kt/session2.vtt", Transcripts));
    }

    [Fact]
    public void AnExplicitLinkToATranscriptThatIsGone_FallsBackToTheName() =>
        Assert.Equal("kt/session1.vtt", AudioLinking.Resolve("kt/session1.mp3", "kt/removed.vtt", Transcripts));

    [Fact]
    public void TheLink_IsKeptInTheFilesMeta_NextToAnythingElseThere()
    {
        var meta = AudioLinking.WithAttachesTo("{\"note\":\"x\"}", "kt/session2.vtt");
        Assert.Equal("kt/session2.vtt", AudioLinking.AttachesTo(meta));
        Assert.Contains("\"note\"", meta);

        var cleared = AudioLinking.WithAttachesTo(meta, null);
        Assert.Null(AudioLinking.AttachesTo(cleared));
        Assert.Contains("\"note\"", cleared);
    }

    [Fact]
    public void ClearingTheOnlyKey_LeavesNoMeta() => Assert.Null(AudioLinking.WithAttachesTo("{\"attachesTo\":\"a.vtt\"}", null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"attachesTo\":5}")]
    public void MetaThatIsNotALink_ReadsAsNone(string? meta) => Assert.Null(AudioLinking.AttachesTo(meta));

    [Fact]
    public void BrokenMeta_IsReplacedRatherThanFailing() =>
        Assert.Equal("kt/a.vtt", AudioLinking.AttachesTo(AudioLinking.WithAttachesTo("not json", "kt/a.vtt")));
}
