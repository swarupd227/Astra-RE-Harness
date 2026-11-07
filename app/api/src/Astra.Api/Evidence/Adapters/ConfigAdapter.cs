using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Api.Evidence;

/// <summary>
/// One chunk per setting in a JSON configuration file: <c>json:$.Ofac.TimeoutSeconds</c> is the value
/// the application reads as <c>Ofac:TimeoutSeconds</c>. A short list of plain values stays together as
/// one chunk. The lines each setting sits on are kept, so the viewer can show it in the file.
/// <para>
/// A chunk feeds search and, later, model prompts, so a value that looks like a secret (a key named
/// password, token, secret …, or <c>Password=…</c> inside a connection string) never enters one: it is
/// replaced by <c>&lt;&lt;MASKED&gt;&gt;</c>. A placeholder such as <c>&lt;&lt;SECRET&gt;&gt;</c> or
/// <c>${VAR}</c> is not a secret and is kept. The same rule hides such values in the lines the viewer
/// shows around a setting (<see cref="RedactLine"/>); the stored file itself is untouched.
/// </para>
/// </summary>
public sealed class ConfigAdapter : IEvidenceAdapter
{
    public const int MaxDepth = 64;
    public const int MaxInlineArray = 12;
    public const int MaxValueChars = 2000;
    public const string Masked = "<<MASKED>>";

    private static readonly Regex SecretName = new(
        "(password|passwd|pwd|passphrase|secret|token|apikey|privatekey|accesskey|accountkey|sharedkey|credentials?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(
        @"^\s*(<<.*>>|\$\{.*\}|\{\{.*\}\}|%[^%]+%|#\{.*\}#|<[^<>]*>)\s*$", RegexOptions.Compiled);

    private static readonly Regex InlineSecret = new(
        @"(?i)\b(password|pwd|secret|accountkey|sharedaccesskey)\s*=\s*([^;]*)", RegexOptions.Compiled);

    private static readonly Regex SimpleName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private sealed class Node
    {
        public string? Name;
        public int Index = -1;
        public JsonTokenType Type;
        public string? Text;
        public long StartOffset;
        public long EndOffset;
        public readonly List<Node> Children = new();
        public Node? Parent;
        public bool IsContainer => Type is JsonTokenType.StartObject or JsonTokenType.StartArray;
    }

    public AdapterResult Build(AdapterInput input)
    {
        if (input.Text is null) return AdapterResult.Empty;

        var bytes = Encoding.UTF8.GetBytes(input.Text.TrimStart('﻿'));
        Node top;
        try
        {
            top = Parse(bytes);
        }
        catch (JsonException ex)
        {
            var fallback = LineWindowAdapter.Instance.Build(input);
            var warnings = new List<string>
            {
                $"This is not valid JSON ({AdapterText.Snippet(ex.Message, 120)}), so it was indexed by line windows and a citation points at lines, not at a setting.",
            };
            warnings.AddRange(fallback.Warnings);
            return new AdapterResult(fallback.Chunks, warnings);
        }

        var lines = LineIndex.Of(bytes);
        var chunks = new List<EvidenceChunkDraft>();

        void Emit(Node n)
        {
            if (!n.IsContainer)
            {
                chunks.Add(ScalarChunk(n, lines));
                return;
            }
            if (n.Type == JsonTokenType.StartArray && n.Children.Count is > 0 and <= MaxInlineArray && n.Children.All(c => !c.IsContainer))
            {
                chunks.Add(ArrayChunk(n, lines));
                return;
            }
            foreach (var child in n.Children) Emit(child);
        }

        foreach (var n in top.Children) Emit(n);
        return new AdapterResult(chunks, Array.Empty<string>());
    }

    private static Node Parse(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = MaxDepth,
        });
        var holder = new Node { Type = JsonTokenType.None };
        var stack = new Stack<Node>();
        stack.Push(holder);
        string? property = null;
        long propertyStart = 0;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    property = reader.GetString();
                    propertyStart = reader.TokenStartIndex;
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    stack.Push(Attach(stack.Peek(), ref property, propertyStart, reader.TokenStartIndex, reader.TokenType));
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop().EndOffset = reader.TokenStartIndex;
                    break;

                default:
                {
                    var n = Attach(stack.Peek(), ref property, propertyStart, reader.TokenStartIndex, reader.TokenType);
                    n.Text = ScalarText(ref reader);
                    n.EndOffset = reader.BytesConsumed - 1;
                    break;
                }
            }
        }
        return holder;
    }

    private static Node Attach(Node parent, ref string? property, long propertyStart, long tokenStart, JsonTokenType type)
    {
        var n = new Node { Type = type, Parent = parent, StartOffset = property is not null ? propertyStart : tokenStart };
        if (parent.Type == JsonTokenType.StartArray) n.Index = parent.Children.Count;
        else if (parent.Type == JsonTokenType.StartObject) n.Name = property ?? "";
        property = null;
        parent.Children.Add(n);
        return n;
    }

    private static string ScalarText(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString() ?? "",
        JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => "null",
    };

    // ── chunks ──────────────────────────────────────────────────────────

    private static EvidenceChunkDraft ScalarChunk(Node n, LineIndex lines)
    {
        var (colon, jsonPath) = PathOf(n);
        var typeName = n.Type switch
        {
            JsonTokenType.String => "string",
            JsonTokenType.Number => "number",
            JsonTokenType.True or JsonTokenType.False => "boolean",
            _ => "null",
        };
        var value = n.Type == JsonTokenType.String ? MaskValue(n.Name, n.Text ?? "", out var masked) : Raw(n.Text ?? "", out masked);
        var shown = AdapterText.Clip(value, MaxValueChars);

        return new EvidenceChunkDraft(
            $"json:{jsonPath}",
            colon.Length > 0 ? colon : "(value)",
            $"{(colon.Length > 0 ? colon : "(value)")} = {(n.Type == JsonTokenType.String && shown.Length == 0 ? "\"\"" : shown)}",
            AdapterJson.Object(w =>
            {
                w.WriteString("key", colon);
                w.WriteString("path", jsonPath);
                w.WriteString("value", shown);
                w.WriteString("type", typeName);
                w.WriteBoolean("masked", masked);
            }),
            lines.LineAt(n.StartOffset),
            lines.LineAt(n.EndOffset));
    }

    private static EvidenceChunkDraft ArrayChunk(Node n, LineIndex lines)
    {
        var (colon, jsonPath) = PathOf(n);
        var anyMasked = false;
        var values = new List<string>(n.Children.Count);
        foreach (var c in n.Children)
        {
            var v = c.Type == JsonTokenType.String ? MaskValue(n.Name, c.Text ?? "", out var m) : Raw(c.Text ?? "", out m);
            anyMasked |= m;
            values.Add(AdapterText.Clip(v, 200));
        }
        var key = colon.Length > 0 ? colon : "(list)";
        return new EvidenceChunkDraft(
            $"json:{jsonPath}",
            key,
            $"{key} = [{string.Join(", ", values)}]",
            AdapterJson.Object(w =>
            {
                w.WriteString("key", colon);
                w.WriteString("path", jsonPath);
                w.WriteString("type", "array");
                w.WriteBoolean("masked", anyMasked);
                w.WriteStartArray("values");
                foreach (var v in values) w.WriteStringValue(v);
                w.WriteEndArray();
            }),
            lines.LineAt(n.StartOffset),
            lines.LineAt(n.EndOffset));
    }

    private static string Raw(string text, out bool masked)
    {
        masked = false;
        return text;
    }

    private static bool IsSecretKey(string? key) =>
        key is not null && SecretName.IsMatch(key.Replace("_", "").Replace("-", "").Replace(" ", ""));

    private static readonly Regex StringPair = new(
        "\"((?:[^\"\\\\]|\\\\.)*)\"(\\s*:\\s*)\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    // The same rule as InlineSecret, but for a line of the file, where the value ends at a quote as well.
    private static readonly Regex InlineSecretInLine = new(
        "(?i)\\b(password|pwd|secret|accountkey|sharedaccesskey)\\s*=\\s*([^;\"\\\\]*)", RegexOptions.Compiled);

    /// <summary>
    /// A line of the file as the viewer may show it: the value of a secret-looking key, and a
    /// <c>Password=…</c> inside any string, are replaced; placeholders are kept. It works a line at a time
    /// (the viewer shows a window of lines), so a list of secrets under one key is not recognised: its
    /// elements are masked in the chunk, not in the file view.
    /// </summary>
    public static string RedactLine(string line)
    {
        var pairs = StringPair.Replace(line, m =>
            IsSecretKey(m.Groups[1].Value) && m.Groups[3].Value.Length > 0 && !Placeholder.IsMatch(m.Groups[3].Value)
                ? $"\"{m.Groups[1].Value}\"{m.Groups[2].Value}\"{Masked}\""
                : m.Value);
        return InlineSecretInLine.Replace(pairs, m =>
            Placeholder.IsMatch(m.Groups[2].Value) ? m.Value : $"{m.Groups[1].Value}={Masked}");
    }

    /// <summary>The value as it may appear in a chunk: a secret-looking one is replaced, a placeholder is kept.</summary>
    public static string MaskValue(string? key, string value, out bool masked)
    {
        masked = false;
        if (value.Length == 0 || Placeholder.IsMatch(value)) return value;

        if (IsSecretKey(key))
        {
            masked = true;
            return Masked;
        }

        var changed = false;
        var result = InlineSecret.Replace(value, m =>
        {
            if (Placeholder.IsMatch(m.Groups[2].Value)) return m.Value;
            changed = true;
            return $"{m.Groups[1].Value}={Masked}";
        });
        masked = changed;
        return result;
    }

    // ── paths ───────────────────────────────────────────────────────────

    /// <summary>The setting's name as the application reads it (<c>Ofac:TimeoutSeconds</c>) and as a JSON path (<c>$.Ofac.TimeoutSeconds</c>).</summary>
    private static (string Colon, string JsonPath) PathOf(Node n)
    {
        // The root value itself (the outermost object) is "$", not a step; its parent is the parse holder.
        var segments = new List<Node>();
        for (var c = n; c.Parent is { Parent: not null }; c = c.Parent!) segments.Add(c);
        segments.Reverse();

        var colon = new StringBuilder();
        var path = new StringBuilder("$");
        foreach (var s in segments)
        {
            if (colon.Length > 0) colon.Append(':');
            if (s.Name is not null)
            {
                colon.Append(s.Name);
                if (SimpleName.IsMatch(s.Name)) path.Append('.').Append(s.Name);
                else path.Append("['").Append(s.Name.Replace("\\", "\\\\").Replace("'", "\\'")).Append("']");
            }
            else
            {
                colon.Append(s.Index);
                path.Append('[').Append(s.Index).Append(']');
            }
        }
        return (colon.ToString(), path.ToString());
    }
}
