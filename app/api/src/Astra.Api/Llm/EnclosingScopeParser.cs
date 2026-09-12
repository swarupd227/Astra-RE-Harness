namespace Astra.Api.Llm;

/// <summary>
/// Derives the enclosing class / namespace of a routine from the qualified
/// name the parser sidecar emits — <c>TIdSMTP.Connect</c> (Delphi),
/// <c>oatpp::web::Server::run</c> (C++), <c>OrderService.getId</c> (Java/C#).
/// The extract prompts have carried <c>{{enclosingClass}}</c> and
/// <c>{{namespace}}</c> placeholders since Phase 9 without anything ever
/// filling them in.
/// </summary>
public static class EnclosingScopeParser
{
    public sealed record Scope(string? Namespace, string? Class);

    public static Scope FromName(string? qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName)) return new(null, null);
        var name = qualifiedName.Trim();

        // Strip a trailing parameter list if the parser included one.
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];

        string[] parts;
        if (name.Contains("::", StringComparison.Ordinal))
            parts = name.Split("::", StringSplitOptions.RemoveEmptyEntries);
        else if (name.Contains('.'))
            parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        else
            return new(null, null);

        if (parts.Length < 2) return new(null, null);
        var cls = parts[^2];
        var ns = parts.Length > 2 ? string.Join("::", parts[..^2]) : null;
        return new(ns, cls);
    }
}
