using System.Text.Json;
using Astra.Api.Knowledge;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>The batched check of whether cited evidence says what a statement says: the requests it builds, the answers it reads, and the offline answer.</summary>
public class SupportCheckTests
{
    private static SupportClaim Claim(int n, string statement = "Delete the staged duplicate row.", string evidence = "Delete the staged duplicate row with the purge procedure.") =>
        new($"st-{n}", statement, new[] { new SupportEvidence($"SQL-{n:x8}", evidence) });

    [Fact]
    public void Claims_AreSentInBatches_EachCallAtTemperatureZero_AndNamedForReplay()
    {
        var claims = Enumerable.Range(1, 45).Select(n => Claim(n)).ToList();
        var requests = SupportCheck.BuildRequests(claims, "SYSTEM", "small-model", "runbook:runbook:restart:write:1");

        Assert.Equal(3, requests.Count);   // 20 + 20 + 5
        Assert.Equal(new[] { 20, 20, 5 }, requests.Select(r => JsonDocument.Parse(r.UserMessage).RootElement.GetProperty("claims").GetArrayLength()).ToArray());
        Assert.Equal(
            new[] { "runbook:runbook:restart:write:1:support:1", "runbook:runbook:restart:write:1:support:2", "runbook:runbook:restart:write:1:support:3" },
            requests.Select(r => r.ReplayKey).ToArray());
        Assert.All(requests, r =>
        {
            Assert.Equal(0d, r.Temperature);
            Assert.Equal("small-model", r.Model);
            Assert.Equal(SupportCheck.ToolName, r.ToolName);
            Assert.Equal(SupportCheck.PromptId, r.PromptId);
            Assert.Equal(new[] { "SYSTEM" }, r.SystemBlocks);
        });
    }

    [Fact]
    public void TheMessage_CarriesEachStatementAndTheEvidenceItCites_WithLongEvidenceCut()
    {
        var long1 = new string('x', SupportCheck.MaxEvidenceChars + 500);
        var request = SupportCheck.BuildRequests(new[] { Claim(1, evidence: long1) }, "S", "m", "k").Single();

        var claim = JsonDocument.Parse(request.UserMessage).RootElement.GetProperty("claims")[0];
        Assert.Equal("st-1", claim.GetProperty("id").GetString());
        Assert.Equal("Delete the staged duplicate row.", claim.GetProperty("statement").GetString());
        var evidence = claim.GetProperty("evidence")[0];
        Assert.Equal("SQL-00000001", evidence.GetProperty("id").GetString());
        Assert.InRange(evidence.GetProperty("text").GetString()!.Length, SupportCheck.MaxEvidenceChars, SupportCheck.MaxEvidenceChars + 1);
    }

    [Fact]
    public void AnAnswer_IsReadIntoVerdicts_AndAClaimItDoesNotAnswerIsNotChecked()
    {
        var verdicts = SupportCheck.Parse("""
            { "verdicts": [
                { "id": "st-1", "verdict": "yes" }, { "id": "st-2", "verdict": " PARTLY " }, { "id": "st-3", "verdict": "no", "reason": "x" },
                { "id": "st-4", "verdict": "probably" }, { "verdict": "yes" }, { "id": "st-5" }, 7 ] }
            """);

        Assert.Equal(new[] { "st-1", "st-2", "st-3" }, verdicts.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(SupportVerdict.Partly, verdicts["st-2"]);
        Assert.Empty(SupportCheck.Parse("not json"));
        Assert.Empty(SupportCheck.Parse("""{ "other": 1 }"""));
    }

    [Theory]
    [InlineData("Delete the staged duplicate row.", "Delete the staged duplicate row with the purge procedure.", SupportVerdict.Yes)]
    [InlineData("Delete the staged duplicate and notify the compliance officer.", "Delete the staged duplicate row.", SupportVerdict.Partly)]
    [InlineData("The warehouse extract runs nightly at midnight.", "Delete the staged duplicate row.", SupportVerdict.No)]
    public void TheOfflineAnswer_IsByHowManyOfTheStatementsWordsTheEvidenceHas(string statement, string evidence, string expected) =>
        Assert.Equal(expected, SupportCheck.MockVerdict(Claim(1, statement, evidence)));

    [Fact]
    public void TheOfflineAnswer_IsWhatARequestsMockPayloadReturns_AndReadsBackAsVerdicts()
    {
        var claims = new[] { Claim(1), Claim(2, "The warehouse extract runs nightly at midnight.") };
        var request = SupportCheck.BuildRequests(claims, "S", "m", "k").Single();

        var verdicts = SupportCheck.Parse(request.MockPayload());
        Assert.Equal(SupportVerdict.Yes, verdicts["st-1"]);
        Assert.Equal(SupportVerdict.No, verdicts["st-2"]);
    }
}
