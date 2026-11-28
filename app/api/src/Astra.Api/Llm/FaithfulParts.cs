using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astra.Api.Llm;

/// <summary>
/// Makes the files of a unit converted in parts compile together. Each part is a model call that writes one
/// partial-class file; two kinds of overlap creep in and are removed here, deterministically, so a slip by the model
/// never becomes a compile error:
///   - a member written by more than one part (same kind, name and parameter count): the first part's copy is kept;
///   - attributes of the class declaration ([SourceUnit]) repeated on each partial: only the first declaration keeps them.
/// The same applies across stub files that declare the same partial type. Files that do not parse are left as they are.
/// </summary>
public static class FaithfulParts
{
    public static List<FaithfulConversion.PackageFile> Dedupe(IReadOnlyList<FaithfulConversion.PackageFile> files)
    {
        var seenMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // type → member keys
        var typesWithAttributes = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FaithfulConversion.PackageFile>(files.Count);
        foreach (var f in files)
        {
            if (!f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !f.Path.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(f);
                continue;
            }
            var tree = CSharpSyntaxTree.ParseText(f.Content);
            var root = tree.GetCompilationUnitRoot();
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                result.Add(f);
                continue;
            }

            var changed = false;
            var newRoot = root.ReplaceNodes(
                root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Modifiers.Any(SyntaxKind.PartialKeyword)),
                (original, rewritten) =>
                {
                    var key = TypeKey(original);
                    var seen = seenMembers.TryGetValue(key, out var s) ? s : seenMembers[key] = new HashSet<string>(StringComparer.Ordinal);
                    var keep = new List<MemberDeclarationSyntax>();
                    foreach (var m in rewritten.Members)
                    {
                        if (m is BaseTypeDeclarationSyntax) { keep.Add(m); continue; }
                        if (seen.Add(MemberKey(m))) keep.Add(m);
                        else changed = true;
                    }
                    var t = rewritten.WithMembers(SyntaxFactory.List(keep));
                    if (t.AttributeLists.Count > 0)
                    {
                        if (!typesWithAttributes.Add(key))
                        {
                            t = t.WithAttributeLists(default).WithLeadingTrivia(rewritten.GetLeadingTrivia());
                            changed = true;
                        }
                    }
                    return t;
                });
            result.Add(changed ? f with { Content = newRoot.ToFullString() } : f);
        }
        return result;
    }

    private static string TypeKey(TypeDeclarationSyntax t)
    {
        var ns = t.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
        var outer = string.Join(".", t.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(x => x.Identifier.Text));
        return $"{ns}|{outer}|{t.Identifier.Text}";
    }

    internal static string MemberKey(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => $"method:{x.Identifier.Text}/{x.ParameterList.Parameters.Count}",
        ConstructorDeclarationSyntax x => $"ctor:{(x.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static" : "")}/{x.ParameterList.Parameters.Count}",
        DestructorDeclarationSyntax => "dtor",
        PropertyDeclarationSyntax x => $"prop:{x.Identifier.Text}",
        IndexerDeclarationSyntax x => $"indexer/{x.ParameterList.Parameters.Count}",
        FieldDeclarationSyntax x => "field:" + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        EventFieldDeclarationSyntax x => "event:" + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        EventDeclarationSyntax x => $"event:{x.Identifier.Text}",
        DelegateDeclarationSyntax x => $"delegate:{x.Identifier.Text}",
        OperatorDeclarationSyntax x => $"op:{x.OperatorToken.Text}/{x.ParameterList.Parameters.Count}",
        _ => "other:" + m.ToString().GetHashCode(StringComparison.Ordinal),
    };
}
