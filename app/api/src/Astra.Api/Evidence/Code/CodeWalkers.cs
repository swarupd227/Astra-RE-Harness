using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astra.Api.Evidence;

/// <summary>The type (or the file's top-level statements) a walker is currently inside, with what it knows about its names.</summary>
internal sealed class TypeFrame
{
    public required TypeScope Scope { get; init; }
    public required ValueResolver Resolver { get; init; }
    public TypeDeclarationSyntax? Decl { get; init; }
    public string Kind { get; init; } = "class";
    public string Namespace { get; init; } = "";
}

/// <summary>
/// Keeps track of the namespace and the type being walked and builds, for each type, the scope its names
/// are looked up in. Top-level statements (a minimal-hosting Program.cs) are one implicit type, "Program".
/// </summary>
internal abstract class TypeAwareWalker : CSharpSyntaxWalker
{
    private readonly Stack<string> _namespaces = new();
    private readonly Stack<TypeFrame> _types = new();
    private TypeFrame? _global;

    protected CodeRegistry Registry { get; }

    protected TypeAwareWalker(CodeRegistry registry) { Registry = registry; }

    protected TypeFrame? Frame => _types.Count > 0 ? _types.Peek() : null;

    protected virtual void OnEnterType(TypeFrame frame) { }
    protected virtual void OnExitType(TypeFrame frame) { }

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        _namespaces.Push(node.Name.ToString());
        base.VisitNamespaceDeclaration(node);
        _namespaces.Pop();
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        _namespaces.Push(node.Name.ToString());
        base.VisitFileScopedNamespaceDeclaration(node);
        _namespaces.Pop();
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node) => Enter(node, () => base.VisitClassDeclaration(node));
    public override void VisitStructDeclaration(StructDeclarationSyntax node) => Enter(node, () => base.VisitStructDeclaration(node));
    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => Enter(node, () => base.VisitInterfaceDeclaration(node));
    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => Enter(node, () => base.VisitRecordDeclaration(node));

    public override void VisitGlobalStatement(GlobalStatementSyntax node)
    {
        if (_global is null)
        {
            var scope = TypeScope.ForGlobalStatements();
            _global = new TypeFrame { Scope = scope, Resolver = new ValueResolver(Registry, scope) };
            OnEnterType(_global);
        }
        _types.Push(_global);
        base.VisitGlobalStatement(node);
        _types.Pop();
    }

    /// <summary>Call once the whole file has been walked, so the implicit Program type is closed.</summary>
    public void Finish()
    {
        if (_global is not null) OnExitType(_global);
        _global = null;
    }

    private void Enter(TypeDeclarationSyntax node, Action visit)
    {
        var outer = _types.Count > 0 && _types.Peek() != _global ? _types.Peek().Scope.FullName : null;
        var ns = string.Join('.', _namespaces.Reverse());
        var simple = node.Identifier.Text;
        var full = outer is not null ? $"{outer}.{simple}" : ns.Length > 0 ? $"{ns}.{simple}" : simple;

        var scope = TypeScope.Build(node, full);
        var frame = new TypeFrame
        {
            Scope = scope,
            Resolver = new ValueResolver(Registry, scope),
            Decl = node,
            Kind = node.Keyword.ValueText,
            Namespace = ns,
        };
        _types.Push(frame);
        OnEnterType(frame);
        visit();
        OnExitType(frame);
        _types.Pop();
    }
}

/// <summary>
/// First pass over every file: the facts that only make sense across files. Which settings section an
/// options class is filled from (<c>Configure&lt;T&gt;(config.GetSection("X"))</c> in one file, read in
/// another), which base address and timeout each HTTP client was registered with (<c>AddHttpClient&lt;T&gt;</c> in
/// Program.cs, used in T), which properties a type has and which text constants a project defines.
/// </summary>
internal sealed class RegistryWalker : TypeAwareWalker
{
    public RegistryWalker(CodeRegistry registry) : base(registry) { }

    protected override void OnEnterType(TypeFrame frame)
    {
        if (frame.Decl is null) return;
        var name = frame.Scope.SimpleName;
        if (!Registry.Properties.TryGetValue(name, out var props))
            Registry.Properties[name] = props = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in frame.Decl.Members.OfType<PropertyDeclarationSyntax>()) props.Add(p.Identifier.Text);
        if (frame.Decl is RecordDeclarationSyntax { ParameterList: { } positional })
            foreach (var p in positional.Parameters) props.Add(p.Identifier.Text);

        foreach (var field in frame.Decl.Members.OfType<FieldDeclarationSyntax>())
        {
            var isConst = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword))
                || (field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)));
            if (!isConst) continue;
            foreach (var v in field.Declaration.Variables)
            {
                if (v.Initializer is null) continue;
                var value = frame.Resolver.Resolve(v.Initializer.Value);
                if (value.Text is not null && value.ConfigKey is null && !value.HasHoles)
                    Registry.Consts[$"{name}.{v.Identifier.Text}"] = value.Text;
            }
        }
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (Frame is { } frame) Inspect(node, frame);
        base.VisitInvocationExpression(node);
    }

    private void Inspect(InvocationExpressionSyntax inv, TypeFrame frame)
    {
        var name = SyntaxFacts2.InvokedName(inv);
        if (name is null) return;
        var args = inv.ArgumentList.Arguments;
        var typeArgs = SyntaxFacts2.TypeArguments(inv);
        var receiver = SyntaxFacts2.ReceiverOf(inv);

        switch (name)
        {
            // services.Configure<OfacOptions>(config.GetSection("Ofac"))
            case "Configure" when typeArgs.Count == 1:
                foreach (var a in args)
                    if (frame.Resolver.SectionName(a.Expression) is { } s)
                        Bind(typeArgs[0], s);
                break;

            // services.AddOptions<OfacOptions>().Bind(config.GetSection("Ofac")) and .BindConfiguration("Ofac")
            case "Bind" or "BindConfiguration" when args.Count >= 1 && receiver is not null && FindAddOptions(receiver) is { } optionsType:
            {
                var section = name == "BindConfiguration"
                    ? frame.Resolver.Resolve(args[0].Expression).Text
                    : frame.Resolver.SectionName(args[0].Expression);
                if (!string.IsNullOrWhiteSpace(section)) Bind(optionsType, section!);
                break;
            }

            // config.GetSection("Ofac").Get<OfacOptions>()
            case "Get" when typeArgs.Count == 1 && receiver is not null:
                if (frame.Resolver.SectionName(receiver) is { } got) Bind(typeArgs[0], got);
                break;

            // config.GetSection("Ofac").Bind(options) where options is an OfacOptions
            case "Bind" when args.Count == 1 && receiver is not null:
                if (frame.Resolver.SectionName(receiver) is { } bound && frame.Resolver.PlainOptionsType(args[0].Expression) is { } t)
                    Bind(t, bound);
                break;

            // services.AddHttpClient<T>(c => c.BaseAddress = …)   services.AddHttpClient("name", c => …)   .ConfigureHttpClient(c => …)
            case "AddHttpClient":
                RegisterClient(inv, frame, typeArgs, args.FirstOrDefault()?.Expression);
                break;
            case "ConfigureHttpClient" when receiver is not null:
                if (FindAddHttpClient(receiver) is { } add)
                    RegisterClient(inv, frame, SyntaxFacts2.TypeArguments(add), add.ArgumentList.Arguments.FirstOrDefault()?.Expression);
                break;
        }
    }

    private void Bind(TypeSyntax type, string section) => Bind(SyntaxFacts2.SimpleTypeName(type.ToString()), section);

    private void Bind(string typeName, string section)
    {
        if (!Registry.Bindings.Any(b => b.TypeName == typeName && b.Section == section))
            Registry.Bindings.Add(new OptionsBinding(typeName, section));
    }

    /// <summary>The options type of an <c>AddOptions&lt;T&gt;()</c> call somewhere up a chain of calls.</summary>
    private static TypeSyntax? FindAddOptions(ExpressionSyntax chain)
    {
        for (var cur = chain; cur is not null;)
        {
            if (cur is InvocationExpressionSyntax inv)
            {
                if (SyntaxFacts2.InvokedName(inv) == "AddOptions" && SyntaxFacts2.TypeArguments(inv) is { Count: 1 } ta) return ta[0];
                cur = SyntaxFacts2.ReceiverOf(inv);
            }
            else if (cur is MemberAccessExpressionSyntax m) cur = m.Expression;
            else break;
        }
        return null;
    }

    private static InvocationExpressionSyntax? FindAddHttpClient(ExpressionSyntax chain)
    {
        for (var cur = chain; cur is not null;)
        {
            if (cur is InvocationExpressionSyntax inv)
            {
                if (SyntaxFacts2.InvokedName(inv) == "AddHttpClient") return inv;
                cur = SyntaxFacts2.ReceiverOf(inv);
            }
            else if (cur is MemberAccessExpressionSyntax m) cur = m.Expression;
            else break;
        }
        return null;
    }

    private void RegisterClient(InvocationExpressionSyntax inv, TypeFrame frame, IReadOnlyList<TypeSyntax> typeArgs, ExpressionSyntax? firstArg)
    {
        var keysByType = typeArgs.Select(t => SyntaxFacts2.SimpleTypeName(t.ToString())).ToList();
        string? clientName = null;
        if (typeArgs.Count == 0 && firstArg is not null)
        {
            var named = frame.Resolver.Resolve(firstArg);
            if (named.Text is not null && !named.HasHoles) clientName = named.Text;
        }

        ValueRef? baseRef = null;
        TimeoutRef? timeout = null;
        foreach (var arg in inv.ArgumentList.Arguments)
        {
            var parameters = arg.Expression switch
            {
                SimpleLambdaExpressionSyntax s => new[] { s.Parameter.Identifier.Text },
                ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.Select(x => x.Identifier.Text).ToArray(),
                _ => null,
            };
            if (parameters is null) continue;

            var body = (SyntaxNode?)(arg.Expression as LambdaExpressionSyntax)?.Body;
            if (body is null) continue;
            foreach (var assign in body.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
            {
                if (assign.Left is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax owner } left || !parameters.Contains(owner.Identifier.Text)) continue;
                if (left.Name.Identifier.Text == "BaseAddress") baseRef = frame.Resolver.Resolve(assign.Right);
                else if (left.Name.Identifier.Text == "Timeout") timeout = frame.Resolver.Timeout(assign.Right, SyntaxFacts2.LineOf(assign));
            }
        }
        if (baseRef is null && timeout is null) return;

        var setup = new ClientSetup(baseRef?.IsKnown == true ? baseRef : null, timeout);
        foreach (var key in keysByType) Merge(Registry.ClientsByType, key, setup);
        if (clientName is not null) Merge(Registry.ClientsByName, clientName, setup);
    }

    private static void Merge(Dictionary<string, ClientSetup> into, string key, ClientSetup setup)
    {
        into[key] = into.TryGetValue(key, out var old)
            ? new ClientSetup(setup.Base ?? old.Base, setup.Timeout ?? old.Timeout)
            : setup;
    }
}
