using Astra.Api.Validation;
using Astra.Api.Validation.Quality;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>The QUALITY gate's own checks: what each rule catches, what it leaves alone, and how a finding travels to a repair.</summary>
public class QualityRulesTests
{
    private static IReadOnlyList<QualityFinding> Scan(string path, string content) => QualityRules.Scan([(path, content)]);
    private static IReadOnlyList<string> Rules(string path, string content) => Scan(path, content).Select(f => f.Rule).ToList();

    [Fact]
    public void A_clean_spring_service_has_no_findings()
    {
        var controller = """
            package com.example.quote;

            import jakarta.validation.Valid;
            import org.slf4j.Logger;
            import org.slf4j.LoggerFactory;
            import org.springframework.web.bind.annotation.PostMapping;
            import org.springframework.web.bind.annotation.RequestBody;
            import org.springframework.web.bind.annotation.RestController;

            @RestController
            public class QuoteController {
                private static final Logger log = LoggerFactory.getLogger(QuoteController.class);
                private final QuoteService service;

                public QuoteController(QuoteService service) {
                    this.service = service;
                }

                @PostMapping("/api/quote")
                public QuoteResult quote(@Valid @RequestBody QuoteRequest request) {
                    log.info("quote requested");
                    return service.quote(request);
                }
            }
            """;
        var advice = """
            package com.example.quote;
            import org.springframework.web.bind.annotation.RestControllerAdvice;
            @RestControllerAdvice
            public class ApiErrors { }
            """;
        var findings = QualityRules.Scan([("src/main/java/com/example/quote/QuoteController.java", controller), ("src/main/java/com/example/quote/ApiErrors.java", advice)]);
        Assert.Empty(findings);
        Assert.True(QualityRules.Summarise(findings).Passed);
    }

    [Fact]
    public void Spring_field_injection_an_unvalidated_body_console_output_and_no_error_handler_are_reported()
    {
        var src = """
            @RestController
            public class QuoteController {
                @Autowired
                private QuoteService service;

                @PostMapping("/api/quote")
                public QuoteResult quote(@RequestBody QuoteRequest request) {
                    System.out.println("quote " + request);
                    try { return service.quote(request); } catch (Exception e) { }
                    return null;
                }
            }
            """;
        var rules = Rules("src/main/java/QuoteController.java", src);
        Assert.Contains("STD-SPRING-FIELD-INJECTION", rules);
        Assert.Contains("STD-SPRING-REQUEST-BODY-NOT-VALIDATED", rules);
        Assert.Contains("STD-JAVA-SYSTEM-OUT", rules);
        Assert.Contains("STD-JAVA-EMPTY-CATCH", rules);
        Assert.Contains("STD-SPRING-NO-ERROR-HANDLER", rules);
        // standards findings are reported but do not block on their own
        Assert.True(QualityRules.Summarise(Scan("src/main/java/QuoteController.java", src)).Passed);
    }

    [Fact]
    public void An_autowired_constructor_is_not_field_injection()
    {
        var src = """
            public class A {
                @Autowired
                public A(B b) { }
            }
            """;
        Assert.DoesNotContain("STD-SPRING-FIELD-INJECTION", Rules("src/main/java/A.java", src));
    }

    [Theory]
    [InlineData("src/main/java/Repo.java", "String sql = \"SELECT * FROM policy WHERE id = \" + id;", "SEC-SQL-CONCATENATION")]
    [InlineData("backend/app/repo.py", "cursor.execute(f\"SELECT * FROM policy WHERE id = {pid}\")", "SEC-SQL-CONCATENATION")]
    [InlineData("backend/app/client.py", "r = httpx.post(url, json=body, verify=False)", "SEC-TLS-DISABLED")]
    [InlineData("backend/app/run.py", "subprocess.run(cmd, shell=True)", "SEC-COMMAND-INJECTION")]
    [InlineData("backend/app/load.py", "data = pickle.loads(blob)", "SEC-UNSAFE-DESERIALIZATION")]
    [InlineData("backend/app/calc.py", "value = eval(expression)", "SEC-DYNAMIC-CODE")]
    [InlineData("src/main/resources/application.properties", "management.endpoints.web.exposure.include=*", "SEC-ACTUATOR-EXPOSED")]
    [InlineData("src/main/java/Web.java", "@CrossOrigin(origins = \"*\")", "SEC-CORS-ANY-ORIGIN")]
    [InlineData("backend/app/main.py", "app.add_middleware(CORSMiddleware, allow_origins=[\"*\"])", "SEC-CORS-ANY-ORIGIN")]
    [InlineData("frontend/src/View.tsx", "<div dangerouslySetInnerHTML={{ __html: html }} />", "SEC-XSS-RAW-HTML")]
    [InlineData("src/main/java/Hash.java", "MessageDigest md = MessageDigest.getInstance(\"MD5\");", "SEC-WEAK-HASH")]
    [InlineData("src/main/java/Client.java", "private static final String API_KEY = \"9f8e7d6c5b4a3210\";", "SEC-HARDCODED-CREDENTIAL")]
    [InlineData("backend/app/settings.py", "password = \"Sup3rSecret!\"", "SEC-HARDCODED-CREDENTIAL")]
    [InlineData("backend/app/client.py", "KEY = \"sk-ant-api03-abcdefghijklmnopqrstuvwxyz012345\"", "SEC-SECRET-KEY")]
    public void Security_problems_are_reported(string path, string line, string rule) =>
        Assert.Contains(rule, Rules(path, line));

    [Theory]
    [InlineData("backend/app/repo.py", "cursor.execute(\"SELECT * FROM policy WHERE id = ?\", (pid,))")]
    [InlineData("src/main/java/Client.java", "@Value(\"${rater.api-key}\") String apiKey;")]
    [InlineData("backend/app/settings.py", "password = os.environ[\"DB_PASSWORD\"]")]
    [InlineData("backend/app/settings.py", "password = \"changeme\"")]
    [InlineData("backend/app/regex.py", "m = pattern.exec(text)")]
    [InlineData("frontend/src/api.ts", "const url = 'http://localhost:8000/api';")]
    [InlineData("backend/app/client.py", "# r = httpx.post(url, verify=False)  (kept for reference)")]
    public void Safe_code_and_placeholders_are_left_alone(string path, string line) =>
        Assert.DoesNotContain(Scan(path, line), f => f.Category == "security");

    [Fact]
    public void Test_files_may_print_and_use_fake_credentials()
    {
        var test = "password = \"Sup3rSecret!\"\nprint(result)\n";
        Assert.Empty(Scan("backend/tests/test_service.py", test));
    }

    [Fact]
    public void A_blocking_finding_fails_the_gate_and_the_summary_says_so()
    {
        var findings = Scan("backend/app/client.py", "r = httpx.post(url, verify=False)\nprint(r)\n");
        var summary = QualityRules.Summarise(findings);
        Assert.False(summary.Passed);
        Assert.Equal(1, summary.Blocking);
        Assert.Equal(1, summary.BySeverity["critical"]);
        Assert.Equal(1, summary.BySeverity["minor"]);
        Assert.Equal("1 blocking finding · 1 critical, 1 minor", QualityValidator.SummaryLine(summary));
        Assert.Equal("critical", findings[0].Severity);              // most severe first
    }

    [Fact]
    public void A_failed_quality_run_becomes_a_repair_hint_with_the_line_and_the_fix()
    {
        var findings = Scan("backend/app/client.py", "import httpx\nr = httpx.post(url, verify=False)\n");
        var log = string.Join("\n", findings.Select(QualityRules.FormatLine)) + "\n=== verdict === FAILED\n";
        var digest = GateFailureDigest.Parse("QUALITY", log);
        Assert.Single(digest.Errors);
        Assert.Equal("backend/app/client.py", digest.Errors[0].File);
        Assert.Equal(2, digest.Errors[0].Line);
        Assert.Equal("SEC-TLS-DISABLED", digest.Errors[0].Code);

        var hint = digest.ToRepairHint(p => p == "backend/app/client.py" ? "import httpx\nr = httpx.post(url, verify=False)\n" : null);
        Assert.Contains("QUALITY gate", hint);
        Assert.Contains("the line was: `r = httpx.post(url, verify=False)`", hint);
        Assert.Contains("fix: Keep the platform's default certificate validation", hint);
        Assert.Contains("found 1 issue", digest.ToMarkdown("PostJson"));
    }

    [Fact]
    public void Minor_and_info_findings_are_not_sent_to_a_repair()
    {
        var log = "QUALITY frontend/src/a.ts:3 STD-TS-ANY minor: A value is typed `any`.\nQUALITY a.py:1 MNT-TODO info: Unfinished work.\n";
        Assert.Empty(GateFailureDigest.Parse("QUALITY", log).Errors);
    }
}
