using System.Diagnostics;
using Astra.Api.Llm;
using Xunit;

namespace Astra.Api.Tests;

public class MockExtractionPacingTests
{
    private static ExtractionRequest Request(bool bulk) => new(
        SubroutineId: Guid.Empty,
        SubroutineName: "CONSUME_ROLL",
        SourcePath: "fortran/consume_roll.f",
        SourceText: "      SUBROUTINE CONSUME_ROLL\n      END\n",
        LineCount: 2,
        PromptTemplateId: "fortran-f77-extract",
        PromptTemplateVersion: "v1",
        BulkMode: bulk);

    [Fact]
    public async Task A_bulk_extraction_does_not_pace_itself()
    {
        // Paced, the mock takes about 12 s; golden-dataset score-all runs it once per entry.
        var clock = Stopwatch.StartNew();
        var types = new List<string>();
        await foreach (var ev in new MockLlmProvider().ExtractAsync(Request(bulk: true), CancellationToken.None))
            types.Add(ev.Type);
        clock.Stop();

        Assert.Equal("__final__", types[^1]);
        Assert.Contains("patch", types);
        Assert.Contains("token", types);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"bulk extraction took {clock.Elapsed}");
    }
}
