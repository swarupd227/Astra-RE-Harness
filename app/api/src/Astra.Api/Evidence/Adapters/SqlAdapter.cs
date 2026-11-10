using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>
/// A database script, cut into the statements that define something: <c>L10-42</c> is the CREATE
/// PROCEDURE on those lines, comments directly above it included. Each such chunk says what it is
/// (table, view, procedure …) and, in its structured part, what is in it: columns and keys for a table;
/// parameters, what it reads, writes and calls, and whether it uses a transaction, a cursor or a loop for a
/// procedure. The same facts feed the entity graph, each pointing at its chunk.
/// <para>
/// ScriptDom reads the T-SQL. A batch it cannot read is split at the next CREATE/ALTER and each piece is
/// tried again; a piece it still cannot read is read by pattern, said so in a warning, and its links are
/// medium confidence. Parsing is bounded in time and size, so one huge or odd file cannot hold up an upload.
/// </para>
/// </summary>
public sealed class SqlAdapter : IEvidenceAdapter
{
    public const int WindowLines = 40;
    public const int MaxBatchChars = 400_000;
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    private static readonly Regex Go = new(@"^\s*GO(?:\s+\d+)?\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TimeSpan _budget;
    private readonly int _maxBatchChars;

    public SqlAdapter() : this(DefaultBudget, MaxBatchChars) { }

    public SqlAdapter(TimeSpan budget, int maxBatchChars)
    {
        _budget = budget;
        _maxBatchChars = maxBatchChars;
    }

    private sealed record Batch(int StartLine, string Text);

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Text is null) return AdapterResult.Empty;
        var text = input.Text.TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(text)) return AdapterResult.Empty;

        var lines = text.Split('\n');
        var clock = Stopwatch.StartNew();
        var spans = new List<SqlStatementSpan>();
        var warnings = new List<string>();

        foreach (var batch in Batches(lines))
        {
            if (string.IsNullOrWhiteSpace(batch.Text)) continue;
            ReadBatch(batch, clock, spans, warnings);
        }
        spans.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));

        var name = input.Path[(input.Path.LastIndexOf('/') + 1)..];
        var chunks = new List<EvidenceChunkDraft>();
        var located = new List<(SqlObject Object, string Locator)>();

        var previousEnd = 0;
        var others = new List<SqlStatementSpan>();
        void FlushOthers()
        {
            if (others.Count == 0) return;
            var from = others[0].StartLine;
            var to = others[^1].EndLine;
            others.Clear();
            if (SqlPatternReader.Strip(Slice(lines, from, to)).Trim().Length == 0) return;
            for (var a = from; a <= to; a += WindowLines)
            {
                var b = Math.Min(a + WindowLines - 1, to);
                chunks.Add(new EvidenceChunkDraft(
                    EvidenceLocator.Lines(a, b), $"{name} · lines {a}–{b}", Slice(lines, a, b), LineStart: a, LineEnd: b));
            }
        }

        foreach (var span in spans)
        {
            if (span.Object is null)
            {
                others.Add(span);
                previousEnd = Math.Max(previousEnd, span.EndLine);
                continue;
            }
            FlushOthers();

            var start = WithComments(lines, span.StartLine, previousEnd);
            var end = Math.Min(span.EndLine, lines.Length);
            span.Object.StartLine = start;
            span.Object.EndLine = end;
            var locator = EvidenceLocator.Lines(start, end);
            chunks.Add(new EvidenceChunkDraft(locator, Label(span.Object), Slice(lines, start, end), StructuredOf(span.Object), start, end));
            located.Add((span.Object, locator));
            previousEnd = Math.Max(previousEnd, end);
        }
        FlushOthers();

        return new AdapterResult(chunks, warnings, SqlFacts.From(located), SummaryOf(located.Select(l => l.Object)));
    }

    // ── reading ─────────────────────────────────────────────────────────

    private void ReadBatch(Batch batch, Stopwatch clock, List<SqlStatementSpan> spans, List<string> warnings)
    {
        string? reason = null;
        if (clock.Elapsed >= _budget) reason = "the time allowed for reading this file ran out";
        else if (batch.Text.Length > _maxBatchChars) reason = $"the batch is longer than {_maxBatchChars:N0} characters";
        else
        {
            var whole = SqlScriptDomReader.TryRead(batch.Text, batch.StartLine - 1, out var error);
            if (whole is not null)
            {
                spans.AddRange(whole);
                return;
            }
            reason = error;
        }

        // Split at each CREATE/ALTER and try the pieces separately: one bad statement should not cost the rest.
        var failedFrom = int.MaxValue;
        var failedTo = 0;
        foreach (var piece in Pieces(batch))
        {
            var readable = clock.Elapsed < _budget && piece.Text.Length <= _maxBatchChars
                ? SqlScriptDomReader.TryRead(piece.Text, piece.StartLine - 1, out _)
                : null;
            if (readable is not null)
            {
                spans.AddRange(readable);
                continue;
            }

            var endLine = piece.StartLine + piece.Text.TrimEnd().Count(c => c == '\n');
            spans.Add(new SqlStatementSpan(piece.StartLine, endLine, SqlPatternReader.Read(piece.Text, piece.StartLine, endLine)));
            failedFrom = Math.Min(failedFrom, piece.StartLine);
            failedTo = Math.Max(failedTo, endLine);
        }

        if (failedTo > 0)
            warnings.Add($"Lines {failedFrom}–{failedTo} could not be read as T-SQL ({reason}), so they were read by pattern: names and links from them are marked medium confidence.");
    }

    /// <summary>The batch cut at every statement that creates or alters something named (the first piece may be anything before it).</summary>
    private static List<Batch> Pieces(Batch batch)
    {
        var headers = SqlPatternReader.HeaderLines(batch.Text);
        var lines = batch.Text.Split('\n');
        var starts = new List<int>();
        if (headers.Count == 0 || headers[0] > 0) starts.Add(0);
        starts.AddRange(headers);

        var pieces = new List<Batch>();
        for (var i = 0; i < starts.Count; i++)
        {
            var to = i + 1 < starts.Count ? starts[i + 1] : lines.Length;
            var text = string.Join('\n', lines[starts[i]..to]);
            if (!string.IsNullOrWhiteSpace(text)) pieces.Add(new Batch(batch.StartLine + starts[i], text));
        }
        return pieces;
    }

    /// <summary>The script cut at its GO separators; each batch remembers the line it starts on.</summary>
    private static IEnumerable<Batch> Batches(string[] lines)
    {
        var current = new List<string>();
        var start = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Go.IsMatch(lines[i]))
            {
                if (current.Count > 0) yield return new Batch(start, string.Join('\n', current));
                current.Clear();
                start = i + 2;
                continue;
            }
            current.Add(lines[i]);
        }
        if (current.Count > 0) yield return new Batch(start, string.Join('\n', current));
    }

    // ── chunks ──────────────────────────────────────────────────────────

    private static string Slice(string[] lines, int from, int to) =>
        string.Join('\n', lines[(Math.Max(1, from) - 1)..Math.Min(to, lines.Length)]);

    /// <summary>Comment lines directly above a statement belong to it ("-- stages one row at a time"); a blank line ends them.</summary>
    private static int WithComments(string[] lines, int startLine, int previousEnd)
    {
        var start = startLine;
        while (start - 1 > previousEnd)
        {
            var above = lines[start - 2].Trim();
            if (above.Length == 0) break;
            var comment = above.StartsWith("--", StringComparison.Ordinal) || above.StartsWith("/*", StringComparison.Ordinal)
                          || above.StartsWith('*') || above.EndsWith("*/", StringComparison.Ordinal);
            if (!comment) break;
            start--;
        }
        return start;
    }

    private static string Label(SqlObject o)
    {
        switch (o.Kind)
        {
            case "table": return $"TABLE {o.Name}";
            case "view": return $"VIEW {o.Name}";
            case "procedure": return $"PROCEDURE {o.Name}";
            case "function": return $"FUNCTION {o.Name}";
            case "trigger": return $"TRIGGER {o.Name}";
            case "index": return o.Table is null ? $"INDEX {o.Name}" : $"INDEX {o.Name} on {o.Table}";
            default:
            {
                var added = o.Keys.FirstOrDefault()?.Name ?? o.ForeignKeys.FirstOrDefault()?.Name;
                return added is null ? $"ALTER TABLE {o.Name}" : $"ALTER TABLE {o.Name} · {added}";
            }
        }
    }

    private static string StructuredOf(SqlObject o) => AdapterJson.Object(w =>
    {
        w.WriteString("object", o.Kind);
        w.WriteString("name", o.Name);
        AdapterJson.Optional(w, "schema", o.Schema);
        AdapterJson.Optional(w, "table", o.Table);
        w.WriteString("parsedBy", o.Parsed ? "scriptdom" : "pattern");

        if (o.Columns.Count > 0)
        {
            w.WriteStartArray("columns");
            foreach (var c in o.Columns)
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteString("type", c.Type);
                w.WriteBoolean("nullable", c.Nullable);
                if (c.InPrimaryKey) w.WriteBoolean("primaryKey", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (o.Keys.Count > 0)
        {
            w.WriteStartArray("keys");
            foreach (var k in o.Keys)
            {
                w.WriteStartObject();
                AdapterJson.Optional(w, "name", k.Name);
                w.WriteString("kind", k.Kind);
                AdapterJson.Strings(w, "columns", k.Columns);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (o.ForeignKeys.Count > 0)
        {
            w.WriteStartArray("foreignKeys");
            foreach (var f in o.ForeignKeys)
            {
                w.WriteStartObject();
                AdapterJson.Optional(w, "name", f.Name);
                AdapterJson.Strings(w, "columns", f.Columns);
                w.WriteString("references", f.References);
                AdapterJson.Strings(w, "referencedColumns", f.ReferencedColumns);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (o.Parameters.Count > 0)
        {
            w.WriteStartArray("parameters");
            foreach (var p in o.Parameters)
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("type", p.Type);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        AdapterJson.Strings(w, "reads", o.Reads);
        AdapterJson.Strings(w, "writes", o.Writes);
        AdapterJson.Strings(w, "calls", o.Calls);
        AdapterJson.Strings(w, "raises", o.Raises);
        if (o is { Kind: "procedure", Parsed: true })
        {
            // Stated both ways: "no transaction" is as much a fact about a procedure as "a transaction".
            w.WriteBoolean("usesTransaction", o.UsesTransaction);
            w.WriteBoolean("hasCursor", o.HasCursor);
            w.WriteBoolean("hasLoop", o.HasLoop);
            w.WriteBoolean("hasTryCatch", o.HasTryCatch);
        }
    });

    private static string? SummaryOf(IEnumerable<SqlObject> objects)
    {
        var list = objects.ToList();
        string Count(string kind, string noun) =>
            list.Where(o => o.Kind == kind).Select(o => o.Name.ToLowerInvariant()).Distinct().Count() is var n and > 0
                ? $"{n} {noun}{(n == 1 ? "" : "s")}"
                : "";
        var parts = new[] { Count("table", "table"), Count("view", "view"), Count("procedure", "stored procedure"), Count("function", "function") }
            .Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return null;
        return "Read " + (parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]) + ".";
    }
}

/// <summary>Turns what a script defines into entity and link facts, each pointing at the chunk that shows it.</summary>
internal static class SqlFacts
{
    public static FactSet From(IReadOnlyList<(SqlObject Object, string Locator)> objects)
    {
        var entities = new List<EntityFact>();
        var links = new List<LinkFact>();

        foreach (var (o, locator) in objects)
        {
            var confidence = o.Parsed ? EntityVocabulary.High : EntityVocabulary.Medium;
            switch (o.Kind)
            {
                case "table":
                case "alter-table":
                case "index":
                {
                    var table = o.Kind == "table" ? o.Name : o.Table;
                    if (string.IsNullOrWhiteSpace(table)) break;
                    entities.Add(new EntityFact(EntityVocabulary.Table, table, locator, TableAttrs(o)));
                    foreach (var fk in o.ForeignKeys.Where(f => f.References.Length > 0))
                        links.Add(new LinkFact(EntityVocabulary.References, new(EntityVocabulary.Table, table), new(EntityVocabulary.Table, fk.References), locator, confidence,
                            Attrs.Of(("columns", fk.Columns.ToList()), ("referencedColumns", fk.ReferencedColumns.ToList()), ("constraint", fk.Name))));
                    break;
                }
                case "view":
                    entities.Add(new EntityFact(EntityVocabulary.View, o.Name, locator, Attrs.Of(("schema", o.Schema), ("parsedBy", o.Parsed ? null : "pattern"))));
                    AddBodyLinks(links, EntityVocabulary.View, o, locator, confidence);
                    break;
                case "procedure":
                    entities.Add(new EntityFact(EntityVocabulary.Procedure, o.Name, locator, Attrs.Of(
                        ("schema", o.Schema),
                        ("parameters", o.Parameters.Select(p => new { name = p.Name, type = p.Type }).ToList()),
                        ("usesTransaction", o.Parsed ? (object?)o.UsesTransaction : null),
                        ("hasCursor", o.Parsed ? (object?)o.HasCursor : null),
                        ("hasLoop", o.Parsed ? (object?)o.HasLoop : null),
                        ("hasTryCatch", o.Parsed ? (object?)o.HasTryCatch : null),
                        ("raises", o.Raises.ToList()),
                        ("parsedBy", o.Parsed ? null : "pattern"))));
                    AddBodyLinks(links, EntityVocabulary.Procedure, o, locator, confidence);
                    break;
                case "function":
                    entities.Add(new EntityFact(EntityVocabulary.Function, o.Name, locator, Attrs.Of(
                        ("schema", o.Schema),
                        ("parameters", o.Parameters.Select(p => new { name = p.Name, type = p.Type }).ToList()))));
                    break;
            }
        }
        return new FactSet(entities, links);
    }

    private static Dictionary<string, object?> TableAttrs(SqlObject o)
    {
        var primary = o.Keys.Where(k => k.Kind == "primary").SelectMany(k => k.Columns).ToList();
        return Attrs.Of(
            ("schema", o.Schema),
            ("columns", o.Columns.Select(c => new { name = c.Name, type = c.Type, nullable = c.Nullable, primaryKey = c.InPrimaryKey }).ToList()),
            ("primaryKey", primary),
            ("uniqueKeys", o.Keys.Where(k => k.Kind == "unique").Select(k => new { name = k.Name, columns = k.Columns.ToList() }).ToList()),
            ("foreignKeys", o.ForeignKeys.Select(f => new { name = f.Name, columns = f.Columns.ToList(), references = f.References, referencedColumns = f.ReferencedColumns.ToList() }).ToList()),
            ("parsedBy", o.Parsed ? null : "pattern"));
    }

    private static void AddBodyLinks(List<LinkFact> links, string fromType, SqlObject o, string locator, string confidence)
    {
        foreach (var t in o.Reads)
            links.Add(new LinkFact(EntityVocabulary.Reads, new(fromType, o.Name), new(EntityVocabulary.Table, t), locator, confidence));
        foreach (var t in o.Writes)
            links.Add(new LinkFact(EntityVocabulary.Writes, new(fromType, o.Name), new(EntityVocabulary.Table, t), locator, confidence));
        foreach (var p in o.Calls)
            links.Add(new LinkFact(EntityVocabulary.Calls, new(fromType, o.Name), new(EntityVocabulary.Procedure, p), locator, confidence));
    }
}
