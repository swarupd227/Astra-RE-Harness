using Astra.Api.Llm;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>A package is generated with the code of its routine and of what that routine calls, so outside requests are copied, not guessed.</summary>
public class CallTreeSourceTests
{
    private const string Module = "Book.xlsm/Rater.bas";

    private static readonly string Text = string.Join("\n", new[]
    {
        "Attribute VB_Name = \"Rater\"",                       // 1
        "Public Sub Calculate()",                              // 2
        "    s = BuildJson(ws)",                               // 3
        "    PostJson s",                                      // 4
        "End Sub",                                             // 5
        "Private Function BuildJson(ws) As String",            // 6
        "    BuildJson = \"{\"\"advanced\"\":{\"\"perils\"\":\" & Q(ws) & \"}}\"", // 7
        "End Function",                                        // 8
        "Private Function Q(ws) As String",                    // 9
        "    Q = \"[1,2]\"",                                   // 10
        "End Function",                                        // 11
        "Private Sub PostJson(s)",                             // 12
        "End Sub",                                             // 13
        "Private Sub Unrelated()",                             // 14
        "End Sub",                                             // 15
    });

    private static readonly CallTreeSource.Routine Calc = new("Calculate", Module, 2, 5, new[] { "BuildJson", "PostJson", "MsgBox" });
    private static readonly List<CallTreeSource.Routine> Project = new()
    {
        Calc,
        new("BuildJson", Module, 6, 8, new[] { "Q" }),
        new("Q", Module, 9, 11, Array.Empty<string>()),
        new("PostJson", Module, 12, 13, Array.Empty<string>()),
        new("Unrelated", Module, 14, 15, Array.Empty<string>()),
    };

    [Fact]
    public void The_routine_and_everything_it_reaches_are_quoted_by_their_own_lines()
    {
        var note = CallTreeSource.Describe(Calc, Project, _ => Text);
        Assert.NotNull(note);
        Assert.Contains("### Calculate (this routine) — Rater.bas L2–L5", note);
        Assert.Contains("### BuildJson (called, depth 1)", note);
        Assert.Contains("### Q (called, depth 2)", note);                      // a helper of a helper: where the body is really built
        Assert.Contains("\"\"advanced\"\":{\"\"perils\"\"", note);              // the request literal, exactly as written
        Assert.DoesNotContain("Unrelated", note);                               // only what is reached
        Assert.DoesNotContain("MsgBox", note);                                  // a call that names no routine of the project
        Assert.True(note!.IndexOf("### Calculate", StringComparison.Ordinal) < note.IndexOf("### BuildJson", StringComparison.Ordinal));
    }

    [Fact]
    public void Depth_is_bounded_and_a_routine_that_calls_nothing_gets_no_note()
    {
        var shallow = CallTreeSource.Describe(Calc, Project, _ => Text, maxDepth: 1);
        Assert.DoesNotContain("### Q", shallow);
        Assert.Null(CallTreeSource.Describe(Project[2], Project, _ => Text));
    }

    [Fact]
    public void A_cycle_does_not_loop_and_an_unreadable_file_is_named_not_quoted()
    {
        var a = new CallTreeSource.Routine("A", "x.bas", 1, 1, new[] { "B" });
        var b = new CallTreeSource.Routine("B", "y.bas", 1, 1, new[] { "A" });
        var note = CallTreeSource.Describe(a, new[] { a, b }, p => p == "x.bas" ? "Sub A: B: End Sub" : null);
        Assert.Contains("### A (this routine)", note);
        Assert.Contains("not quoted here", note);
        Assert.Contains("B", note);
    }
}
