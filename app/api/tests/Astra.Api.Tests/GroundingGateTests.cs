using Astra.Api.Knowledge;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Which identifiers are looked up, and the gate that keeps a statement only if what it says is grounded.</summary>
public class GroundingGateTests
{
    private const string Slot = "runbook:failure-handling";

    // ── identifiers ─────────────────────────────────────────────────────

    private static IReadOnlyList<(string Text, IdentifierKind Kind)> Ids(string text) =>
        IdentifierExtractor.Extract(text).Select(i => (i.Text, i.Kind)).ToList();

    [Fact]
    public void Identifiers_AreTheThingsThatCanBeLookedUpVerbatim_AndPlainWordsAreNot()
    {
        var ids = Ids("If `TLR_EOD_POST` fails with SQL error 2627 (ticket INC-T-0412), the call in OfacScreeningClient ignores Ofac:TimeoutSeconds " +
                      "and posts to https://ofac.brfc.example/screen from src/services/OfacScreeningClient.cs; the extract goes to \\\\dw-stage\\teller\\ and " +
                      "D:\\Scripts\\EodReport.ps1 runs it; the PowerShell script and the Drawer are not names.");

        Assert.Contains(("TLR_EOD_POST", IdentifierKind.Quoted), ids);
        Assert.Contains(("2627", IdentifierKind.Code), ids);
        Assert.Contains(("INC-T-0412", IdentifierKind.Code), ids);
        Assert.Contains(("OfacScreeningClient", IdentifierKind.Name), ids);
        Assert.Contains(("Ofac:TimeoutSeconds", IdentifierKind.Setting), ids);
        Assert.Contains(("https://ofac.brfc.example/screen", IdentifierKind.Url), ids);
        Assert.Contains(("src/services/OfacScreeningClient.cs", IdentifierKind.Path), ids);
        Assert.Contains(ids, i => i.Kind == IdentifierKind.Path && i.Text == "\\\\dw-stage\\teller\\");
        Assert.Contains(ids, i => i.Kind == IdentifierKind.Path && i.Text.EndsWith("EodReport.ps1", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, i => i.Text is "PowerShell" or "Drawer" or "SQL" or "extract");
    }

    [Fact]
    public void Identifiers_InAQuotedCommand_AreCheckedWordByWord_WhenTheWholeIsNotThere()
    {
        var id = Assert.Single(IdentifierExtractor.Extract("Run `EXEC usp_PurgeStage @TxnRef = ...` first."));
        Assert.Equal(IdentifierKind.Quoted, id.Kind);
        Assert.Contains("usp_PurgeStage", id.Parts);

        var texts = new[] { IdentifierExtractor.Normalise("Delete the staged duplicate with usp_PurgeStage and the TxnRef of the second row.") };
        Assert.True(IdentifierExtractor.IsIn(id, texts));
        Assert.False(IdentifierExtractor.IsIn(id, new[] { IdentifierExtractor.Normalise("Delete the staged duplicate.") }));
    }

    [Fact]
    public void Lookup_IgnoresCaseWhitespaceAndTheDirectionAndDoublingOfSlashes()
    {
        var path = IdentifierExtractor.Extract("The extract goes to \\\\dw-stage\\teller\\ nightly.").Single();
        Assert.True(IdentifierExtractor.IsIn(path, new[] { IdentifierExtractor.Normalise("bcp ... queryout //DW-STAGE/teller/ -c") }));
        Assert.True(IdentifierExtractor.IsIn(path, new[] { IdentifierExtractor.Normalise("queryout \\\\dw-stage\\teller\\ -c") }));
        Assert.False(IdentifierExtractor.IsIn(path, new[] { IdentifierExtractor.Normalise("queryout \\\\other-stage\\teller\\") }));
    }

    // ── the gate ────────────────────────────────────────────────────────

    private static GateChunk Structured(string id, string text, string label = "") => new(id, "sql", "structured", label, text);
    private static GateChunk Narrative(string id, string text, string label = "") => new(id, "transcript", "narrative", label, text);

    private static StatementItem Item(string id, string text, params string[] cites) => new(id, Slot, text, cites);

    private static StatementDocument Doc(params StatementItem[] items) =>
        new("runbook", "T", new[] { new StatementSection("H", items) }, Array.Empty<StatementConflict>(), Array.Empty<OpenQuestion>());

    private static readonly IEvidenceLookup Evidence = new DictionaryEvidence(new[]
    {
        Structured("SQL-0000000a", "CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)", "db/ddl.sql L10-18"),
        Structured("JOB-0000000b", "<Task Num=\"3\" Job=\"TLR_EOD_POST\" After=\"2\" Restart=\"true\"/>", "TLR_EOD · Task 3"),
        Narrative("TRN-0000000c", "If it fails with 2627, delete the staged duplicate with usp_PurgeStage and rerun from step three."),
        new GateChunk("TKT-0000000d", "ticket", "narrative", "INC-T-0412 · EOD post fails", "number: INC-T-0412\ndescription: TLR_EOD_POST failed, 2627, UQ_TLR_STAGE_TXN_REF violated"),
    });

    [Fact]
    public void Statement_WithNoCitation_IsDropped()
    {
        var result = GroundingGate.Check(Doc(Item("st-1", "The job restarts at step 3.")), Evidence);
        var dropped = Assert.Single(result.Dropped);
        Assert.Equal(GateRules.NoCitation, Assert.Single(dropped.Issues).Rule);
        Assert.Empty(result.Document.Sections);   // a section with nothing left is gone
    }

    [Fact]
    public void Citation_ThatIsNotEvidenceOfThisVersion_IsRemoved_AndAStatementLeftWithNoneIsDropped()
    {
        var mixed = GroundingGate.Check(Doc(Item("st-1", "Restarts at task 3.", "JOB-0000000b", "JOB-deadbeef")), Evidence);
        var kept = Assert.Single(mixed.Document.Items);
        Assert.Equal(new[] { "JOB-0000000b" }, kept.Citations);
        Assert.Contains(kept.Notes!, n => n.Contains("JOB-deadbeef") && n.Contains("removed"));
        Assert.Contains(mixed.Issues, i => i.Rule == GateRules.UnknownCitation);
        Assert.Empty(mixed.Dropped);

        var none = GroundingGate.Check(Doc(Item("st-2", "Restarts at task 3.", "JOB-deadbeef")), Evidence);
        Assert.Equal(GateRules.UnknownCitation, Assert.Single(Assert.Single(none.Dropped).Issues).Rule);
    }

    [Fact]
    public void Statement_WithAnIdentifierItsEvidenceDoesNotHave_IsDropped_AndTheMissingOnesAreNamed()
    {
        var result = GroundingGate.Check(Doc(
            Item("st-1", "Run `usp_PurgeStaging` and check TLR_NOPE.", "TRN-0000000c"),
            Item("st-2", "Run usp_PurgeStage if it fails with error 2627.", "TRN-0000000c")), Evidence);

        var issue = Assert.Single(Assert.Single(result.Dropped).Issues);
        Assert.Equal(GateRules.IdentifierNotInEvidence, issue.Rule);
        Assert.Contains("'usp_PurgeStaging'", issue.Message);
        Assert.Contains("'TLR_NOPE'", issue.Message);
        Assert.Equal("st-2", Assert.Single(result.Document.Items).Id);
    }

    [Fact]
    public void AnIdentifier_MayBeFoundInTheLabelOfTheEvidenceOrInAnyOfTheEvidenceCited()
    {
        var result = GroundingGate.Check(Doc(
            Item("st-1", "Task 3 of TLR_EOD restarts it, and error 2627 comes from UQ_TLR_STAGE_TXN_REF.", "JOB-0000000b", "SQL-0000000a", "TRN-0000000c")), Evidence);
        Assert.Empty(result.Dropped);
    }

    [Fact]
    public void TheWowMoment_OneStatementRestingOnATranscriptATicketAndAConstraint_IsGroundedAndSupported()
    {
        var statement = Item("st-1",
            "If `TLR_EOD_POST` fails with error 2627, clear the duplicate in staging with `usp_PurgeStage` and rerun from step 3; `UQ_TLR_STAGE_TXN_REF` is the key it breaks.",
            "TRN-0000000c", "TKT-0000000d", "SQL-0000000a");
        var support = new Dictionary<string, string> { ["st-1"] = SupportVerdict.Yes };

        var result = GroundingGate.Evaluate(Doc(statement), Evidence, support);
        Assert.Empty(result.Dropped);
        var kept = Assert.Single(result.Document.Items);
        Assert.Equal(StatementStatus.Supported, kept.Status);   // structured evidence is among what it cites
        Assert.Equal(new[] { "TRN-0000000c", "TKT-0000000d", "SQL-0000000a" }, kept.Citations);
    }

    // ── status ──────────────────────────────────────────────────────────

    private static string StatusOf(string cite, string? verdict, IReadOnlySet<string>? verified = null)
    {
        var item = Item("st-1", "Restarts at task 3.", cite);
        var support = verdict is null ? null : new Dictionary<string, string> { ["st-1"] = verdict };
        var result = GroundingGate.Finish(Doc(item), Evidence, support, verified);
        return result.Document.Items.Single().Status!;
    }

    [Fact]
    public void Status_IsSupportedOnlyForStructuredEvidenceTheCheckSaysYesTo_OrAVerifiedClaim()
    {
        Assert.Equal(StatementStatus.Supported, StatusOf("JOB-0000000b", SupportVerdict.Yes));
        Assert.Equal(StatementStatus.Unverified, StatusOf("TRN-0000000c", SupportVerdict.Yes));       // only people said it
        Assert.Equal(StatementStatus.Unverified, StatusOf("JOB-0000000b", SupportVerdict.Partly));    // only partly said
        Assert.Equal(StatementStatus.Unverified, StatusOf("JOB-0000000b", null));                     // not checked
        Assert.Equal(StatementStatus.Supported, StatusOf("TRN-0000000c", null, new HashSet<string> { "st-1" }));   // the claims pipeline verified it
    }

    [Fact]
    public void Statement_TheCheckSaysNoTo_IsDropped()
    {
        var result = GroundingGate.Finish(Doc(Item("st-1", "Restarts at task 3.", "JOB-0000000b")), Evidence,
            new Dictionary<string, string> { ["st-1"] = SupportVerdict.No });
        Assert.Equal(GateRules.SupportNo, Assert.Single(Assert.Single(result.Dropped).Issues).Rule);
        Assert.Empty(result.Document.Items);
    }

    [Fact]
    public void Status_ExplainsItselfInNotes_AndAnUncheckedStatementIsSaidToBeUnchecked()
    {
        var narrative = GroundingGate.Finish(Doc(Item("st-1", "Restarts at task 3.", "TRN-0000000c")), Evidence,
            new Dictionary<string, string> { ["st-1"] = SupportVerdict.Partly }).Document.Items.Single();
        Assert.Contains(narrative.Notes!, n => n.Contains("people said or wrote"));
        Assert.Contains(narrative.Notes!, n => n.Contains("part of this"));

        var unchecked1 = GroundingGate.Finish(Doc(Item("st-1", "Restarts at task 3.", "JOB-0000000b")), Evidence, null);
        Assert.Contains(unchecked1.Issues, i => i.Rule == GateRules.SupportNotChecked);
    }

    // ── conflicts ───────────────────────────────────────────────────────

    [Fact]
    public void Conflict_IsRealOnlyWhenItNamesTwoPiecesOfEvidenceThatExist()
    {
        var doc = Doc(Item("st-1", "The chain restarts from the top.", "TRN-0000000c")) with
        {
            Conflicts = new[] { new StatementConflict("st-1", new[] { "TRN-0000000c", "JOB-0000000b", "JOB-0000000b" }, "The plan restarts at task 3.") },
        };
        var real = GroundingGate.Finish(doc, Evidence, new Dictionary<string, string> { ["st-1"] = SupportVerdict.Yes });
        Assert.Equal(StatementStatus.Conflict, real.Document.Items.Single().Status);
        Assert.Equal(new[] { "TRN-0000000c", "JOB-0000000b" }, Assert.Single(real.Document.Conflicts).EvidenceIds);
        Assert.Equal(1, real.Counts.Conflict);

        // One piece of evidence named twice is not two; an invented one does not count.
        foreach (var ids in new[] { new[] { "TRN-0000000c", "TRN-0000000c" }, new[] { "TRN-0000000c", "JOB-deadbeef" } })
        {
            var fake = doc with { Conflicts = new[] { new StatementConflict("st-1", ids, "x") } };
            var result = GroundingGate.Finish(fake, Evidence, new Dictionary<string, string> { ["st-1"] = SupportVerdict.Yes });
            Assert.Equal(StatementStatus.Unverified, result.Document.Items.Single().Status);   // downgraded, never Supported
            Assert.Empty(result.Document.Conflicts);
            Assert.Contains(result.Issues, i => i.Rule == GateRules.ConflictNotEvidenced);
        }
    }

    // ── what is left over ───────────────────────────────────────────────

    [Fact]
    public void ADroppedStatement_BecomesAnOpenQuestionNotAnAssertion_AndOnlyOnce()
    {
        var checkedDoc = GroundingGate.Check(Doc(Item("st-1", "The job restarts at step 3.")), Evidence);
        var demoted = GroundingGate.DemoteDropped(checkedDoc.Document, checkedDoc.Dropped);
        var question = Assert.Single(demoted.OpenQuestions);
        Assert.Equal("Not confirmed by the evidence: The job restarts at step 3.", question.Text);
        Assert.Equal(Slot, question.SlotKey);
        Assert.Empty(demoted.Items);

        Assert.Single(GroundingGate.DemoteDropped(demoted, checkedDoc.Dropped).OpenQuestions);
        Assert.Same(checkedDoc.Document, GroundingGate.DemoteDropped(checkedDoc.Document, Array.Empty<DroppedStatement>()));
    }

    [Fact]
    public void Evaluate_RunsBothHalves_AndCountsWhatItDid()
    {
        var result = GroundingGate.Evaluate(Doc(
            Item("st-1", "Restarts at task 3.", "JOB-0000000b"),
            Item("st-2", "No citation here."),
            Item("st-3", "Narrative only claim about usp_PurgeStage.", "TRN-0000000c"),
            Item("st-4", "Not backed by it at all.", "JOB-0000000b")), Evidence,
            new Dictionary<string, string>
            {
                ["st-1"] = SupportVerdict.Yes, ["st-3"] = SupportVerdict.Yes, ["st-4"] = SupportVerdict.No,
            });

        Assert.Equal(new[] { "st-1", "st-3" }, result.Document.Items.Select(i => i.Id).ToArray());
        Assert.Equal(new GateCounts(Supported: 1, Unverified: 1, Conflict: 0, Dropped: 2), result.Counts);
        Assert.True(result.AnythingDropped);
    }
}
