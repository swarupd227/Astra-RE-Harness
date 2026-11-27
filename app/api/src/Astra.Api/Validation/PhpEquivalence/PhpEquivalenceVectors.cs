using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Api.Validation.PhpEquivalence;

/// <summary>
/// Input vectors for an EQUIVALENCE run, drawn from the signed spec — never invented from the code alone:
/// <list type="bullet">
/// <item>each claim's <c>generatorHints.examples</c> (as written) and <c>generatorHints.inputs</c> (bounds, midpoints,
/// empty and sample strings, one input varied at a time);</item>
/// <item>each <c>http_contracts</c> entry's request parameters (all required ones; each optional one added; each
/// required one left out; a numeric one given "0" and a non-number), placed where the source reads them —
/// query → $_GET, form/body → $_POST, cookie → $_COOKIE, session → $_SESSION, header → $_SERVER;</item>
/// <item>the spec's typed <c>inputs</c> (in / input-output / reference / superglobal), varied one at a time from a
/// baseline.</item>
/// </list>
/// Plus any vectors an engineer supplies with the request. Deterministic and de-duplicated; capped.
/// </summary>
public static class PhpEquivalenceVectors
{
    public const int MaxVectors = 24;

    public sealed record EqVector(string Id, string Source, string Label, JsonObject Named, JsonObject? Superglobals)
    {
        /// <summary>What the Java method is given: the named inputs plus every request parameter.</summary>
        public JsonObject JavaNamed()
        {
            var o = (JsonObject)Named.DeepClone();
            if (Superglobals is not null)
                foreach (var area in new[] { "get", "post", "cookie" })
                    if (Superglobals[area] is JsonObject vals)
                        foreach (var (k, v) in vals)
                            if (!o.ContainsKey(k)) o[k] = v?.DeepClone();
            return o;
        }
    }

    public static List<EqVector> Build(JsonElement spec, IEnumerable<JsonObject>? engineerVectors = null)
    {
        var all = new List<EqVector>();
        var n = 0;
        string NextId() => $"V{++n}";

        foreach (var ev in engineerVectors ?? Array.Empty<JsonObject>())
        {
            var named = ev["named"] as JsonObject ?? (ev["superglobals"] is null && ev["args"] is null ? ev : new JsonObject());
            var sg = ev["superglobals"] as JsonObject;
            all.Add(new EqVector(NextId(), "engineer", ev["label"]?.GetValue<string>() ?? "supplied with the request",
                (JsonObject)named.DeepClone(), (JsonObject?)sg?.DeepClone()));
        }

        if (spec.ValueKind == JsonValueKind.Object)
        {
            foreach (var section in new[] { "invariants", "edge_cases" })
            {
                if (!spec.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var claim in arr.EnumerateArray())
                {
                    if (!claim.TryGetProperty("generatorHints", out var hints) || hints.ValueKind != JsonValueKind.Object) continue;
                    var cid = claim.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? section : section;
                    if (hints.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array)
                        foreach (var e in ex.EnumerateArray().Take(6))
                            if (JsonNode.Parse(e.GetRawText()) is JsonObject eo)
                                all.Add(new EqVector(NextId(), cid, "example from the claim", Unwrap(eo), null));
                    if (hints.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
                        foreach (var (label, named) in FromHintInputs(inputs).Take(6))
                            all.Add(new EqVector(NextId(), cid, label, named, null));
                }
            }

            if (spec.TryGetProperty("http_contracts", out var hc) && hc.ValueKind == JsonValueKind.Array)
                foreach (var contract in hc.EnumerateArray())
                {
                    var cid = contract.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "HTTP" : "HTTP";
                    if (!contract.TryGetProperty("request", out var req) || req.ValueKind != JsonValueKind.Array) continue;
                    foreach (var (label, named, sg) in FromHttpRequest(req).Take(8))
                        all.Add(new EqVector(NextId(), cid, label, named, sg));
                }

            if (spec.TryGetProperty("inputs", out var io) && io.ValueKind == JsonValueKind.Array)
                foreach (var (label, named, sg) in FromSpecInputs(io).Take(8))
                    all.Add(new EqVector(NextId(), "inputs", label, named, sg));
        }

        // De-duplicate by what is actually sent; keep the first source that produced it.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EqVector>();
        foreach (var v in all)
        {
            var key = v.Named.ToJsonString() + "|" + (v.Superglobals?.ToJsonString() ?? "");
            if (!seen.Add(key)) continue;
            result.Add(v);
            if (result.Count >= MaxVectors) break;
        }
        // Renumber so ids are V1..Vn in the order shown.
        return result.Select((v, i) => v with { Id = $"V{i + 1}" }).ToList();
    }

    /// <summary>An example may be written {inputs: {...}} or {input: {...}} or flat; a flat one is the inputs.</summary>
    private static JsonObject Unwrap(JsonObject e)
    {
        foreach (var k in new[] { "inputs", "input", "args" })
            if (e[k] is JsonObject inner) return (JsonObject)inner.DeepClone();
        var o = new JsonObject();
        foreach (var (k, v) in e)
            if (k is not ("expected" or "output" or "outputs" or "result" or "note" or "label" or "this" or "$this")) o[k] = v?.DeepClone();
        return o;
    }

    // ── generatorHints.inputs ───────────────────────────────────────────────

    private static IEnumerable<(string Label, JsonObject Named)> FromHintInputs(JsonElement inputs)
    {
        var specs = new List<(string Name, List<JsonNode?> Values)>();
        foreach (var inp in inputs.EnumerateArray())
        {
            var name = inp.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var type = inp.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
            double? min = inp.TryGetProperty("min", out var mi) && mi.ValueKind == JsonValueKind.Number ? mi.GetDouble() : null;
            double? max = inp.TryGetProperty("max", out var ma) && ma.ValueKind == JsonValueKind.Number ? ma.GetDouble() : null;
            int? maxLen = inp.TryGetProperty("maxLen", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : null;
            var alphabet = inp.TryGetProperty("alphabet", out var al) && al.ValueKind == JsonValueKind.String ? al.GetString() : null;
            specs.Add((name!, HintValues(type, min, max, maxLen, alphabet)));
        }
        return OneAtATime(specs);
    }

    private static List<JsonNode?> HintValues(string type, double? min, double? max, int? maxLen, string? alphabet)
    {
        var kind = Kind(type);
        var vals = new List<JsonNode?>();
        switch (kind)
        {
            case "int":
            {
                long lo = (long)Math.Ceiling(min ?? 0), hi = (long)Math.Floor(max ?? Math.Max(lo + 100, 100));
                vals.Add(JsonValue.Create(lo + (hi - lo) / 2));
                vals.Add(JsonValue.Create(lo));
                vals.Add(JsonValue.Create(hi));
                if (lo < 0 && hi > 0) vals.Add(JsonValue.Create(0L));
                break;
            }
            case "float":
            {
                double lo = min ?? 0, hi = max ?? Math.Max(lo + 100, 100);
                vals.Add(JsonValue.Create(Math.Round(lo + (hi - lo) / 2, 4)));
                vals.Add(JsonValue.Create(lo));
                vals.Add(JsonValue.Create(hi));
                break;
            }
            case "bool":
                vals.Add(JsonValue.Create(true));
                vals.Add(JsonValue.Create(false));
                break;
            case "array":
                vals.Add(new JsonArray(JsonValue.Create(1), JsonValue.Create(2)));
                vals.Add(new JsonArray());
                break;
            default:
            {
                var abc = string.IsNullOrEmpty(alphabet) ? "abc" : alphabet;
                var len = Math.Max(1, Math.Min(maxLen ?? 3, 3));
                vals.Add(JsonValue.Create(new string(Enumerable.Range(0, len).Select(i => abc[i % abc.Length]).ToArray())));
                vals.Add(JsonValue.Create(""));
                if (maxLen is > 3) vals.Add(JsonValue.Create(new string(Enumerable.Range(0, maxLen.Value).Select(i => abc[i % abc.Length]).ToArray())));
                break;
            }
        }
        return vals;
    }

    /// <summary>Baseline = each input's first value; then each input takes each of its other values in turn.</summary>
    private static IEnumerable<(string Label, JsonObject Named)> OneAtATime(List<(string Name, List<JsonNode?> Values)> specs)
    {
        if (specs.Count == 0) yield break;
        JsonObject Baseline()
        {
            var o = new JsonObject();
            foreach (var (name, values) in specs) o[name] = values.Count > 0 ? values[0]?.DeepClone() : null;
            return o;
        }
        yield return ("typical values", Baseline());
        foreach (var (name, values) in specs)
            for (var i = 1; i < values.Count; i++)
            {
                var o = Baseline();
                o[name] = values[i]?.DeepClone();
                yield return ($"{name} = {Show(values[i])}", o);
            }
    }

    // ── http_contracts.request ──────────────────────────────────────────────

    private static IEnumerable<(string Label, JsonObject Named, JsonObject Sg)> FromHttpRequest(JsonElement request)
    {
        var ps = new List<(string Name, string Area, string Kind, bool Required)>();
        foreach (var p in request.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var source = p.TryGetProperty("source", out var s) ? (s.GetString() ?? "query").ToLowerInvariant() : "query";
            var type = p.TryGetProperty("type", out var t) ? t.GetString() ?? "string" : "string";
            var required = p.TryGetProperty("required", out var r) && (r.ValueKind == JsonValueKind.True
                || r.ValueKind == JsonValueKind.String && (r.GetString() ?? "").StartsWith("y", StringComparison.OrdinalIgnoreCase));
            if (source == "file") continue;
            ps.Add((name!, source, Kind(type), required));
        }
        if (ps.Count == 0) yield break;

        (JsonObject, JsonObject) Make(Func<(string Name, string Area, string Kind, bool Required), JsonNode?> pick)
        {
            var named = new JsonObject();
            var sg = new JsonObject();
            foreach (var p in ps)
            {
                var v = pick(p);
                if (v is null) continue;
                Place(p.Name, p.Area, v, named, sg);
            }
            return (named, sg);
        }

        JsonNode Typical((string Name, string Area, string Kind, bool Required) p) => p.Kind switch
        {
            "int" => JsonValue.Create("1")!,
            "float" => JsonValue.Create("1.5")!,
            "bool" => JsonValue.Create("1")!,
            "array" => new JsonArray(JsonValue.Create("1")),
            _ => JsonValue.Create("abc")!,
        };

        var (n0, s0) = Make(p => p.Required ? Typical(p) : null);
        yield return ("the required parameters", n0, s0);
        if (ps.Any(p => !p.Required))
        {
            var (n1, s1) = Make(Typical);
            yield return ("every parameter", n1, s1);
        }
        foreach (var miss in ps.Where(p => p.Required).Take(3))
        {
            var (n2, s2) = Make(p => p.Required && p.Name != miss.Name ? Typical(p) : null);
            yield return ($"without {miss.Name}", n2, s2);
        }
        foreach (var num in ps.Where(p => p.Kind is "int" or "float").Take(2))
        {
            var (n3, s3) = Make(p => p.Name == num.Name ? JsonValue.Create("0") : p.Required ? Typical(p) : null);
            yield return ($"{num.Name} = \"0\"", n3, s3);
            var (n4, s4) = Make(p => p.Name == num.Name ? JsonValue.Create("abc") : p.Required ? Typical(p) : null);
            yield return ($"{num.Name} = \"abc\"", n4, s4);
        }
    }

    private static void Place(string name, string area, JsonNode value, JsonObject named, JsonObject sg)
    {
        string? bucket = area switch
        {
            "query" or "get" => "get",
            "form" or "body" or "post" or "json" => "post",
            "cookie" => "cookie",
            "session" => "session",
            "header" => "server",
            _ => null,   // path parameters are the routine's own arguments
        };
        if (bucket is null) { named[name] = value; return; }
        if (sg[bucket] is not JsonObject b) sg[bucket] = b = new JsonObject();
        var key = bucket == "server" ? "HTTP_" + Regex.Replace(name, "[^A-Za-z0-9]", "_").ToUpperInvariant() : name;
        b[key] = value;
        if (bucket is "session") named[name] = value.DeepClone();
    }

    // ── spec inputs ─────────────────────────────────────────────────────────

    private static IEnumerable<(string Label, JsonObject Named, JsonObject? Sg)> FromSpecInputs(JsonElement io)
    {
        var specs = new List<(string Name, string? Area, List<JsonNode?> Values)>();
        foreach (var inp in io.EnumerateArray())
        {
            var name = inp.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var dir = inp.TryGetProperty("direction", out var d) ? (d.GetString() ?? "in").ToLowerInvariant() : "in";
            if (dir is "return" or "output" or "out") continue;
            var type = inp.TryGetProperty("type", out var t) ? t.GetString() ?? "mixed" : "mixed";
            string? area = null;
            var m = Regex.Match(name!, @"^\$?_?(GET|POST|REQUEST|COOKIE|SESSION)\s*[\[.]\s*['""]?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                area = m.Groups[1].Value.ToUpperInvariant() switch { "GET" => "query", "POST" or "REQUEST" => "form", "COOKIE" => "cookie", _ => "session" };
                name = m.Groups[2].Value;
            }
            else if (dir == "superglobal") area = "form";
            name = name!.TrimStart('$', '&');
            // $this (and its properties) is the object the method runs on, not an argument: never a vector input.
            if (name.Equals("this", StringComparison.OrdinalIgnoreCase) || name.StartsWith("this->", StringComparison.OrdinalIgnoreCase)) continue;
            var kind = Kind(type);
            var values = area is null ? TypedValues(kind) : TypedValues(kind).Select(v => v is JsonValue jv && kind is "int" or "float" or "bool" ? JsonValue.Create(Show(jv).Trim('"')) : v).ToList();
            specs.Add((name, area, values));
        }
        if (specs.Count == 0) yield break;
        (JsonObject, JsonObject?) Make(int varied, int index)
        {
            var named = new JsonObject();
            var sg = new JsonObject();
            for (var i = 0; i < specs.Count; i++)
            {
                var (name, area, values) = specs[i];
                var v = (i == varied ? values[index] : values[0])?.DeepClone();
                if (v is null) continue;
                if (area is null) named[name] = v;
                else Place(name, area, v, named, sg);
            }
            return (named, sg.Count == 0 ? null : sg);
        }
        var (bn, bs) = Make(-1, 0);
        yield return ("typical inputs", bn, bs);
        for (var i = 0; i < specs.Count; i++)
            for (var j = 1; j < specs[i].Values.Count; j++)
            {
                var (vn, vs) = Make(i, j);
                yield return ($"{specs[i].Name} = {Show(specs[i].Values[j])}", vn, vs);
            }
    }

    private static List<JsonNode?> TypedValues(string kind) => kind switch
    {
        "int" => new() { JsonValue.Create(1), JsonValue.Create(0), JsonValue.Create(-1), JsonValue.Create(100) },
        "float" => new() { JsonValue.Create(1.5), JsonValue.Create(0.0), JsonValue.Create(-2.25), JsonValue.Create(100.1) },
        "bool" => new() { JsonValue.Create(true), JsonValue.Create(false) },
        "array" => new() { new JsonArray(JsonValue.Create(1), JsonValue.Create(2)), new JsonArray() },
        "map" => new() { new JsonObject { ["key"] = "value" }, new JsonObject() },
        "string" => new() { JsonValue.Create("abc"), JsonValue.Create(""), JsonValue.Create("0") },
        _ => new() { JsonValue.Create("1"), JsonValue.Create("") },
    };

    /// <summary>A spec type in PHP or plain words, reduced to the kinds the generators know.</summary>
    public static string Kind(string type)
    {
        var t = (type ?? "").Trim().TrimStart('?').ToLowerInvariant();
        if (t.Length == 0) return "mixed";
        if (Regex.IsMatch(t, @"^(int|integer|long|short|smallint|bigint|tinyint)\b")) return "int";
        if (Regex.IsMatch(t, @"^(float|double|decimal|number|numeric|real|money|currency)\b")) return "float";
        if (Regex.IsMatch(t, @"^(bool|boolean)\b")) return "bool";
        if (Regex.IsMatch(t, @"^(string|str|text|varchar|char|email|url|date|datetime)\b")) return "string";
        if (Regex.IsMatch(t, @"^(list|array<int|int\[\]|string\[\]|\w+\[\])") || t == "array" || t.StartsWith("list")) return "array";
        if (Regex.IsMatch(t, @"^(map|dict|assoc|array<string|object|stdclass|record)")) return "map";
        return "mixed";
    }

    private static string Show(JsonNode? v) => v is null ? "null" : v.ToJsonString();
}
