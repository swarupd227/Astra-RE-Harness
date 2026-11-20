using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astra.Api.Evidence;

/// <summary>
/// Works out, from the text of the code alone, what an expression is made of: a literal, a setting
/// read from configuration, a number, or text with some parts only known at run time. It follows a name
/// back to what it was set to (a local, a field, a constant of the same class) a few steps, and knows
/// the usual ways .NET code reads a setting. It never runs anything; what it cannot tell is
/// <see cref="ValueRef.Unknown"/>, and a caller treats that as "not found", never as a guess.
/// </summary>
internal sealed class ValueResolver
{
    private const int MaxDepth = 8;
    private const string High = "high";

    private static readonly Regex ConfigName = new("config|setting|cfg|appsettings", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FormatHole = new(@"\{(?<i>\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static readonly HashSet<string> PassThrough = new(StringComparer.Ordinal)
    {
        "TrimEnd", "TrimStart", "Trim", "ToString", "ToLower", "ToLowerInvariant", "ToUpper", "ToUpperInvariant",
    };

    private static readonly HashSet<string> NumberParsers = new(StringComparer.Ordinal)
    {
        "Parse", "ToInt32", "ToInt64", "ToDouble", "ToDecimal", "ToUInt32", "ToSingle",
    };

    private readonly CodeRegistry _registry;
    private readonly TypeScope _type;
    private readonly Dictionary<SyntaxNode, Dictionary<string, List<VariableDeclaratorSyntax>>> _locals = new();

    /// <summary>True when the last <see cref="TryConfigRead"/> found a setting read whose key is not spelled out in the code (built at run time).</summary>
    public bool ComputedKey { get; private set; }

    public ValueResolver(CodeRegistry registry, TypeScope type)
    {
        _registry = registry;
        _type = type;
    }

    // ── expressions ─────────────────────────────────────────────────────

    public ValueRef Resolve(ExpressionSyntax? e, int depth = 0)
    {
        if (e is null || depth > MaxDepth) return ValueRef.Unknown;

        switch (e)
        {
            case LiteralExpressionSyntax lit:
                if (lit.IsKind(SyntaxKind.StringLiteralExpression)) return new ValueRef(Text: lit.Token.ValueText);
                if (lit.IsKind(SyntaxKind.NumericLiteralExpression) && lit.Token.Value is { } n)
                    return new ValueRef(Number: Convert.ToDouble(n, System.Globalization.CultureInfo.InvariantCulture));
                return ValueRef.Unknown;

            case ParenthesizedExpressionSyntax p:
                return Resolve(p.Expression, depth + 1);

            case CastExpressionSyntax c:
                return Resolve(c.Expression, depth + 1);

            case PostfixUnaryExpressionSyntax pu when pu.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return Resolve(pu.Operand, depth + 1);

            case InterpolatedStringExpressionSyntax i:
                return ResolveInterpolation(i, depth);

            case BinaryExpressionSyntax b:
                return ResolveBinary(b, depth);

            case ConditionalExpressionSyntax c:
            {
                var yes = Resolve(c.WhenTrue, depth + 1);
                var no = Resolve(c.WhenFalse, depth + 1);
                if (yes.ConfigKey is not null) return no.Text is not null ? yes with { Fallback = no.Text } : yes;
                if (no.ConfigKey is not null) return yes.Text is not null ? no with { Fallback = yes.Text } : no;
                return yes.IsKnown ? yes : no;
            }

            case ObjectCreationExpressionSyntax oc:
                return ResolveCreation(oc.Type.ToString(), oc.ArgumentList, depth);

            case ImplicitObjectCreationExpressionSyntax io:
                return ResolveCreation("", io.ArgumentList, depth);

            case InvocationExpressionSyntax inv:
                return ResolveInvocation(inv, depth);

            case ElementAccessExpressionSyntax el:
                return TryConfigRead(el) is { } read ? new ValueRef(ConfigKey: read.Key) : ValueRef.Unknown;

            case ConditionalAccessExpressionSyntax ca:
                // cfg?["Key"] and cfg?.GetValue<T>("Key"): the part after the ?. is read against the receiver.
                return ResolveConditionalAccess(ca, depth);

            case IdentifierNameSyntax id:
                return ResolveName(id.Identifier.Text, id, depth);

            case MemberAccessExpressionSyntax ma:
                return ResolveMember(ma, depth);

            default:
                return ValueRef.Unknown;
        }
    }

    private ValueRef ResolveInterpolation(InterpolatedStringExpressionSyntax i, int depth)
    {
        var acc = new ValueRef(Text: "");
        foreach (var part in i.Contents)
        {
            switch (part)
            {
                case InterpolatedStringTextSyntax t:
                    acc = Combine(acc, new ValueRef(Text: t.TextToken.ValueText));
                    break;
                case InterpolationSyntax x:
                    acc = Combine(acc, OrHole(Resolve(x.Expression, depth + 1), x.Expression));
                    break;
            }
        }
        return acc;
    }

    private ValueRef ResolveBinary(BinaryExpressionSyntax b, int depth)
    {
        if (b.IsKind(SyntaxKind.CoalesceExpression))
        {
            var left = Resolve(b.Left, depth + 1);
            var right = Resolve(b.Right, depth + 1);
            if (!left.IsKnown) return right;
            return left.ConfigKey is not null && right.Text is not null ? left with { Fallback = right.Text } : left;
        }

        // A long chain of + would recurse once per operand: walk it from the left instead.
        if (b.IsKind(SyntaxKind.AddExpression))
        {
            var operands = new Stack<ExpressionSyntax>();
            ExpressionSyntax cur = b;
            while (cur is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression) && operands.Count < 200)
            {
                operands.Push(bin.Right);
                cur = bin.Left;
            }
            var acc = OrHole(Resolve(cur, depth + 1), cur);
            while (operands.Count > 0)
            {
                var next = operands.Pop();
                var v = OrHole(Resolve(next, depth + 1), next);
                acc = acc.Number is not null && v.Number is not null && acc.Text is null && v.Text is null
                    ? new ValueRef(Number: acc.Number + v.Number)
                    : Combine(acc, v);
            }
            return acc;
        }

        if (b.IsKind(SyntaxKind.MultiplyExpression) || b.IsKind(SyntaxKind.DivideExpression) || b.IsKind(SyntaxKind.SubtractExpression))
        {
            var l = Resolve(b.Left, depth + 1);
            var r = Resolve(b.Right, depth + 1);
            if (l.Number is { } x && r.Number is { } y)
            {
                if (b.IsKind(SyntaxKind.MultiplyExpression)) return new ValueRef(Number: x * y);
                if (b.IsKind(SyntaxKind.SubtractExpression)) return new ValueRef(Number: x - y);
                if (y != 0) return new ValueRef(Number: x / y);
            }
        }
        return ValueRef.Unknown;
    }

    private ValueRef ResolveCreation(string typeName, ArgumentListSyntax? args, int depth)
    {
        var list = args?.Arguments ?? default;
        var simple = SyntaxFacts2.SimpleTypeName(typeName);

        if (simple == "Uri" && list.Count >= 1)
        {
            var first = Resolve(list[0].Expression, depth + 1);
            if (list.Count >= 2 && !list[1].Expression.ToString().StartsWith("UriKind", StringComparison.Ordinal))
            {
                var second = Resolve(list[1].Expression, depth + 1);
                if (second.Text is not null) return Join(first, second.Text);
            }
            return first;
        }

        if (simple == "TimeSpan")
        {
            var numbers = list.Select(a => Resolve(a.Expression, depth + 1).Number).ToList();
            if (numbers.Count >= 3 && numbers.All(n => n is not null))
            {
                var parts = numbers.Select(n => n!.Value).ToList();
                var seconds = parts.Count == 3
                    ? parts[0] * 3600 + parts[1] * 60 + parts[2]
                    : parts[0] * 86400 + parts[1] * 3600 + parts[2] * 60 + parts[3];
                return new ValueRef(Number: seconds);
            }
        }
        return ValueRef.Unknown;
    }

    private ValueRef ResolveInvocation(InvocationExpressionSyntax inv, int depth)
    {
        if (TryConfigRead(inv) is { } read) return new ValueRef(ConfigKey: read.Key);

        var name = SyntaxFacts2.InvokedName(inv);
        var args = inv.ArgumentList.Arguments;
        var receiver = SyntaxFacts2.ReceiverOf(inv);

        if (name is not null && PassThrough.Contains(name) && receiver is not null)
            return Resolve(receiver, depth + 1);

        if (name is not null && NumberParsers.Contains(name) && args.Count >= 1)
            return Resolve(args[0].Expression, depth + 1);

        if (name == "Format" && args.Count >= 2 && SyntaxFacts2.LastName(receiver)?.ToLowerInvariant() == "string")
        {
            var fmt = Resolve(args[0].Expression, depth + 1);
            if (fmt.Text is null) return ValueRef.Unknown;
            var values = args.Skip(1).Select(a => OrHole(Resolve(a.Expression, depth + 1), a.Expression)).ToList();
            return new ValueRef(Text: FormatHole.Replace(fmt.Text, m =>
            {
                var at = int.Parse(m.Groups["i"].Value, System.Globalization.CultureInfo.InvariantCulture);
                return at < values.Count ? values[at].Text ?? ("{" + values[at].ConfigKey + "}") : m.Value;
            }));
        }

        if (name is "FromSeconds" or "FromMilliseconds" or "FromMinutes" or "FromHours" or "FromDays"
            && args.Count == 1 && SyntaxFacts2.LastName(receiver) == "TimeSpan")
        {
            var v = Resolve(args[0].Expression, depth + 1);
            var factor = name switch
            {
                "FromMilliseconds" => 0.001,
                "FromMinutes" => 60,
                "FromHours" => 3600,
                "FromDays" => 86400,
                _ => 1,
            };
            return new ValueRef(Number: v.Number * factor, ConfigKey: v.ConfigKey);
        }
        return ValueRef.Unknown;
    }

    private ValueRef ResolveConditionalAccess(ConditionalAccessExpressionSyntax ca, int depth)
    {
        switch (ca.WhenNotNull)
        {
            case ElementBindingExpressionSyntax eb when eb.ArgumentList.Arguments.Count == 1:
            {
                var key = Resolve(eb.ArgumentList.Arguments[0].Expression, depth + 1);
                return ConfigFromReceiver(ca.Expression, key, "indexer") is { } r ? new ValueRef(ConfigKey: r.Key) : ValueRef.Unknown;
            }
            case InvocationExpressionSyntax inv:
                return TryConfigRead(inv) is { } read ? new ValueRef(ConfigKey: read.Key) : ValueRef.Unknown;
            default:
                return ValueRef.Unknown;
        }
    }

    private ValueRef ResolveMember(MemberAccessExpressionSyntax ma, int depth)
    {
        if (ma.Expression is ThisExpressionSyntax) return ResolveName(ma.Name.Identifier.Text, ma, depth);

        if (OptionsRead(ma) is { } options) return new ValueRef(ConfigKey: options.Key);

        // A constant of the same class (ThisClass.Path) or of any class in the project (Paths.Screen).
        if (ma.Expression is IdentifierNameSyntax owner)
        {
            if (owner.Identifier.Text == _type.SimpleName) return ResolveName(ma.Name.Identifier.Text, ma, depth);
            if (_registry.Consts.TryGetValue(owner.Identifier.Text + "." + ma.Name.Identifier.Text, out var text))
                return new ValueRef(Text: text);
        }
        return ValueRef.Unknown;
    }

    private ValueRef ResolveName(string name, SyntaxNode at, int depth)
    {
        if (FindLocal(at, name) is { Initializer: { } init }) return Resolve(init.Value, depth + 1);
        if (_type.Values.TryGetValue(name, out var v)) return Resolve(v, depth + 1);
        return ValueRef.Unknown;
    }

    // ── text pieces ─────────────────────────────────────────────────────

    private static ValueRef OrHole(ValueRef v, ExpressionSyntax e)
    {
        if (v.IsKnown) return v;
        return new ValueRef(Text: "{" + (SyntaxFacts2.LastName(e) ?? "?") + "}");
    }

    private static ValueRef Combine(ValueRef a, ValueRef b)
    {
        // A setting followed by text: the text is what comes after the setting's value.
        if (a.ConfigKey is not null)
            return a with { Suffix = (a.Suffix ?? "") + (b.Text ?? (b.ConfigKey is not null ? "{" + b.ConfigKey + "}" : "{?}")) };
        if (b.ConfigKey is not null && string.IsNullOrEmpty(a.Text)) return b;
        var left = a.Text ?? (a.Number is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "{?}");
        var right = b.ConfigKey is not null
            ? "{" + b.ConfigKey + "}" + b.Suffix
            : b.Text ?? (b.Number is { } m ? m.ToString(System.Globalization.CultureInfo.InvariantCulture) : "{?}");
        return new ValueRef(Text: left + right);
    }

    /// <summary><c>new Uri(baseUri, "relative")</c>: the two joined with exactly one slash between them.</summary>
    private static ValueRef Join(ValueRef baseRef, string relative)
    {
        string Glue(string head) => head.EndsWith('/') || relative.StartsWith('/') || head.Length == 0 ? relative : "/" + relative;
        if (baseRef.ConfigKey is not null)
            return baseRef with { Suffix = (baseRef.Suffix ?? "") + Glue(baseRef.Suffix ?? "x") };
        if (baseRef.Text is null) return ValueRef.Unknown;
        return new ValueRef(Text: baseRef.Text + Glue(baseRef.Text));
    }

    // ── settings ────────────────────────────────────────────────────────

    /// <summary>
    /// The setting an expression reads, if it is one of the usual ways: <c>config["A:B"]</c>,
    /// <c>GetValue&lt;T&gt;("A:B")</c>, <c>GetSection("A")["B"]</c>, <c>GetConnectionString("X")</c>,
    /// <c>ConfigurationManager.AppSettings["X"]</c>, <c>Environment.GetEnvironmentVariable("X")</c>.
    /// </summary>
    public ConfigRead? TryConfigRead(ExpressionSyntax e)
    {
        ComputedKey = false;
        switch (e)
        {
            case ElementAccessExpressionSyntax el when el.ArgumentList.Arguments.Count == 1:
                return ConfigFromReceiver(el.Expression, Resolve(el.ArgumentList.Arguments[0].Expression), "indexer");

            case InvocationExpressionSyntax inv:
            {
                var name = SyntaxFacts2.InvokedName(inv);
                var args = inv.ArgumentList.Arguments;
                var receiver = SyntaxFacts2.ReceiverOf(inv);
                if (name is null || args.Count == 0 || receiver is null) return null;

                // GetValue<T>("key", default) and GetValue(typeof(T), "key").
                if (name == "GetValue")
                {
                    var at = args[0].Expression is TypeOfExpressionSyntax && args.Count > 1 ? 1 : 0;
                    var key = Resolve(args[at].Expression);
                    return KeyOrNull(key, SectionPrefix(receiver, 0) ?? "", "GetValue", ConfigConfidence(receiver));
                }
                if (name == "GetConnectionString")
                    return KeyOrNull(Resolve(args[0].Expression), "ConnectionStrings:", "connection string", High);

                if (name == "GetEnvironmentVariable" && SyntaxFacts2.LastName(receiver) == "Environment")
                    return KeyOrNull(Resolve(args[0].Expression), "env:", "environment", High);

                // ConfigurationManager.AppSettings.Get("key")
                if (name == "Get" && SyntaxFacts2.LastName(receiver) == "AppSettings")
                    return KeyOrNull(Resolve(args[0].Expression), "", "AppSettings", High);
                return null;
            }
            default:
                return null;
        }
    }

    private ConfigRead? ConfigFromReceiver(ExpressionSyntax receiver, ValueRef key, string form)
    {
        var last = SyntaxFacts2.LastName(receiver);
        if (last == "AppSettings") return KeyOrNull(key, "", "AppSettings", High);
        if (last == "ConnectionStrings" && receiver.ToString().Contains("ConfigurationManager", StringComparison.Ordinal))
            return KeyOrNull(key, "ConnectionStrings:", "connection string", High);

        var prefix = SectionPrefix(receiver, 0);
        if (prefix is not null) return KeyOrNull(key, prefix, form, High);
        if (LooksLikeConfiguration(receiver)) return KeyOrNull(key, "", form, ConfigConfidence(receiver));
        return null;
    }

    private ConfigRead? KeyOrNull(ValueRef key, string prefix, string form, string confidence)
    {
        // A key spelled out nowhere in the code (built at run time, or itself read from a setting) is not one we can name.
        var failed = key.Text is null || key.Text.Length == 0 || key.Text.Contains('{') || key.ConfigKey is not null;
        ComputedKey = failed;
        return failed ? null : new ConfigRead(prefix + key.Text!.Trim(), form, confidence);
    }

    /// <summary>High when what is read from is declared as a configuration type, medium when only its name says so.</summary>
    private string ConfigConfidence(ExpressionSyntax receiver)
    {
        var name = SyntaxFacts2.LastName(receiver);
        if (name is not null && _type.Types.TryGetValue(name, out var declared)
            && Regex.IsMatch(declared, @"IConfiguration|IConfigurationSection|IConfigurationRoot|NameValueCollection"))
            return "high";
        if (receiver is InvocationExpressionSyntax) return "high";
        return "medium";
    }

    private bool LooksLikeConfiguration(ExpressionSyntax receiver)
    {
        var name = SyntaxFacts2.LastName(receiver);
        if (name is null) return false;
        if (ConfigName.IsMatch(name)) return true;
        return _type.Types.TryGetValue(name, out var declared) && Regex.IsMatch(declared, @"IConfiguration|IConfigurationSection|NameValueCollection");
    }

    /// <summary>
    /// "A:B:" when the expression is a settings section (<c>config.GetSection("A").GetSection("B")</c>, or a
    /// name that was set to one); null when it is not.
    /// </summary>
    private string? SectionPrefix(ExpressionSyntax e, int depth)
    {
        if (depth > MaxDepth) return null;
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return SectionPrefix(p.Expression, depth + 1);
            case PostfixUnaryExpressionSyntax pu when pu.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return SectionPrefix(pu.Operand, depth + 1);
            case InvocationExpressionSyntax inv:
            {
                var name = SyntaxFacts2.InvokedName(inv);
                if (name is not ("GetSection" or "GetRequiredSection") || inv.ArgumentList.Arguments.Count != 1) return null;
                var section = Resolve(inv.ArgumentList.Arguments[0].Expression);
                if (section.Text is null || section.Text.Contains('{') || section.ConfigKey is not null) return null;
                var receiver = SyntaxFacts2.ReceiverOf(inv);
                var outer = receiver is null ? "" : SectionPrefix(receiver, depth + 1) ?? "";
                return outer + section.Text.Trim(':') + ":";
            }
            case IdentifierNameSyntax id:
                return SectionPrefixOfName(id.Identifier.Text, id, depth);
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma:
                return SectionPrefixOfName(ma.Name.Identifier.Text, ma, depth);
            default:
                return null;
        }
    }

    private string? SectionPrefixOfName(string name, SyntaxNode at, int depth)
    {
        if (FindLocal(at, name) is { Initializer: { } init }) return SectionPrefix(init.Value, depth + 1);
        if (_type.Values.TryGetValue(name, out var v)) return SectionPrefix(v, depth + 1);
        return null;
    }

    // ── options classes ─────────────────────────────────────────────────

    /// <summary>
    /// <c>options.Value.BaseUrl</c> where <c>options</c> is an <c>IOptions&lt;T&gt;</c> and <c>T</c> is bound to a
    /// section: the setting <c>Section:BaseUrl</c>. Only a property the options class really has counts.
    /// </summary>
    public ConfigRead? OptionsRead(MemberAccessExpressionSyntax ma)
    {
        var property = ma.Name.Identifier.Text;
        if (property is "Value" or "CurrentValue") return null;
        var typed = OptionsTypeOf(ma.Expression, 0);
        if (typed is not { Wrapped: false } t || !_registry.HasBinding(t.Name) || !_registry.MayHaveProperty(t.Name, property)) return null;

        var sections = _registry.SectionsFor(t.Name).ToList();
        return new ConfigRead($"{sections[0]}:{property}", "options", sections.Count == 1 ? "high" : "medium");
    }

    /// <summary>"A:B" when the expression is the settings section <c>GetSection("A").GetSection("B")</c> (or a name set to one); null otherwise.</summary>
    public string? SectionName(ExpressionSyntax e) => SectionPrefix(e, 0)?.TrimEnd(':');

    /// <summary>The options class an expression holds an instance of (not the <c>IOptions&lt;T&gt;</c> around it), or null.</summary>
    public string? PlainOptionsType(ExpressionSyntax e) => OptionsTypeOf(e, 0) is { Wrapped: false } t ? t.Name : null;

    /// <summary>A duration the expression spells out (<c>TimeSpan.FromSeconds(5)</c>) or reads from a setting; null when it is neither.</summary>
    public TimeoutRef? Timeout(ExpressionSyntax e, int line)
    {
        var v = Resolve(e);
        return v.Number is not null || v.ConfigKey is not null ? new TimeoutRef(v.Number, v.ConfigKey, line) : null;
    }

    /// <summary><c>CancelAfter(5000)</c> and <c>new CancellationTokenSource(5000)</c> take milliseconds when given a bare number.</summary>
    public TimeoutRef? CancellationTimeout(ExpressionSyntax e, int line)
    {
        var inner = e;
        while (inner is ParenthesizedExpressionSyntax p) inner = p.Expression;
        if (inner is LiteralExpressionSyntax { Token.Value: int or long } lit)
            return new TimeoutRef(Convert.ToDouble(lit.Token.Value, System.Globalization.CultureInfo.InvariantCulture) / 1000, null, line);
        return Timeout(e, line);
    }

    private (string Name, bool Wrapped)? OptionsTypeOf(ExpressionSyntax e, int depth)
    {
        if (depth > MaxDepth) return null;
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return OptionsTypeOf(p.Expression, depth + 1);
            case InvocationExpressionSyntax inv when SyntaxFacts2.InvokedName(inv) == "Get" && SyntaxFacts2.TypeArguments(inv) is { Count: 1 } typeArgs:
                return (SyntaxFacts2.SimpleTypeName(typeArgs[0].ToString()), false);
            case ObjectCreationExpressionSyntax oc:
                return (SyntaxFacts2.SimpleTypeName(oc.Type.ToString()), false);
            case IdentifierNameSyntax id:
                return OptionsTypeOfName(id.Identifier.Text, id, depth);
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma:
                return OptionsTypeOfName(ma.Name.Identifier.Text, ma, depth);
            case MemberAccessExpressionSyntax ma when ma.Name.Identifier.Text is "Value" or "CurrentValue":
                return OptionsTypeOf(ma.Expression, depth + 1) is { Wrapped: true } inner ? (inner.Name, false) : null;
            default:
                return null;
        }
    }

    private (string Name, bool Wrapped)? OptionsTypeOfName(string name, SyntaxNode at, int depth)
    {
        if (FindLocal(at, name) is { } local)
        {
            var declared = (local.Parent as VariableDeclarationSyntax)?.Type;
            if (declared is not null && !declared.IsVar) return ParseType(declared.ToString());
            return local.Initializer is { } init ? OptionsTypeOf(init.Value, depth + 1) : null;
        }
        return _type.Types.TryGetValue(name, out var text) && text != "var" ? ParseType(text) : null;
    }

    private static (string Name, bool Wrapped)? ParseType(string text)
    {
        var t = text.Trim().TrimEnd('?');
        var m = SyntaxFacts2.OptionsWrapper.Match(t);
        if (m.Success) return (SyntaxFacts2.SimpleTypeName(m.Groups["t"].Value), true);
        return (SyntaxFacts2.SimpleTypeName(t), false);
    }

    // ── locals ──────────────────────────────────────────────────────────

    /// <summary>The nearest declaration of a local before <paramref name="at"/>, within the member (or the file's top-level statements) it is in.</summary>
    public VariableDeclaratorSyntax? FindLocal(SyntaxNode at, string name)
    {
        var root = at.FirstAncestorOrSelf<SyntaxNode>(n =>
            n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or PropertyDeclarationSyntax or CompilationUnitSyntax);
        if (root is null) return null;

        if (!_locals.TryGetValue(root, out var map))
        {
            map = new Dictionary<string, List<VariableDeclaratorSyntax>>(StringComparer.Ordinal);
            foreach (var d in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (d.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax) continue;
                if (!map.TryGetValue(d.Identifier.Text, out var list)) map[d.Identifier.Text] = list = new List<VariableDeclaratorSyntax>();
                list.Add(d);
            }
            _locals[root] = map;
        }
        if (!map.TryGetValue(name, out var declared)) return null;
        VariableDeclaratorSyntax? best = null;
        foreach (var d in declared)
            if (d.Span.End <= at.SpanStart) best = d;
        return best ?? declared[0];
    }
}
