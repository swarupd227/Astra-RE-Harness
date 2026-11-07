using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>One record of a CSV file with the lines it occupies (a quoted value can span several).</summary>
public sealed record CsvRecord(IReadOnlyList<string> Fields, int StartLine, int EndLine);

/// <summary>
/// A small, forgiving CSV reader: quoted values with embedded delimiters, quotes (<c>""</c>) and line
/// breaks, any of comma, semicolon, tab or pipe as the delimiter, blank lines skipped. It keeps the line
/// numbers of each record so a row can be shown in the file it came from.
/// </summary>
public static class CsvReader
{
    public static char SniffDelimiter(string text)
    {
        var inQuotes = false;
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
        foreach (var c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (c == '\n' || c == '\r')) { if (counts.Values.Any(v => v > 0)) break; }
            else if (!inQuotes && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key == ',' ? 0 : 1).First();
        return best.Value > 0 ? best.Key : ',';
    }

    public static List<CsvRecord> Read(string text, char delimiter, out bool unterminatedQuote)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var inQuotes = false;
        var line = 1;
        var recordStart = 1;
        unterminatedQuote = false;

        void EndField()
        {
            fields.Add(field.ToString().Trim());
            field.Clear();
            quoted = false;
        }

        void EndRecord(int endLine)
        {
            // A line with nothing on it is not a record.
            if (fields.Count == 0 && field.Length == 0 && !quoted) return;
            EndField();
            records.Add(new CsvRecord(fields.ToArray(), recordStart, endLine));
            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    if (c == '\r' || c == '\n') { line++; field.Append('\n'); }
                    else field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0 && !quoted) { inQuotes = true; quoted = true; }
            else if (c == delimiter) EndField();
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRecord(line);
                line++;
                recordStart = line;
            }
            else field.Append(c);
        }

        if (inQuotes) unterminatedQuote = true;
        EndRecord(line);
        return records;
    }
}

/// <summary>
/// One chunk per row of a ticket export: <c>row:INC-T-0412</c> is the row whose number column holds
/// that value. The chunk's text is the row as <c>column: value</c> lines, so a search or a check that a
/// name appears verbatim reads it as a person would; the facts clustering needs (id, title, priority,
/// state, opened) are in the structured part, with every column in order as <c>fields: [{name, value}]</c>.
/// The row's lines in the file are kept.
/// </summary>
public sealed class TicketAdapter : IEvidenceAdapter
{
    private static readonly string[] IdColumns =
        { "number", "ticket_number", "incident_number", "incident", "ticket", "ticket_id", "id", "key", "issue_key", "reference", "ref" };
    private static readonly string[] TitleColumns = { "short_description", "summary", "title", "subject", "name", "description" };
    private static readonly string[] PriorityColumns = { "priority", "severity", "urgency", "impact" };
    private static readonly string[] StateColumns = { "state", "status", "stage" };
    private static readonly string[] OpenedColumns = { "opened_at", "opened", "created_at", "created", "created_on", "date_opened", "reported_at", "open_date", "date" };

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Text is null) return AdapterResult.Empty;

        var text = input.Text.TrimStart('﻿');
        var records = CsvReader.Read(text, CsvReader.SniffDelimiter(text), out var unterminated);
        var warnings = new List<string>();
        if (records.Count == 0) return AdapterResult.Empty;
        if (records.Count == 1)
            return AdapterResult.Nothing("It has a header row and no tickets.");
        if (unterminated)
            warnings.Add("A quoted value is never closed, so the last row runs to the end of the file.");

        var headers = UniqueHeaders(records[0].Fields);
        var keys = headers.Select(Normalise).ToArray();
        int Col(string[] names) => Array.FindIndex(keys, k => names.Contains(k));

        var idCol = Col(IdColumns);
        var titleCol = Col(TitleColumns);
        var priorityCol = Col(PriorityColumns);
        var stateCol = Col(StateColumns);
        var openedCol = Col(OpenedColumns);
        if (idCol < 0)
            warnings.Add("No column looks like a ticket number, so rows are cited by their position (row:1, row:2 …).");

        var chunks = new List<EvidenceChunkDraft>(records.Count - 1);
        var ragged = 0;
        var firstRagged = 0;
        for (var n = 1; n < records.Count; n++)
        {
            var r = records[n];
            if (r.Fields.All(string.IsNullOrWhiteSpace)) continue;
            if (r.Fields.Count != headers.Count)
            {
                if (ragged++ == 0) firstRagged = r.StartLine;
            }

            string At(int col) => col >= 0 && col < r.Fields.Count ? r.Fields[col] : "";
            var id = At(idCol).Trim();
            var locatorId = id.Length > 0 ? Regex.Replace(id, @"\s+", "_") : n.ToString();
            var title = At(titleCol);

            var body = new StringBuilder();
            for (var c = 0; c < r.Fields.Count; c++)
            {
                if (r.Fields[c].Length == 0) continue;
                var header = c < headers.Count ? headers[c] : $"column_{c + 1}";
                body.Append(header).Append(": ").Append(r.Fields[c]).Append('\n');
            }

            chunks.Add(new EvidenceChunkDraft(
                $"row:{locatorId}",
                id.Length > 0 ? (title.Length > 0 ? $"{id} · {AdapterText.Snippet(title, 100)}" : id) : $"Row {n}",
                body.ToString().TrimEnd('\n'),
                AdapterJson.Object(w =>
                {
                    AdapterJson.Optional(w, "id", id);
                    AdapterJson.Optional(w, "title", title);
                    AdapterJson.Optional(w, "priority", At(priorityCol));
                    AdapterJson.Optional(w, "state", At(stateCol));
                    AdapterJson.Optional(w, "openedAt", At(openedCol));
                    // A list, not an object: the structured part is stored as jsonb, which does not keep the
                    // order of an object's keys, and a ticket reads in the order its export has the columns.
                    w.WriteStartArray("fields");
                    for (var c = 0; c < r.Fields.Count; c++)
                    {
                        if (r.Fields[c].Length == 0) continue;
                        w.WriteStartObject();
                        w.WriteString("name", c < headers.Count ? headers[c] : $"column_{c + 1}");
                        w.WriteString("value", r.Fields[c]);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }),
                r.StartLine,
                r.EndLine));
        }

        if (ragged > 0)
            warnings.Add($"{ragged:N0} {(ragged == 1 ? "row has" : "rows have")} a different number of columns than the header (the first starts at line {firstRagged}); values were kept as read.");
        return new AdapterResult(chunks, warnings);
    }

    private static List<string> UniqueHeaders(IReadOnlyList<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var h = raw[i].Length > 0 ? raw[i] : $"column_{i + 1}";
            var unique = h;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{h}_{n}";
            result.Add(unique);
        }
        return result;
    }

    private static string Normalise(string header) =>
        Regex.Replace(header.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
}
