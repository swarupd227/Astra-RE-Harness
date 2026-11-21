using Astra.Api.Knowledge;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>The statement document: how a model's answer is read, how a statement keeps its identity, and the markdown derived from it.</summary>
public class StatementDocumentTests
{
    private const string Slot = "runbook:failure-handling";

    private static StatementDocument Parse(string json) =>
        StatementJson.Parse(json, "runbook", Slot).Document!;

    [Fact]
    public void AnAnswer_IsReadIntoSectionsStatementsAndQuestions_WithEveryCitationInItsCanonicalForm()
    {
        var doc = Parse("""
            {
              "title": "Failure handling",
              "sections": [
                { "heading": "Posting", "items": [
                    { "text": "  If   `TLR_EOD_POST` fails with error 2627, delete the duplicate. ", "citations": ["[sql-3FA9C01E]", "TRN-00000001", "sql-3fa9c01e"] },
                    { "text": "Second statement.", "citations": ["TKT-0000000a"], "slotKey": "runbook:restart" } ] }
              ],
              "openQuestions": [ { "text": "Who approves deleting a duplicate in PROD?", "citations": ["TRN-00000001"] }, "Is the extract late?" ]
            }
            """);

        Assert.Equal("Failure handling", doc.Title);
        var first = doc.Sections[0].Items[0];
        Assert.Equal("If `TLR_EOD_POST` fails with error 2627, delete the duplicate.", first.Text);   // whitespace collapsed
        Assert.Equal(new[] { "SQL-3fa9c01e", "TRN-00000001" }, first.Citations);                       // normalised, repeated one once
        Assert.Equal(Slot, first.SlotKey);                                                             // defaulted to the slot asked about
        Assert.Equal("runbook:restart", doc.Sections[0].Items[1].SlotKey);
        Assert.Equal(2, doc.OpenQuestions.Count);
        Assert.Equal(new[] { "TRN-00000001" }, doc.OpenQuestions[0].Citations);
        Assert.Null(doc.OpenQuestions[1].Citations);
        Assert.All(doc.OpenQuestions, q => Assert.StartsWith("oq-", q.Id));
    }

    [Fact]
    public void WhatIsUnusable_IsSkippedAndSaid_AndWhatIsNotAnObjectIsRefused()
    {
        var result = StatementJson.Parse("""
            { "sections": [ { "heading": "A", "items": [ { "text": "", "citations": ["SQL-00000001"] }, { "text": "Kept.", "citations": [] }, 7 ] },
                            { "heading": "Empty", "items": [] } ] }
            """, "runbook", Slot);

        Assert.True(result.Ok);
        Assert.Single(result.Document!.Sections);
        Assert.Equal("Kept.", Assert.Single(result.Document.Items).Text);
        Assert.Contains(result.Problems, p => p.Contains("no text"));
        Assert.Contains(result.Problems, p => p.Contains("not an object"));

        Assert.False(StatementJson.Parse("not json", "runbook", Slot).Ok);
        Assert.False(StatementJson.Parse("[1,2]", "runbook", Slot).Ok);
    }

    [Fact]
    public void ANotice_OfStatusOrSupportInTheAnswer_IsKeptOnlyWhenItIsOneTheGateUses()
    {
        var item = Parse("""{ "sections": [ { "heading": "H", "items": [ { "text": "A.", "citations": ["SQL-00000001"], "status": "supported", "support": "maybe" } ] } ] }""")
            .Items.Single();
        Assert.Equal("supported", item.Status);
        Assert.Null(item.Support);
    }

    [Fact]
    public void TheSameWordsOnTheSameEvidence_KeepTheirIdentity_AndAChangeOfEitherChangesIt()
    {
        var a = StatementIds.Stable(Slot, "Delete the duplicate row.", new[] { "SQL-3fa9c01e", "TRN-00000001" });
        var sameDifferentOrderAndPunctuation = StatementIds.Stable(Slot, "delete the  duplicate row", new[] { "TRN-00000001", "SQL-3fa9c01e" });
        Assert.Equal(a, sameDifferentOrderAndPunctuation);

        Assert.NotEqual(a, StatementIds.Stable(Slot, "Delete the duplicate rows.", new[] { "SQL-3fa9c01e" }));
        Assert.NotEqual(a, StatementIds.Stable(Slot, "Delete the duplicate row.", new[] { "TKT-0000000a" }));
        Assert.NotEqual(a, StatementIds.Stable("runbook:restart", "Delete the duplicate row.", new[] { "SQL-3fa9c01e" }));
        Assert.StartsWith("st-", a);

        // Parsed twice, a statement has the same id both times.
        const string json = """{ "sections": [ { "heading": "H", "items": [ { "text": "Delete the duplicate row.", "citations": ["SQL-3fa9c01e"] } ] } ] }""";
        Assert.Equal(Parse(json).Items.Single().Id, Parse(json).Items.Single().Id);
    }

    [Fact]
    public void TwoStatementsWithTheSameIdentity_AreOneStatement()
    {
        var result = StatementJson.Parse("""
            { "sections": [ { "heading": "H", "items": [
                { "text": "Same.", "citations": ["SQL-00000001"] }, { "text": "same", "citations": ["SQL-00000001"] } ] } ] }
            """, "runbook", Slot);
        Assert.Single(result.Document!.Items);
        Assert.Contains(result.Problems, p => p.Contains("same text and evidence"));
    }

    [Fact]
    public void ADocument_SurvivesItsStoredForm()
    {
        var doc = new StatementDocument(
            "runbook", "Failure handling",
            new[] { new StatementSection("Posting", new[] { new StatementItem("st-1", Slot, "Delete the row.", new[] { "SQL-3fa9c01e" }, StatementStatus.Supported, SupportVerdict.Yes, new[] { "A note." }) }) },
            new[] { new StatementConflict("st-1", new[] { "SQL-3fa9c01e", "TRN-00000001" }, "The call says top; the plan says task 3.") },
            new[] { new OpenQuestion("oq-1", "Who approves?", Slot) },
            0.8);

        var json = StatementJson.ToJson(doc);
        Assert.Contains("\"schema\":\"statements/v1\"", json);
        var back = StatementJson.Parse(json, "runbook", Slot).Document!;
        var item = back.Items.Single();
        Assert.Equal(("st-1", StatementStatus.Supported, SupportVerdict.Yes), (item.Id, item.Status, item.Support));
        Assert.Equal(0.8, back.Confidence);
        Assert.Equal(new[] { "SQL-3fa9c01e", "TRN-00000001" }, Assert.Single(back.Conflicts).EvidenceIds);
        Assert.Equal("oq-1", Assert.Single(back.OpenQuestions).Id);
    }

    [Fact]
    public void Markdown_IsDerivedFromTheDocument_AndIsTheSameEveryTime()
    {
        var doc = new StatementDocument(
            "runbook", "Failure handling",
            new[]
            {
                new StatementSection("Posting", new[]
                {
                    new StatementItem("st-1", Slot, "Delete the duplicate row.", new[] { "SQL-3fa9c01e", "TKT-0000000a" }, StatementStatus.Supported),
                    new StatementItem("st-2", Slot, "The chain restarts from the top.", new[] { "TRN-00000001" }, StatementStatus.Conflict),
                    new StatementItem("st-3", Slot, "It takes about 25 minutes.", new[] { "TRN-00000002" }, StatementStatus.Unverified),
                }),
            },
            new[] { new StatementConflict("st-2", new[] { "TRN-00000001", "JOB-00000009" }, "The plan restarts at task 3.") },
            new[] { new OpenQuestion("oq-1", "Who approves?", Slot, new[] { "TRN-00000001" }) });

        var md = StatementMarkdown.Render(doc);
        Assert.Equal(md, StatementMarkdown.Render(doc));
        Assert.StartsWith("# Failure handling\n\n## Posting\n\n", md);
        Assert.Contains("- Delete the duplicate row. [SQL-3fa9c01e] [TKT-0000000a]\n", md);          // supported: no mark
        Assert.Contains("- The chain restarts from the top. [TRN-00000001] _(conflict)_\n", md);
        Assert.Contains("- It takes about 25 minutes. [TRN-00000002] _(unverified)_\n", md);
        Assert.Contains("## Conflicts\n\n- The chain restarts from the top. — The plan restarts at task 3. [TRN-00000001] [JOB-00000009]\n", md);
        Assert.Contains("## Open questions\n\n- Who approves? [TRN-00000001]\n", md);
        Assert.EndsWith("\n", md);
    }
}
