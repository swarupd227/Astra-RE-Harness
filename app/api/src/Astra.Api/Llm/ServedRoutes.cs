using System.Text.Json;
using Astra.Api.Evidence;

namespace Astra.Api.Llm;

/// <summary>
/// A route a routine serves, as extraction tells the model: the method and path come from the route table (a
/// routes file, an attribute, a front controller's switch), which is usually not the routine's own source.
/// </summary>
/// <param name="Role">"handler" — the routine answers the route; "dispatcher" — the routine is the script whose
/// statements choose the handler by a request parameter.</param>
public sealed record ServedRoute(
    string Name,
    string Method,
    string Path,
    string Role,
    string? Detector,
    string? DeclaredIn,
    string? Parameter,
    string? Value,
    IReadOnlyList<string> EvidenceIds);

/// <summary>
/// Which routes a routine serves, matched from the graph's route → triggers links. Pure, and neutral about the
/// language: a link to a <c>class</c> names the method that handles the route; a link to a <c>script</c> stands for
/// the file's own statements and lists the routines each case calls. A routine's types are what its parser keeps in
/// CommonBlockRefs (the enclosing class, by short name).
/// </summary>
public static class ServedRoutes
{
    public const string Handler = "handler";
    public const string Dispatcher = "dispatcher";

    public static IReadOnlyList<ServedRoute> For(
        string routineName, string sourcePath, IReadOnlyList<string> routineTypes, IReadOnlyList<RouteTrigger> triggers)
    {
        var types = routineTypes.Select(ShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var path = NormalisePath(sourcePath);
        var stem = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        var result = new Dictionary<string, ServedRoute>(StringComparer.Ordinal);

        foreach (var t in triggers)
        {
            string? role = null;
            var calls = Strings(t.LinkAttrs, "calls").Concat(Strings(t.RouteAttrs, "calls")).ToList();
            if (t.TargetType == EntityVocabulary.Class)
            {
                if (types.Contains(ShortName(t.TargetName)))
                {
                    var method = Str(t.LinkAttrs, "method");
                    if (method is not null ? Same(method, routineName) : Same(routineName, "__invoke")) role = Handler;
                    else if (calls.Any(c => Calls(c, routineName, types))) role = Handler;
                }
            }
            else if (t.TargetType == EntityVocabulary.Script)
            {
                var inFile = string.Equals(NormalisePath(t.TargetName), path, StringComparison.OrdinalIgnoreCase);
                if (inFile && Same(routineName, stem)) role = Dispatcher;
                else if (calls.Any(c => Calls(c, routineName, types))) role = Handler;
            }
            if (role is null) continue;

            var route = new ServedRoute(
                t.RouteName,
                Str(t.RouteAttrs, "method") ?? t.RouteName.Split(' ', 2)[0],
                Str(t.RouteAttrs, "path") ?? (t.RouteName.Split(' ', 2) is { Length: 2 } p ? p[1] : t.RouteName),
                role,
                Str(t.RouteAttrs, "detector"),
                Str(t.RouteAttrs, "file"),
                Str(t.RouteAttrs, "parameter"),
                Str(t.RouteAttrs, "value"),
                t.EvidenceIds);
            // A handler role wins over dispatcher when both apply (a script that is also called by name).
            if (!result.TryGetValue(route.Name, out var seen) || (seen.Role == Dispatcher && role == Handler))
                result[route.Name] = route;
        }
        return result.Values.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a call as a route records it (<c>get_customer</c>, <c>Billing.charge</c>, <c>Billing::charge</c>) names this routine.</summary>
    private static bool Calls(string call, string routineName, HashSet<string> types)
    {
        var c = call.Trim();
        string owner, member;
        var colons = c.LastIndexOf("::", StringComparison.Ordinal);
        var dot = c.LastIndexOf('.');
        if (colons >= 0) (owner, member) = (c[..colons], c[(colons + 2)..]);
        else if (dot >= 0) (owner, member) = (c[..dot], c[(dot + 1)..]);
        else return Same(c, routineName);
        return Same(member, routineName) && types.Contains(ShortName(owner));
    }

    private static string ShortName(string name)
    {
        var n = name.Trim().TrimStart('\\');
        var i = Math.Max(n.LastIndexOf('\\'), n.LastIndexOf('/'));
        return i >= 0 ? n[(i + 1)..] : n;
    }

    private static string NormalisePath(string p) => p.Replace('\\', '/').TrimStart('/');

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement? attrs, string name) =>
        attrs is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static IEnumerable<string> Strings(JsonElement? attrs, string name)
    {
        if (attrs is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(name, out var v)) yield break;
        if (v.ValueKind == JsonValueKind.String) { yield return v.GetString()!; yield break; }
        if (v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())) yield return e.GetString()!;
    }
}
