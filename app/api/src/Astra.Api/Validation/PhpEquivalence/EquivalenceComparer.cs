using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation.PhpEquivalence;

/// <summary>
/// Decides one vector: did the PHP routine and the Java method do the same thing? PASSED when both return equal values
/// (or both reject the input, or both answer the same HTTP status and body); FAILED when they differ, with the first
/// difference; SKIPPED when the vector could not be run fairly on one side (an input with no value in the signature,
/// a collaborator or function the run cannot supply); ERRORED when a side produced nothing. Pure.
///
/// Equality is PHP-aware but not loose: numbers compare by value (5 == 5.0, "5.00" == 5.00 with a note that the
/// types differ), objects by their fields with snake_case/camelCase names folded, an empty PHP array equals an empty
/// list or map, a missing field equals null. Strings compare exactly.
/// </summary>
public static partial class EquivalenceComparer
{
    public const string Passed = "PASSED", Failed = "FAILED", Skipped = "SKIPPED", Errored = "ERRORED";

    public sealed record Verdict(string Status, string Reason, string? Diff, List<string> Notes);

    [GeneratedRegex(@"Argument #\d+ .*must be of type|Too few arguments|Unknown named parameter|must be of type .* given, called in", RegexOptions.IgnoreCase)]
    private static partial Regex SignatureMismatch();

    [GeneratedRegex(@"Call to undefined (?:function|method) ([\w\\:]+)|Class ""?([\w\\]+)""? not found|Interface ""?([\w\\]+)""? not found", RegexOptions.IgnoreCase)]
    private static partial Regex MissingSymbol();

    // The routine reached something the run cannot give it: a collaborator or global that is null here, a constant the
    // application defines at boot (DOL_DOCUMENT_ROOT, ABSPATH, …), a method a stubbed base class does not have.
    [GeneratedRegex(@"Call to a member function (\w+)\(\) on null|Undefined constant ""?([\w\\]+)""?|Call to undefined method ([\w\\]+::\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingEnvironment();

    // The Java side reached a collaborator the harness cannot wire: a Spring bean left null, a DataSource or
    // EntityManager, a repository. Not a difference in behaviour — the vector cannot be judged here.
    [GeneratedRegex(@"DataSource|EntityManager|JdbcTemplate|ApplicationContext|SessionFactory|Repository|\bNo \w+ (set|configured|available|bound)\b|not (been )?initiali[sz]ed", RegexOptions.IgnoreCase)]
    private static partial Regex JavaEnvironment();

    public static Verdict Compare(JsonObject? php, JsonObject? java)
    {
        var notes = new List<string>();
        if (php is null) return new(Errored, "The PHP run returned nothing for this vector.", null, notes);
        if (Bool(php, "timedOut")) return new(Errored, "The PHP run was stopped at its time limit.", null, notes);
        if (Str(php, "loadError") is { } le) return new(Skipped, $"The PHP routine could not be loaded: {le}", null, notes);
        if (Str(php, "missingInput") is { } mi) return new(Skipped, $"This vector gives no value for {mi}, which the PHP routine requires.", null, notes);
        if (php["fatal"] is JsonObject fatal) return new(Errored, $"PHP stopped with a fatal error: {Str(fatal, "message")}", null, notes);
        if (!Bool(php, "ok") && Str(php, "error") is { } pe) return new(Errored, $"The PHP run failed: {pe}", null, notes);
        AddNotes(php, notes, "PHP");

        var phpEx = php["exception"] as JsonObject;
        if (phpEx is not null)
        {
            var msg = Str(phpEx, "message") ?? "";
            var cls = Str(phpEx, "class") ?? "Error";
            if ((cls is "TypeError" or "ArgumentCountError") && SignatureMismatch().IsMatch(msg))
                return new(Skipped, $"The inputs do not fit the PHP signature ({cls}: {msg}).", null, notes);
            if (MissingEnvironment().Match(msg) is { Success: true } me)
            {
                var what = me.Groups[1].Success ? $"`{me.Groups[1].Value}()` on an object it does not have here (a global or an injected collaborator)"
                    : me.Groups[2].Success ? $"the constant `{me.Groups[2].Value}`, which the application defines when it boots"
                    : $"`{me.Groups[3].Value}()`, which a base class outside the supplied files would provide";
                return new(Skipped, $"The PHP routine reaches {what}; the run cannot supply it.", null, notes);
            }
            if (MissingSymbol().Match(msg) is { Success: true } ms)
            {
                var sym = ms.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success)?.Value ?? "a symbol";
                return new(Skipped, $"The PHP routine needs `{sym}`, which is not in the supplied files or is not allowed in the sandbox.", null, notes);
            }
        }

        if (java is null) return new(Errored, "The Java harness printed no result for this vector (see the maven log).", null, notes);
        if (Str(java, "inputError") is { } ie) return new(Skipped, $"The inputs have no Java value: {ie}", null, notes);
        if (java["unsupplied"] is JsonArray un && un.Count > 0)
            return new(Skipped, $"The Java method reached `{un[0]}`, a collaborator an equivalence run cannot supply.", null, notes);
        AddNotes(java, notes, "Java");
        var javaEx = java["exception"] as JsonObject;
        if (javaEx is not null)
        {
            var jc = Str(javaEx, "class") ?? "";
            var jm = Str(javaEx, "message") ?? "";
            if (jc == "NullPointerException" || JavaEnvironment().IsMatch(jm))
                return new(Skipped, $"The Java method reached a collaborator the run does not wire ({jc}: {Shorten(jm, 140)}).", null, notes);
        }

        var phpCode = Int(php, "responseCode") ?? 200;
        if (Bool(php, "exited")) notes.Add("PHP ended the request with exit/die");

        // ── exceptions ──
        if (phpEx is not null && javaEx is not null)
            return new(Passed, $"Both reject the input (PHP {Str(phpEx, "class")}, Java {Str(javaEx, "class")}).", null, notes);
        if (phpEx is not null)
            return new(Failed, $"PHP throws {Str(phpEx, "class")} ({Str(phpEx, "message")}); Java returns {Show(JavaValue(java, out _))}.", null, notes);
        if (javaEx is not null)
        {
            var status = Int(javaEx, "status");
            if (status is not null && status == phpCode && phpCode != 200)
                return new(Passed, $"Both answer HTTP {phpCode} (Java raises {Str(javaEx, "class")}).", null, notes);
            var phpSaid = PhpValue(php, out _);
            return new(Failed, $"Java throws {Str(javaEx, "class")} ({Str(javaEx, "message")}); PHP returns {Show(phpSaid)}"
                + (phpCode != 200 ? $" with HTTP {phpCode}" : "") + ".", null, notes);
        }

        // ── values ──
        var javaVal = JavaValue(java, out var javaStatus);
        var phpVal = PhpValue(php, out var fromOutput);
        if (javaStatus is not null && javaStatus != phpCode)
            return new(Failed, $"PHP answers HTTP {phpCode}; Java answers HTTP {javaStatus}.", $"$status: PHP {phpCode} ≠ Java {javaStatus}", notes);
        if (javaStatus is null && phpCode != 200)
        {
            var loc = (php["headers"] as JsonArray)?.Select(h => h?.ToString()).FirstOrDefault(h => h?.StartsWith("Location:", StringComparison.OrdinalIgnoreCase) == true);
            return new(Skipped, $"PHP answers HTTP {phpCode}{(loc is null ? "" : $" ({loc})")}; the Java method returns a plain value, so the status cannot be compared here (the HTTP parity replay compares it).", null, notes);
        }
        if (Bool(php, "sessionChanged")) notes.Add("PHP wrote to $_SESSION; session writes are not compared");
        if (fromOutput) notes.Add("PHP's echoed output is compared with the Java return value");

        var diff = Diff(phpVal, javaVal, "$", notes, fromOutput);
        if (diff is null)
            return new(Passed, phpVal is null && javaVal is null ? "Neither returns a value." : $"Both give {Show(phpVal)}.", null, notes);
        return new(Failed, $"PHP gives {Show(phpVal)}; Java gives {Show(javaVal)}.", diff, notes);
    }

    /// <summary>The PHP observable: the return value, else the echoed output (parsed when it is JSON), else null.</summary>
    private static JsonNode? PhpValue(JsonObject php, out bool fromOutput)
    {
        fromOutput = false;
        var ret = php["returnValue"];
        if (ret is not null) return ret;
        var output = Str(php, "output");
        if (string.IsNullOrEmpty(output)) return null;
        fromOutput = true;
        var t = output.Trim();
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try { return JsonNode.Parse(t); } catch { /* not JSON: keep the text */ }
        }
        return JsonValue.Create(output);
    }

    private static JsonNode? JavaValue(JsonObject java, out int? status)
    {
        status = null;
        var ret = java["returnValue"];
        if (ret is JsonObject o && o["__http"] is JsonValue)
        {
            status = Int(o, "status");
            var body = o["body"];
            if (body is JsonValue bv && bv.TryGetValue<string>(out var s) && (s.TrimStart().StartsWith('{') || s.TrimStart().StartsWith('[')))
            {
                try { return JsonNode.Parse(s); } catch { }
            }
            return body;
        }
        return ret;
    }

    /// <summary>Null when equal; otherwise the path and the two values at the first difference.</summary>
    public static string? Diff(JsonNode? a, JsonNode? b, string path, List<string>? notes = null, bool textual = false)
    {
        if (a is null && b is null) return null;
        if (a is JsonArray { Count: 0 } && b is JsonObject { Count: 0 } || a is JsonObject { Count: 0 } && b is JsonArray { Count: 0 }) return null;
        if (a is JsonObject ao && IsEmptyObjectAfterMeta(ao) && b is null || b is JsonObject bo0 && IsEmptyObjectAfterMeta(bo0) && a is null) return null;
        if (a is null || b is null)
            return $"{path}: PHP {Show(a)} ≠ Java {Show(b)}";

        if (a is JsonValue av && b is JsonValue bv)
        {
            var an = Number(av, out var aIsString);
            var bn = Number(bv, out var bIsString);
            if (an is not null && bn is not null)
            {
                if (NumbersEqual(an.Value, bn.Value))
                {
                    if (aIsString != bIsString && notes is not null)
                        notes.Add($"{path}: PHP gives a {(aIsString ? "string" : "number")}, Java a {(bIsString ? "string" : "number")}; equal by value");
                    return null;
                }
                return $"{path}: PHP {Show(a)} ≠ Java {Show(b)}";
            }
            if (av.TryGetValue<bool>(out var ab) && bv.TryGetValue<bool>(out var bb))
                return ab == bb ? null : $"{path}: PHP {ab.ToString().ToLowerInvariant()} ≠ Java {bb.ToString().ToLowerInvariant()}";
            var asS = av.ToString();
            var bsS = bv.ToString();
            if (av.TryGetValue<string>(out var sa) && bv.TryGetValue<string>(out var sb))
            {
                if (string.Equals(sa, sb, StringComparison.Ordinal)) return null;
                if (textual && Squash(sa) == Squash(sb)) { notes?.Add($"{path}: equal apart from whitespace"); return null; }
                if (IsEnumName(sb) && string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase)) { notes?.Add($"{path}: equal ignoring case (Java enum name)"); return null; }
                return $"{path}: PHP {Show(a)} ≠ Java {Show(b)}";
            }
            return string.Equals(asS, bsS, StringComparison.Ordinal) ? null : $"{path}: PHP {Show(a)} ≠ Java {Show(b)}";
        }

        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count) return $"{path}: PHP has {aa.Count} item(s), Java {ba.Count}";
            for (var i = 0; i < aa.Count; i++)
                if (Diff(aa[i], ba[i], $"{path}[{i}]", notes, textual) is { } d) return d;
            return null;
        }

        if (a is JsonObject oa && b is JsonObject ob)
        {
            var fa = Fields(oa);
            var fb = Fields(ob);
            foreach (var key in fa.Keys.Union(fb.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                fa.TryGetValue(key, out var x);
                fb.TryGetValue(key, out var y);
                var label = x.Name ?? y.Name ?? key;
                if (Diff(x.Value, y.Value, $"{path}.{label}", notes, textual) is { } d) return d;
            }
            return null;
        }

        // A PHP list against a Java map with the keys 0..n-1, or the other way round.
        if (a is JsonArray la && b is JsonObject mb && mb.Select(kv => kv.Key).SequenceEqual(Enumerable.Range(0, la.Count).Select(i => i.ToString(CultureInfo.InvariantCulture))))
            return Diff(la, new JsonArray(mb.Select(kv => kv.Value?.DeepClone()).ToArray()), path, notes, textual);
        return $"{path}: PHP {Kind(a)} {Show(a)} ≠ Java {Kind(b)} {Show(b)}";
    }

    private static bool IsEmptyObjectAfterMeta(JsonObject o) => o.All(kv => kv.Key.StartsWith("__"));

    private static Dictionary<string, (string? Name, JsonNode? Value)> Fields(JsonObject o)
    {
        var d = new Dictionary<string, (string?, JsonNode?)>(StringComparer.Ordinal);
        foreach (var (k, v) in o)
        {
            if (k.StartsWith("__")) continue;
            d[JavaTargetLocator.Norm(k)] = (k, v);
        }
        return d;
    }

    private static decimal? Number(JsonValue v, out bool isString)
    {
        isString = false;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var db) && double.IsFinite(db)) return (decimal)db;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && Regex.IsMatch(s.Trim(), @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")
            && decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            isString = true;
            return parsed;
        }
        return null;
    }

    private static bool NumbersEqual(decimal a, decimal b)
    {
        if (a == b) return true;
        var scale = Math.Max(1m, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= 1e-9m * scale;
    }

    private static bool IsEnumName(string s) => s.Length > 0 && Regex.IsMatch(s, "^[A-Z][A-Z0-9_]*$");

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string Kind(JsonNode n) => n switch { JsonArray => "list", JsonObject => "object", _ => "value" };

    private static void AddNotes(JsonObject side, List<string> notes, string who)
    {
        if (side["notes"] is JsonArray arr)
            foreach (var n in arr.Take(4))
                if (n?.ToString() is { Length: > 0 } s) notes.Add($"{who}: {s}");
        if (who == "PHP" && side["warnings"] is JsonArray w && w.Count > 0)
            notes.Add($"PHP raised {w.Count} warning(s), first: {(w[0] as JsonObject)?["message"]}");
    }

    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public static string Show(JsonNode? n)
    {
        if (n is null) return "null";
        var s = n.ToJsonString();
        return s.Length <= 120 ? s : s[..117] + "…";
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    private static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static int? Int(JsonObject o, string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<decimal>(out var d)) return (int)d;
        if (v.TryGetValue<double>(out var db)) return (int)db;
        return null;
    }
}
