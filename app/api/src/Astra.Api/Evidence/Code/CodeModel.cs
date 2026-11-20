using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astra.Api.Evidence;

/// <summary>A C# file to read, by the path it has in the project.</summary>
internal sealed record CodeSource(string Path, string Text);

/// <summary>What one C# file states, and what could not be read from it.</summary>
internal sealed record CodeFileFacts(FactSet Facts, IReadOnlyList<string> Notes);

/// <summary>
/// What an expression is worth knowing about, without running it: text it spells out (with
/// <c>{name}</c> where a part is only known at run time), a setting it reads, the text used when that
/// setting is missing, a number. <see cref="Suffix"/> is what follows a setting's value
/// (<c>$"{baseUrl}/screen"</c> is the setting <c>…BaseUrl</c> followed by <c>/screen</c>).
/// </summary>
internal sealed record ValueRef(
    string? Text = null,
    string? ConfigKey = null,
    string? Fallback = null,
    string? Suffix = null,
    double? Number = null)
{
    public static readonly ValueRef Unknown = new();

    public bool IsKnown => Text is not null || ConfigKey is not null || Number is not null;
    public bool HasHoles => Text is not null && Text.Contains('{');
}

/// <summary>How long something may take: a number written in the code, or the setting that holds it.</summary>
internal sealed record TimeoutRef(double? Seconds, string? ConfigKey, int Line);

/// <summary>A setting an expression reads, and how (<c>indexer</c>, <c>GetValue</c>, <c>options</c> …).</summary>
internal sealed record ConfigRead(string Key, string Form, string Confidence);

/// <summary>A settings section an options class is filled from (<c>Configure&lt;OfacOptions&gt;(config.GetSection("Ofac"))</c>).</summary>
internal sealed record OptionsBinding(string TypeName, string Section);

/// <summary>The base address and timeout a named or typed HTTP client was registered with.</summary>
internal sealed record ClientSetup(ValueRef? Base, TimeoutRef? Timeout);

/// <summary>
/// What the whole set of C# files says that one file cannot: which settings sections options classes
/// are bound to, which base address each registered HTTP client gets, and which properties a type has.
/// Built in a first pass over every file; the second pass reads each file against it.
/// </summary>
internal sealed class CodeRegistry
{
    public List<OptionsBinding> Bindings { get; } = new();
    public Dictionary<string, ClientSetup> ClientsByType { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ClientSetup> ClientsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>Text constants of the project by <c>Type.Name</c>, so a path kept in one class and used in another can be followed.</summary>
    public Dictionary<string, string> Consts { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> SectionsFor(string typeName) =>
        Bindings.Where(b => b.TypeName == typeName).Select(b => b.Section).Distinct(StringComparer.Ordinal);

    public bool HasBinding(string typeName) => Bindings.Any(b => b.TypeName == typeName);

    /// <summary>True when the type is known to have the property, or is not declared in the project (then anything goes).</summary>
    public bool MayHaveProperty(string typeName, string property) =>
        !Properties.TryGetValue(typeName, out var set) || set.Contains(property);
}

/// <summary>
/// Everything a type says about its own names: the expression each field starts as or is first set to,
/// the declared type of each field, property and parameter. It is how an identifier such as
/// <c>_baseUrl</c> or <c>_options</c> is followed back to the setting it came from.
/// </summary>
internal sealed class TypeScope
{
    public string SimpleName { get; init; } = "";
    public string FullName { get; init; } = "";
    public Dictionary<string, ExpressionSyntax> Values { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Types { get; } = new(StringComparer.Ordinal);
    public int MethodCount { get; set; }

    public static TypeScope ForGlobalStatements() => new() { SimpleName = "Program", FullName = "Program" };

    public static TypeScope Build(TypeDeclarationSyntax type, string fullName)
    {
        var scope = new TypeScope { SimpleName = type.Identifier.Text, FullName = fullName };
        var members = type.DescendantNodes(n => n == type || n is not BaseTypeDeclarationSyntax).ToList();

        // Declared things first, so an assignment is only recorded for a name the type really declares.
        foreach (var m in members)
        {
            switch (m)
            {
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        scope.Types[v.Identifier.Text] = f.Declaration.Type.ToString();
                        if (v.Initializer is { } init) scope.Values[v.Identifier.Text] = init.Value;
                    }
                    break;
                case PropertyDeclarationSyntax p:
                    scope.Types[p.Identifier.Text] = p.Type.ToString();
                    if (p.Initializer is { } pi) scope.Values[p.Identifier.Text] = pi.Value;
                    else if (p.ExpressionBody is { } eb) scope.Values[p.Identifier.Text] = eb.Expression;
                    break;
                case BaseMethodDeclarationSyntax method:
                    scope.MethodCount++;
                    foreach (var prm in method.ParameterList.Parameters)
                        if (prm.Type is not null) scope.Types.TryAdd(prm.Identifier.Text, prm.Type.ToString());
                    break;
                case RecordDeclarationSyntax { ParameterList: { } rp }:
                    foreach (var prm in rp.Parameters)
                        if (prm.Type is not null) scope.Types.TryAdd(prm.Identifier.Text, prm.Type.ToString());
                    break;
            }
        }

        foreach (var m in members)
        {
            if (m is not AssignmentExpressionSyntax a || !a.IsKind(SyntaxKind.SimpleAssignmentExpression)) continue;
            var name = a.Left switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax n } => n.Identifier.Text,
                _ => null,
            };
            if (name is not null && scope.Types.ContainsKey(name)) scope.Values.TryAdd(name, a.Right);
        }
        return scope;
    }
}

internal static class SyntaxFacts2
{
    /// <summary>The last name in a chain: <c>_config</c>, <c>this.Config</c>, <c>builder.Configuration</c> → the part after the last dot.</summary>
    public static string? LastName(ExpressionSyntax? e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        ParenthesizedExpressionSyntax p => LastName(p.Expression),
        PostfixUnaryExpressionSyntax { Operand: var o } when e.IsKind(SyntaxKind.SuppressNullableWarningExpression) => LastName(o),
        InvocationExpressionSyntax i => LastName(i.Expression),
        ConditionalAccessExpressionSyntax c => LastName(c.WhenNotNull),
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        PredefinedTypeSyntax pt => pt.Keyword.Text,
        _ => null,
    };

    /// <summary>The name of the method an invocation calls, with a generic argument list dropped.</summary>
    public static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        _ => null,
    };

    /// <summary>What an invocation is called on (<c>client</c> in <c>client.GetAsync(…)</c>), through a <c>?.</c> as well.</summary>
    public static ExpressionSyntax? ReceiverOf(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Expression,
        MemberBindingExpressionSyntax when inv.Parent is ConditionalAccessExpressionSyntax c => c.Expression,
        _ => null,
    };

    public static IReadOnlyList<TypeSyntax> TypeArguments(InvocationExpressionSyntax inv)
    {
        var name = inv.Expression switch
        {
            GenericNameSyntax g => g,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax g } => g,
            MemberBindingExpressionSyntax { Name: GenericNameSyntax g } => g,
            _ => null,
        };
        return name is null ? Array.Empty<TypeSyntax>() : name.TypeArgumentList.Arguments.ToList();
    }

    public static string SimpleTypeName(string typeText)
    {
        var t = typeText.Trim().TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        var dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
    }

    public static int LineOf(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    public static (int From, int To) LinesOf(SyntaxNode node)
    {
        var span = node.SyntaxTree.GetLineSpan(node.Span);
        return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    /// <summary>The statement a node is part of, so a citation shows the whole call and not half a chained one.</summary>
    public static SyntaxNode Statement(SyntaxNode node)
    {
        var s = node.FirstAncestorOrSelf<StatementSyntax>(x => x is not BlockSyntax);
        if (s is null) return node;
        var (a, b) = LinesOf(s);
        return b - a > 40 ? node : s;
    }

    public static readonly Regex OptionsWrapper = new(
        @"^(?:[\w.]+\.)?I(?:Options|OptionsSnapshot|OptionsMonitor)<\s*(?<t>[\w.]+)\s*>$", RegexOptions.Compiled);
}
