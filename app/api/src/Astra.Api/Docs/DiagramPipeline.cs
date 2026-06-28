using System.Text;
using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.d — diagram generation. Two stages:
///
///  * **sequence-diagram** (LLM-driven). For each headline-tier routine,
///    feeds the routine source + summary + callees into Sonnet and
///    persists a Mermaid `sequenceDiagram` payload. Cost-bounded by
///    limiting to headline tier.
///
///  * **dependency-diagram** (deterministic, no LLM). For each module,
///    walks Subroutine.CalledSubroutines to emit Mermaid `graph TD`
///    nodes + edges. Skips modules whose routines have no internal or
///    cross-module calls. Cheap and reproducible — no model variance.
///
/// Both stages persist DocSection rows with sectionKind="diagram",
/// scope="subroutine" (sequence) or "module" (dependency). The doc-site
/// renderer in Phase 11.0.g will pick these up and render the Mermaid
/// source inline; no extra rendering step is needed in this pipeline.
/// </summary>
public sealed class DiagramPipeline
{
    private const string SequencePromptId = "fortran-doc-sequence-diagram";
    private const string SequencePromptVersion = "v1.0";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly string _sequencePrompt;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _apiVersion;
    private readonly ILogger<DiagramPipeline> _logger;

    public DiagramPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IConfiguration cfg,
        IWebHostEnvironment env,
        ILogger<DiagramPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _logger = logger;
        _apiKey = cfg.GetValue<string>("Llm:Anthropic:ApiKey") ?? "";
        _baseUrl = cfg.GetValue<string>("Llm:Anthropic:BaseUrl") ?? "https://api.anthropic.com";
        _apiVersion = cfg.GetValue<string>("Llm:Anthropic:ApiVersion") ?? "2023-06-01";

        var candidates = new[]
        {
            Path.Combine(env.ContentRootPath, "Llm", "Prompts", "fortran-f77", "doc-sequence-diagram.v1.md"),
            Path.Combine(env.ContentRootPath, "..", "..", "Llm", "Prompts", "fortran-f77", "doc-sequence-diagram.v1.md"),
        };
        var promptPath = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("doc-sequence-diagram prompt not found.");
        _sequencePrompt = StripFrontmatter(File.ReadAllText(promptPath));
    }

    public sealed record StageOutcome(int Diagrams, int Failed, int Skipped);

    /// <summary>Generate one sequence diagram per headline-tier routine.</summary>
    public async Task<StageOutcome> RunSequenceStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobClient>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        // Find headline-tier routines: read routine-summary DocSections and
        // pick the ones whose payload.tier == "headline".
        var routineSections = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "routine-summary"
                     && s.SubroutineId != null)
            .ToListAsync(ct);

        var headlineSubIds = new List<Guid>();
        foreach (var sec in routineSections)
        {
            var root = sec.PayloadJson.RootElement;
            var tier = root.TryGetProperty("tier", out var t) ? t.GetString() : null;
            if (string.Equals(tier, "headline", StringComparison.OrdinalIgnoreCase))
                headlineSubIds.Add(sec.SubroutineId!.Value);
        }

        if (headlineSubIds.Count == 0)
            return new StageOutcome(0, 0, 0);

        // Idempotency: skip headline routines that already have a
        // sequence-diagram DocSection for this SourceVersion.
        var existing = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "diagram"
                     && s.SubroutineId != null)
            .Where(s => headlineSubIds.Contains(s.SubroutineId!.Value))
            .ToListAsync(ct);

        // Only skip those whose payload.diagramKind == "sequence".
        var existingSeqSubIds = new HashSet<Guid>();
        foreach (var sec in existing)
        {
            var root = sec.PayloadJson.RootElement;
            if (root.TryGetProperty("diagramKind", out var dk) && dk.GetString() == "sequence")
                existingSeqSubIds.Add(sec.SubroutineId!.Value);
        }

        if (force && existingSeqSubIds.Count > 0)
        {
            await db.DocSections
                .Where(s => s.CorpusId == corpusId
                         && s.SourceVersionId == sourceVersionId
                         && s.SectionKind == "diagram"
                         && s.SubroutineId != null
                         && existingSeqSubIds.Contains(s.SubroutineId!.Value))
                .ExecuteDeleteAsync(ct);
            existingSeqSubIds.Clear();
        }

        var todoIds = headlineSubIds.Where(id => !existingSeqSubIds.Contains(id)).ToList();
        if (todoIds.Count == 0) return new StageOutcome(0, 0, headlineSubIds.Count);

        var subs = await db.Subroutines
            .Include(s => s.SourceFile)
            .AsNoTracking()
            .Where(s => todoIds.Contains(s.Id))
            .ToListAsync(ct);

        var summaryById = routineSections.ToDictionary(
            s => s.SubroutineId!.Value,
            s => s.PayloadJson.RootElement.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "");

        var sem = new SemaphoreSlim(Math.Max(1, _docsOpts.MaxConcurrency));
        var successCount = 0;
        var failureCount = 0;
        var lockObj = new object();
        var tasks = new List<Task>();

        foreach (var sub in subs)
        {
            var captured = sub;
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var fileSource = await blob.GetTextAsync(captured.SourceFile!.BlobUri, ct);
                    var routineLines = ExtractRoutineLines(fileSource, captured.LineStart, captured.LineEnd);
                    var summary = summaryById.TryGetValue(captured.Id, out var s) ? s : "";
                    var callees = ExtractCalleeNames(captured.CalledSubroutines);

                    var userMessage = BuildSequenceUserMessage(captured.Name, summary, callees, routineLines);
                    var payload = await CallAnthropicAsync(
                        httpFactory, _docsOpts.SonnetModel, _sequencePrompt, userMessage, ct);

                    using var doc = JsonDocument.Parse(payload);

                    using var innerScope = _scopeFactory.CreateScope();
                    var innerDb = innerScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var section = new DocSection
                    {
                        Id = Guid.NewGuid(),
                        CorpusId = corpusId,
                        SourceVersionId = sourceVersionId,
                        SectionKind = "diagram",
                        Scope = "subroutine",
                        SubroutineId = captured.Id,
                        State = "DRAFT",
                        PayloadJson = JsonDocument.Parse(payload),
                        RenderedMarkdown = RenderSequenceMarkdown(payload, captured.Name),
                        GenerationRunId = runId,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    innerDb.DocSections.Add(section);
                    await innerDb.SaveChangesAsync(ct);

                    lock (lockObj) { successCount++; }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sequence diagram failed for {Routine}", captured.Name);
                    lock (lockObj) { failureCount++; }
                }
                finally
                {
                    sem.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
        return new StageOutcome(successCount, failureCount, existingSeqSubIds.Count);
    }

    /// <summary>Generate one dependency diagram per module from call-graph data.</summary>
    public async Task<StageOutcome> RunDependencyStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Idempotency on module-scope dependency diagrams.
        var existingModules = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "diagram"
                     && s.ModuleName != null)
            .ToListAsync(ct);

        var existingDepModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sec in existingModules)
        {
            var root = sec.PayloadJson.RootElement;
            if (root.TryGetProperty("diagramKind", out var dk) && dk.GetString() == "dependency"
                && sec.ModuleName is not null)
                existingDepModules.Add(sec.ModuleName);
        }

        if (force && existingDepModules.Count > 0)
        {
            await db.DocSections
                .Where(s => s.CorpusId == corpusId
                         && s.SourceVersionId == sourceVersionId
                         && s.SectionKind == "diagram"
                         && s.ModuleName != null
                         && existingDepModules.Contains(s.ModuleName!))
                .ExecuteDeleteAsync(ct);
            existingDepModules.Clear();
        }

        // Load every routine + its callees, grouped by source file.
        var subs = await db.Subroutines
            .Include(s => s.SourceFile)
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
            .ToListAsync(ct);

        var byFile = subs
            .GroupBy(s => s.SourceFile!.RelativePath)
            .ToList();

        var diagramCount = 0;
        var skipped = 0;
        foreach (var fileGroup in byFile)
        {
            var moduleName = Path.GetFileNameWithoutExtension(fileGroup.Key);
            if (existingDepModules.Contains(moduleName)) { skipped++; continue; }

            var routines = fileGroup.ToList();
            var routineNames = routines.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Edges: routine → callee for each routine in this module.
            // Distinguish internal (callee in same module) from external
            // (callee in a different module). Skip modules with zero
            // call activity — the diagram would be a list of disconnected
            // nodes.
            var edges = new List<(string from, string to, bool internalCall)>();
            var externalNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in routines)
            {
                if (r.CalledSubroutines is null) continue;
                foreach (var callee in EnumerateCalleeNames(r.CalledSubroutines))
                {
                    var isInternal = routineNames.Contains(callee);
                    edges.Add((r.Name, callee, isInternal));
                    if (!isInternal) externalNodes.Add(callee);
                }
            }

            if (edges.Count == 0) { skipped++; continue; }

            var mermaid = BuildDependencyMermaid(moduleName, routines, edges, externalNodes);
            var narrative = BuildDependencyNarrative(moduleName, routines.Count, edges, externalNodes);
            var payload = JsonSerializer.Serialize(new
            {
                id = $"dg.{moduleName.ToLowerInvariant()}.dep.v1",
                diagramKind = "dependency",
                title = $"{moduleName} — dependency",
                mermaidSource = mermaid,
                narrative,
                citations = new[] { new { lines = $"{routines.Count} routines" } },
            });

            var section = new DocSection
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                SectionKind = "diagram",
                Scope = "module",
                ModuleName = moduleName,
                State = "DRAFT",
                PayloadJson = JsonDocument.Parse(payload),
                RenderedMarkdown = RenderDependencyMarkdown(moduleName, mermaid, narrative),
                GenerationRunId = runId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.DocSections.Add(section);
            diagramCount++;
        }
        await db.SaveChangesAsync(ct);
        return new StageOutcome(diagramCount, 0, skipped);
    }

    private static List<(string name, string? role)> ExtractCalleeNames(JsonDocument? called)
    {
        var list = new List<(string, string?)>();
        if (called is null) return list;
        try
        {
            var root = called.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add((s!, null));
                }
                else if (item.ValueKind == JsonValueKind.Object
                         && item.TryGetProperty("name", out var n)
                         && n.ValueKind == JsonValueKind.String)
                {
                    var name = n.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string? role = null;
                    if (string.Equals(name, "XERBLA", StringComparison.OrdinalIgnoreCase)) role = "error";
                    else if (string.Equals(name, "LSAME", StringComparison.OrdinalIgnoreCase)) role = "validation";
                    list.Add((name, role));
                }
            }
        }
        catch { /* tolerate malformed jsonb */ }
        return list;
    }

    private static IEnumerable<string> EnumerateCalleeNames(JsonDocument called)
    {
        var root = called.RootElement;
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) yield return s!;
            }
            else if (item.ValueKind == JsonValueKind.Object
                     && item.TryGetProperty("name", out var n)
                     && n.ValueKind == JsonValueKind.String)
            {
                var name = n.GetString();
                if (!string.IsNullOrWhiteSpace(name)) yield return name!;
            }
        }
    }

    private static string BuildDependencyMermaid(string moduleName,
        IReadOnlyList<Subroutine> routines,
        IReadOnlyList<(string from, string to, bool internalCall)> edges,
        IReadOnlySet<string> externalNodes)
    {
        var sb = new StringBuilder();
        sb.Append("graph TD\n");
        // Internal routine nodes.
        foreach (var r in routines)
            sb.Append("    ").Append(Sanitise(r.Name)).Append("[\"").Append(EscapeMermaid(r.Name)).Append("\"]\n");
        // External callee nodes (rendered as different shape).
        foreach (var ext in externalNodes.OrderBy(x => x))
            sb.Append("    ").Append(Sanitise(ext)).Append("(\"").Append(EscapeMermaid(ext)).Append("\")\n");
        // Edges. Dedup by (from,to).
        var seen = new HashSet<string>();
        foreach (var (from, to, _) in edges)
        {
            var key = from + "→" + to;
            if (!seen.Add(key)) continue;
            sb.Append("    ").Append(Sanitise(from)).Append(" --> ").Append(Sanitise(to)).Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildDependencyNarrative(string moduleName, int routineCount,
        IReadOnlyList<(string from, string to, bool internalCall)> edges,
        IReadOnlySet<string> externalNodes)
    {
        var externalSample = externalNodes.OrderBy(x => x).Take(3).ToList();
        var sample = externalSample.Count switch
        {
            0 => "no external callees",
            1 => $"calls {externalSample[0]}",
            _ => "calls " + string.Join(", ", externalSample) + (externalNodes.Count > externalSample.Count ? $", and {externalNodes.Count - externalSample.Count} others" : ""),
        };
        return $"Call structure of `{moduleName}`: {routineCount} routine(s), {edges.Count} call edge(s). External: {sample}.";
    }

    private static string Sanitise(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string EscapeMermaid(string s) => s.Replace("\"", "\\\"");

    private static string BuildSequenceUserMessage(string routineName, string summary,
        List<(string name, string? role)> callees, string sourceLines)
    {
        var calleeArr = callees
            .Select(c => new { name = c.name, role = c.role })
            .Cast<object>()
            .ToList();
        return JsonSerializer.Serialize(new
        {
            routine_name = routineName,
            summary,
            callees = calleeArr,
            source = sourceLines,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ExtractRoutineLines(string fullSource, int lineStart, int lineEnd)
    {
        if (lineStart <= 0 || lineEnd <= 0 || lineEnd < lineStart) return fullSource;
        var allLines = fullSource.Replace("\r\n", "\n").Split('\n');
        var lo = Math.Max(0, lineStart - 1);
        var hi = Math.Min(allLines.Length, lineEnd);
        var sb = new StringBuilder();
        for (var i = lo; i < hi; i++)
            sb.Append(i + 1).Append(": ").Append(allLines[i]).Append('\n');
        return sb.ToString();
    }

    private async Task<string> CallAnthropicAsync(
        IHttpClientFactory httpFactory, string model, string systemPrompt, string userMessage, CancellationToken ct)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new { type = "ephemeral" },
                },
            },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userMessage },
            },
        };

        var http = httpFactory.CreateClient("docs-summary");
        http.Timeout = TimeSpan.FromMinutes(3);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", _apiVersion);

        using var resp = await http.SendAsync(req, ct);
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Anthropic {(int)resp.StatusCode}: {Truncate(bodyText, 400)}");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        string? text = null;
        foreach (var block in root.GetProperty("content").EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                text = block.GetProperty("text").GetString();
        if (text is null) throw new InvalidOperationException("No text block in Anthropic response.");

        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close <= open)
            throw new InvalidOperationException("Not a JSON object: " + Truncate(text, 200));
        return text[open..(close + 1)];
    }

    private static string RenderSequenceMarkdown(string payloadJson, string routineName)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? $"{routineName} — sequence" : $"{routineName} — sequence";
        var mermaid = root.TryGetProperty("mermaidSource", out var m) ? m.GetString() ?? "" : "";
        var narrative = root.TryGetProperty("narrative", out var n) ? n.GetString() ?? "" : "";
        var sb = new StringBuilder();
        sb.Append("### ").Append(title).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(narrative))
            sb.Append(narrative).Append("\n\n");
        sb.Append("```mermaid\n").Append(mermaid).Append("\n```\n");
        return sb.ToString();
    }

    private static string RenderDependencyMarkdown(string moduleName, string mermaid, string narrative)
    {
        var sb = new StringBuilder();
        sb.Append("### ").Append(moduleName).Append(" — dependency diagram\n\n");
        sb.Append(narrative).Append("\n\n");
        sb.Append("```mermaid\n").Append(mermaid).Append("\n```\n");
        return sb.ToString();
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
