using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.b — hierarchical rollup that sits on top of Phase 11.0.a's
/// routine-summary sections. Two stages, in order:
///
///   * **Module rollup.** Group routine-summary sections by SourceFile,
///     send the per-routine JSON payloads (NOT raw source again) into the
///     doc-module prompt, produce one module-scope DocSection per file.
///   * **Overview synthesis.** Send every module summary into the
///     doc-overview prompt, produce one corpus-scope DocSection.
///
/// Cost profile: the input at every rollup step is already-summarised
/// content, not raw source — so a 169-module corpus's overview is
/// roughly the same input cost as a single MID-tier routine summary. The
/// expensive bit (Phase 11.0.a routine summaries) is already paid; the
/// rollup is comparatively cheap.
///
/// Quality risk: hierarchical summarisation drops load-bearing nouns
/// when each layer paraphrases its predecessor. Both prompts inline
/// the "do not invent capabilities" rule explicitly because that's the
/// canonical failure mode — Sonnet will happily fabricate plausible-
/// sounding error handling if you don't tell it not to.
/// </summary>
public sealed class HierarchicalRollupPipeline
{
    private const string ModulePromptId = "fortran-doc-module";
    private const string ModulePromptVersion = "v1.0";
    private const string OverviewPromptId = "fortran-doc-overview";
    private const string OverviewPromptVersion = "v1.0";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly string _modulePrompt;
    private readonly string _overviewPrompt;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _apiVersion;
    private readonly ILogger<HierarchicalRollupPipeline> _logger;

    public HierarchicalRollupPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IConfiguration cfg,
        IWebHostEnvironment env,
        ILogger<HierarchicalRollupPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _logger = logger;
        _apiKey = cfg.GetValue<string>("Llm:Anthropic:ApiKey") ?? "";
        _baseUrl = cfg.GetValue<string>("Llm:Anthropic:BaseUrl") ?? "https://api.anthropic.com";
        _apiVersion = cfg.GetValue<string>("Llm:Anthropic:ApiVersion") ?? "2023-06-01";

        _modulePrompt = StripFrontmatter(File.ReadAllText(
            ResolvePromptPath(env.ContentRootPath, "doc-module.v1.md")));
        _overviewPrompt = StripFrontmatter(File.ReadAllText(
            ResolvePromptPath(env.ContentRootPath, "doc-overview.v1.md")));
    }

    /// <summary>Run module rollup. Requires routine-summary sections to exist already.</summary>
    public async Task<(int succeeded, int failed)> RunModuleStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        // Group routine-summary sections by SourceFile (via Subroutine).
        var rows = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "routine-summary"
                     && s.SubroutineId != null)
            .Join(db.Subroutines.Include(sub => sub.SourceFile),
                  section => section.SubroutineId!.Value,
                  sub => sub.Id,
                  (section, sub) => new
                  {
                      section.PayloadJson,
                      RoutineName = sub.Name,
                      SourceFileId = sub.SourceFileId,
                      RelativePath = sub.SourceFile!.RelativePath,
                      sub.LineStart,
                      sub.LineEnd,
                  })
            .ToListAsync(ct);

        var grouped = rows
            .GroupBy(r => r.SourceFileId)
            .Select(g => new
            {
                SourceFileId = g.Key,
                RelativePath = g.First().RelativePath,
                Routines = g.OrderBy(x => x.LineStart).ToList(),
            })
            .ToList();

        // Idempotency: skip files that already have a module-scope DocSection
        // for this SourceVersion, unless force=true.
        var existingModuleNames = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "module"
                     && s.ModuleName != null)
            .Select(s => s.ModuleName!)
            .ToListAsync(ct);
        var existingSet = existingModuleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (force && existingModuleNames.Count > 0)
        {
            await db.DocSections
                .Where(s => s.CorpusId == corpusId
                         && s.SourceVersionId == sourceVersionId
                         && s.SectionKind == "module")
                .ExecuteDeleteAsync(ct);
            existingSet.Clear();
        }

        var todo = grouped.Where(g => !existingSet.Contains(ModuleNameOf(g.RelativePath))).ToList();
        if (todo.Count == 0) return (0, 0);

        var sem = new SemaphoreSlim(Math.Max(1, _docsOpts.MaxConcurrency));
        var failureCount = 0;
        var successCount = 0;
        var lockObj = new object();
        var tasks = new List<Task>();

        foreach (var group in todo)
        {
            var captured = group;
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var moduleName = ModuleNameOf(captured.RelativePath);
                    var routineInputs = captured.Routines
                        .Select(r => new RoutineInput(r.RoutineName, r.PayloadJson, r.LineStart, r.LineEnd))
                        .ToList();
                    var userMessage = BuildModuleUserMessage(moduleName, captured.RelativePath, routineInputs);
                    var (payload, _) = await CallAnthropicAsync(
                        httpFactory, _docsOpts.SonnetModel, _modulePrompt, userMessage,
                        "emit_module_documentation", ModuleToolSchema, ct);

                    using var innerScope = _scopeFactory.CreateScope();
                    var innerDb = innerScope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var section = new DocSection
                    {
                        Id = Guid.NewGuid(),
                        CorpusId = corpusId,
                        SourceVersionId = sourceVersionId,
                        SectionKind = "module",
                        Scope = "module",
                        ModuleName = moduleName,
                        State = "DRAFT",
                        PayloadJson = JsonDocument.Parse(payload),
                        RenderedMarkdown = RenderModuleMarkdown(payload, moduleName),
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
                    _logger.LogWarning(ex, "Module rollup failed for {Path}", captured.RelativePath);
                    lock (lockObj) { failureCount++; }
                }
                finally
                {
                    sem.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
        return (successCount, failureCount);
    }

    /// <summary>Run corpus-overview synthesis. Requires module sections to exist.</summary>
    public async Task<(int succeeded, int failed)> RunOverviewStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        var modules = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "module")
            .OrderBy(s => s.ModuleName)
            .Select(s => new { s.ModuleName, s.PayloadJson })
            .ToListAsync(ct);

        if (modules.Count == 0)
            throw new InvalidOperationException("Cannot synthesise overview before module rollup runs.");

        // Idempotency for overview.
        var existing = await db.DocSections
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "overview")
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            if (!force) return (0, 0);
            db.DocSections.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
        }

        var corpus = await db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == corpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");

        try
        {
            var userMessage = BuildOverviewUserMessage(corpus.Name, modules.Select(m =>
                (m.ModuleName ?? "(unknown)", m.PayloadJson)).ToList());
            var (payload, _) = await CallAnthropicAsync(
                httpFactory, _docsOpts.SonnetModel, _overviewPrompt, userMessage,
                "emit_system_overview", OverviewToolSchema, ct);

            var section = new DocSection
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                SectionKind = "overview",
                Scope = "corpus",
                State = "DRAFT",
                PayloadJson = JsonDocument.Parse(payload),
                RenderedMarkdown = RenderOverviewMarkdown(payload),
                GenerationRunId = runId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.DocSections.Add(section);
            await db.SaveChangesAsync(ct);
            return (1, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Overview synthesis failed for corpus {Corpus}", corpusId);
            return (0, 1);
        }
    }

    private sealed record RoutineInput(string RoutineName, JsonDocument PayloadJson, int LineStart, int LineEnd);

    private static string BuildModuleUserMessage(string moduleName, string filePath,
        IReadOnlyList<RoutineInput> routines)
    {
        var routineArray = new List<object>();
        foreach (var r in routines)
        {
            var root = r.PayloadJson.RootElement;
            var entry = new Dictionary<string, object?>
            {
                ["name"] = r.RoutineName,
                ["summary"] = root.TryGetProperty("summary", out var s) ? s.GetString() : "",
                ["inputs"] = ExtractStringArray(root, "inputs"),
                ["outputs"] = ExtractStringArray(root, "outputs"),
                ["sideEffects"] = ExtractStringArray(root, "sideEffects"),
                ["tier"] = root.TryGetProperty("tier", out var t) ? t.GetString() : "utility",
                ["lineRange"] = $"{r.LineStart}-{r.LineEnd}",
            };
            routineArray.Add(entry);
        }

        return JsonSerializer.Serialize(new
        {
            module_name = moduleName,
            file_path = filePath,
            routine_count = routineArray.Count,
            routines = routineArray,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildOverviewUserMessage(string corpusName,
        IReadOnlyList<(string moduleName, JsonDocument payload)> modules)
    {
        var moduleArray = new List<object>();
        foreach (var (moduleName, payload) in modules)
        {
            var root = payload.RootElement;
            var entry = new Dictionary<string, object?>
            {
                ["moduleName"] = moduleName,
                ["purpose"] = root.TryGetProperty("purpose", out var p) ? p.GetString() : "",
                ["publicSurface"] = ExtractStringArray(root, "publicSurface"),
                ["touchWhen"] = root.TryGetProperty("touchWhen", out var tw) ? tw.GetString() : "",
                ["knownRisks"] = ExtractStringArray(root, "knownRisks"),
            };
            moduleArray.Add(entry);
        }

        return JsonSerializer.Serialize(new
        {
            corpus_name = corpusName,
            module_count = moduleArray.Count,
            modules = moduleArray,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static List<string> ExtractStringArray(JsonElement root, string prop)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? "");
        return list;
    }

    private static string ModuleNameOf(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath);

    private static string RenderModuleMarkdown(string payloadJson, string moduleName)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var purpose = root.TryGetProperty("purpose", out var p) ? p.GetString() : "";
        var touch = root.TryGetProperty("touchWhen", out var tw) ? tw.GetString() : null;
        var sb = new StringBuilder();
        sb.Append("## Module: ").Append(moduleName).Append("\n\n");
        // The model's purpose prose may carry its own headings; push them below
        // the module title so they nest as sub-sections instead of colliding
        // with it. Note groups sit at the same level (###) as those subheads.
        sb.Append(DemotePurposeHeadings(purpose ?? "")).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(touch))
            sb.Append("> **When to edit:** ").Append(touch).Append("\n\n");
        AppendModuleCallouts(sb, root, "architecturalNotes", "### Architectural Notes");
        AppendPublicSurface(sb, root);
        AppendModuleCallouts(sb, root, "knownRisks", "### Known Risks");
        return sb.ToString();
    }

    // Push a purpose paragraph's headings so the shallowest becomes H3 (below
    // the ## module title), preserving relative depth. Fenced code is skipped.
    private static string DemotePurposeHeadings(string purpose, int floor = 3)
    {
        var lines = purpose.Replace("\r\n", "\n").Split('\n');
        var inFence = false;
        var min = int.MaxValue;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            { inFence = !inFence; continue; }
            if (inFence) continue;
            var h = HeadingDepth(line);
            if (h > 0) min = Math.Min(min, h);
        }
        if (min == int.MaxValue) return purpose;   // no headings
        var delta = floor - min;
        if (delta <= 0) return purpose;            // already deep enough

        var sb = new StringBuilder();
        inFence = false;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
            { inFence = !inFence; sb.Append(line).Append('\n'); continue; }
            var h = inFence ? 0 : HeadingDepth(line);
            if (h > 0)
                sb.Append(new string('#', Math.Min(h + delta, 6))).Append(line[h..]).Append('\n');
            else
                sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    // ATX heading depth if the line is a heading (#..###### followed by space),
    // else 0.
    private static int HeadingDepth(string line)
    {
        var h = 0;
        while (h < line.Length && line[h] == '#') h++;
        return h is >= 1 and <= 6 && h < line.Length && line[h] == ' ' ? h : 0;
    }

    private static void AppendPublicSurface(StringBuilder sb, JsonElement root)
    {
        if (!root.TryGetProperty("publicSurface", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return;
        sb.Append("### Public API\n\n");
        sb.Append("| Routine |\n");
        sb.Append("|---------|\n");
        foreach (var item in arr.EnumerateArray())
            sb.Append("| `").Append(item.GetString()).Append("` |\n");
        sb.Append('\n');
    }

    private static void AppendModuleCallouts(StringBuilder sb, JsonElement root, string prop, string heading)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return;
        sb.Append(heading).Append("\n\n");
        foreach (var item in arr.EnumerateArray())
            sb.Append("> ").Append(item.GetString()).Append('\n');
        sb.Append('\n');
    }

    private static string RenderOverviewMarkdown(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "System overview" : "System overview";
        var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
        // The overview prompt asks the model to put the title at the top of
        // the markdown body itself. If it complied we just return summary;
        // otherwise we add a title heading. Sniff the first non-blank line
        // for a top-level heading (`# `) — present means the model handled
        // it; absent means we prepend our own.
        var trimmed = summary.TrimStart();
        if (trimmed.StartsWith("# "))
            return summary.EndsWith('\n') ? summary : summary + "\n";
        return $"# {title}\n\n{summary}\n";
    }

    // Structured output via tool use. Forcing the model to answer through a
    // tool whose input_schema we define means the Anthropic API assembles the
    // JSON — an unescaped quote or control character inside a string value
    // becomes structurally impossible, unlike hand-written JSON in a text block
    // (which reliably broke on content-heavy modules such as order-post.p /
    // AccountController). The tool_use block's `input` is always valid JSON.
    private async Task<(string payload, TokenUsage usage)> CallAnthropicAsync(
        IHttpClientFactory httpFactory, string model, string systemPrompt, string userMessage,
        string toolName, object inputSchema, CancellationToken ct)
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
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = toolName,
                    ["description"] = "Emit the documentation section as structured fields.",
                    ["input_schema"] = inputSchema,
                },
            },
            // Pin the answer to the tool so every response is a tool_use block
            // whose `input` the API has already validated as JSON.
            ["tool_choice"] = new Dictionary<string, object?>
            {
                ["type"] = "tool",
                ["name"] = toolName,
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

        // The tool_use block's `input` is API-assembled JSON — extract it verbatim.
        string? payload = null;
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use"
                && block.TryGetProperty("input", out var input))
            {
                payload = input.GetRawText();
                break;
            }
        }
        if (payload is null)
            throw new InvalidOperationException(
                "No tool_use block in Anthropic response: " + Truncate(bodyText, 300));

        var usage = root.GetProperty("usage");
        int cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0;
        int cacheCreate = usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0;
        return (payload, new TokenUsage(
            usage.GetProperty("input_tokens").GetInt32(),
            usage.GetProperty("output_tokens").GetInt32(),
            cacheRead, cacheCreate));
    }

    // ── Tool input schemas (structured-output contracts) ─────────────────
    // Mirror the shapes the doc-module / doc-overview prompts describe. Optional
    // fields stay out of "required" so a module with no known risks still
    // validates; the Render* methods read fields defensively with TryGetProperty.

    private static Dictionary<string, object?> StringArray(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
    };

    private static Dictionary<string, object?> CitationsArray() => new()
    {
        ["type"] = "array",
        ["items"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["lines"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
        },
    };

    private static readonly object ModuleToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["moduleName"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["purpose"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "300–600 words of markdown covering the module's role, the abstraction it provides, its call-graph position, algorithmic strategy, and performance characteristics.",
            },
            ["architecturalNotes"] = StringArray("Design-level observations visible only at module scope; one sentence each."),
            ["publicSurface"] = StringArray("Names of the routines callable from outside the module."),
            ["touchWhen"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["knownRisks"] = StringArray("Risks grounded in the per-routine summaries."),
            ["citations"] = CitationsArray(),
        },
        ["required"] = new[] { "moduleName", "purpose", "publicSurface" },
    };

    private static readonly object OverviewToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["id"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["title"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["summary"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "2–5 pages of markdown: an elevator-pitch first paragraph, subsystem sections, and the load-bearing concepts a new engineer must internalise.",
            },
            ["subsystems"] = StringArray("Titles of the subsystem sections produced in the summary markdown."),
            ["citations"] = CitationsArray(),
        },
        ["required"] = new[] { "title", "summary" },
    };

    private static string StripFrontmatter(string md)
    {
        if (!md.StartsWith("---")) return md;
        var endIdx = md.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIdx < 0) return md;
        var after = endIdx + 4;
        while (after < md.Length && (md[after] == '\n' || md[after] == '\r')) after++;
        return md[after..];
    }

    private static string ResolvePromptPath(string contentRoot, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Llm", "Prompts", "fortran-f77", fileName),
            Path.Combine(contentRoot, "..", "..", "Llm", "Prompts", "fortran-f77", fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return Path.GetFullPath(c);
        throw new FileNotFoundException($"Prompt {fileName} not found. Searched: {string.Join(", ", candidates)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private record TokenUsage(int input, int output, int cacheRead, int cacheCreate);
}
