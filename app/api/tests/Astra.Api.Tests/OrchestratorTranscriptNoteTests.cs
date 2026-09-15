using Astra.Api.Copilot;
using Xunit;

namespace Astra.Api.Tests;

public class OrchestratorTranscriptNoteTests
{
    [Fact]
    public void An_imitated_confirmed_note_is_removed_from_the_answer()
    {
        var text = "Confirm and I'll retry. [proposed action generate_scaffold: confirmed] Retrying the conversion now.";
        Assert.Equal("Confirm and I'll retry. Retrying the conversion now.", CopilotOrchestrator.StripTranscriptNotes(text));
    }

    [Fact]
    public void Ordinary_answers_are_left_alone()
    {
        const string text = "The spec for `TIdSMTP.Connect` has 21 claims [INV-1 … EC-4]; see the card below.";
        Assert.Equal(text, CopilotOrchestrator.StripTranscriptNotes(text));
    }

    [Fact]
    public void A_trailing_note_leaves_no_dangling_whitespace()
    {
        Assert.Equal("Done.", CopilotOrchestrator.StripTranscriptNotes("Done.\n\n[proposed action run_gate: pending]"));
    }
}
