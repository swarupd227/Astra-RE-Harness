using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// What the C# reader states about a project's code: the settings a class reads, the web endpoints and
/// queues it talks to, the procedures and tables its SQL names. Every case is a few lines of ordinary
/// code, read the way the real files are.
/// </summary>
public class CSharpFactsTests
{
    private static IReadOnlyDictionary<string, CodeFileFacts> Read(params (string Path, string Text)[] files) =>
        CSharpFacts.Read(files.Select(f => new CodeSource(f.Path, f.Text)).ToList());

    private static FactSet One(string text, string path = "src/Thing.cs") => Read((path, text))[path].Facts;

    private static LinkFact[] From(FactSet facts, string relation, string toType) =>
        facts.Links.Where(l => l.Relation == relation && l.To.Type == toType).ToArray();

    private static List<Dictionary<string, object?>> Sites(LinkFact link) =>
        (List<Dictionary<string, object?>>)link.Attrs!["sites"]!;

    // ── settings ────────────────────────────────────────────────────────

    [Fact]
    public void Settings_AreFoundInTheUsualWaysAndNamedByKey()
    {
        var facts = One("""
            using System;
            using Microsoft.Extensions.Configuration;
            namespace Teller.Services;

            public class SessionExtension
            {
                private readonly IConfiguration _config;
                public SessionExtension(IConfiguration config) { _config = config; }
                public bool IsTeller(string group) => group == _config["Auth:TellerAdGroup"];
                public int Limit() => _config.GetValue<int>("Limits:Max");
                public string Name() => _config.GetSection("Branch").GetValue<string>("Name");
                public string Conn() => _config.GetConnectionString("Teller");
                public string Nested() => _config.GetSection("A").GetSection("B")["C"];
                public string Legacy() => System.Configuration.ConfigurationManager.AppSettings["Legacy:Flag"];
                public string Mode() => Environment.GetEnvironmentVariable("TLR_MODE");
            }
            """);

        var keys = From(facts, "reads", "config-key").Select(l => l.To.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "A:B:C", "Auth:TellerAdGroup", "Branch:Name", "ConnectionStrings:Teller", "Legacy:Flag", "Limits:Max", "env:TLR_MODE" }
                .OrderBy(x => x).ToArray(),
            keys);
        Assert.All(From(facts, "reads", "config-key"), l => Assert.Equal("Teller.Services.SessionExtension", l.From.Name));

        var tellerGroup = Assert.Single(facts.Links, l => l.To.Name == "Auth:TellerAdGroup");
        Assert.Equal("high", tellerGroup.Confidence);
        Assert.Equal("Teller.Services.SessionExtension.IsTeller".Replace("Teller.Services.", ""), (string)Sites(tellerGroup)[0]["routine"]!);
    }

    [Fact]
    public void Settings_ReadThroughASectionVariable_KeepTheSectionPrefix()
    {
        var facts = One("""
            public class Reader
            {
                private readonly IConfiguration _config;
                public string Url()
                {
                    var section = _config.GetSection("Ofac");
                    return section["BaseUrl"];
                }
            }
            """);

        Assert.Equal("Ofac:BaseUrl", Assert.Single(From(facts, "reads", "config-key")).To.Name);
    }

    [Fact]
    public void Settings_WhoseKeyIsBuiltAtRunTime_AreCountedNotInvented()
    {
        var all = Read(("src/A.cs", """
            public class A
            {
                private readonly IConfiguration _config;
                public string Get(string env) => _config["Env:" + env + ":Url"];
                public string Fixed() => _config["Fixed:Key"];
            }
            """));

        var file = all["src/A.cs"];
        Assert.Equal("Fixed:Key", Assert.Single(From(file.Facts, "reads", "config-key")).To.Name);
        Assert.Contains(file.Notes, n => n.Contains("setting read") && n.Contains("built at run time"));
    }

    [Fact]
    public void OptionsClasses_BoundInOneFile_AreReadAsTheirSettingsInAnother()
    {
        var all = Read(
            ("src/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.Configure<ReceiptOptions>(builder.Configuration.GetSection("ReceiptPrinter"));
                """),
            ("src/ReceiptPrinterExtension.cs", """
                public class ReceiptOptions { public string Url { get; set; } = ""; public int Copies { get; set; } }
                public class ReceiptPrinterExtension
                {
                    private readonly ReceiptOptions _options;
                    public ReceiptPrinterExtension(IOptions<ReceiptOptions> options) { _options = options.Value; }
                    public string Target() => _options.Url;
                }
                public class Wrapped
                {
                    private readonly IOptions<ReceiptOptions> _wrapped;
                    public int Count() => _wrapped.Value.Copies;
                    public string NotAProperty() => _wrapped.Value.ToString();
                }
                """));

        var reads = all["src/ReceiptPrinterExtension.cs"].Facts.Links.Where(l => l.Relation == "reads").ToArray();
        var printer = Assert.Single(reads, l => l.From.Name == "ReceiptPrinterExtension");
        Assert.Equal("ReceiptPrinter:Url", printer.To.Name);
        Assert.Equal("high", printer.Confidence);
        Assert.Equal("ReceiptPrinter:Copies", Assert.Single(reads, l => l.From.Name == "Wrapped").To.Name);
        // ToString is not a property of the options class, so it is not a setting.
        Assert.DoesNotContain(reads, l => l.To.Name.EndsWith(":ToString"));
    }

    [Fact]
    public void OptionsClasses_BoundWithGetAndAddOptions_AreRecognised()
    {
        var all = Read(
            ("src/Startup.cs", """
                public class Startup
                {
                    public void Configure(IServiceCollection services, IConfiguration config)
                    {
                        services.AddOptions<QueueOptions>().Bind(config.GetSection("RabbitMq"));
                        var ofac = config.GetSection("Ofac").Get<OfacOptions>();
                        var url = ofac.BaseUrl;
                    }
                }
                public class QueueOptions { public string Exchange { get; set; } }
                public class OfacOptions { public string BaseUrl { get; set; } }
                public class Publisher
                {
                    private readonly QueueOptions _q;
                    public string Target() => _q.Exchange;
                }
                """));

        var reads = all["src/Startup.cs"].Facts.Links.Where(l => l.Relation == "reads").Select(l => (l.From.Name, l.To.Name)).ToArray();
        Assert.Contains(("Publisher", "RabbitMq:Exchange"), reads);
        Assert.Contains(("Startup", "Ofac:BaseUrl"), reads);
    }

    // ── web endpoints ───────────────────────────────────────────────────

    [Fact]
    public void WebCall_WithALiteralAddress_IsAnEndpointNamedByVerbHostAndPath()
    {
        var facts = One("""
            public class LoanClient
            {
                private readonly HttpClient _http = new HttpClient();
                public async Task<string> Get(string loanRef)
                {
                    return await _http.GetStringAsync("https://loanserv.brfc.example/api/loans/" + loanRef);
                }
            }
            """);

        var endpoint = Assert.Single(facts.Entities, e => e.Type == "endpoint");
        Assert.Equal("GET https://loanserv.brfc.example/api/loans/{loanRef}", endpoint.Name);
        Assert.Equal("https://loanserv.brfc.example", endpoint.Attrs!["baseUrl"]);
        var call = Assert.Single(From(facts, "calls", "endpoint"));
        Assert.Equal("LoanClient", call.From.Name);
        Assert.Equal("high", call.Confidence);
        Assert.Equal("LoanClient.Get", Sites(call)[0]["routine"]);

        // The literal is also kept on the class, so it is findable even where a call could not be read.
        var cls = Assert.Single(facts.Entities, e => e.Type == "class");
        Assert.Contains("https://loanserv.brfc.example/api/loans/", (IEnumerable<string>)cls.Attrs!["urls"]!);
    }

    [Fact]
    public void WebCall_RelativeToABaseAddressFromASetting_NamesTheSettingAndKeepsTheFallback()
    {
        var facts = One("""
            public class LoanLookupClient
            {
                private readonly HttpClient _http;
                public LoanLookupClient(HttpClient http, IConfiguration config)
                {
                    _http = http;
                    _http.BaseAddress = new Uri(config["LoanLookup:BaseUrl"] ?? "https://loanserv.brfc.example");
                }
                public Task<string> Get(string r) => _http.GetStringAsync($"/api/loans/{r}");
            }
            """);

        var endpoint = Assert.Single(facts.Entities, e => e.Type == "endpoint");
        Assert.Equal("GET {LoanLookup:BaseUrl}/api/loans/{r}", endpoint.Name);
        Assert.Equal("LoanLookup:BaseUrl", endpoint.Attrs!["baseKey"]);
        Assert.Equal("https://loanserv.brfc.example", endpoint.Attrs["fallbackUrl"]);

        // Said once from the setting's side too: this setting decides where the endpoint is.
        var configures = Assert.Single(From(facts, "configures", "endpoint"));
        Assert.Equal("LoanLookup:BaseUrl", configures.From.Name);
        Assert.Equal("base-address", configures.Attrs!["role"]);
        Assert.Contains(From(facts, "reads", "config-key"), l => l.To.Name == "LoanLookup:BaseUrl");
    }

    [Fact]
    public void WebCall_ThroughATypedClientRegisteredElsewhere_TakesTheRegisteredBaseAndTimeout()
    {
        var all = Read(
            ("src/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddHttpClient<OfacScreeningClient>(c =>
                {
                    c.BaseAddress = new Uri(builder.Configuration["Ofac:BaseUrl"]!);
                });
                """),
            ("src/OfacScreeningClient.cs", """
                public class OfacScreeningClient
                {
                    private readonly HttpClient _http;
                    public OfacScreeningClient(HttpClient http)
                    {
                        _http = http;
                        _http.Timeout = TimeSpan.FromSeconds(5);
                    }
                    public async Task<bool> Screen(string name)
                    {
                        var r = await _http.PostAsJsonAsync("/screen", new { name });
                        return r.IsSuccessStatusCode;
                    }
                }
                """));

        var facts = all["src/OfacScreeningClient.cs"].Facts;
        var endpoint = Assert.Single(facts.Entities, e => e.Type == "endpoint");
        Assert.Equal("POST {Ofac:BaseUrl}/screen", endpoint.Name);

        var call = Assert.Single(From(facts, "calls", "endpoint"));
        var site = Sites(call)[0];
        Assert.Equal(5d, site["timeoutSeconds"]);
        Assert.Equal("code", site["timeoutSource"]);
        // Nothing in this class reads the timeout setting: that is for the graph to notice.
        Assert.DoesNotContain(facts.Links, l => l.To.Name == "Ofac:TimeoutSeconds");

        // The registration itself reads the base setting.
        Assert.Contains(all["src/Program.cs"].Facts.Links, l => l.Relation == "reads" && l.To.Name == "Ofac:BaseUrl" && l.From.Name == "Program");
    }

    [Fact]
    public void WebCall_WithATimeoutFromASetting_SaysWhichSettingAndConfiguresTheEndpoint()
    {
        var facts = One("""
            public class Client
            {
                private readonly HttpClient _http;
                public Client(HttpClient http, IConfiguration config)
                {
                    _http = http;
                    _http.BaseAddress = new Uri("https://svc.brfc.example");
                    _http.Timeout = TimeSpan.FromSeconds(config.GetValue<int>("Svc:TimeoutSeconds"));
                }
                public Task Ping() => _http.GetAsync("/ping");
            }
            """);

        var site = Sites(Assert.Single(From(facts, "calls", "endpoint")))[0];
        Assert.Equal("setting", site["timeoutSource"]);
        Assert.Equal("Svc:TimeoutSeconds", site["timeoutKey"]);
        Assert.Contains(From(facts, "configures", "endpoint"), l => l.From.Name == "Svc:TimeoutSeconds" && (string)l.Attrs!["role"]! == "timeout");
    }

    [Fact]
    public void WebCall_InsideALoopOrARetryPolicy_SaysSo()
    {
        var facts = One("""
            public class Poster
            {
                private readonly HttpClient _http = new HttpClient { BaseAddress = new Uri("https://gl.brfc.example") };
                private readonly IAsyncPolicy _retryPolicy;
                public async Task Loop()
                {
                    for (var i = 0; i < 3; i++)
                    {
                        await _http.PostAsync("/post", null);
                    }
                }
                public Task Policy() => _retryPolicy.ExecuteAsync(() => _http.PutAsync("/put", null));
                public Task Plain() => _http.DeleteAsync("/plain");
            }
            """);

        var byVerb = facts.Links.Where(l => l.Relation == "calls").ToDictionary(l => l.To.Name.Split(' ')[0], l => Sites(l)[0]);
        Assert.Equal("loop", byVerb["POST"]["retry"]);
        Assert.Equal("policy", byVerb["PUT"]["retry"]);
        Assert.False(byVerb["DELETE"].ContainsKey("retry"));
    }

    [Fact]
    public void WebCall_ThroughARequestMessage_AndAnUnrelatedAsyncMethod()
    {
        var facts = One("""
            public class Mixed
            {
                private readonly HttpClient _client = new HttpClient();
                private readonly IRepo _repo;
                public async Task A()
                {
                    var request = new HttpRequestMessage(HttpMethod.Patch, "https://x.brfc.example/items/1");
                    await _client.SendAsync(request);
                    await _repo.GetAsync(42);
                    await _repo.DeleteAsync("name");
                }
            }
            """);

        var endpoint = Assert.Single(facts.Entities, e => e.Type == "endpoint");
        Assert.Equal("PATCH https://x.brfc.example/items/1", endpoint.Name);
    }

    [Fact]
    public void WebCall_WhoseAddressIsOnlyKnownAtRunTime_IsCountedAndNotTurnedIntoAnEndpoint()
    {
        var all = Read(("src/A.cs", """
            public class A
            {
                private readonly HttpClient _http;
                public Task Go(string url) => _http.GetAsync(url);
            }
            """));

        var file = all["src/A.cs"];
        Assert.Empty(file.Facts.Entities);
        Assert.Contains(file.Notes, n => n.Contains("web call") && n.Contains("built at run time"));
    }

    // ── queues ──────────────────────────────────────────────────────────

    [Fact]
    public void Queues_PublishedConsumedAndNamedFromASetting()
    {
        var facts = One("""
            public class GlPublisher
            {
                private readonly IConfiguration _config;
                public void Publish(IModel channel, byte[] body)
                {
                    channel.BasicPublish(exchange: "tlr.gl", routingKey: "gl.posting.in", basicProperties: null, body: body);
                    channel.BasicPublish("", "work.queue", null, body);
                    channel.BasicPublish(_config["RabbitMq:Exchange"], "k", null, body);
                }
                public void Listen(IModel channel) => channel.BasicConsume(queue: "gl.ack", autoAck: true, consumer: null);
            }
            """);

        var queues = facts.Entities.Where(e => e.Type == "queue").ToDictionary(e => e.Name);
        Assert.Equal("exchange", queues["tlr.gl"].Attrs!["kind"]);
        Assert.Equal("queue", queues["work.queue"].Attrs!["kind"]);
        Assert.Contains("{RabbitMq:Exchange}", queues.Keys);

        var writes = From(facts, "writes", "queue");
        Assert.Equal("gl.posting.in", Sites(Assert.Single(writes, l => l.To.Name == "tlr.gl"))[0]["routingKey"]);
        Assert.Equal(3, writes.Length);
        Assert.Equal("gl.ack", Assert.Single(From(facts, "reads", "queue")).To.Name);
    }

    // ── SQL text ────────────────────────────────────────────────────────

    [Fact]
    public void SqlText_NamesProceduresAndTables()
    {
        var facts = One("""
            public class Repo
            {
                public void Post(SqlConnection conn)
                {
                    using var cmd = new SqlCommand("EXEC dbo.usp_PostStage @Mode = 'X'", conn);
                    cmd.ExecuteNonQuery();
                }
                public void Purge(SqlCommand cmd)
                {
                    cmd.CommandText = "usp_PurgeStage";
                    cmd.CommandType = CommandType.StoredProcedure;
                }
                public void Rows(IDbConnection conn)
                {
                    conn.Query("SELECT TXN_REF FROM TLR_TXN WHERE POSTED = 0");
                    conn.Execute("INSERT INTO TLR_STAGE (TXN_REF) VALUES (@r)");
                }
            }
            """);

        var calls = From(facts, "calls", "procedure").ToDictionary(l => l.To.Name);
        Assert.Equal("high", calls["usp_PostStage"].Confidence);
        Assert.Equal("high", calls["usp_PurgeStage"].Confidence);
        var read = Assert.Single(From(facts, "reads", "table"));
        Assert.Equal("TLR_TXN", read.To.Name);
        Assert.Equal("medium", read.Confidence);
        Assert.Equal("TLR_STAGE", Assert.Single(From(facts, "writes", "table")).To.Name);
    }

    [Fact]
    public void SqlText_ThatIsOnlyAWordInAString_IsNotSql()
    {
        var facts = One("""
            public class Messages
            {
                public string Text() => "Please select a drawer and update your count";
                public void Log(ILogger log) => log.Query("a");
            }
            """);
        Assert.True(facts.IsEmpty);
    }

    // ── shape of the output ─────────────────────────────────────────────

    [Fact]
    public void AClassThatTouchesNothing_IsNotInTheGraph()
    {
        Assert.True(One("""
            namespace N;
            public class Plain { public int Add(int a, int b) => a + b; }
            public record Dto(string Name);
            """).IsEmpty);
    }

    [Fact]
    public void Class_CarriesItsKindNamespaceImportsAndLinesOfItsHeader()
    {
        var facts = One("""
            using System;
            using RabbitMQ.Client;
            namespace Teller.Services
            {
                public class GlPublisher : IPublisher
                {
                    public void Go(IModel c) => c.BasicPublish("ex", "rk", null, null);
                }
            }
            """);

        var cls = Assert.Single(facts.Entities, e => e.Type == "class");
        Assert.Equal("Teller.Services.GlPublisher", cls.Name);
        Assert.Equal("class", cls.Attrs!["kind"]);
        Assert.Equal("Teller.Services", cls.Attrs["namespace"]);
        Assert.Equal(new[] { "RabbitMQ.Client" }, (IEnumerable<string>)cls.Attrs["imports"]!);
        Assert.Equal(new[] { "IPublisher" }, (IEnumerable<string>)cls.Attrs["baseTypes"]!);
        // From the declaration to its opening brace, which is on the next line here.
        Assert.Equal("L5-6", cls.Locator);
    }

    [Fact]
    public void Evidence_IsTheLinesOfTheWholeStatement()
    {
        var facts = One("""
            public class A
            {
                private readonly HttpClient _http = new HttpClient();
                public async Task Go()
                {
                    var r = await _http.PostAsync(
                        "https://a.brfc.example/x",
                        null);
                }
            }
            """);

        Assert.Equal("L6-8", Assert.Single(From(facts, "calls", "endpoint")).Locator);
        Assert.Equal(6, Sites(Assert.Single(From(facts, "calls", "endpoint")))[0]["line"]);
    }

    [Fact]
    public void NestedTypes_AreSeparateClassesWithQualifiedNames()
    {
        var facts = One("""
            namespace N;
            public class Outer
            {
                public class Inner
                {
                    private readonly IConfiguration _c;
                    public string Get() => _c["K:V"];
                }
            }
            """);
        Assert.Equal("N.Outer.Inner", Assert.Single(facts.Entities, e => e.Type == "class").Name);
    }

    [Fact]
    public void AFileWithSyntaxErrors_StillYieldsWhatItCouldRead()
    {
        var all = Read(("src/Broken.cs", """
            public class Broken
            {
                private readonly IConfiguration _c;
                public string Get() => _c["Good:Key"];
                public void Bad( { int x = ; }
            }
            """));
        var file = all["src/Broken.cs"];
        Assert.Contains(From(file.Facts, "reads", "config-key"), l => l.To.Name == "Good:Key");
        Assert.Contains(file.Notes, n => n.Contains("syntax error"));
    }

    [Fact]
    public void TheResult_DoesNotDependOnTheOrderFilesAreGivenIn()
    {
        var a = ("src/A.cs", "public class A { private readonly IConfiguration _c; public string G() => _c[\"A:Key\"]; }");
        var b = ("src/B.cs", "public class B { private readonly HttpClient _http = new HttpClient(); public Task G() => _http.GetAsync(\"https://b.brfc.example/b\"); }");
        var one = Read(a, b);
        var two = Read(b, a);
        Assert.Equal(one["src/A.cs"].Facts.ToJson(), two["src/A.cs"].Facts.ToJson());
        Assert.Equal(one["src/B.cs"].Facts.ToJson(), two["src/B.cs"].Facts.ToJson());
    }

    [Fact]
    public void FactsSurviveTheirStorageForm()
    {
        var facts = One("""
            public class Poster
            {
                private readonly HttpClient _http = new HttpClient { BaseAddress = new Uri("https://gl.brfc.example") };
                public async Task Go() { for (var i = 0; i < 2; i++) await _http.PostAsync("/post", null); }
            }
            """);
        var back = FactSet.FromJson(facts.ToJson());
        Assert.Equal(facts.Entities.Count, back.Entities.Count);
        Assert.Equal(facts.Links.Count, back.Links.Count);
        Assert.Contains("\"retry\":\"loop\"", facts.ToJson());
    }

    [Fact]
    public void LargeAndNonCSharpInput_IsSkippedWithASentence()
    {
        var big = new string(' ', CSharpFacts.MaxFileChars + 1);
        var all = Read(("src/Big.cs", big));
        Assert.True(all["src/Big.cs"].Facts.IsEmpty);
        Assert.Contains(all["src/Big.cs"].Notes, n => n.Contains("not read for facts"));

        Assert.True(CSharpFacts.IsCSharp("a/B.CS"));
        Assert.True(CSharpFacts.IsCSharp("a/b.csx"));
        Assert.False(CSharpFacts.IsCSharp("a/b.cpp"));
    }
}
