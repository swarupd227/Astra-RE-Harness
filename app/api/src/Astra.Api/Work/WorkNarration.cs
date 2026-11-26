using Astra.Api.Conversations;

namespace Astra.Api.Work;

public sealed record WorkNote(string Markdown, IReadOnlyList<SuggestionDto> Chips, bool ShowRun = false, bool ShowReviewQueue = false);

/// <summary>
/// What the Narrator says about a multi-routine run, and when. Deterministic
/// text from the run's own figures; one instance per followed run remembers what
/// it has already said so a hundred item changes do not become a hundred posts.
/// </summary>
public sealed class WorkNarration
{
    private bool _halfway;
    private bool _paused;
    private int _waitingSaid;

    public WorkNote? Next(WorkRunSnapshot s)
    {
        if (s.IsTerminal) return null; // the closing summary is Final()

        if (s.State == "PAUSED")
        {
            if (_paused) return null;
            _paused = true;
            return new($"**{s.Label}** — {s.Summary}", new SuggestionDto[]
            {
                new("Resume", "Resume the wave run"),
                new("Cancel the run", "Cancel the wave run"),
                new("Run progress", "How is the wave run going?"),
            }, ShowRun: true);
        }
        _paused = false;

        // Nothing left for an agent: say so once per batch of new waiting specs.
        if (s.IdleOnPeople)
        {
            if (s.WaitingForSignoffCount == _waitingSaid) return null;
            var fewer = s.WaitingForSignoffCount < _waitingSaid;
            _waitingSaid = s.WaitingForSignoffCount;
            // Signing one of several only shrinks the queue; the earlier post still stands.
            if (fewer) return null;
            var n = s.WaitingForSignoffCount;
            var chips = new List<SuggestionDto>
            {
                new("Review queue", "Show the review queue"),
                new("Run progress", "How is the wave run going?"),
            };
            if (s.FailedCount > 0) chips.Add(new("Retry failed", "Retry the failed steps of the wave run"));
            chips.Add(new("Pause", "Pause the wave run"));
            return new($"**{s.Label}** — {s.Summary}. Agents are idle until an SME signs: " +
                       $"{n} {(n == 1 ? "spec is" : "specs are")} in the review queue. Sign in any order; each routine carries on as soon as its spec is signed.",
                chips, ShowRun: true, ShowReviewQueue: true);
        }

        var machine = s.Stages.Where(c => !WorkStages.IsHuman(c.Stage)).ToList();
        var total = machine.Sum(c => c.Total);
        var closed = machine.Sum(c => c.Total - c.Open);
        if (!_halfway && total >= 4 && closed * 2 >= total && closed < total)
        {
            _halfway = true;
            var chips = new List<SuggestionDto>
            {
                new("Pause", "Pause the wave run"),
                new("Cancel the run", "Cancel the wave run"),
            };
            if (s.FailedCount > 0) chips.Add(new("Retry failed", "Retry the failed steps of the wave run"));
            return new($"**{s.Label}** halfway: {s.Summary}.", chips);
        }
        return null;
    }

    public static string Final(WorkRunSnapshot s)
    {
        var verb = s.State == "CANCELLED" ? "was cancelled" : s.FailedCount > 0 ? "finished with failures" : "finished";
        var text = $"**{s.Label}** {verb}: {s.Summary}.";
        var m = s.Metrics;
        if (m.Items > 0)
        {
            text += $" Agent time: {WorkRunText.Duration(m.BusyMs)} working in parallel against {WorkRunText.Duration(m.SerialMs)} one step at a time " +
                    $"({m.Speedup:0.##}× faster, up to {m.PeakConcurrency} at once; waiting for sign-off excluded). " +
                    $"{m.Calls:N0} model call(s), {m.InputTokens:N0} tokens in, {m.OutputTokens:N0} out.";
        }
        if (s.FailedCount > 0)
        {
            var first = s.Failures[0];
            text += $" First failure — `{first.Name}` at {first.Kind}: {first.Error}";
        }
        return text;
    }

    public static IReadOnlyList<SuggestionDto> FinalChips(WorkRunSnapshot s)
    {
        var chips = new List<SuggestionDto>();
        if (s.State != "CANCELLED" && s.FailedCount > 0) chips.Add(new("Retry failed", "Retry the failed steps of the wave run"));
        if (s.Kind == "wave" && s.State == "COMPLETED" && s.FailedCount == 0) chips.Add(new("Run the next wave", "Run the next wave"));
        chips.Add(new("Review queue", "Show the review queue"));
        chips.Add(new("Where are we?", "Show me the flow board"));
        return chips;
    }
}
