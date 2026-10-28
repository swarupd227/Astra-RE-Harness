using Astra.Api.Ingest;
using Xunit;

namespace Astra.Api.Tests;

public class IntakeTallyTests
{
    [Fact]
    public void NothingSkipped_MeansNoNotes()
    {
        var t = new IntakeTally();
        Assert.Empty(t.Notes());
        Assert.Equal(0, t.Total);
    }

    [Fact]
    public void NotSource_IsCountedByExtension_MostCommonFirst()
    {
        var t = new IntakeTally();
        foreach (var p in new[] { "a.md", "b.md", "c.md", "img/x.PNG", "img/y.png", "LICENSE" })
            t.Skip(IntakeTally.NotSource, p);

        var note = Assert.Single(t.Notes());
        Assert.Contains("Skipped 6 files", note);
        Assert.Contains(".md ×3", note);
        Assert.Contains(".png ×2", note);
        Assert.Contains("no extension ×1", note);
        Assert.True(note.IndexOf(".md ×3", StringComparison.Ordinal) < note.IndexOf(".png ×2", StringComparison.Ordinal));
    }

    [Fact]
    public void NotSource_ExtensionsBeyondTheLimit_AreElided()
    {
        var t = new IntakeTally();
        foreach (var ext in new[] { ".a", ".b", ".c", ".d", ".e", ".f1", ".g" })
            t.Skip(IntakeTally.NotSource, "x" + ext);
        Assert.Contains("…", Assert.Single(t.Notes()));
    }

    [Fact]
    public void ASingleFile_UsesTheSingularWording()
    {
        var t = new IntakeTally();
        t.Skip(IntakeTally.NotSource, "README.md");
        Assert.Contains("Skipped 1 file that is not supported source code", Assert.Single(t.Notes()));
    }

    [Fact]
    public void TooLarge_NamesTheFiles_AndTheLimit()
    {
        var t = new IntakeTally();
        t.Skip(IntakeTally.TooLarge, "src/huge.f");
        t.Skip(IntakeTally.TooLarge, "src/big.f");
        var note = Assert.Single(t.Notes());
        Assert.Contains("Skipped 2 files over 8 MiB", note);
        Assert.Contains("src/huge.f", note);
        Assert.Contains("src/big.f", note);
    }

    [Fact]
    public void TooLarge_NamesAtMostFive_ThenCountsTheRest()
    {
        var t = new IntakeTally();
        for (var i = 0; i < 8; i++) t.Skip(IntakeTally.TooLarge, $"f{i}.f");
        var note = Assert.Single(t.Notes());
        Assert.Contains("f4.f", note);
        Assert.DoesNotContain("f5.f", note);
        Assert.Contains("(+3 more)", note);
    }

    [Fact]
    public void Unsafe_ExplainsWhyThePathWasRefused()
    {
        var t = new IntakeTally();
        t.Skip(IntakeTally.Unsafe, "../../evil.f");
        var note = Assert.Single(t.Notes());
        Assert.Contains("unsafe path", note);
        Assert.Contains("../../evil.f", note);
    }

    [Fact]
    public void NotesComeInAStableOrder_RoutineFirst()
    {
        var t = new IntakeTally();
        t.Skip(IntakeTally.Unsafe, "../x.f");
        t.Skip(IntakeTally.TooLarge, "big.f");
        t.Skip(IntakeTally.NotSource, "a.md");

        var notes = t.Notes();
        Assert.Equal(3, notes.Count);
        Assert.Contains("not supported source code", notes[0]);
        Assert.Contains("over 8 MiB", notes[1]);
        Assert.Contains("unsafe path", notes[2]);
        Assert.Equal(3, t.Total);
    }

    [Fact]
    public void TheLimitIsReportedAsGiven()
    {
        var t = new IntakeTally();
        t.Skip(IntakeTally.TooLarge, "big.f");
        Assert.Contains("over 16 MiB", Assert.Single(t.Notes(16L * 1024 * 1024)));
    }
}
