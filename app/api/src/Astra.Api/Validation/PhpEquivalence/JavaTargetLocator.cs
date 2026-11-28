using System.Text.RegularExpressions;

namespace Astra.Api.Validation.PhpEquivalence;

/// <summary>
/// Finds the Java method in a generated package that corresponds to a PHP routine: the same name (PHP's snake_case
/// read as camelCase), a class named like the PHP class, or a method/class that names the PHP routine in an
/// annotation or comment (<c>@TargetMapping(phpConstruct = "qty.php resolveQty")</c>, <c>@SourceRoutine</c>, a
/// javadoc line). Only main sources; interfaces and abstract methods are not callable. Pure: reads the text.
/// </summary>
public static partial class JavaTargetLocator
{
    public sealed record Target(string ClassName, string MethodName, int ParamCount, string Path, int Score, string Why);

    private sealed record Method(string Name, int ParamCount, int Offset, bool IsStatic);

    [GeneratedRegex(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex Package();

    [GeneratedRegex(@"(?:^|\s)(?:(?:public|protected|private|final|abstract|sealed|non-sealed|static)\s+)*(class|record|enum|interface|@interface)\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex TypeDecl();

    // A method header up to its opening parenthesis: modifiers, optional type parameters, a return type, the name.
    [GeneratedRegex(@"(?m)(?:^|(?<=[{;}]))[ \t]*(?:@[\w.]+(?:\([^()]*\))?\s+)*((?:(?:public|protected|private|static|final|synchronized|abstract|default|native|strictfp)\s+)*)(?:<[^>{};()]+>\s*)?[\w.$]+(?:\s*<[^{};()]*>)?(?:\s*\[\s*\])*\s+(\w+)\s*\(")]
    private static partial Regex MethodHead();

    [GeneratedRegex(@"(Dto|DTO|Request|Response|Payload|Form|Data|Status|Result|Record|Entity|Config|Settings)$")]
    private static partial Regex DataName();

    [GeneratedRegex(@"(Service|Repository|Repo|Manager|Handler|Facade|Model|Helper|Validator|Calculator|Gateway)$")]
    private static partial Regex BehaviourName();

    private static readonly HashSet<string> NotMethods = new(StringComparer.Ordinal)
        { "if", "for", "while", "switch", "catch", "return", "new", "else", "do", "try", "synchronized", "throw", "assert" };

    public static Target? Find(IEnumerable<(string Path, string Content)> javaFiles, string phpRoutine, string? phpClass)
    {
        var candidates = Rank(javaFiles, phpRoutine, phpClass);
        return candidates.Count > 0 && candidates[0].Score >= 7 ? candidates[0] : null;
    }

    public static List<Target> Rank(IEnumerable<(string Path, string Content)> javaFiles, string phpRoutine, string? phpClass)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal) { phpRoutine, Camel(phpRoutine) };
        var wantedNorm = Norm(phpRoutine);
        var phpClassNorm = string.IsNullOrWhiteSpace(phpClass) ? null : Norm(ShortName(phpClass!));
        var results = new List<Target>();
        foreach (var (path, content) in javaFiles)
        {
            var p = path.Replace('\\', '/');
            if (!p.EndsWith(".java", StringComparison.OrdinalIgnoreCase) || p.Contains("/src/test/") || p.StartsWith("src/test/")) continue;
            var code = StripStringsAndComments(content, out var commentsAndStrings);
            var pkg = Package().Match(content) is { Success: true } pm ? pm.Groups[1].Value : "";
            var type = MainType(code, p);
            if (type is null || type.Value.Kind is "interface" or "@interface") continue;
            var cls = pkg.Length > 0 ? pkg + "." + type.Value.Name : type.Value.Name;
            var fileMentions = MentionsRoutine(commentsAndStrings, phpRoutine);
            var classBonus = phpClassNorm is not null && (Norm(type.Value.Name) == phpClassNorm
                || Norm(type.Value.Name).StartsWith(phpClassNorm) || Norm(type.Value.Name).EndsWith(phpClassNorm)) ? 4 : 0;
            // A record header or a constructor reads like a method named after the type: never a target.
            var typeNames = new HashSet<string>(TypeDecl().Matches(code).Select(x => x.Groups[2].Value), StringComparer.Ordinal);
            // Behaviour lives in services, controllers, repositories; a record or DTO named like the routine is data.
            var kindBonus = type.Value.Kind == "record" ? -2
                : DataName().IsMatch(type.Value.Name) ? -1
                : type.Value.Name.EndsWith("Service", StringComparison.Ordinal) ? 2
                : BehaviourName().IsMatch(type.Value.Name) ? 1 : 0;
            foreach (var m in Methods(code))
            {
                if (typeNames.Contains(m.Name)) continue;
                int score = 0;
                var why = new List<string>();
                if (wanted.Contains(m.Name)) { score += 10; why.Add("same name"); }
                else if (Norm(m.Name) == wantedNorm) { score += 8; why.Add("same name ignoring case"); }
                else if (VerbOnNoun(wantedNorm, Norm(m.Name), Norm(type.Value.Name))) { score += 7; why.Add("the routine's verb on a class named for its noun"); }
                // What sits between the previous member and this header: its javadoc and annotations.
                var from = Math.Max(0, m.Offset - 800);
                var codeBefore = code[from..m.Offset];
                var lastBrace = Math.Max(Math.Max(codeBefore.LastIndexOf('}'), codeBefore.LastIndexOf(';')), codeBefore.LastIndexOf('{'));
                var ownPreamble = content[(from + lastBrace + 1)..m.Offset];
                if (MentionsRoutine(ownPreamble, phpRoutine)) { score += 7; why.Add("its annotations name the PHP routine"); }
                else if (score > 0 && fileMentions) { score += 2; why.Add("the class names the PHP routine"); }
                if (score > 0 && classBonus > 0) { score += classBonus; why.Add("class named like the PHP class"); }
                if (score == 0) continue;
                if (kindBonus < 0) why.Add(type.Value.Kind == "record" ? "a record (data), ranked lower" : "a data class, ranked lower");
                else if (kindBonus > 0) why.Add("a service-like class");
                results.Add(new Target(cls, m.Name, m.ParamCount, p, score + kindBonus, string.Join(", ", why)));
            }
        }
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Path, StringComparer.Ordinal).ToList();
    }

    private static (string Kind, string Name)? MainType(string code, string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        (string, string)? first = null;
        foreach (Match m in TypeDecl().Matches(code))
        {
            var t = (m.Groups[1].Value, m.Groups[2].Value);
            if (m.Groups[2].Value == fileName) return t;
            first ??= t;
        }
        return first;
    }

    private static IEnumerable<Method> Methods(string code)
    {
        foreach (Match m in MethodHead().Matches(code))
        {
            var name = m.Groups[2].Value;
            if (NotMethods.Contains(name)) continue;
            var open = m.Index + m.Length - 1;
            var close = MatchParen(code, open);
            if (close < 0) continue;
            var after = code[(close + 1)..].TrimStart();
            if (after.StartsWith("throws")) after = after[(after.IndexOf('{') is var b and >= 0 ? b : after.Length)..];
            if (!after.StartsWith("{")) continue;   // abstract, interface or a call
            var parms = code[(open + 1)..close];
            yield return new Method(name, CountParams(parms), m.Index, m.Groups[1].Value.Contains("static"));
        }
    }

    private static int MatchParen(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static int CountParams(string parms)
    {
        if (string.IsNullOrWhiteSpace(parms)) return 0;
        int depth = 0, count = 1;
        foreach (var c in parms)
        {
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) count++;
        }
        return count;
    }

    /// <summary>Blanks string literals and comments (same length) so the patterns see code; collects them apart.</summary>
    private static string StripStringsAndComments(string src, out string removed)
    {
        var code = src.ToCharArray();
        var other = new System.Text.StringBuilder();
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == '/' && i + 1 < code.Length && (code[i + 1] == '/' || code[i + 1] == '*'))
            {
                var block = code[i + 1] == '*';
                var j = i;
                while (j < code.Length && (block ? !(code[j] == '*' && j + 1 < code.Length && code[j + 1] == '/' && j > i + 1) : code[j] != '\n')) j++;
                if (block) j = Math.Min(j + 2, code.Length);
                other.Append(src, i, j - i).Append('\n');
                for (var k = i; k < j; k++) if (code[k] != '\n') code[k] = ' ';
                i = j - 1;
            }
            else if (code[i] == '"' || code[i] == '\'')
            {
                var q = code[i];
                var j = i + 1;
                while (j < code.Length && code[j] != q && code[j] != '\n') { if (code[j] == '\\') j++; j++; }
                other.Append(src, i, Math.Min(j + 1, code.Length) - i).Append('\n');
                for (var k = i + 1; k < j && k < code.Length; k++) code[k] = ' ';
                i = j;
            }
        }
        removed = other.ToString();
        return new string(code);
    }

    private static bool MentionsRoutine(string text, string routine) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_$]){Regex.Escape(routine)}(?![A-Za-z0-9_])");

    /// <summary>createNote → NoteService.create: the method is the routine's verb and the class carries the rest.</summary>
    public static bool VerbOnNoun(string routineNorm, string methodNorm, string classNorm) =>
        methodNorm.Length >= 3 && routineNorm.Length > methodNorm.Length && routineNorm.StartsWith(methodNorm, StringComparison.Ordinal)
        && routineNorm[methodNorm.Length..] is { Length: >= 3 } noun && classNorm.Contains(noun, StringComparison.Ordinal);

    public static string Camel(string s)
    {
        if (!s.Contains('_')) return s;
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return s;
        return parts[0].ToLowerInvariant() + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private static string ShortName(string fqn) => fqn.Contains('\\') ? fqn[(fqn.LastIndexOf('\\') + 1)..] : fqn;

    public static string Norm(string s) => Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToLowerInvariant();
}
