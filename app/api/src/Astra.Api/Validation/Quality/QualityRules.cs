using System.Text.RegularExpressions;

namespace Astra.Api.Validation.Quality;

/// <summary>One thing the QUALITY gate found: which rule, how serious, where, and why it matters.</summary>
public sealed record QualityFinding(
    string Rule,
    string Category,      // security | standards | maintainability
    string Severity,      // blocker | critical | major | minor | info
    string File,
    int? Line,
    string Message,
    string Why,
    string Fix,
    string Source = "rule",   // rule | tool (SpotBugs/PMD/Checkstyle) — deterministic, decide the verdict | ai (the reviewer agent; advisory)
    string? SpecClaimId = null);

/// <summary>
/// The platform's own checks on generated code, for every target stack: secrets, unsafe calls, injection-prone
/// queries, open CORS, disabled certificate checks, and the coding standards a reviewer would raise first (Spring
/// constructor injection and validated request bodies, no console output or swallowed exceptions, typed TypeScript).
/// Pure and deterministic — a rule is a pattern over a line (or a whole package) with a severity, a reason and a fix,
/// so the same package always gets the same findings and a finding can be fed back to the generator verbatim.
/// Tool-based analysis (Checkstyle, PMD, SpotBugs, ruff, bandit, eslint) adds to this per stack; it does not replace it.
/// </summary>
public static class QualityRules
{
    public static readonly string[] Severities = ["blocker", "critical", "major", "minor", "info"];

    /// <summary>Severities that fail the gate unless a policy says otherwise.</summary>
    public static readonly HashSet<string> BlockingByDefault = new(StringComparer.Ordinal) { "blocker", "critical" };

    private sealed record LineRule(
        string Id, string Category, string Severity, string[] Extensions, Regex Pattern,
        string Message, string Why, string Fix, bool SkipTests = false, Regex? Unless = null);

    private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const RegexOptions OptI = Opt | RegexOptions.IgnoreCase;

    private static readonly string[] Code = [".java", ".kt", ".py", ".ts", ".tsx", ".js", ".jsx", ".cs", ".vb", ".go", ".php"];
    private static readonly string[] Config = [".properties", ".yml", ".yaml", ".json", ".env", ".ini", ".toml", ".xml", ".config"];
    private static readonly string[] Any = [.. Code, .. Config];
    private static readonly string[] Java = [".java", ".kt"];
    private static readonly string[] Py = [".py"];
    private static readonly string[] Ts = [".ts", ".tsx", ".js", ".jsx"];
    private static readonly string[] Cs = [".cs"];

    private static readonly LineRule[] Rules =
    [
        // ── security: secrets ────────────────────────────────────────────────
        new("SEC-SECRET-KEY", "security", "blocker", Any,
            new(@"(AKIA[0-9A-Z]{16}|sk-ant-[A-Za-z0-9_\-]{20,}|sk-[A-Za-z0-9]{32,}|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_\-]{35})", Opt),
            "A live-looking access key is written into the code.",
            "Anyone who can read the repository or the built package can use the key; keys in code end up in logs, images and backups.",
            "Read the key from configuration or a secret store (environment variable, Key Vault) and keep a placeholder in the example config."),
        new("SEC-PRIVATE-KEY", "security", "blocker", Any,
            new(@"-----BEGIN (RSA |EC |DSA |OPENSSH |)PRIVATE KEY-----", Opt),
            "A private key is embedded in the package.",
            "A private key in source control is compromised the moment the repository is shared.",
            "Load the key from a secret store at run time; never ship it with the code."),
        new("SEC-HARDCODED-CREDENTIAL", "security", "critical", Any,
            new(@"(?<![\w.])(password|passwd|pwd|secret|api[_-]?key|apikey|access[_-]?token|auth[_-]?token|client[_-]?secret)\s*[""']?\s*[:=]\s*[""'](?![\s""']|\$\{|<|changeme|change-me|your[-_]|example|placeholder|xxx|\*\*\*)[^""'\s]{6,}[""']", OptI),
            "A password, token or API key is assigned a literal value.",
            "A credential in code is shared with everyone who sees the code and cannot be rotated without a release.",
            "Inject it from configuration (`@Value(\"${...}\")`, `os.environ[...]`, `IConfiguration`) and keep only a placeholder in examples.",
            SkipTests: true),

        // ── security: transport and trust ────────────────────────────────────
        new("SEC-TLS-DISABLED", "security", "critical", Code,
            new(@"(verify\s*=\s*False|rejectUnauthorized\s*:\s*false|NODE_TLS_REJECT_UNAUTHORIZED|ServerCertificateCustomValidationCallback\s*=.*=>\s*true|DangerousAcceptAnyServerCertificateValidator|TrustAllCerts|InsecureTrustManagerFactory|NoopHostnameVerifier|setHostnameVerifier\(\s*\(\s*\w*\s*,\s*\w*\s*\)\s*->\s*true)", Opt),
            "Certificate or host-name checking is switched off.",
            "Without certificate checks anyone on the network path can read or change the traffic (man-in-the-middle).",
            "Keep the platform's default certificate validation; trust a private CA through the trust store, not by disabling checks."),
        new("SEC-PLAIN-HTTP", "security", "minor", Code,
            new(@"[""']http://(?!localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]|host\.docker\.internal|example\.(com|org)|www\.w3\.org|xmlns|schemas\.)[\w.-]+", OptI),
            "An outside address is called over plain HTTP.",
            "Plain HTTP sends data, including any credentials or personal data, unencrypted.",
            "Use https:// and make the address configurable.",
            SkipTests: true),
        new("SEC-CORS-ANY-ORIGIN", "security", "major", [.. Code, .. Config],
            new(@"(@CrossOrigin\s*$|@CrossOrigin\s*\(\s*\)|@CrossOrigin\s*\(\s*(origins\s*=\s*)?""\*""|allow_origins\s*=\s*\[\s*[""']\*[""']|AllowAnyOrigin\s*\(\s*\)|Access-Control-Allow-Origin[""']?\s*[,:]\s*[""']\*|allowedOrigins\s*\(\s*""\*""\s*\))", Opt),
            "Cross-origin requests are allowed from any site.",
            "Any web page a user visits can call the API with the user's browser, which defeats same-origin protection.",
            "List the front end's origins explicitly from configuration."),
        new("SEC-ACTUATOR-EXPOSED", "security", "critical", Config,
            new(@"management\.endpoints\.web\.exposure\.include\s*[=:]\s*[""']?\*|include:\s*[""']?\*[""']?\s*$", Opt),
            "Every Spring Boot actuator endpoint is exposed.",
            "Actuator endpoints reveal configuration, environment variables and heap dumps to anyone who can reach the service.",
            "Expose only `health` and `info`, and put the rest behind authentication."),
        new("SEC-DEBUG-ENABLED", "security", "major", [.. Py, .. Config],
            new(@"(app\.run\([^)]*debug\s*=\s*True|^\s*DEBUG\s*=\s*True|debug\s*:\s*true\s*$)", Opt),
            "The application runs in debug mode.",
            "Debug mode shows stack traces and an interactive console to callers.",
            "Turn debug off by default and enable it only from local configuration.",
            SkipTests: true),

        // ── security: injection ──────────────────────────────────────────────
        new("SEC-SQL-CONCATENATION", "security", "critical", Code,
            new(@"((?i:""\s*(SELECT|INSERT|UPDATE|DELETE|MERGE)\b[^""]*""\s*\+\s*\w)|(?i:\.(execute|executeQuery|executeUpdate|createQuery|createNativeQuery|query|raw)\s*\(\s*f[""'])|(?i:\.(execute|executeQuery|createQuery|createNativeQuery)\s*\(\s*[""'][^""']*[""']\s*(\+|%)\s*\w)|(?i:String\.format\(\s*""\s*(SELECT|INSERT|UPDATE|DELETE)\b)|(?i:\$""\s*(SELECT|INSERT|UPDATE|DELETE)\b[^""]*\{))", Opt),
            "A SQL statement is built by joining text with values.",
            "Values pasted into SQL text allow SQL injection: a crafted input can read or change any data the service can reach.",
            "Use bound parameters (`?` / `:name` / `@p`) or the repository's query methods, never string building."),
        new("SEC-COMMAND-INJECTION", "security", "critical", Code,
            new(@"(shell\s*=\s*True|os\.system\s*\(|os\.popen\s*\(|subprocess\.\w+\(\s*f[""']|Runtime\.getRuntime\(\)\.exec\s*\(|child_process|execSync\s*\()", Opt),
            "An operating-system command is run, possibly through a shell.",
            "A command built from input lets a caller run arbitrary programs on the server.",
            "Avoid the shell: pass a fixed program and an argument list, and validate every argument against an allow-list.",
            SkipTests: true),
        new("SEC-DYNAMIC-CODE", "security", "critical", [.. Py, .. Ts],
            new(@"(?<![\w.])(eval|exec)\s*\(|new\s+Function\s*\(", Opt),
            "Text is executed as code.",
            "Executing text lets whoever controls the text run any code inside the service.",
            "Parse the input into data (JSON, a number, an enum) instead of evaluating it.",
            SkipTests: true, Unless: new(@"\.(eval|exec)\s*\(|re\.exec|regex\.exec|\.exec\(\s*\)", Opt)),
        new("SEC-UNSAFE-DESERIALIZATION", "security", "critical", Code,
            new(@"(pickle\.loads?\s*\(|yaml\.load\s*\((?![^)]*SafeLoader)|ObjectInputStream|BinaryFormatter|XMLDecoder|enableDefaultTyping\s*\()", Opt),
            "Untrusted data is deserialised into arbitrary objects.",
            "These deserialisers can be made to construct objects that run code — a well-known remote-code-execution route.",
            "Use a data format bound to a fixed schema (JSON into a record/DTO, `yaml.safe_load`)."),
        new("SEC-XSS-RAW-HTML", "security", "major", Ts,
            new(@"(dangerouslySetInnerHTML|\.innerHTML\s*=|\.outerHTML\s*=|document\.write\s*\()", Opt),
            "Raw HTML is written into the page.",
            "HTML built from data lets injected script run in the user's browser (cross-site scripting).",
            "Render text through the framework (`{value}` in JSX) or sanitise with a vetted library."),
        new("SEC-WEAK-HASH", "security", "major", Code,
            new(@"(MessageDigest\.getInstance\(\s*""(MD5|SHA-?1)""|hashlib\.(md5|sha1)\s*\(|MD5\.Create\s*\(|SHA1\.Create\s*\(|createHash\(\s*['""](md5|sha1)['""])", OptI),
            "MD5 or SHA-1 is used.",
            "Both are broken for security purposes; collisions can be produced cheaply.",
            "Use SHA-256 or better for integrity, and a password hash (bcrypt, Argon2, PBKDF2) for passwords."),
        new("SEC-INSECURE-RANDOM", "security", "minor", Code,
            new(@"(?i:(token|password|secret|nonce|salt|otp)\w*\s*=.*\b(new\s+Random\s*\(|Math\.random\s*\(|random\.(random|randint|choice)\s*\())", Opt),
            "A security value is drawn from a predictable random generator.",
            "Ordinary random generators can be predicted, so tokens or salts made with them can be guessed.",
            "Use a cryptographic generator (`SecureRandom`, `secrets`, `RandomNumberGenerator`, `crypto.getRandomValues`)."),

        // ── standards: Java / Spring Boot ────────────────────────────────────
        new("STD-JAVA-SYSTEM-OUT", "standards", "major", Java,
            new(@"System\.(out|err)\.print", Opt),
            "Output goes to `System.out` / `System.err`.",
            "Console output bypasses the logging configuration (levels, format, correlation ids) that operations rely on.",
            "Use an SLF4J logger: `private static final Logger log = LoggerFactory.getLogger(X.class);`.",
            SkipTests: true),
        new("STD-JAVA-PRINT-STACK-TRACE", "standards", "major", Java,
            new(@"\.printStackTrace\s*\(\s*\)", Opt),
            "An exception is printed instead of logged or handled.",
            "The stack trace goes to standard error with no context and the failure is otherwise ignored.",
            "Log it with context (`log.error(\"...\", e)`) and rethrow or translate it."),
        new("STD-JAVA-EMPTY-CATCH", "standards", "major", [.. Java, .. Cs],
            new(@"catch\s*\([^)]*\)\s*\{\s*\}", Opt),
            "An exception is caught and silently ignored.",
            "Swallowed exceptions hide failures and leave the program in an unknown state.",
            "Handle it, log it with context, or let it propagate."),
        new("STD-JAVA-CATCH-GENERIC", "standards", "minor", Java,
            new(@"catch\s*\(\s*(final\s+)?(Exception|Throwable|RuntimeException)\s+\w+\s*\)", Opt),
            "A catch-all exception handler.",
            "Catching everything also catches programming errors that should fail loudly.",
            "Catch the specific exceptions the call can throw; leave the rest to the global handler.",
            SkipTests: true),
        new("STD-JAVA-WILDCARD-IMPORT", "standards", "minor", Java,
            new(@"^\s*import\s+(static\s+)?[\w.]+\.\*\s*;", Opt),
            "A wildcard import.",
            "Wildcard imports hide where names come from and can change meaning when a library adds a class.",
            "Import each class explicitly.",
            SkipTests: true),
        new("STD-SPRING-REQUEST-BODY-NOT-VALIDATED", "standards", "major", Java,
            new(@"@RequestBody\b", Opt),
            "A request body is accepted without `@Valid`.",
            "Without `@Valid` the bean-validation constraints on the request type are never checked, so bad input reaches the service.",
            "Annotate the parameter `@Valid @RequestBody` and put constraints (`@NotNull`, `@Size`, `@Positive` …) on the request type.",
            Unless: new(@"@(Valid|Validated)\b", Opt)),

        // ── standards: Python ────────────────────────────────────────────────
        new("STD-PY-BARE-EXCEPT", "standards", "major", Py,
            new(@"^\s*except\s*:", Opt),
            "A bare `except:` catches everything, including interrupts.",
            "It hides real errors and makes the program hard to stop.",
            "Catch specific exceptions (`except ValueError:`)."),
        new("STD-PY-PRINT", "standards", "minor", Py,
            new(@"^\s*print\s*\(", Opt),
            "Output goes to `print` instead of the logger.",
            "Print output has no level or context and is lost in production logging.",
            "Use `logging.getLogger(__name__)`.",
            SkipTests: true),

        // ── standards: TypeScript ────────────────────────────────────────────
        new("STD-TS-ANY", "standards", "minor", [".ts", ".tsx"],
            new(@"(:\s*any\b|as\s+any\b|<any>)", Opt),
            "A value is typed `any`.",
            "`any` switches type checking off for that value and everything it touches.",
            "Give it a real type or `unknown` with a narrowing check.",
            SkipTests: true),
        new("STD-TS-CONSOLE", "standards", "minor", Ts,
            new(@"console\.(log|debug)\s*\(", Opt),
            "Debug output left in the front end.",
            "Console output leaks internal data to anyone with developer tools open.",
            "Remove it or route it through the app's error reporting.",
            SkipTests: true),

        // ── standards: C# ────────────────────────────────────────────────────
        new("STD-CS-CONSOLE", "standards", "minor", Cs,
            new(@"Console\.(Write|WriteLine)\s*\(", Opt),
            "Output goes to the console instead of `ILogger`.",
            "Console output bypasses log levels and structured logging.",
            "Inject `ILogger<T>` and log through it.",
            SkipTests: true),

        // ── maintainability ──────────────────────────────────────────────────
        new("MNT-TODO", "maintainability", "info", Code,
            new(@"\b(TODO|FIXME|HACK|XXX)\b", Opt),
            "Unfinished work is marked in the code.",
            "Each marker is a known gap in what was delivered.",
            "Resolve it or record it as a tracked item before release."),
    ];

    private static readonly Regex FieldDeclaration = new(
        @"^\s*(private|protected|public)?\s*(final\s+)?[\w.<>,?\[\]\s]+\s+\w+\s*(=.*)?;\s*$", Opt);
    private static readonly Regex RestController = new(@"@RestController\b|@Controller\b", Opt);
    private static readonly Regex ControllerAdvice = new(@"@(Rest)?ControllerAdvice\b|@ExceptionHandler\b", Opt);
    private const int LongFileLines = 600;

    /// <summary>A path inside a test tree, or a test file by its name.</summary>
    public static bool IsTestPath(string path)
    {
        var p = path.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        return p.Contains("/test/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("test/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || p.Contains("/__tests__/", StringComparison.Ordinal)
            || name.StartsWith("test_", StringComparison.Ordinal)
            || name.EndsWith("_test.py", StringComparison.Ordinal)
            || name.EndsWith("Test.java", StringComparison.Ordinal)
            || name.EndsWith("Tests.java", StringComparison.Ordinal)
            || name.EndsWith("Tests.cs", StringComparison.Ordinal)
            || name.EndsWith("Test.cs", StringComparison.Ordinal)
            || name.Contains(".test.", StringComparison.Ordinal)
            || name.Contains(".spec.", StringComparison.Ordinal);
    }

    /// <summary>Runs every rule over a package. Findings come back in file and line order.</summary>
    public static IReadOnlyList<QualityFinding> Scan(IEnumerable<(string Path, string Content)> files)
    {
        var findings = new List<QualityFinding>();
        var list = files.Where(f => !IsGenerated(f.Path)).ToList();
        foreach (var (path, content) in list)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (path.EndsWith(".env", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).StartsWith(".env", StringComparison.Ordinal)) ext = ".env";
            var isTest = IsTestPath(path);
            var lines = content.Replace("\r\n", "\n").Split('\n');
            var applicable = Rules.Where(r => r.Extensions.Contains(ext) && !(r.SkipTests && isTest)).ToList();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length > 2000) continue;   // minified or data — not code a reviewer reads
                var code = StripLineComment(line, ext);
                foreach (var r in applicable)
                {
                    var text = r.Category == "maintainability" ? line : code;
                    if (!r.Pattern.IsMatch(text)) continue;
                    if (r.Unless is not null && r.Unless.IsMatch(text)) continue;
                    findings.Add(new(r.Id, r.Category, r.Severity, path, i + 1, r.Message, r.Why, r.Fix));
                }
                // Spring: an @Autowired on a field (the next code line is a declaration, not a constructor or method).
                if (ext is ".java" or ".kt" && !isTest && code.Trim() == "@Autowired")
                {
                    var next = lines.Skip(i + 1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('@'));
                    if (next is not null && !next.Contains('(') && FieldDeclaration.IsMatch(next))
                        findings.Add(new("STD-SPRING-FIELD-INJECTION", "standards", "major", path, i + 1,
                            "A dependency is injected into a field with `@Autowired`.",
                            "Field injection hides required dependencies, prevents `final` fields and makes the class hard to test without Spring.",
                            "Use constructor injection: a `final` field set by the (single) constructor; `@Autowired` is then unnecessary."));
                }
            }
            if (lines.Length > LongFileLines && !isTest && Code.Contains(ext))
                findings.Add(new("MNT-FILE-TOO-LONG", "maintainability", "minor", path, null,
                    $"The file is {lines.Length:N0} lines long.",
                    "Long files usually mix several responsibilities and are hard to review and change safely.",
                    $"Split it by responsibility; aim for under {LongFileLines} lines."));
        }

        // Package-level: a Spring web API without one place that turns exceptions into responses.
        var javaMain = list.Where(f => f.Path.EndsWith(".java", StringComparison.OrdinalIgnoreCase) && !IsTestPath(f.Path)).ToList();
        var controller = javaMain.FirstOrDefault(f => RestController.IsMatch(f.Content));
        if (controller != default && !javaMain.Any(f => ControllerAdvice.IsMatch(f.Content)))
            findings.Add(new("STD-SPRING-NO-ERROR-HANDLER", "standards", "major", controller.Path, null,
                "The API has no `@RestControllerAdvice` / `@ExceptionHandler`.",
                "Without one place that maps exceptions to responses, callers get stack traces or inconsistent error bodies.",
                "Add a `@RestControllerAdvice` that maps domain and validation exceptions to problem-detail responses."));

        return findings
            .OrderBy(f => Array.IndexOf(Severities, f.Severity))
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? 0)
            .ToList();
    }

    /// <summary>Lock files, build output and vendored code are not the generated code a reviewer reads.</summary>
    private static bool IsGenerated(string path)
    {
        var p = path.Replace('\\', '/');
        var name = p[(p.LastIndexOf('/') + 1)..];
        return name is "package-lock.json" or "yarn.lock" or "pnpm-lock.yaml" or "poetry.lock"
            || p.Contains("/node_modules/", StringComparison.Ordinal)
            || p.Contains("/target/", StringComparison.Ordinal)
            || p.Contains("/bin/", StringComparison.Ordinal)
            || p.Contains("/obj/", StringComparison.Ordinal)
            || p.Contains("/dist/", StringComparison.Ordinal);
    }

    /// <summary>Drops a trailing line comment so a commented-out call is not reported (strings are left alone).</summary>
    private static string StripLineComment(string line, string ext)
    {
        var marker = ext is ".py" or ".properties" or ".yml" or ".yaml" or ".toml" or ".env" or ".ini" ? "#" : ext is ".vb" ? "'" : "//";
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(marker, StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal))
            return "";
        var inStr = false; char q = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inStr) { if (c == '\\') { i++; continue; } if (c == q) inStr = false; continue; }
            if (c is '"' or '\'') { inStr = true; q = c; continue; }
            if (string.CompareOrdinal(line, i, marker, 0, marker.Length) == 0 && (marker != "//" || i == 0 || line[i - 1] != ':'))
                return line[..i];
        }
        return line;
    }

    /// <summary>Counts per severity and per category, plus whether the package passes under the given blocking set.</summary>
    public static QualitySummary Summarise(IReadOnlyList<QualityFinding> findings, IReadOnlySet<string>? blocking = null)
    {
        blocking ??= BlockingByDefault;
        var bySeverity = Severities.ToDictionary(s => s, s => findings.Count(f => f.Severity == s));
        var byCategory = new[] { "security", "standards", "maintainability" }.ToDictionary(c => c, c => findings.Count(f => f.Category == c));
        // The verdict is the rules' alone: the same code always gets the same result. The reviewer agent's findings
        // are listed and counted, never blocking on their own.
        var blockingCount = findings.Count(f => f.Source != "ai" && blocking.Contains(f.Severity));
        return new QualitySummary(findings.Count, blockingCount, bySeverity, byCategory, blockingCount == 0);
    }

    /// <summary>How many rules the platform's own scan has (line rules plus the three package-level checks).</summary>
    public static int RuleCount => Rules.Length + 3;

    /// <summary>A short fingerprint of the rule table (ids, severities, patterns), so a report can name exactly
    /// which rule set checked the code: the same table always gives the same fingerprint.</summary>
    public static string RuleSetFingerprint { get; } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Rules.Select(r => $"{r.Id}|{r.Severity}|{r.Pattern}|{r.Unless}"))
            + "\nSTD-SPRING-FIELD-INJECTION|STD-SPRING-NO-ERROR-HANDLER|MNT-FILE-TOO-LONG")))[..12].ToLowerInvariant();

    /// <summary>One line per finding, in the shape <see cref="GateFailureDigest"/> reads back for a repair.</summary>
    public static string FormatLine(QualityFinding f) =>
        $"QUALITY {f.File}{(f.Line is { } n ? $":{n}" : "")} {f.Rule} {f.Severity}: {f.Message}";

    /// <summary>The fix for a rule, by id — what a regeneration is told to do.</summary>
    public static string? FixFor(string ruleId) =>
        Rules.FirstOrDefault(r => r.Id == ruleId)?.Fix
        ?? ruleId switch
        {
            "STD-SPRING-FIELD-INJECTION" => "Use constructor injection: a `final` field set by the (single) constructor.",
            "STD-SPRING-NO-ERROR-HANDLER" => "Add a `@RestControllerAdvice` that maps domain and validation exceptions to problem-detail responses.",
            "MNT-FILE-TOO-LONG" => "Split the file by responsibility.",
            _ => null,
        };
}

public sealed record QualitySummary(
    int Total,
    int Blocking,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByCategory,
    bool Passed);
