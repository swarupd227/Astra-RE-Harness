namespace Astra.Api.Evidence;

public enum LocatorKind { Lines, Time, Row, XPath, Heading, JsonPath, Other }

/// <summary>
/// A locator says where inside a file a piece of evidence sits: lines <c>L120-168</c>, a time
/// range <c>t=00:14:32-00:15:40</c>, a record <c>row:INC-T-0412</c>, an XML path
/// <c>xpath:/JOBP/Tasks/Task[3]</c>, a heading <c>h=Restart</c> or a JSON path
/// <c>json:$.Ofac.TimeoutSeconds</c>. Only line locators can be shown as a window of the file.
/// </summary>
public static class EvidenceLocator
{
    public static string Lines(int start, int end) => $"L{start}-{end}";

    public static LocatorKind Classify(string? locator)
    {
        if (string.IsNullOrEmpty(locator)) return LocatorKind.Other;
        if (TryParseLines(locator, out _, out _)) return LocatorKind.Lines;
        if (locator.StartsWith("t=", StringComparison.Ordinal)) return LocatorKind.Time;
        if (locator.StartsWith("row:", StringComparison.Ordinal)) return LocatorKind.Row;
        if (locator.StartsWith("xpath:", StringComparison.Ordinal)) return LocatorKind.XPath;
        if (locator.StartsWith("h=", StringComparison.Ordinal)) return LocatorKind.Heading;
        if (locator.StartsWith("json:", StringComparison.Ordinal)) return LocatorKind.JsonPath;
        return LocatorKind.Other;
    }

    /// <summary>Parses <c>L120-168</c> (or <c>L7</c>, one line). Lines are 1-based and start ≤ end.</summary>
    public static bool TryParseLines(string? locator, out int start, out int end)
    {
        start = end = 0;
        if (locator is null || locator.Length < 2 || locator[0] != 'L') return false;
        var body = locator.AsSpan(1);
        var dash = body.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(body, System.Globalization.NumberStyles.None, null, out start) || start < 1) return false;
            end = start;
            return true;
        }
        if (!int.TryParse(body[..dash], System.Globalization.NumberStyles.None, null, out start)
            || !int.TryParse(body[(dash + 1)..], System.Globalization.NumberStyles.None, null, out end))
        {
            start = end = 0;
            return false;
        }
        if (start < 1 || end < start)
        {
            start = end = 0;
            return false;
        }
        return true;
    }
}
