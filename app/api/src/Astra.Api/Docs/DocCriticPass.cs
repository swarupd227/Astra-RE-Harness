using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Draft → critique → revise for model-authored documents.
///
/// 1. Deterministic checks in C# (no model): every <c>[path:L…]</c> citation
///    resolves to a real file and a line range inside a real routine; module
///    documents mention every routine in the file; no empty sections; no
///    banned phrases; no table longer than <see cref="MaxTableRows"/> rows
///    unless the kind is a catalog.
/// 2. One critic call scoring accuracy / completeness / clarity / structure
///    1–5 with concrete fixes (the deterministic findings are handed to it).
/// 3. One revise call when the total is below <see cref="DocsOptions.CriticThreshold"/>
///    or a deterministic check failed. The revision is kept only if it has no
///    more check problems than the draft.
///
/// Under the mock provider (or <see cref="DocsOptions.CriticEnabled"/> = false)
/// only step 1 runs. The result is persisted on <c>DocSection.QualityJson</c>.
/// </summary>
public sealed class DocCriticPass
{
    public const string CriticPromptId = "docs-critic";
    public const string CriticPromptVersion = "v1.0";
    public const string RevisePromptId = "docs-revise";
    public const string RevisePromptVersion = "v1.0";
    public const int MaxTableRows = 12;

    private readonly DocsOptions _opts;
    private readonly DocPromptAssets _assets;
    private readonly IDocWriter _writer;
    private readonly ILogger<DocCriticPass> _logger;

    public DocCriticPass(
        IOptions<DocsOptions> opts,
        DocPromptAssets assets,
        IDocWriter writer,
        ILogger<DocCriticPass> logger)
    {
        _opts = opts.Value;
        _assets = assets;
        _writer = writer;
        _logger = logger;
    }

    public sealed record Check(string Name, bool Passed, IReadOnlyList<string> Problems);

    public sealed record Scores(int Accuracy, int Completeness, int Clarity, int Structure)
    {
        public int Total => Accuracy + Completeness + Clarity + Structure;
    }

    public sealed record Critique(Scores Scores, IReadOnlyList<string> Fixes, string Summary);

    public sealed record Report(
        int? Score,
        int Threshold,
        IReadOnlyList<Check> Checks,
        Critique? Critique,
        bool Revised,
        string? Note,
        string Provider,
        string? CriticModel)
    {
        public bool AllChecksPassed => Checks.All(c => c.Passed);
        public int ProblemCount => Checks.Sum(c => c.Problems.Count);

        public string ToJson() => JsonSerializer.Serialize(new
        {
            score = Score,
            threshold = Threshold,
            provider = Provider,
            criticModel = CriticModel,
            checks = Checks.Select(c => new { name = c.Name, passed = c.Passed, problems = c.Problems }),
            critique = Critique is null ? null : new
            {
                scores = new
                {
                    accuracy = Critique.Scores.Accuracy,
                    completeness = Critique.Scores.Completeness,
                    clarity = Critique.Scores.Clarity,
                    structure = Critique.Scores.Structure,
                },
                total = Critique.Scores.Total,
                fixes = Critique.Fixes,
                summary = Critique.Summary,
            },
            revised = Revised,
            note = Note,
        });

        /// <summary>Deterministic-only report for kinds that do not go
        /// through the critic (catalog entries, routine summaries).</summary>
        public static Report ChecksOnly(IReadOnlyList<Check> checks, string provider, string note) =>
            new(null, 0, checks, null, false, note, provider, null);
    }

    public sealed record Input(
        string Kind,
        string Title,
        DocEnvelope Envelope,
        DocWriteRequest WriterRequest,
        DocCitations.CorpusIndex Index,
        IReadOnlyCollection<string> ExpectedRoutines,
        bool IsCatalog,
        bool RequireCitations);

    public sealed record Outcome(DocEnvelope Envelope, Report Report, IReadOnlyList<DocCallRecord> Calls);

    // ── Deterministic checks ─────────────────────────────────────────────

    public static IReadOnlyList<Check> RunChecks(
        string markdown,
        DocCitations.CorpusIndex? index,
        IReadOnlyCollection<string> expectedRoutines,
        bool isCatalog,
        bool requireCitations)
    {
        var checks = new List<Check>(5);

        var citations = DocCitations.Parse(markdown);
        var citationProblems = new List<string>();
        if (index is not null)
            citationProblems.AddRange(DocCitations.Check(markdown, index).Problems);
        if (requireCitations && citations.Count == 0)
            citationProblems.Add("no [path:L…] citations in the document");
        checks.Add(new Check("citations", citationProblems.Count == 0, citationProblems));

        if (expectedRoutines.Count > 0)
        {
            var missing = expectedRoutines
                .Where(n => !DocMarkdown.MentionsRoutine(markdown, n))
                .Select(n => $"routine '{n}' is not mentioned")
                .ToList();
            checks.Add(new Check("routine-coverage", missing.Count == 0, missing));
        }

        var empty = DocMarkdown.EmptySections(markdown);
        checks.Add(new Check("empty-sections", empty.Count == 0, empty.Select(e => $"section '{e}' is empty").ToList()));

        var banned = DocBannedPhrases.Find(markdown);
        checks.Add(new Check("banned-phrases", banned.Count == 0, banned.Select(h => $"'{h.Phrase}' ×{h.Count}").ToList()));

        var rows = DocMarkdown.MaxTableRows(markdown);
        var tableOk = isCatalog || rows <= MaxTableRows;
        checks.Add(new Check("table-length", tableOk,
            tableOk ? Array.Empty<string>() : new[] { $"a table has {rows} rows (limit {MaxTableRows} outside catalogs)" }));

        return checks;
    }

    // ── Critic → revise ──────────────────────────────────────────────────

    public async Task<Outcome> ReviewAsync(Input input, CancellationToken ct)
    {
        var envelope = input.Envelope;
        var calls = new List<DocCallRecord>(2);
        var checks = RunChecks(envelope.Markdown, input.Index, input.ExpectedRoutines, input.IsCatalog, input.RequireCitations);
        var provider = _writer.ProviderName;
        var threshold = _opts.CriticThreshold;

        if (_writer.IsMock || !_opts.CriticEnabled)
        {
            return new Outcome(envelope,
                new Report(null, threshold, checks, null, false,
                    _writer.IsMock ? "critic skipped: mock provider" : "critic disabled", provider, null),
                calls);
        }

        var criticModel = string.IsNullOrWhiteSpace(_opts.CriticModel) ? _opts.SonnetModel : _opts.CriticModel!;
        Critique critique;
        try
        {
            var criticRequest = new DocWriteRequest(
                CriticPromptId, CriticPromptVersion,
                new[] { _assets.StyleGuide, _assets.CriticPrompt },
                BuildCriticUserMessage(input, envelope, checks),
                "emit_critique", "Score the draft on four axes and list concrete fixes.",
                CritiqueSchema, criticModel, _opts.CriticMaxOutputTokens,
                "docs:critic:" + CriticPromptVersion,
                () => """{"scores":{"accuracy":5,"completeness":5,"clarity":5,"structure":5},"fixes":[],"summary":"mock"}""");
            var criticResult = await _writer.WriteAsync(criticRequest, ct);
            calls.Add(new DocCallRecord(criticRequest, criticResult));
            critique = ParseCritique(criticResult.ToolInputJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Critic call failed for {Kind} '{Title}'", input.Kind, input.Title);
            return new Outcome(envelope,
                new Report(null, threshold, checks, null, false, "critic failed: " + FirstLine(ex.Message), provider, criticModel),
                calls);
        }

        var needsRevision = critique.Scores.Total < threshold || checks.Any(c => !c.Passed);
        if (!needsRevision)
        {
            return new Outcome(envelope,
                new Report(critique.Scores.Total, threshold, checks, critique, false, "accepted", provider, criticModel),
                calls);
        }

        try
        {
            var w = input.WriterRequest;
            var reviseRequest = w with
            {
                PromptId = RevisePromptId,
                PromptVersion = RevisePromptVersion,
                SystemBlocks = w.SystemBlocks.Append(_assets.RevisePrompt).ToList(),
                UserMessage = BuildReviseUserMessage(input, envelope, checks, critique),
                CacheKey = w.CacheKey + ":revise",
            };
            var reviseResult = await _writer.WriteAsync(reviseRequest, ct);
            calls.Add(new DocCallRecord(reviseRequest, reviseResult));

            var revised = DocEnvelope.Parse(reviseResult.ToolInputJson);
            var revisedChecks = RunChecks(revised.Markdown, input.Index, input.ExpectedRoutines, input.IsCatalog, input.RequireCitations);
            var before = checks.Sum(c => c.Problems.Count);
            var after = revisedChecks.Sum(c => c.Problems.Count);
            if (after <= before)
            {
                return new Outcome(revised,
                    new Report(critique.Scores.Total, threshold, revisedChecks, critique, true,
                        $"revised: check problems {before}→{after}", provider, criticModel),
                    calls);
            }
            _logger.LogInformation(
                "Revision of {Kind} '{Title}' rejected: check problems {Before}→{After}", input.Kind, input.Title, before, after);
            return new Outcome(envelope,
                new Report(critique.Scores.Total, threshold, checks, critique, false,
                    $"revision rejected: check problems {before}→{after}", provider, criticModel),
                calls);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Revise call failed for {Kind} '{Title}'", input.Kind, input.Title);
            return new Outcome(envelope,
                new Report(critique.Scores.Total, threshold, checks, critique, false, "revise failed: " + FirstLine(ex.Message), provider, criticModel),
                calls);
        }
    }

    // ── Prompt assembly ──────────────────────────────────────────────────

    private static string BuildCriticUserMessage(Input input, DocEnvelope draft, IReadOnlyList<Check> checks)
    {
        return JsonSerializer.Serialize(new
        {
            kind = input.Kind,
            title = input.Title,
            deterministic_findings = Findings(checks),
            inputs = InputsElement(input.WriterRequest.UserMessage),
            draft = draft.Markdown,
            draft_meta = draft.Meta,
        });
    }

    private static string BuildReviseUserMessage(Input input, DocEnvelope draft, IReadOnlyList<Check> checks, Critique critique)
    {
        return JsonSerializer.Serialize(new
        {
            task = "revise",
            kind = input.Kind,
            title = input.Title,
            inputs = InputsElement(input.WriterRequest.UserMessage),
            draft = draft.Markdown,
            draft_meta = draft.Meta,
            critique = new
            {
                scores = new
                {
                    accuracy = critique.Scores.Accuracy,
                    completeness = critique.Scores.Completeness,
                    clarity = critique.Scores.Clarity,
                    structure = critique.Scores.Structure,
                },
                fixes = critique.Fixes,
                summary = critique.Summary,
            },
            deterministic_findings = Findings(checks),
        });
    }

    private static IReadOnlyList<string> Findings(IReadOnlyList<Check> checks) =>
        checks.Where(c => !c.Passed).SelectMany(c => c.Problems.Select(p => $"{c.Name}: {p}")).ToList();

    /// <summary>Embed the writer's user message as JSON when it is JSON, so
    /// the critic sees structure rather than an escaped string.</summary>
    private static object InputsElement(string userMessage)
    {
        try
        {
            using var doc = JsonDocument.Parse(userMessage);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return userMessage;
        }
    }

    public static Critique ParseCritique(string toolInputJson)
    {
        using var doc = JsonDocument.Parse(toolInputJson);
        var root = doc.RootElement;
        var scores = root.TryGetProperty("scores", out var s) && s.ValueKind == JsonValueKind.Object ? s : root;
        var fixes = new List<string>();
        if (root.TryGetProperty("fixes", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in f.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } str) fixes.Add(str);
                else if (item.ValueKind == JsonValueKind.Object) fixes.Add(item.GetRawText());
            }
        }
        var summary = root.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() ?? "" : "";
        return new Critique(
            new Scores(Axis(scores, "accuracy"), Axis(scores, "completeness"), Axis(scores, "clarity"), Axis(scores, "structure")),
            fixes, summary);
    }

    private static int Axis(JsonElement scores, string name)
    {
        if (scores.ValueKind != JsonValueKind.Object || !scores.TryGetProperty(name, out var v)) return 3;
        var n = v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (int)Math.Round(v.GetDouble()),
            JsonValueKind.String => int.TryParse(v.GetString(), out var p) ? p : 3,
            _ => 3,
        };
        return Math.Clamp(n, 1, 5);
    }

    private static readonly object CritiqueSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["required"] = new[] { "scores", "fixes", "summary" },
        ["properties"] = new Dictionary<string, object?>
        {
            ["scores"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["required"] = new[] { "accuracy", "completeness", "clarity", "structure" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["accuracy"] = AxisSchema("Every claim is grounded in the inputs."),
                    ["completeness"] = AxisSchema("Covers what the reader needs and what the inputs show."),
                    ["clarity"] = AxisSchema("Plain, specific, present tense, no filler."),
                    ["structure"] = AxisSchema("Follows the kind's skeleton; tables only for reference data; no empty sections."),
                },
            },
            ["fixes"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "One line each: <where> — <what is wrong> — <what to write instead>. Ordered by impact.",
                ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["summary"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "One paragraph." },
        },
    };

    private static Dictionary<string, object?> AxisSchema(string description) => new()
    {
        ["type"] = "integer",
        ["minimum"] = 1,
        ["maximum"] = 5,
        ["description"] = description,
    };

    private static string FirstLine(string msg)
    {
        var nl = msg.IndexOfAny(new[] { '\n', '\r' });
        return nl > 0 ? msg[..nl] : msg;
    }
}
