using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0 vertical slice — Fortran routine-summary extraction.
///
/// Deliberately thin: single-shot non-streaming Anthropic call per routine,
/// no batching, no prompt caching, no tier-based model selection. The slice's
/// only job is to prove the doc-summary prompt produces sensible output and
/// that the DocSection entity model round-trips end to end. Phase 11.0.a
/// adds the production pipeline (batching, caching, tier-based dispatch,
/// metrics rollup into DocGenerationRun).
/// </summary>
public sealed class DocsExtractionService
{
    private const string PromptId = "fortran-doc-summary";
    private const string PromptVersion = "v1.0";

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly HttpClient _http;
    private readonly AnthropicOptions _opts;
    private readonly string _promptBody;
    private readonly ILogger<DocsExtractionService> _logger;

    public DocsExtractionService(
        AppDbContext db,
        IBlobClient blob,
        IHttpClientFactory httpFactory,
        IOptions<AnthropicOptions> opts,
        IWebHostEnvironment env,
        ILogger<DocsExtractionService> logger)
    {
        _db = db;
        _blob = blob;
        _http = httpFactory.CreateClient("docs-summary");
        _http.Timeout = TimeSpan.FromMinutes(2);
        _opts = opts.Value;
        _logger = logger;

        // Prompt sits under ContentRoot/Llm/Prompts/fortran-f77/doc-summary.v1.md
        // when the project is published; under src/Llm/... in dev-watch mode.
        var promptPath = ResolvePromptPath(env.ContentRootPath);
        _promptBody = File.ReadAllText(promptPath);
    }

    private static string ResolvePromptPath(string contentRoot)
    {
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Llm", "Prompts", "fortran-f77", "doc-summary.v1.md"),
            Path.Combine(contentRoot, "..", "..", "Llm", "Prompts", "fortran-f77", "doc-summary.v1.md"),
        };
        foreach (var c in candidates)
            if (File.Exists(c))
                return Path.GetFullPath(c);
        throw new FileNotFoundException(
            "doc-summary prompt not found. Searched: " + string.Join(", ", candidates));
    }

    public sealed record SliceResult(
        Guid GenerationRunId,
        int Requested,
        int Succeeded,
        int Failed,
        IReadOnlyList<SectionSummary> Sections);

    public sealed record SectionSummary(
        Guid SectionId,
        string RoutineName,
        string Tier,
        string Summary,
        int InputTokens,
        int OutputTokens);

    public async Task<SliceResult> RunSliceAsync(
        Guid corpusId,
        int take,
        CancellationToken ct)
    {
        var corpus = await _db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == corpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");
        if (corpus.LatestVersionId is null)
            throw new InvalidOperationException($"Corpus {corpus.Name} has no ingested version.");
        var sourceVersionId = corpus.LatestVersionId.Value;

        var routines = await _db.Subroutines
            .Include(s => s.SourceFile)
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
            .OrderBy(s => s.SourceFile!.RelativePath)
            .ThenBy(s => s.LineStart)
            .Take(take)
            .ToListAsync(ct);

        if (routines.Count == 0)
            throw new InvalidOperationException(
                $"No subroutines under corpus {corpus.Name}'s latest version.");

        var run = new DocGenerationRun
        {
            Id = Guid.NewGuid(),
            CorpusId = corpusId,
            SourceVersionId = sourceVersionId,
            StagesRequested = "routine-summary",
            State = "RUNNING",
            Summary = $"Slice: {routines.Count} routines",
            StartedAt = DateTimeOffset.UtcNow,
        };
        _db.DocGenerationRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var sections = new List<SectionSummary>(routines.Count);
        int succeeded = 0, failed = 0;
        long totalIn = 0, totalOut = 0;

        foreach (var sub in routines)
        {
            try
            {
                var source = await _blob.GetTextAsync(sub.SourceFile!.BlobUri, ct);
                var routineLines = ExtractRoutineLines(source, sub.LineStart, sub.LineEnd);
                var tier = "utility"; // Slice default. Phase 11.0.a picks from blast-radius.

                var (payload, usage) = await CallAnthropicAsync(
                    routineName: sub.Name,
                    enclosingModule: TryGetEnclosingModule(sub),
                    tier: tier,
                    sourceLines: routineLines,
                    ct);

                var section = new DocSection
                {
                    Id = Guid.NewGuid(),
                    CorpusId = corpusId,
                    SourceVersionId = sourceVersionId,
                    SectionKind = "routine-summary",
                    Scope = "subroutine",
                    SubroutineId = sub.Id,
                    State = "DRAFT",
                    PayloadJson = JsonDocument.Parse(payload),
                    RenderedMarkdown = RenderMarkdown(payload, sub.Name),
                    GenerationRunId = run.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                _db.DocSections.Add(section);
                await _db.SaveChangesAsync(ct);

                sections.Add(new SectionSummary(
                    section.Id, sub.Name, tier,
                    ExtractSummaryField(payload),
                    usage.input, usage.output));

                succeeded++;
                totalIn += usage.input;
                totalOut += usage.output;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Doc-summary slice failed for {Routine}", sub.Name);
                failed++;
            }
        }

        run.State = failed == 0 ? "SUCCEEDED" : (succeeded > 0 ? "PARTIAL" : "FAILED");
        run.CompletedAt = DateTimeOffset.UtcNow;
        run.Summary = $"Slice: {succeeded}/{routines.Count} routines succeeded";
        run.MetricsJson = JsonSerializer.Serialize(new
        {
            stage = "routine-summary",
            model = _opts.Model,
            promptId = PromptId,
            promptVersion = PromptVersion,
            sections = succeeded,
            failed,
            inputTokens = totalIn,
            outputTokens = totalOut,
        });
        await _db.SaveChangesAsync(ct);

        return new SliceResult(run.Id, routines.Count, succeeded, failed, sections);
    }

    private static string ExtractRoutineLines(string fullSource, int lineStart, int lineEnd)
    {
        if (lineStart <= 0 || lineEnd <= 0 || lineEnd < lineStart)
            return fullSource;
        var allLines = fullSource.Replace("\r\n", "\n").Split('\n');
        var lo = Math.Max(0, lineStart - 1);
        var hi = Math.Min(allLines.Length, lineEnd);
        var sb = new StringBuilder();
        for (var i = lo; i < hi; i++)
            sb.Append(i + 1).Append(": ").Append(allLines[i]).Append('\n');
        return sb.ToString();
    }

    private static string? TryGetEnclosingModule(Subroutine sub)
    {
        if (sub.SourceFile?.RelativePath is null) return null;
        return Path.GetFileNameWithoutExtension(sub.SourceFile.RelativePath);
    }

    private record TokenUsage(int input, int output);

    private async Task<(string payload, TokenUsage usage)> CallAnthropicAsync(
        string routineName, string? enclosingModule, string tier, string sourceLines,
        CancellationToken ct)
    {
        // System prompt = the markdown body minus the YAML frontmatter.
        var system = StripFrontmatter(_promptBody);
        var user = $$"""
            routine_name: {{routineName}}
            enclosing_module: {{enclosingModule ?? "null"}}
            tier: {{tier}}
            source:
            {{sourceLines}}
            """;

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _opts.Model,
            ["max_tokens"] = 1024,
            ["system"] = system,
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = user },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _opts.ApiKey);
        req.Headers.Add("anthropic-version", _opts.ApiVersion);

        using var resp = await _http.SendAsync(req, ct);
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Anthropic {(int)resp.StatusCode}: {Truncate(bodyText, 400)}");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        var contentArr = root.GetProperty("content");
        string? text = null;
        foreach (var block in contentArr.EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                text = block.GetProperty("text").GetString();
        if (text is null)
            throw new InvalidOperationException("No text block in Anthropic response.");

        var payload = TrimToJsonObject(text);
        using (var _ = JsonDocument.Parse(payload)) { /* parse-validate */ }

        var usage = root.GetProperty("usage");
        return (payload, new TokenUsage(
            usage.GetProperty("input_tokens").GetInt32(),
            usage.GetProperty("output_tokens").GetInt32()));
    }

    private static string StripFrontmatter(string md)
    {
        if (!md.StartsWith("---")) return md;
        var endIdx = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIdx < 0) return md;
        var after = endIdx + 4;
        while (after < md.Length && (md[after] == '\n' || md[after] == '\r')) after++;
        return md[after..];
    }

    private static string TrimToJsonObject(string text)
    {
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close <= open)
            throw new InvalidOperationException("Anthropic response is not a JSON object: " + Truncate(text, 200));
        return text[open..(close + 1)];
    }

    private static string ExtractSummaryField(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("summary", out var s) ? (s.GetString() ?? "") : "";
    }

    private static string RenderMarkdown(string payloadJson, string routineName)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : "";
        var sb = new StringBuilder();
        sb.Append("### ").Append(routineName).Append("\n\n").Append(summary).Append("\n\n");
        AppendList(sb, root, "inputs", "**Inputs**");
        AppendList(sb, root, "outputs", "**Outputs**");
        AppendList(sb, root, "sideEffects", "**Side effects**");
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, JsonElement root, string prop, string heading)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return;
        sb.Append(heading).Append('\n');
        foreach (var item in arr.EnumerateArray())
            sb.Append("- ").Append(item.GetString()).Append('\n');
        sb.Append('\n');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
