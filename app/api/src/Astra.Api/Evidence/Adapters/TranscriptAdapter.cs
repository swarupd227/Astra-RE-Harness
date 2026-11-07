using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>
/// Cuts a transcript into turns: a speaker's consecutive cues, merged while they follow each other
/// closely, become one chunk located by time (<c>t=00:14:32-00:15:40</c>). That is what a citation
/// needs: who said it, when, and the moment to play. WebVTT/SRT cues (with or without
/// <c>&lt;v Name&gt;</c> voice tags) and plain-text transcripts with timestamps are read; a transcript with
/// no timing at all is cut into line windows, because there is nothing to play from.
/// </summary>
public sealed class TranscriptAdapter : IEvidenceAdapter
{
    public const double MergeGapSeconds = 4;
    public const double MaxTurnSeconds = 75;
    public const int MaxTurnChars = 1800;

    private const string Time = @"(?:\d{1,3}:)?\d{1,2}:\d{2}(?:[.,]\d{1,3})?";
    // A name is one to four capitalised words: "Dale Henry", "Dr. Okafor", "Speaker 1".
    private const string Name = @"[\p{Lu}\p{N}][\p{L}\p{N}.'’\-]*(?: [\p{Lu}\p{N}][\p{L}\p{N}.'’\-]*){0,3}";

    private static readonly Regex Timing = new($@"^\s*(?<a>{Time})\s*-->\s*(?<b>{Time})", RegexOptions.Compiled);
    private static readonly Regex VoiceTag = new(@"<v(?:\.[^\s>]*)*\s+(?<n>[^>]*)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyTag = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex NameThenTime = new($@"^(?<n>{Name})\s*[\[\(]\s*(?<t>{Time})\s*[\]\)]\s*:?\s*(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex NameSpaceTime = new($@"^(?<n>{Name})\s+(?<t>{Time})\s*$", RegexOptions.Compiled);
    private static readonly Regex Bracketed = new($@"^[\[\(]\s*(?<t>{Time})\s*[\]\)]\s*(?:[-–—]\s*)?(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex Leading = new($@"^(?<t>{Time})\s*(?:[-–—]\s*)?(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex SpeakerPrefix = new($@"^(?<n>{Name}):\s+(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex TimeShape = new($"^(?:(?<h>\\d{{1,3}}):)?(?<m>\\d{{1,2}}):(?<s>\\d{{2}})(?:[.,](?<f>\\d{{1,3}}))?$", RegexOptions.Compiled);

    private sealed class Cue
    {
        public double Start;
        public double End;
        public string? Speaker;
        public string Text = "";
        public int LineStart;
        public int LineEnd;
        public bool EndEstimated;
    }

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Text is null) return AdapterResult.Empty;

        var lines = input.Text.TrimStart('﻿').Split('\n');
        var warnings = new List<string>();
        var asCues = lines.Any(l => l.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal)) || lines.Any(l => Timing.IsMatch(l));
        var cues = asCues ? ReadCues(lines, warnings) : ReadTimestampedText(lines);

        if (cues.Count == 0)
        {
            var fallback = LineWindowAdapter.Instance.Build(input);
            warnings.Insert(0, "No timestamps were found, so it was indexed by line windows and a citation cannot play the recording from a moment.");
            warnings.AddRange(fallback.Warnings);
            return new AdapterResult(fallback.Chunks, warnings);
        }

        AssignPrefixSpeakers(cues);

        var chunks = new List<EvidenceChunkDraft>();
        foreach (var turn in Merge(cues))
        {
            var startSec = Math.Floor(turn.Start);
            var endSec = Math.Max(Math.Ceiling(turn.End), startSec + 1);
            var text = turn.Speaker is null ? turn.Text : $"{turn.Speaker}: {turn.Text}";
            var snippet = AdapterText.Snippet(turn.Text, 70);
            var shown = Display(startSec);
            chunks.Add(new EvidenceChunkDraft(
                $"t={Hms(startSec)}-{Hms(endSec)}",
                turn.Speaker is null ? $"{shown} · {snippet}" : $"{turn.Speaker} · {shown} · {snippet}",
                text,
                AdapterJson.Object(w =>
                {
                    AdapterJson.Optional(w, "speaker", turn.Speaker);
                    w.WriteNumber("startSeconds", Math.Round(turn.Start, 3));
                    w.WriteNumber("endSeconds", Math.Round(turn.End, 3));
                    w.WriteString("start", Hms(startSec));
                    w.WriteNumber("cues", turn.Cues);
                    if (turn.EndEstimated) w.WriteBoolean("endEstimated", true);
                }),
                turn.LineStart,
                turn.LineEnd));
        }
        return new AdapterResult(chunks, warnings);
    }

    // ── reading ─────────────────────────────────────────────────────────

    private static List<Cue> ReadCues(string[] lines, List<string> warnings)
    {
        var cues = new List<Cue>();
        var i = 0;
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            while (i < lines.Length && lines[i].Trim().Length > 0) i++;
        }

        var unreadable = 0;
        while (i < lines.Length)
        {
            if (lines[i].Trim().Length == 0) { i++; continue; }
            var first = i;
            while (i < lines.Length && lines[i].Trim().Length > 0) i++;
            var last = i - 1;

            var head = lines[first].TrimStart();
            if (head.StartsWith("NOTE", StringComparison.Ordinal) || head.StartsWith("STYLE", StringComparison.Ordinal) || head.StartsWith("REGION", StringComparison.Ordinal))
                continue;

            var at = -1;
            for (var k = first; k <= Math.Min(last, first + 1); k++)
                if (lines[k].Contains("-->", StringComparison.Ordinal)) { at = k; break; }
            if (at < 0) continue;

            var m = Timing.Match(lines[at]);
            if (!m.Success || !TryTime(m.Groups["a"].Value, out var start) || !TryTime(m.Groups["b"].Value, out var end))
            {
                unreadable++;
                continue;
            }

            var raw = string.Join(' ', lines[(at + 1)..(last + 1)].Select(l => l.Trim()));
            foreach (var (speaker, text) in Voices(raw))
            {
                if (text.Length == 0) continue;
                cues.Add(new Cue { Start = start, End = Math.Max(end, start), Speaker = speaker, Text = text, LineStart = first + 1, LineEnd = last + 1 });
            }
        }
        if (unreadable > 0)
            warnings.Add($"{unreadable:N0} {(unreadable == 1 ? "cue has" : "cues have")} a timing that could not be read and {(unreadable == 1 ? "was" : "were")} left out.");
        return cues;
    }

    private static IEnumerable<(string? Speaker, string Text)> Voices(string raw)
    {
        var tags = VoiceTag.Matches(raw);
        if (tags.Count == 0)
        {
            yield return (null, Clean(raw));
            yield break;
        }
        var before = Clean(raw[..tags[0].Index]);
        if (before.Length > 0) yield return (null, before);
        for (var t = 0; t < tags.Count; t++)
        {
            var from = tags[t].Index + tags[t].Length;
            var to = t + 1 < tags.Count ? tags[t + 1].Index : raw.Length;
            var name = WebUtility.HtmlDecode(tags[t].Groups["n"].Value).Trim();
            yield return (name.Length > 0 ? name : null, Clean(raw[from..to]));
        }
    }

    private static string Clean(string s) =>
        Regex.Replace(WebUtility.HtmlDecode(AnyTag.Replace(s, " ")), @"\s+", " ").Trim();

    private static List<Cue> ReadTimestampedText(string[] lines)
    {
        var cues = new List<Cue>();
        Cue? current = null;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0) continue;

            if (TryTimedLine(line, out var time, out var speaker, out var rest))
            {
                current = new Cue { Start = time, Speaker = speaker, Text = rest, LineStart = n + 1, LineEnd = n + 1 };
                cues.Add(current);
            }
            else if (current is not null)
            {
                current.Text = current.Text.Length == 0 ? line : current.Text + " " + line;
                current.LineEnd = n + 1;
            }
        }

        // A plain-text cue has no end: it lasts until the next one starts (the last one, for as long as it takes to say).
        for (var c = 0; c < cues.Count; c++)
        {
            var cue = cues[c];
            if (c + 1 < cues.Count)
                cue.End = Math.Max(cues[c + 1].Start, cue.Start + 1);
            else
            {
                var words = cue.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                cue.End = cue.Start + Math.Clamp(words / 2.5, 3, 120);
                cue.EndEstimated = true;
            }
        }
        cues.RemoveAll(c => c.Text.Length == 0);
        return cues;
    }

    private static bool TryTimedLine(string line, out double time, out string? speaker, out string rest)
    {
        speaker = null;
        rest = "";
        time = 0;

        var m = NameThenTime.Match(line);
        if (m.Success && TryTime(m.Groups["t"].Value, out time))
        {
            speaker = m.Groups["n"].Value;
            rest = m.Groups["rest"].Value.Trim();
            return true;
        }
        m = NameSpaceTime.Match(line);
        if (m.Success && TryTime(m.Groups["t"].Value, out time))
        {
            speaker = m.Groups["n"].Value;
            return true;
        }
        m = Bracketed.Match(line);
        if (!m.Success) m = Leading.Match(line);
        if (m.Success && TryTime(m.Groups["t"].Value, out time))
        {
            rest = m.Groups["rest"].Value.Trim();
            return true;
        }
        return false;
    }

    /// <summary>
    /// "Dale Henry: …" is a speaker only if that name starts more than one cue (or the transcript is
    /// tiny): a one-off "Note: …" or "Warning: …" is something somebody said, not somebody.
    /// </summary>
    private static void AssignPrefixSpeakers(List<Cue> cues)
    {
        var candidates = new Dictionary<string, int>(StringComparer.Ordinal);
        var matches = new Match?[cues.Count];
        for (var i = 0; i < cues.Count; i++)
        {
            if (cues[i].Speaker is not null) continue;
            var m = SpeakerPrefix.Match(cues[i].Text);
            if (!m.Success) continue;
            matches[i] = m;
            var name = m.Groups["n"].Value;
            candidates[name] = candidates.GetValueOrDefault(name) + 1;
        }
        for (var i = 0; i < cues.Count; i++)
        {
            if (matches[i] is not { } m) continue;
            var name = m.Groups["n"].Value;
            if (candidates[name] < 2 && cues.Count > 3) continue;
            cues[i].Speaker = name;
            cues[i].Text = m.Groups["rest"].Value.Trim();
        }
    }

    // ── turns ───────────────────────────────────────────────────────────

    private sealed class Turn
    {
        public string? Speaker;
        public double Start;
        public double End;
        public string Text = "";
        public int Cues;
        public int LineStart;
        public int LineEnd;
        public bool EndEstimated;
    }

    private static IEnumerable<Turn> Merge(List<Cue> cues)
    {
        Turn? turn = null;
        foreach (var c in cues)
        {
            if (turn is not null
                && turn.Speaker == c.Speaker
                && c.Start - turn.End <= MergeGapSeconds
                && Math.Max(c.End, turn.End) - turn.Start <= MaxTurnSeconds
                && turn.Text.Length + c.Text.Length < MaxTurnChars)
            {
                turn.End = Math.Max(turn.End, c.End);
                turn.Text += " " + c.Text;
                turn.Cues++;
                turn.LineStart = Math.Min(turn.LineStart, c.LineStart);
                turn.LineEnd = Math.Max(turn.LineEnd, c.LineEnd);
                turn.EndEstimated = c.EndEstimated;
                continue;
            }
            if (turn is not null) yield return turn;
            turn = new Turn
            {
                Speaker = c.Speaker, Start = c.Start, End = c.End, Text = c.Text, Cues = 1,
                LineStart = c.LineStart, LineEnd = c.LineEnd, EndEstimated = c.EndEstimated,
            };
        }
        if (turn is not null) yield return turn;
    }

    // ── time ────────────────────────────────────────────────────────────

    public static bool TryTime(string s, out double seconds)
    {
        seconds = 0;
        var m = TimeShape.Match(s.Trim());
        if (!m.Success) return false;
        var h = m.Groups["h"].Success ? int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var sec = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (sec > 59) return false;
        var frac = m.Groups["f"].Success ? double.Parse("0." + m.Groups["f"].Value, CultureInfo.InvariantCulture) : 0;
        seconds = h * 3600 + min * 60 + sec + frac;
        return true;
    }

    /// <summary>The locator form of a time: <c>00:14:32</c>.</summary>
    public static string Hms(double seconds)
    {
        var total = (long)Math.Max(0, Math.Floor(seconds));
        return $"{total / 3600:00}:{total % 3600 / 60:00}:{total % 60:00}";
    }

    /// <summary>The form a person reads: <c>14:32</c>, or <c>1:14:32</c> past the hour.</summary>
    public static string Display(double seconds)
    {
        var total = (long)Math.Max(0, Math.Floor(seconds));
        return total >= 3600
            ? $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }

    /// <summary>Parses a time locator, <c>t=00:14:32-00:15:40</c>, into seconds; the end is optional.</summary>
    public static bool TryParseLocator(string? locator, out double start, out double end)
    {
        start = end = 0;
        if (locator is null || !locator.StartsWith("t=", StringComparison.Ordinal)) return false;
        var body = locator[2..];
        var tilde = body.IndexOf('~');
        if (tilde >= 0) body = body[..tilde];
        var dash = body.IndexOf('-');
        if (dash < 0) return TryTime(body, out start) && (end = start) >= 0;
        return TryTime(body[..dash], out start) && TryTime(body[(dash + 1)..], out end);
    }
}
