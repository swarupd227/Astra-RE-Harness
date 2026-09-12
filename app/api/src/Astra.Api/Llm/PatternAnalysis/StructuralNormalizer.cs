using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>
/// Zero-LLM structural fingerprint of a routine. Strips comments, replaces
/// literals with placeholders, maps every non-keyword identifier to
/// <c>$n</c> in first-seen order, and hashes the resulting token stream —
/// so two routines that are the same code modulo names, comments and
/// whitespace share a hash. Pattern analysis surveys one exemplar per hash
/// and propagates its digest to the rest, which on bean/DTO-style corpora
/// removes half or more of the LLM calls before any model is involved.
/// Also recognises the pure accessor shapes (<c>return $1;</c>,
/// <c>$1 = $2;</c>) that need no model at all.
/// </summary>
public static class StructuralNormalizer
{
    public sealed record Result(string Hash, int TokenCount, string Normalized, string? TrivialShape);

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\b\d[\d_.]*(?:[eE][+-]?\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TokenRx = new(@"[A-Za-z_$][A-Za-z0-9_$]*|:=|::|->|==|!=|<=|>=|<>|&&|\|\||\+\+|--|[^\sA-Za-z0-9_$]", RegexOptions.Compiled);

    private static readonly Regex GetterShape = new(@"^(?:begin\s+)?(?:return|result\s*:=)\s+\$\d+\s*;?\s*(?:end\s*;?)?$", RegexOptions.Compiled);
    private static readonly Regex SetterShape = new(@"^(?:begin\s+)?\$\d+\s*(?::=|=)\s*\$\d+\s*;?\s*(?:end\s*;?)?$", RegexOptions.Compiled);

    public static Result Normalize(string sourceLanguage, string routineText)
    {
        var family = FamilyOf(sourceLanguage);
        var text = StripComments(family, routineText ?? "");
        text = ReplaceStrings(family, text);
        text = Number.Replace(text, "0");
        if (family.CaseInsensitive) text = text.ToLowerInvariant();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (Match m in TokenRx.Matches(text))
        {
            var tok = m.Value;
            if (Identifier.IsMatch(tok) && tok[0] != '$' && !family.Keywords.Contains(tok))
            {
                if (!map.TryGetValue(tok, out var placeholder))
                {
                    placeholder = "$" + (map.Count + 1);
                    map[tok] = placeholder;
                }
                tok = placeholder;
            }
            tokens.Add(tok);
        }

        var normalized = string.Join(' ', tokens);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return new Result(hash, tokens.Count, normalized, DetectTrivialShape(family, routineText ?? "", tokens));
    }

    /// <summary>
    /// A body (everything after the first line) that is exactly a
    /// <c>return x;</c> / <c>Result := x;</c> or <c>a := b;</c> is an
    /// accessor; there is nothing for a model to say about it.
    /// </summary>
    private static string? DetectTrivialShape(Family family, string original, List<string> allTokens)
    {
        if (allTokens.Count > 24) return null;
        var lines = original.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2) return null;
        var body = string.Join('\n', lines.Skip(1));
        body = StripComments(family, body);
        body = ReplaceStrings(family, body);
        body = Number.Replace(body, "0");
        if (family.CaseInsensitive) body = body.ToLowerInvariant();

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var toks = new List<string>();
        foreach (Match m in TokenRx.Matches(body))
        {
            var tok = m.Value;
            if (Identifier.IsMatch(tok) && !family.Keywords.Contains(tok) && tok is not ("result" or "return"))
            {
                if (!map.TryGetValue(tok, out var p)) { p = "$" + (map.Count + 1); map[tok] = p; }
                tok = p;
            }
            // Drop the braces that C-family bodies wrap themselves in.
            if (tok is "{" or "}") continue;
            toks.Add(tok);
        }
        var flat = Whitespace.Replace(string.Join(' ', toks), " ").Trim();
        if (flat.Length == 0) return null;
        if (GetterShape.IsMatch(flat)) return "trivial-getter";
        if (SetterShape.IsMatch(flat)) return "trivial-setter";
        return null;
    }

    // ── Language families ────────────────────────────────────────────────

    private sealed record Family(string Name, bool CaseInsensitive, HashSet<string> Keywords, string[] LineComments, (string Open, string Close)[] BlockComments, char[] StringQuotes, bool ColumnOneComments);

    private static readonly HashSet<string> CLikeKeywords = new(StringComparer.Ordinal)
    {
        "if","else","for","while","do","switch","case","break","continue","return","new","delete","this","null","true","false",
        "int","long","short","char","bool","boolean","double","float","void","string","var","auto","const","static","public",
        "private","protected","class","struct","enum","interface","namespace","using","import","package","try","catch","finally",
        "throw","throws","template","typename","virtual","override","final","abstract","extends","implements","async","await",
        "unsigned","signed","sizeof","nullptr","operator","inline","explicit","default","goto","function","echo","foreach","as",
    };

    private static readonly HashSet<string> PascalKeywords = new(StringComparer.Ordinal)
    {
        "begin","end","procedure","function","var","const","type","class","record","if","then","else","for","to","downto","do",
        "while","repeat","until","case","of","with","try","except","finally","raise","result","nil","true","false","and","or","not",
        "div","mod","integer","string","boolean","double","cardinal","int64","pointer","array","inherited","self","exit","override",
        "virtual","property","read","write","constructor","destructor","unit","interface","implementation","uses","in","is","as",
    };

    private static readonly HashSet<string> BasicKeywords = new(StringComparer.Ordinal)
    {
        "sub","function","end","if","then","else","elseif","for","to","next","do","loop","while","wend","select","case","dim","as",
        "set","let","new","nothing","true","false","and","or","not","return","exit","call","byval","byref","integer","long","string",
        "boolean","double","variant","object","private","public","property","get","on","error","resume","goto","with","each","in",
    };

    private static readonly HashSet<string> FortranKeywords = new(StringComparer.Ordinal)
    {
        "subroutine","function","end","if","then","else","endif","do","enddo","continue","return","call","integer","real","double",
        "precision","character","logical","dimension","common","data","goto","stop","write","read","format","implicit","none",
    };

    private static readonly HashSet<string> CobolKeywords = new(StringComparer.Ordinal)
    {
        "move","to","perform","if","else","end-if","compute","add","subtract","multiply","divide","display","read","write","rewrite",
        "delete","open","close","call","goback","stop","run","exit","until","varying","from","by","section","paragraph","pic",
    };

    private static readonly Family CLike = new("c-like", false, CLikeKeywords, new[] { "//" }, new[] { ("/*", "*/") }, new[] { '"', '\'' }, false);
    private static readonly Family Pascal = new("pascal", true, PascalKeywords, new[] { "//" }, new[] { ("{", "}"), ("(*", "*)") }, new[] { '\'' }, false);
    private static readonly Family Basic = new("basic", true, BasicKeywords, new[] { "'" }, Array.Empty<(string, string)>(), new[] { '"' }, false);
    private static readonly Family Fortran = new("fortran", true, FortranKeywords, new[] { "!" }, Array.Empty<(string, string)>(), new[] { '\'', '"' }, true);
    private static readonly Family Cobol = new("cobol", true, CobolKeywords, new[] { "*>" }, Array.Empty<(string, string)>(), new[] { '\'', '"' }, true);
    private static readonly Family Abl = new("abl", true, PascalKeywords, new[] { "//" }, new[] { ("/*", "*/") }, new[] { '"', '\'' }, false);
    private static readonly Family UniBasic = new("unibasic", true, BasicKeywords, new[] { "*", "!" }, Array.Empty<(string, string)>(), new[] { '"', '\'' }, false);

    private static Family FamilyOf(string? language) => (language ?? "").ToLowerInvariant() switch
    {
        "delphi" => Pascal,
        "vb6" or "vbnet" => Basic,
        "fortran-f77" or "fortran" => Fortran,
        "cobol" => Cobol,
        "openedge" or "abl" => Abl,
        "unibasic" => UniBasic,
        _ => CLike,
    };

    // ── Comment / string stripping ───────────────────────────────────────

    private static string StripComments(Family family, string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var atLineStart = true;
        while (i < text.Length)
        {
            var c = text[i];

            // Column-1 comment markers (Fortran C/c/*, COBOL '*' in col 7 is
            // approximated by a leading '*').
            if (atLineStart && family.ColumnOneComments && (c is 'C' or 'c' or '*') && family.Name != "cobol")
            {
                i = SkipToLineEnd(text, i);
                continue;
            }
            if (atLineStart && family.Name == "cobol" && LeadingStarInIndicatorArea(text, i))
            {
                i = SkipToLineEnd(text, i);
                continue;
            }

            // Strings: copy verbatim here; ReplaceStrings handles them after.
            if (Array.IndexOf(family.StringQuotes, c) >= 0 && !(family.Name == "basic" && c == '\''))
            {
                var end = FindStringEnd(text, i, c);
                sb.Append(text, i, end - i);
                i = end;
                atLineStart = false;
                continue;
            }

            var handled = false;
            foreach (var (open, close) in family.BlockComments)
            {
                if (string.CompareOrdinal(text, i, open, 0, open.Length) == 0)
                {
                    var end = text.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + close.Length;
                    sb.Append(' ');
                    handled = true;
                    break;
                }
            }
            if (handled) continue;

            foreach (var lc in family.LineComments)
            {
                // UniBasic '*' / '!' comments only count at line start.
                if (family.Name == "unibasic" && !atLineStart) break;
                if (string.CompareOrdinal(text, i, lc, 0, lc.Length) == 0)
                {
                    i = SkipToLineEnd(text, i);
                    handled = true;
                    break;
                }
            }
            if (handled) continue;

            sb.Append(c);
            atLineStart = c == '\n' || (atLineStart && char.IsWhiteSpace(c));
            i++;
        }
        return sb.ToString();
    }

    private static bool LeadingStarInIndicatorArea(string text, int i)
    {
        // COBOL: '*' or '/' in column 7 marks a comment line. Accept a
        // line whose first non-blank char (within the first 7 columns) is '*'.
        var j = i;
        while (j < text.Length && j - i < 7 && (text[j] == ' ' || text[j] == '\t')) j++;
        return j < text.Length && (text[j] == '*' || text[j] == '/') && j - i <= 6;
    }

    private static int SkipToLineEnd(string text, int i)
    {
        var nl = text.IndexOf('\n', i);
        return nl < 0 ? text.Length : nl;
    }

    private static int FindStringEnd(string text, int start, char quote)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && quote == '"') { i += 2; continue; }
            if (text[i] == quote)
            {
                // Doubled quote escapes ('' / "") in Pascal/BASIC/COBOL.
                if (i + 1 < text.Length && text[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            if (text[i] == '\n') return i;
            i++;
        }
        return text.Length;
    }

    private static string ReplaceStrings(Family family, string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (Array.IndexOf(family.StringQuotes, c) >= 0 && !(family.Name == "basic" && c == '\''))
            {
                var end = FindStringEnd(text, i, c);
                sb.Append("\"S\"");
                i = end;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
