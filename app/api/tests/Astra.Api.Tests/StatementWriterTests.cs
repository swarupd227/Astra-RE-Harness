using System.Text.Json;
using Astra.Api.Docs;
using Astra.Api.Knowledge;
using Astra.Api.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The writer service for one slot, driven by a writer that answers as the test says: what it sends, what it keeps,
/// the one retry, what becomes an open question, and that a failure is reported with its reason.
/// </summary>
public class StatementWriterTests
{
    private const string Slot = "runbook:failure-handling";

    private sealed class ScriptedWriter : IDocWriter
    {
        private readonly Func<DocWriteRequest, int, string> _answer;
        public List<DocWriteRequest> Requests { get; } = new();
        public int Writes { get; private set; }

        public ScriptedWriter(Func<DocWriteRequest, int, string> answer) { _answer = answer; }

        public string ProviderName => "test";
        public bool IsMock => false;
        public string ConfigVersion => "test";

        public Task<DocWriteResult> WriteAsync(DocWriteRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var nth = request.ToolName == StatementWriter.ToolName ? ++Writes : 0;
            var json = _answer(request, nth);
            return Task.FromResult(new DocWriteResult(json, new AnthropicUsage(10, 5, 0, 0, request.Model), request.Model, 1, "end_turn"));
        }
    }

    private static readonly GateChunk Ddl = new("SQL-0000000a", "sql", "structured", "db/ddl.sql L10-18",
        "CREATE TABLE dbo.TLR_STAGE (\n    TXN_REF VARCHAR(40) NOT NULL,\n    CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)\n);", "db/ddl.sql L10-18");

    private static readonly GateChunk Ticket = new("TKT-0000000d", "ticket", "narrative", "INC-T-0412 · EOD post fails",
        "number: INC-T-0412\ndescription: TLR_EOD_POST failed with 2627; the second row with the same TXN_REF was deleted and the chain restarted from step 3",
        "tickets/export.csv row:INC-T-0412");

    private static StatementRequest Request(Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<GateChunk>>>? resolve = null, string replayBase = "") =>
        new("runbook", Slot, "Failure handling", "Write what to do when the nightly posting fails.", new[] { Ddl, Ticket }, resolve, ReplayBase: replayBase);

    private static StatementWriter Make(IDocWriter writer, IStatementFixtures? fixtures = null, int attempts = 2, Action<KnowledgeOptions>? tune = null)
    {
        var options = new KnowledgeOptions { MaxAttempts = attempts };
        tune?.Invoke(options);
        return new StatementWriter(
            writer, Options.Create(new DocsOptions()), Options.Create(options),
            new StatementPrompts("WRITER PROMPT", "SUPPORT PROMPT"), fixtures ?? new NoStatementFixtures(), NullLogger<StatementWriter>.Instance);
    }

    private static string Answer(params (string Text, string[] Cites)[] items) => JsonSerializer.Serialize(new
    {
        title = "Failure handling",
        sections = new[] { new { heading = "Posting", items = items.Select(i => new { text = i.Text, citations = i.Cites }).ToArray() } },
        openQuestions = Array.Empty<object>(),
    });

    /// <summary>Says yes to every claim it is given.</summary>
    private static string AllYes(DocWriteRequest support) => JsonSerializer.Serialize(new
    {
        verdicts = JsonDocument.Parse(support.UserMessage).RootElement.GetProperty("claims").EnumerateArray()
            .Select(c => new { id = c.GetProperty("id").GetString(), verdict = "yes" }).ToArray(),
    });

    private const string GoodStructured = "The `UQ_TLR_STAGE_TXN_REF` key stops a second staged row with the same TXN_REF.";
    private const string GoodNarrative = "INC-T-0412 shows TLR_EOD_POST failed with 2627 and the chain was restarted from step 3.";

    // ── the ordinary run ────────────────────────────────────────────────

    [Fact]
    public async Task AGroundedAnswer_IsKeptWithStatusesTheGateDecided_InTwoCallsAtTemperatureZero()
    {
        var writer = new ScriptedWriter((r, _) => r.ToolName == StatementWriter.ToolName
            ? Answer((GoodStructured, new[] { "SQL-0000000a" }), (GoodNarrative, new[] { "TKT-0000000d" }))
            : AllYes(r));

        var outcome = await Make(writer).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal(1, outcome.Attempts);
        Assert.Equal(2, outcome.Calls.Count);
        Assert.Equal(new GateCounts(Supported: 1, Unverified: 1, Conflict: 0, Dropped: 0), outcome.Counts);
        var items = outcome.Document.Items.ToDictionary(i => i.Citations[0]);
        Assert.Equal(StatementStatus.Supported, items["SQL-0000000a"].Status);      // structured evidence, and the check said yes
        Assert.Equal(StatementStatus.Unverified, items["TKT-0000000d"].Status);     // only a ticket says it
        Assert.All(outcome.Document.Items, i => Assert.Equal(Slot, i.SlotKey));
        Assert.Empty(outcome.Document.OpenQuestions);

        // Every call is repeatable: temperature 0, a replay key, the writer model for the write and the small one for the check.
        Assert.All(writer.Requests, r => Assert.Equal(0d, r.Temperature));
        Assert.Equal(new[] { "runbook:runbook:failure-handling:write:1", "runbook:runbook:failure-handling:write:1:support:1" }, writer.Requests.Select(r => r.ReplayKey).ToArray());
        Assert.Equal(new DocsOptions().SonnetModel, writer.Requests[0].Model);
        Assert.Equal(new KnowledgeOptions().SupportModel, writer.Requests[1].Model);
        Assert.Equal(new[] { "WRITER PROMPT" }, writer.Requests[0].SystemBlocks);
        Assert.Equal(new[] { "SUPPORT PROMPT" }, writer.Requests[1].SystemBlocks);
    }

    [Fact]
    public async Task TheWriter_IsShownOnlyTheEvidenceItWasGiven_EachPieceByItsId()
    {
        var writer = new ScriptedWriter((r, _) => r.ToolName == StatementWriter.ToolName ? Answer((GoodStructured, new[] { "SQL-0000000a" })) : AllYes(r));
        await Make(writer, tune: o => o.MaxChunkChars = 40).WriteAsync(Request(), CancellationToken.None);

        var message = writer.Requests[0].UserMessage;
        Assert.Contains("Slot: runbook:failure-handling — Failure handling", message);
        Assert.Contains("Write: Write what to do when the nightly posting fails.", message);
        Assert.Contains("[SQL-0000000a] sql · structured · db/ddl.sql L10-18", message);
        Assert.Contains("[TKT-0000000d] ticket · narrative · tickets/export.csv row:INC-T-0412", message);
        Assert.DoesNotContain("CONSTRAINT UQ_TLR_STAGE_TXN_REF", message);   // cut at MaxChunkChars
        Assert.DoesNotContain("Already accepted", message);
    }

    // ── the retry ───────────────────────────────────────────────────────

    [Fact]
    public async Task WhatTheGateThrowsAway_IsWrittenAgainOnce_AndMergedWithWhatAlreadyStood()
    {
        const string invented = "Run `usp_Invented` to clear it.";
        const string rewritten = "The ticket INC-T-0412 records TLR_EOD_POST failing with 2627 on a duplicate TXN_REF.";
        var writer = new ScriptedWriter((r, nth) =>
        {
            if (r.ToolName != StatementWriter.ToolName) return AllYes(r);
            return nth == 1
                ? Answer((GoodStructured, new[] { "SQL-0000000a" }), (invented, new[] { "SQL-0000000a" }))
                : Answer((rewritten, new[] { "TKT-0000000d" }));
        });

        var outcome = await Make(writer).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(new[] { GoodStructured, rewritten }, outcome.Document.Items.Select(i => i.Text).ToArray());
        var dropped = Assert.Single(outcome.Dropped);
        Assert.Equal(invented, dropped.Item.Text);
        Assert.Equal(GateRules.IdentifierNotInEvidence, Assert.Single(dropped.Issues).Rule);
        Assert.Empty(outcome.Document.OpenQuestions);   // the second answer was clean: nothing is left over
        Assert.Contains(outcome.Notes, n => n.Contains("thrown away") && n.Contains("again"));

        // The retry says what stands and what was thrown away, and why.
        var retry = writer.Requests.Single(r => r.ToolName == StatementWriter.ToolName && r.ReplayKey!.EndsWith(":write:2"));
        Assert.Contains("Already accepted", retry.UserMessage);
        Assert.Contains(GoodStructured, retry.UserMessage);
        Assert.Contains("thrown away", retry.UserMessage);
        Assert.Contains("usp_Invented", retry.UserMessage);
        Assert.Contains("does not appear in the evidence", retry.UserMessage);
        Assert.Equal(
            new[] { "write:1", "write:1:support:1", "write:2", "write:2:support:1" },
            writer.Requests.Select(r => r.ReplayKey!["runbook:runbook:failure-handling:".Length..]).ToArray());
    }

    [Fact]
    public async Task AStatementThatStillCannotBeGrounded_BecomesAnOpenQuestion_NotAClaim()
    {
        var writer = new ScriptedWriter((r, _) => r.ToolName == StatementWriter.ToolName
            ? Answer(("The job restarts from the top.", Array.Empty<string>()))
            : AllYes(r));

        var outcome = await Make(writer).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, outcome.Attempts);
        Assert.Empty(outcome.Document.Items);
        var question = Assert.Single(outcome.Document.OpenQuestions);
        Assert.Equal("Not confirmed by the evidence: The job restarts from the top.", question.Text);
        Assert.Equal(Slot, question.SlotKey);
        Assert.Equal(2, outcome.Dropped.Count);         // thrown away on each attempt, and said so
        Assert.Equal(0, outcome.Counts.Supported + outcome.Counts.Unverified);
    }

    [Fact]
    public async Task WithOneAttemptAllowed_NothingIsRetried()
    {
        var writer = new ScriptedWriter((r, _) => r.ToolName == StatementWriter.ToolName ? Answer(("No cite.", Array.Empty<string>())) : AllYes(r));
        var outcome = await Make(writer, attempts: 1).WriteAsync(Request(), CancellationToken.None);
        Assert.Equal(1, outcome.Attempts);
        Assert.Equal(1, writer.Writes);
        Assert.Single(outcome.Document.OpenQuestions);
    }

    [Fact]
    public async Task AnAnswerThatCannotBeRead_IsSaid_AndTheNextAttemptStartsFromScratch()
    {
        var writer = new ScriptedWriter((r, nth) =>
            r.ToolName != StatementWriter.ToolName ? AllYes(r)
            : nth == 1 ? "this is not json"
            : Answer((GoodStructured, new[] { "SQL-0000000a" })));

        var outcome = await Make(writer).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal(2, outcome.Attempts);
        Assert.Contains(outcome.Notes, n => n.Contains("could not be read"));
        Assert.Single(outcome.Document.Items);
    }

    // ── evidence the writer was not shown ───────────────────────────────

    [Fact]
    public async Task EvidenceCitedWithoutBeingShown_IsLookedUpInTheVersion_AndOneThatIsNotThereIsInvented()
    {
        var other = new GateChunk("DOC-00000099", "document", "narrative", "Notes · Restart", "Restart from the failed step.", "docs/notes.docx h=Restart");
        var writer = new ScriptedWriter((r, _) => r.ToolName == StatementWriter.ToolName
            ? Answer(("Restart from the failed step, as the notes say.", new[] { "DOC-00000099" }), ("Restart from step 3.", new[] { "DOC-0000dead" }))
            : AllYes(r));
        IReadOnlyCollection<string>? asked = null;

        var outcome = await Make(writer, attempts: 1).WriteAsync(Request((ids, _) =>
        {
            asked = ids;
            return Task.FromResult<IReadOnlyList<GateChunk>>(ids.Contains("DOC-00000099") ? new[] { other } : Array.Empty<GateChunk>());
        }), CancellationToken.None);

        Assert.Equal(new[] { "DOC-00000099", "DOC-0000dead" }, asked!.OrderBy(x => x).ToArray());
        Assert.Equal("DOC-00000099", Assert.Single(outcome.Document.Items).Citations.Single());
        Assert.Equal(GateRules.UnknownCitation, Assert.Single(Assert.Single(outcome.Dropped).Issues).Rule);
    }

    // ── failures ────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailedSupportCheck_LeavesTheStatementsUnverified_AndSaysWhy()
    {
        var writer = new ScriptedWriter((r, _) => r.ToolName == StatementWriter.ToolName
            ? Answer((GoodStructured, new[] { "SQL-0000000a" }))
            : throw new InvalidOperationException("provider says: overloaded"));

        var outcome = await Make(writer, attempts: 1).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal(StatementStatus.Unverified, Assert.Single(outcome.Document.Items).Status);   // structured, but never checked: not Supported
        Assert.Contains(outcome.Notes, n => n.Contains("provider says: overloaded"));
        Assert.Single(outcome.Calls);                       // the failed call is not recorded as made
        Assert.Contains(outcome.Issues, i => i.Rule == GateRules.SupportNotChecked);
    }

    [Fact]
    public async Task AFailedWrite_IsNotHidden_TheProvidersReasonReachesTheCaller()
    {
        var writer = new ScriptedWriter((_, _) => throw new InvalidOperationException("credit balance is too low"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Make(writer).WriteAsync(Request(), CancellationToken.None));
        Assert.Contains("credit balance is too low", ex.Message);
    }

    // ── offline: the mock and the fixtures ──────────────────────────────

    [Fact]
    public async Task WithTheMockProvider_EveryPieceOfEvidenceGivesAStatement_AndTheStatusesFollowTheTrustTier()
    {
        var outcome = await Make(new MockDocWriter()).WriteAsync(Request(), CancellationToken.None);

        Assert.Equal(1, outcome.Attempts);
        Assert.Equal(2, outcome.Document.Items.Count());
        var byCite = outcome.Document.Items.ToDictionary(i => i.Citations[0]);
        Assert.Equal(StatementStatus.Supported, byCite["SQL-0000000a"].Status);
        Assert.Equal(StatementStatus.Unverified, byCite["TKT-0000000d"].Status);
        Assert.All(outcome.Calls, c => Assert.Equal("mock", c.Result.Model));
        Assert.Equal(0, outcome.Dropped.Count);
    }

    [Fact]
    public async Task AFixture_PlantsAStatementTheGateMustCatch_WithoutAModel()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-fixtures-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var key = "runbook:runbook:failure-handling:write:1";
            Assert.Equal("runbook_runbook_failure-handling_write_1.json", DirectoryStatementFixtures.FileNameFor(key));
            File.WriteAllText(Path.Combine(dir, DirectoryStatementFixtures.FileNameFor(key)), Answer(
                (GoodStructured, new[] { "SQL-0000000a" }),
                ("Run `usp_Invented` to clear it.", new[] { "SQL-0000000a" }),                     // a name the evidence does not have
                ("The warehouse extract runs nightly at midnight.", new[] { "SQL-0000000a" })));  // evidence that does not say it

            var fixtures = new DirectoryStatementFixtures(new[] { dir });
            Assert.Null(fixtures.Find("runbook:runbook:failure-handling:write:2"));
            Assert.NotNull(fixtures.Find(key));

            var outcome = await Make(new MockDocWriter(), fixtures, attempts: 1).WriteAsync(Request(), CancellationToken.None);

            Assert.Equal(new[] { GoodStructured }, outcome.Document.Items.Select(i => i.Text).ToArray());
            Assert.Equal(
                new[] { GateRules.IdentifierNotInEvidence, GateRules.SupportNo },
                outcome.Dropped.Select(d => d.Issues.Single().Rule).ToArray());
            Assert.Equal(2, outcome.Document.OpenQuestions.Count);   // one attempt only: both are left as questions
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReplayKeys_NameTheCallByDeliverableSlotPurposeAndAttempt_OrByTheCallersBase()
    {
        Assert.Equal("runbook:runbook:failure-handling:write:1", StatementWriter.ReplayKeyFor(Request(), "write", 1));
        Assert.Equal("capture:item-7:write:2", StatementWriter.ReplayKeyFor(Request(replayBase: "capture:item-7"), "write", 2));
    }

    [Fact]
    public void TheMessage_ListsAtMostTheConfiguredNumberOfPiecesOfEvidence()
    {
        var many = Enumerable.Range(1, 6).Select(n => new GateChunk($"SQL-{n:x8}", "sql", "structured", $"L{n}", $"text {n}")).ToList();
        var request = new StatementRequest("runbook", Slot, "Failure handling", "x", many);
        var message = Make(new MockDocWriter(), tune: o => o.MaxEvidenceChunks = 3).BuildUserMessage(request, StatementDocument.Empty("runbook", ""), Array.Empty<DroppedStatement>());
        Assert.Contains("[SQL-00000003]", message);
        Assert.DoesNotContain("[SQL-00000004]", message);
    }
}
