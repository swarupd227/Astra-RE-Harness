using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astra.Api.Evidence;

/// <summary>
/// Second pass over one file, with the registry of the whole project in hand: what each class reads from
/// configuration, which web endpoints it calls (and with what timeout, and whether in a loop or a retry),
/// which message queues it publishes to or consumes from, and which procedures and tables its SQL text
/// names. Everything is read from the text of the code; nothing is run and nothing is guessed — a call
/// whose address is built at run time is counted and reported, not turned into an endpoint.
/// </summary>
internal sealed class FactWalker : TypeAwareWalker
{
    private const string High = EntityVocabulary.High;
    private const string Medium = EntityVocabulary.Medium;
    private const int MaxUrlsPerClass = 10;

    private static readonly Regex Absolute = new(@"^(?<o>https?://[^/?#{}\s]+)(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HttpReceiver = new("http|client|rest|web|api|request", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HttpType = new("HttpClient|RestClient|WebClient|HttpWebRequest|IRestClient", RegexOptions.Compiled);
    private static readonly Regex NotAUrl = new("w3\\.org|schemas\\.|xmlsoap\\.org|purl\\.org|json-schema\\.org|openxmlformats|xmlns", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LooksLikeSql = new(@"\b(select|insert|update|delete|merge|exec|execute|truncate)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BareProcedure = new(@"^\s*(?:\[?\w+\]?\.)?\[?(?<p>[A-Za-z_]\w*)\]?\s*$", RegexOptions.Compiled);
    private static readonly Regex RetryName = new("retry", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PolicyReceiver = new("retry|polic|resilien", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> SqlApis = new(StringComparer.Ordinal)
    {
        "Query", "QueryAsync", "QueryFirst", "QueryFirstAsync", "QueryFirstOrDefault", "QueryFirstOrDefaultAsync",
        "QuerySingle", "QuerySingleAsync", "QuerySingleOrDefault", "QuerySingleOrDefaultAsync", "QueryMultiple", "QueryMultipleAsync",
        "Execute", "ExecuteAsync", "ExecuteScalar", "ExecuteScalarAsync", "ExecuteReader", "ExecuteReaderAsync",
        "ExecuteSqlRaw", "ExecuteSqlRawAsync", "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
        "FromSqlRaw", "FromSqlInterpolated", "SqlQuery", "SqlQueryRaw",
    };

    private readonly FactBuilder _fb = new();
    private readonly string _path;
    private readonly List<string> _imports = new();
    private readonly Dictionary<TypeFrame, ClassAcc> _classes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeFrame, HttpScan> _scans = new(ReferenceEqualityComparer.Instance);
    private CompilationUnitSyntax? _root;

    private int _unresolvedHttp;
    private int _computedKeys;
    private int _httpCalls;
    private int _queueOps;
    private int _sqlStatements;

    private sealed class ClassAcc
    {
        public bool Linked;
        public readonly HashSet<string> Urls = new(StringComparer.Ordinal);
        public string? HeaderLocator;

        /// <summary>Where the first thing the class does was found: the locator of a class that has no declaration to point at (top-level statements).</summary>
        public string? FirstLocator;
    }

    private sealed class HttpScan
    {
        public readonly List<(string? Receiver, ValueRef Value)> Bases = new();
        public readonly List<(string? Receiver, TimeoutRef Timeout, SyntaxNode? Member)> Timeouts = new();
    }

    public FactWalker(string path, CodeRegistry registry) : base(registry) { _path = path; }

    public IReadOnlyList<string> Notes
    {
        get
        {
            var notes = new List<string>();
            if (_unresolvedHttp > 0)
                notes.Add($"{_unresolvedHttp} web call{(_unresolvedHttp == 1 ? "" : "s")} whose address is built at run time {(_unresolvedHttp == 1 ? "was" : "were")} not read.");
            if (_computedKeys > 0)
                notes.Add($"{_computedKeys} setting read{(_computedKeys == 1 ? "" : "s")} whose key is built at run time {(_computedKeys == 1 ? "was" : "were")} not read.");
            return notes;
        }
    }

    public int WebCalls => _httpCalls;
    public int QueueOperations => _queueOps;
    public int SqlStatements => _sqlStatements;

    public FactSet Read(CompilationUnitSyntax root)
    {
        _root = root;
        Visit(root);
        Finish();
        return _fb.Build();
    }

    // ── types ───────────────────────────────────────────────────────────

    protected override void OnEnterType(TypeFrame frame)
    {
        var acc = new ClassAcc();
        if (frame.Decl is { } decl)
        {
            // The declaration up to its opening brace: attributes, name, base types.
            var from = SyntaxFacts2.LineOf(decl);
            var brace = decl.OpenBraceToken.IsMissing ? from : decl.SyntaxTree.GetLineSpan(decl.OpenBraceToken.Span).StartLinePosition.Line + 1;
            acc.HeaderLocator = EvidenceLocator.Lines(from, Math.Max(from, brace));
        }
        _classes[frame] = acc;
        _scans[frame] = ScanHttp(frame);
    }

    protected override void OnExitType(TypeFrame frame)
    {
        if (!_classes.TryGetValue(frame, out var acc) || !acc.Linked) return;

        var baseTypes = frame.Decl?.BaseList?.Types.Select(t => t.Type.ToString()).ToList();
        _fb.Entity(EntityVocabulary.Class, frame.Scope.FullName, acc.HeaderLocator ?? acc.FirstLocator, Attrs.Of(
            ("kind", frame.Decl is null ? "program" : frame.Kind),
            ("namespace", frame.Namespace),
            ("baseTypes", baseTypes),
            ("imports", _imports.Where(i => i != "System" && !i.StartsWith("System.", StringComparison.Ordinal)).Distinct().Take(25).ToList()),
            ("methods", frame.Scope.MethodCount),
            ("urls", acc.Urls.OrderBy(u => u, StringComparer.Ordinal).ToList())));
    }

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        if (node.Name is not null && node.Alias is null) _imports.Add(node.Name.ToString());
        base.VisitUsingDirective(node);
    }

    // ── what is visited ─────────────────────────────────────────────────

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (Frame is { } frame)
        {
            if (frame.Resolver.TryConfigRead(node) is { } read) AddConfigRead(frame, node, read);
            else if (frame.Resolver.ComputedKey) _computedKeys++;

            var name = SyntaxFacts2.InvokedName(node);
            if (name is not null)
            {
                HandleHttpCall(node, frame, name);
                HandleQueue(node, frame, name);
                if (SqlApis.Contains(name) && node.ArgumentList.Arguments.Count > 0)
                    HandleSql(node, frame, frame.Resolver.Resolve(node.ArgumentList.Arguments[0].Expression));
            }
        }
        base.VisitInvocationExpression(node);
    }

    public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        if (Frame is { } frame)
        {
            if (frame.Resolver.TryConfigRead(node) is { } read) AddConfigRead(frame, node, read);
            else if (frame.Resolver.ComputedKey) _computedKeys++;
        }
        base.VisitElementAccessExpression(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // options.Value.BaseUrl where the options class is bound to a section (not when it is what a call is made on).
        var isCallee = node.Parent is InvocationExpressionSyntax call && ReferenceEquals(call.Expression, node);
        if (!isCallee && Frame is { } frame && frame.Resolver.OptionsRead(node) is { } read) AddConfigRead(frame, node, read);
        base.VisitMemberAccessExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (Frame is { } frame)
        {
            var type = SyntaxFacts2.SimpleTypeName(node.Type.ToString());
            var args = node.ArgumentList?.Arguments ?? default;
            if (type == "HttpRequestMessage" && args.Count >= 2)
            {
                var url = frame.Resolver.Resolve(args[1].Expression);
                if (url.IsKnown) HandleEndpoint(node, frame, VerbOfMethod(args[0].Expression, frame), url, null);
                else _unresolvedHttp++;
            }
            else if (type == "RestRequest" && args.Count >= 1)
            {
                var url = frame.Resolver.Resolve(args[0].Expression);
                var verb = args.Count >= 2 ? VerbOfMethod(args[1].Expression, frame) : "GET";
                if (url.IsKnown) HandleEndpoint(node, frame, verb, url, null);
                else _unresolvedHttp++;
            }
            else if ((type.EndsWith("Command", StringComparison.Ordinal) || type.EndsWith("DataAdapter", StringComparison.Ordinal)) && args.Count >= 1)
            {
                HandleSql(node, frame, frame.Resolver.Resolve(args[0].Expression));
            }
        }
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // cmd.CommandText = "EXEC dbo.usp_X"
        if (Frame is { } frame && node.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && SyntaxFacts2.LastName(node.Left) == "CommandText")
        {
            HandleSql(node, frame, frame.Resolver.Resolve(node.Right));
        }
        base.VisitAssignmentExpression(node);
    }

    public override void VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (Frame is { } frame && node.IsKind(SyntaxKind.StringLiteralExpression)
            && _classes.TryGetValue(frame, out var acc) && acc.Urls.Count < MaxUrlsPerClass)
        {
            var text = node.Token.ValueText;
            if (text.Length is > 8 and < 300 && Absolute.IsMatch(text) && !NotAUrl.IsMatch(text) && !text.Contains(' '))
                acc.Urls.Add(text);
        }
        base.VisitLiteralExpression(node);
    }

    // ── settings ────────────────────────────────────────────────────────

    private void AddConfigRead(TypeFrame frame, SyntaxNode node, ConfigRead read)
    {
        Link(frame, EntityVocabulary.Reads, (EntityVocabulary.ConfigKey, read.Key), node, read.Confidence,
            Site(node, frame, ("form", read.Form)));
    }

    // ── web calls ───────────────────────────────────────────────────────

    private void HandleHttpCall(InvocationExpressionSyntax inv, TypeFrame frame, string name)
    {
        var verb = VerbOfApi(name);
        var args = inv.ArgumentList.Arguments;
        if (verb is null || args.Count == 0) return;

        var receiverName = SyntaxFacts2.LastName(SyntaxFacts2.ReceiverOf(inv));
        var httpLike = LooksHttp(receiverName, frame);
        var url = frame.Resolver.Resolve(args[0].Expression);
        var text = url.Text;

        // An Async method with a verb for a name is common outside HTTP too (repo.GetAsync(id)): a receiver that
        // does not look like a web client has to show an address in its argument to count.
        var showsAddress = url.ConfigKey is not null || (text is not null && text.Contains("://"));
        if (!httpLike && !showsAddress) return;

        // Nothing known about the address, or all of it is only known at run time.
        var wholeHole = text is not null && Regex.IsMatch(text, @"^\{[^}]*\}$");
        if (url.ConfigKey is null && (text is null || wholeHole))
        {
            _unresolvedHttp++;
            return;
        }
        HandleEndpoint(inv, frame, verb, url, receiverName);
    }

    private void HandleEndpoint(SyntaxNode node, TypeFrame frame, string verb, ValueRef url, string? receiverName)
    {
        _httpCalls++;
        string? baseKey = null, baseUrl = null, fallbackUrl = null;
        var baseUnresolved = false;
        string path;

        if (url.ConfigKey is not null)
        {
            baseKey = url.ConfigKey;
            path = url.Suffix ?? "";
            fallbackUrl = OriginOf(url.Fallback);
        }
        else if (Split(url.Text) is { } abs)
        {
            baseUrl = abs.Origin;
            path = abs.Remainder;
        }
        else
        {
            path = url.Text ?? "";
            if (url.Text is { } holeOnly && holeOnly.StartsWith('{'))
            {
                // The host itself is built at run time.
                _unresolvedHttp++;
                _httpCalls--;
                return;
            }
            var b = BaseFor(frame, receiverName, node);
            if (b is null) baseUnresolved = true;
            else if (b.ConfigKey is not null)
            {
                baseKey = b.ConfigKey;
                path = JoinPath(b.Suffix, path);
                fallbackUrl = OriginOf(b.Fallback);
            }
            else if (Split(b.Text) is { } baseAbs)
            {
                baseUrl = baseAbs.Origin;
                path = JoinPath(baseAbs.Remainder, path);
            }
            else baseUnresolved = true;
        }

        string? query = null;
        var q = path.IndexOf('?');
        if (q >= 0)
        {
            query = path[(q + 1)..];
            path = path[..q];
        }
        path = path.Length == 0 ? "/" : path.StartsWith('/') ? path : "/" + path;

        var baseToken = baseKey is not null ? "{" + baseKey + "}" : baseUrl ?? "";
        var endpoint = $"{verb} {baseToken}{path}";
        var locator = LocatorOf(node);

        _fb.Entity(EntityVocabulary.Endpoint, endpoint, locator, Attrs.Of(
            ("protocol", "http"),
            ("verb", verb),
            ("path", path),
            ("baseKey", baseKey),
            ("baseUrl", baseUrl),
            ("fallbackUrl", fallbackUrl),
            ("baseUnresolved", baseUnresolved ? (object?)true : null),
            ("query", query)));

        var timeout = TimeoutFor(frame, receiverName, node);
        Link(frame, EntityVocabulary.Calls, (EntityVocabulary.Endpoint, endpoint), node, High, Site(node, frame,
            ("timeoutSeconds", timeout?.Seconds),
            ("timeoutSource", timeout is null ? null : timeout.ConfigKey is not null ? "setting" : "code"),
            ("timeoutKey", timeout?.ConfigKey)));

        // The settings that decide where the call goes and how long it waits, said once, from the setting's side.
        if (baseKey is not null)
            _fb.Link(EntityVocabulary.Configures, (EntityVocabulary.ConfigKey, baseKey), (EntityVocabulary.Endpoint, endpoint), locator, High,
                attrs: new Dictionary<string, object?> { ["role"] = "base-address" });
        if (timeout?.ConfigKey is { } timeoutKey)
            _fb.Link(EntityVocabulary.Configures, (EntityVocabulary.ConfigKey, timeoutKey), (EntityVocabulary.Endpoint, endpoint), locator, High,
                attrs: new Dictionary<string, object?> { ["role"] = "timeout" });
    }

    /// <summary>The base address a call to a relative path goes to: the receiver's own, the registered client's, or the one the class sets.</summary>
    private ValueRef? BaseFor(TypeFrame frame, string? receiverName, SyntaxNode node)
    {
        var scan = _scans[frame];
        if (receiverName is not null)
        {
            var own = scan.Bases.FirstOrDefault(b => b.Receiver == receiverName);
            if (own.Receiver is not null) return own.Value;
        }

        if (Registry.ClientsByType.TryGetValue(frame.Scope.SimpleName, out var typed) && typed.Base is not null) return typed.Base;

        // A client made by a factory: factory.CreateClient("name").
        if (receiverName is not null && NamedClient(frame, receiverName, node) is { } named
            && Registry.ClientsByName.TryGetValue(named, out var byName) && byName.Base is not null)
            return byName.Base;

        var distinct = scan.Bases.Select(b => b.Value).Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static string? NamedClient(TypeFrame frame, string receiverName, SyntaxNode node)
    {
        ExpressionSyntax? init = frame.Resolver.FindLocal(node, receiverName)?.Initializer?.Value;
        if (init is null && frame.Scope.Values.TryGetValue(receiverName, out var v)) init = v;
        if (init is InvocationExpressionSyntax inv && SyntaxFacts2.InvokedName(inv) == "CreateClient" && inv.ArgumentList.Arguments.Count == 1)
            return frame.Resolver.Resolve(inv.ArgumentList.Arguments[0].Expression).Text;
        return null;
    }

    private TimeoutRef? TimeoutFor(TypeFrame frame, string? receiverName, SyntaxNode node)
    {
        var scan = _scans[frame];
        var member = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        if (receiverName is not null)
        {
            var own = scan.Timeouts.FirstOrDefault(t => t.Receiver == receiverName);
            if (own.Receiver is not null) return own.Timeout;
        }
        var local = scan.Timeouts.FirstOrDefault(t => t.Member is not null && ReferenceEquals(t.Member, member));
        if (local.Timeout is not null) return local.Timeout;
        if (Registry.ClientsByType.TryGetValue(frame.Scope.SimpleName, out var typed) && typed.Timeout is not null) return typed.Timeout;
        var any = scan.Timeouts.Where(t => t.Receiver is not null).Select(t => t.Timeout).Distinct().ToList();
        return any.Count == 1 ? any[0] : null;
    }

    private HttpScan ScanHttp(TypeFrame frame)
    {
        var scan = new HttpScan();
        IEnumerable<SyntaxNode> nodes = frame.Decl is { } decl
            ? decl.DescendantNodes(n => n == decl || n is not BaseTypeDeclarationSyntax)
            : _root?.Members.OfType<GlobalStatementSyntax>().SelectMany(g => g.DescendantNodesAndSelf()) ?? Enumerable.Empty<SyntaxNode>();

        foreach (var n in nodes)
        {
            switch (n)
            {
                case AssignmentExpressionSyntax a when a.IsKind(SyntaxKind.SimpleAssignmentExpression):
                {
                    var (receiver, property) = a.Left switch
                    {
                        MemberAccessExpressionSyntax m => (SyntaxFacts2.LastName(m.Expression), m.Name.Identifier.Text),
                        IdentifierNameSyntax id when a.Parent is InitializerExpressionSyntax { Parent: ObjectCreationExpressionSyntax oc }
                            && SyntaxFacts2.SimpleTypeName(oc.Type.ToString()) is "HttpClient" or "RestClient" or "RestClientOptions"
                            => (CreatedInto(oc), id.Identifier.Text),
                        _ => ((string?)null, ""),
                    };
                    if (property == "BaseAddress" && (receiver is null || LooksHttp(receiver, frame)))
                        scan.Bases.Add((receiver, frame.Resolver.Resolve(a.Right)));
                    else if (property == "Timeout" && (receiver is null || LooksHttp(receiver, frame))
                             && frame.Resolver.Timeout(a.Right, SyntaxFacts2.LineOf(a)) is { } t)
                        scan.Timeouts.Add((receiver, t, a.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()));
                    break;
                }
                case InvocationExpressionSyntax inv when SyntaxFacts2.InvokedName(inv) == "CancelAfter" && inv.ArgumentList.Arguments.Count == 1:
                    if (frame.Resolver.CancellationTimeout(inv.ArgumentList.Arguments[0].Expression, SyntaxFacts2.LineOf(inv)) is { } c)
                        scan.Timeouts.Add((null, c, inv.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()));
                    break;
                case ObjectCreationExpressionSyntax oc2:
                {
                    var type = SyntaxFacts2.SimpleTypeName(oc2.Type.ToString());
                    var args = oc2.ArgumentList?.Arguments ?? default;
                    if (type == "CancellationTokenSource" && args.Count == 1
                        && frame.Resolver.CancellationTimeout(args[0].Expression, SyntaxFacts2.LineOf(oc2)) is { } cts)
                        scan.Timeouts.Add((null, cts, oc2.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()));
                    else if (type == "RestClient" && args.Count >= 1 && frame.Resolver.Resolve(args[0].Expression) is { IsKnown: true } restBase)
                        scan.Bases.Add((CreatedInto(oc2), restBase));
                    break;
                }
            }
        }
        return scan;
    }

    /// <summary>The name a newly created object is stored in: <c>var client = new HttpClient …</c> or <c>_http = new HttpClient …</c>.</summary>
    private static string? CreatedInto(ObjectCreationExpressionSyntax oc) => oc.Parent switch
    {
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v } => v.Identifier.Text,
        AssignmentExpressionSyntax a => SyntaxFacts2.LastName(a.Left),
        _ => null,
    };

    private static bool LooksHttp(string? name, TypeFrame frame) =>
        name is not null && (HttpReceiver.IsMatch(name) || (frame.Scope.Types.TryGetValue(name, out var t) && HttpType.IsMatch(t)));

    private static string? VerbOfApi(string method) => method switch
    {
        "GetAsync" or "GetStringAsync" or "GetStreamAsync" or "GetByteArrayAsync" or "GetFromJsonAsync" or "DownloadString" or "DownloadStringAsync"
            or "DownloadStringTaskAsync" or "DownloadData" or "DownloadDataAsync" or "DownloadDataTaskAsync" or "DownloadFile" or "DownloadFileAsync" => "GET",
        "PostAsync" or "PostAsJsonAsync" or "PostAsXmlAsync" or "UploadString" or "UploadStringAsync" or "UploadStringTaskAsync"
            or "UploadData" or "UploadDataTaskAsync" => "POST",
        "PutAsync" or "PutAsJsonAsync" => "PUT",
        "PatchAsync" or "PatchAsJsonAsync" => "PATCH",
        "DeleteAsync" or "DeleteFromJsonAsync" => "DELETE",
        _ => null,
    };

    private string VerbOfMethod(ExpressionSyntax method, TypeFrame frame)
    {
        // HttpMethod.Post, Method.Post (RestSharp) and new HttpMethod("POST")
        if (method is MemberAccessExpressionSyntax m) return m.Name.Identifier.Text.ToUpperInvariant();
        if (method is ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 1 } oc
            && frame.Resolver.Resolve(oc.ArgumentList.Arguments[0].Expression).Text is { } text) return text.ToUpperInvariant();
        return "HTTP";
    }

    private static (string Origin, string Remainder)? Split(string? text)
    {
        if (text is null) return null;
        var m = Absolute.Match(text);
        return m.Success ? (m.Groups["o"].Value.TrimEnd('/'), m.Groups["rest"].Value) : null;
    }

    private static string? OriginOf(string? text) => Split(text)?.Origin;

    private static string JoinPath(string? head, string tail)
    {
        if (string.IsNullOrEmpty(head)) return tail;
        if (string.IsNullOrEmpty(tail)) return head;
        return head.TrimEnd('/') + "/" + tail.TrimStart('/');
    }

    // ── message queues ──────────────────────────────────────────────────

    private void HandleQueue(InvocationExpressionSyntax inv, TypeFrame frame, string name)
    {
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            // RabbitMQ: BasicPublish(exchange, routingKey, …) — with the default exchange ("") the routing key is the queue.
            case "BasicPublish" or "BasicPublishAsync":
            {
                var exchange = QueueName(frame, Arg(args, "exchange", 0), allowEmpty: true);
                var routing = QueueName(frame, Arg(args, "routingKey", 1), allowEmpty: true);
                if (exchange is null && routing is null) return;
                var (destination, kind) = !string.IsNullOrEmpty(exchange) ? (exchange!, "exchange") : (routing!, "queue");
                if (destination.Length == 0) return;
                QueueLink(frame, inv, destination, kind, "rabbitmq", EntityVocabulary.Writes,
                    ("routingKey", kind == "exchange" && !string.IsNullOrEmpty(routing) ? routing : null));
                break;
            }
            case "BasicConsume" or "BasicConsumeAsync":
                if (QueueName(frame, Arg(args, "queue", 0), allowEmpty: false) is { } consumed)
                    QueueLink(frame, inv, consumed, "queue", "rabbitmq", EntityVocabulary.Reads);
                break;
            case "QueueDeclare" or "QueueDeclareAsync":
                if (QueueName(frame, Arg(args, "queue", 0), allowEmpty: false) is { } declared)
                    QueueLink(frame, inv, declared, "queue", "rabbitmq", EntityVocabulary.References, ("operation", "declare"));
                break;
            case "ExchangeDeclare" or "ExchangeDeclareAsync":
                if (QueueName(frame, Arg(args, "exchange", 0), allowEmpty: false) is { } exchangeDeclared)
                    QueueLink(frame, inv, exchangeDeclared, "exchange", "rabbitmq", EntityVocabulary.References, ("operation", "declare"));
                break;

            // Azure Service Bus: client.CreateSender("queue"), CreateProcessor / CreateReceiver("queue")
            case "CreateSender" when args.Count >= 1:
                if (QueueName(frame, args[0].Expression, allowEmpty: false) is { } sender)
                    QueueLink(frame, inv, sender, "queue", "servicebus", EntityVocabulary.Writes);
                break;
            case "CreateProcessor" or "CreateReceiver" when args.Count >= 1:
                if (QueueName(frame, args[0].Expression, allowEmpty: false) is { } receiver)
                    QueueLink(frame, inv, receiver, "queue", "servicebus", EntityVocabulary.Reads);
                break;
        }
    }

    private static ExpressionSyntax? Arg(SeparatedSyntaxList<ArgumentSyntax> args, string name, int position)
    {
        foreach (var a in args)
            if (a.NameColon?.Name.Identifier.Text == name) return a.Expression;
        var positional = args.Where(a => a.NameColon is null).ToList();
        return position < positional.Count ? positional[position].Expression : null;
    }

    /// <summary>A queue's name when the code spells it out (or reads it from a setting, then <c>{Setting:Key}</c>); null otherwise.</summary>
    private static string? QueueName(TypeFrame frame, ExpressionSyntax? e, bool allowEmpty)
    {
        if (e is null) return null;
        var v = frame.Resolver.Resolve(e);
        if (v.ConfigKey is not null) return "{" + v.ConfigKey + "}";
        if (v.Text is null || v.HasHoles) return null;
        return v.Text.Length == 0 && !allowEmpty ? null : v.Text;
    }

    private void QueueLink(TypeFrame frame, SyntaxNode node, string name, string kind, string broker, string relation, params (string, object?)[] site)
    {
        _queueOps++;
        _fb.Entity(EntityVocabulary.Queue, name, LocatorOf(node), Attrs.Of(("kind", kind), ("broker", broker)));
        Link(frame, relation, (EntityVocabulary.Queue, name), node, High, Site(node, frame, site));
    }

    // ── SQL text ────────────────────────────────────────────────────────

    private void HandleSql(SyntaxNode node, TypeFrame frame, ValueRef text)
    {
        if (text.Text is not { Length: > 3 } sql || text.ConfigKey is not null) return;

        // A bare procedure name is a call when the same member says the command is a stored procedure.
        var member = node.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>() as SyntaxNode ?? _root;
        var bare = BareProcedure.Match(sql);
        if (bare.Success && member is not null
            && member.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m => m.Name.Identifier.Text == "StoredProcedure"))
        {
            _sqlStatements++;
            Link(frame, EntityVocabulary.Calls, (EntityVocabulary.Procedure, bare.Groups["p"].Value), node, High,
                Site(node, frame, ("via", "stored procedure command")));
            return;
        }
        if (!LooksLikeSql.IsMatch(sql)) return;

        _sqlStatements++;
        var body = SqlPatternReader.ScanBody(sql);
        var explicitExec = Regex.IsMatch(sql, @"^\s*exec(ute)?\b", RegexOptions.IgnoreCase);
        foreach (var p in body.Calls)
            Link(frame, EntityVocabulary.Calls, (EntityVocabulary.Procedure, p), node, explicitExec ? High : Medium, Site(node, frame, ("via", "SQL text")));
        foreach (var t in body.Reads)
            Link(frame, EntityVocabulary.Reads, (EntityVocabulary.Table, t), node, Medium, Site(node, frame, ("via", "SQL text")));
        foreach (var t in body.Writes)
            Link(frame, EntityVocabulary.Writes, (EntityVocabulary.Table, t), node, Medium, Site(node, frame, ("via", "SQL text")));
    }

    // ── shared ──────────────────────────────────────────────────────────

    private void Link(TypeFrame frame, string relation, (string Type, string Name) to, SyntaxNode node, string confidence, IReadOnlyDictionary<string, object?> site)
    {
        var locator = LocatorOf(node);
        if (_classes.TryGetValue(frame, out var acc))
        {
            acc.Linked = true;
            acc.FirstLocator ??= locator;
        }
        _fb.Link(relation, (EntityVocabulary.Class, frame.Scope.FullName), to, locator, confidence, site);
    }

    private static string LocatorOf(SyntaxNode node)
    {
        var (from, to) = SyntaxFacts2.LinesOf(SyntaxFacts2.Statement(node));
        return EvidenceLocator.Lines(from, to);
    }

    private static Dictionary<string, object?> Site(SyntaxNode node, TypeFrame frame, params (string Key, object? Value)[] extra)
    {
        var site = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["routine"] = RoutineOf(node, frame),
            ["line"] = SyntaxFacts2.LineOf(node),
        };
        // Whatever it is, a call made in a loop or a retry policy is not the same as one made once.
        if (RetryOf(node) is { } retry) site["retry"] = retry;
        foreach (var (k, v) in extra)
            if (v is not null) site[k] = v;
        return site;
    }

    private static string RoutineOf(SyntaxNode node, TypeFrame frame)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case MethodDeclarationSyntax m: return $"{frame.Scope.SimpleName}.{m.Identifier.Text}";
                case ConstructorDeclarationSyntax: return $"{frame.Scope.SimpleName}.{frame.Scope.SimpleName}";
                case PropertyDeclarationSyntax p: return $"{frame.Scope.SimpleName}.{p.Identifier.Text}";
                case FieldDeclarationSyntax: return $"{frame.Scope.SimpleName} (field)";
                case GlobalStatementSyntax: return "(top-level statements)";
            }
        }
        return frame.Scope.SimpleName;
    }

    /// <summary>"loop" when the node is inside a for/while/do, "policy" inside a retry policy's delegate; null otherwise.</summary>
    private static string? RetryOf(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null && n is not BaseMethodDeclarationSyntax; n = n.Parent)
        {
            switch (n)
            {
                case ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                    return "loop";
                case LambdaExpressionSyntax { Parent: ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax call } } }:
                {
                    var callee = SyntaxFacts2.InvokedName(call);
                    if (callee is null) break;
                    if (RetryName.IsMatch(callee)) return "policy";
                    if (callee.StartsWith("Execute", StringComparison.Ordinal) && SyntaxFacts2.LastName(SyntaxFacts2.ReceiverOf(call)) is { } r && PolicyReceiver.IsMatch(r))
                        return "policy";
                    break;
                }
            }
        }
        return null;
    }
}
