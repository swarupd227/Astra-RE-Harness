using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Module and overview rollups on top of the routine-summary sections.
///
/// WS6 rewrite — the model writes the document:
///
///   * **Module.** For each source file, the writer receives every routine's
///     full summary (no truncation) AND its line-numbered source slice
///     (<see cref="DocsOptions.ModuleSourceLinesPerRoutine"/> lines max),
///     fitted under <see cref="DocsOptions.InputBudgetTokens"/> by dropping
///     source from the least important routines first, then whole routines
///     — the message names what was left out. The answer is a
///     <c>{markdown, meta}</c> envelope; C# ensures the title, runs the
///     critic loop, injects the dependency diagram when one exists, and
///     stores the markdown verbatim. Files that contain a headline routine
///     are written on the Opus tier.
///   * **Overview.** The writer receives every module document in full
///     (degrading the smallest modules to their summary, then omitting them
///     by name, under the same budget) plus each module's meta. Written on
///     the Opus tier — it is the most-read page. The old builder dropped
///     <c>architecturalNotes</c> on the floor and truncated purpose text;
///     nothing is truncated now.
///
/// Every call goes through <see cref="IDocWriter"/> (retrying send, rate
/// limiter, LlmCall row) and the result carries a <c>QualityJson</c> record.
/// </summary>
public sealed class HierarchicalRollupPipeline
{
    public const string ModulePromptId = "fortran-doc-module";
    public const string ModulePromptVersion = "v3.0";
    public const string OverviewPromptId = "fortran-doc-overview";
    public const string OverviewPromptVersion = "v2.0";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly DocPromptAssets _assets;
    private readonly IDocWriter _writer;
    private readonly DocCriticPass _critic;
    private readonly string _modulePrompt;
    private readonly string _overviewPrompt;
    private readonly ILogger<HierarchicalRollupPipeline> _logger;

    public HierarchicalRollupPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IWebHostEnvironment env,
        DocPromptAssets assets,
        IDocWriter writer,
        DocCriticPass critic,
        ILogger<HierarchicalRollupPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _assets = assets;
        _writer = writer;
        _critic = critic;
        _logger = logger;
        _modulePrompt = DocPromptAssets.ReadPrompt(env.ContentRootPath, "fortran-f77", "doc-module.v1.md");
        _overviewPrompt = DocPromptAssets.ReadPrompt(env.ContentRootPath, "fortran-f77", "doc-overview.v1.md");
    }

    // ── Prompt input shapes (property names are what the prompts document) ──

    public sealed record RoutineInput(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("lineRange")] string LineRange,
        [property: JsonPropertyName("tier")] string Tier,
        [property: JsonPropertyName("callers")] IReadOnlyList<string> Callers,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("inputs")] IReadOnlyList<string> Inputs,
        [property: JsonPropertyName("outputs")] IReadOnlyList<string> Outputs,
        [property: JsonPropertyName("sideEffects")] IReadOnlyList<string> SideEffects,
        [property: JsonPropertyName("preconditions")] IReadOnlyList<string> Preconditions,
        [property: JsonPropertyName("edgeCases")] IReadOnlyList<string> EdgeCases,
        [property: JsonPropertyName("source")] string? Source);

    public sealed record ModuleInput(
        [property: JsonPropertyName("moduleName")] string ModuleName,
        [property: JsonPropertyName("filePath")] string? FilePath,
        [property: JsonPropertyName("routineCount")] int RoutineCount,
        [property: JsonPropertyName("headlineRoutines")] int HeadlineRoutines,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("publicSurface")] IReadOnlyList<string> PublicSurface,
        [property: JsonPropertyName("architecturalNotes")] IReadOnlyList<string> ArchitecturalNotes,
        [property: JsonPropertyName("knownRisks")] IReadOnlyList<string> KnownRisks,
        [property: JsonPropertyName("touchWhen")] string? TouchWhen,
        [property: JsonPropertyName("markdown")] string? Markdown);

    private sealed record RoutineRow(
        Guid SubroutineId, string Name, JsonDocument Payload,
        Guid SourceFileId, string RelativePath, string BlobUri, int LineStart, int LineEnd);

    private sealed record ModuleGroup(Guid SourceFileId, string RelativePath, string BlobUri, List<RoutineRow> Routines);

    // ── Module stage ────────────────────────────────────────────────────

    /// <summary>Run module rollup. Requires routine-summary sections to exist already.</summary>
    public async Task<(int succeeded, int failed)> RunModuleStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobClient>();
        var classifier = scope.ServiceProvider.GetRequiredService<RoutineTierClassifier>();

        var rows = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "routine-summary"
                     && s.SubroutineId != null)
            .Join(db.Subroutines,
                  section => section.SubroutineId!.Value,
                  sub => sub.Id,
                  (section, sub) => new
                  {
                      section.PayloadJson,
                      SubroutineId = sub.Id,
                      RoutineName = sub.Name,
                      sub.SourceFileId,
                      RelativePath = sub.SourceFile!.RelativePath,
                      BlobUri = sub.SourceFile!.BlobUri,
                      sub.LineStart,
                      sub.LineEnd,
                  })
            .ToListAsync(ct);

        var grouped = rows
            .GroupBy(r => r.SourceFileId)
            .Select(g => new ModuleGroup(
                g.Key,
                g.First().RelativePath,
                g.First().BlobUri,
                g.OrderBy(x => x.LineStart)
                 .Select(x => new RoutineRow(x.SubroutineId, x.RoutineName, x.PayloadJson, x.SourceFileId, x.RelativePath, x.BlobUri, x.LineStart, x.LineEnd))
                 .ToList()))
            .ToList();

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

        var tiers = await classifier.ClassifyForCorpusAsync(corpusId, sourceVersionId, ct);
        var index = await DocCitations.CorpusIndex.LoadAsync(db, sourceVersionId, ct);
        var diagrams = await LoadDependencyDiagramsAsync(db, corpusId, sourceVersionId, ct);

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
                    await WriteModuleAsync(captured, tiers, index, diagrams, blob, runId, corpusId, sourceVersionId, ct);
                    lock (lockObj) { successCount++; }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Module document failed for {Path}", captured.RelativePath);
                    lock (lockObj) { failureCount++; }
                }
                finally
                {
                    sem.Release();
                }
            }, ct));
        }
        await Task.WhenAll(tasks);
        return (successCount, failureCount);
    }

    private async Task WriteModuleAsync(
        ModuleGroup group,
        IReadOnlyDictionary<Guid, RoutineTierClassifier.TierAssignment> tiers,
        DocCitations.CorpusIndex index,
        IReadOnlyDictionary<string, string> diagrams,
        IBlobClient blob,
        Guid runId, Guid corpusId, Guid sourceVersionId,
        CancellationToken ct)
    {
        var moduleName = ModuleNameOf(group.RelativePath);

        string[]? lines = null;
        try
        {
            lines = DocSourceSlices.SplitLines(await blob.GetTextAsync(group.BlobUri, ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source unavailable for {Path}; module document written from summaries only", group.RelativePath);
        }

        var cap = Math.Max(20, _docsOpts.ModuleSourceLinesPerRoutine);
        var items = new List<DocInputBudget.Item<RoutineInput>>(group.Routines.Count);
        var hasHeadline = false;
        foreach (var r in group.Routines)
        {
            var root = r.Payload.RootElement;
            var assignment = tiers.TryGetValue(r.SubroutineId, out var ta) ? ta : null;
            var tier = Str(root, "tier") ?? assignment?.Tier ?? "standard";
            hasHeadline |= tier == "headline";
            var slice = lines is null ? null : DocSourceSlices.Slice(lines, r.LineStart, r.LineEnd, cap);
            var input = new RoutineInput(
                r.Name, group.RelativePath, $"{r.LineStart}-{r.LineEnd}", tier,
                (assignment?.CallerNames ?? Array.Empty<string>()).Take(10).ToList(),
                Str(root, "summary") ?? "",
                Arr(root, "inputs"), Arr(root, "outputs"), Arr(root, "sideEffects"),
                Arr(root, "preconditions"), Arr(root, "edgeCases"),
                string.IsNullOrWhiteSpace(slice) ? null : slice);
            var coreChars = JsonSerializer.Serialize(input with { Source = null }, JsonOpts).Length;
            items.Add(new DocInputBudget.Item<RoutineInput>(
                input, tier, Math.Max(1, r.LineEnd - r.LineStart + 1), coreChars, input.Source?.Length ?? 0));
        }

        var fit = DocInputBudget.Fit(items, _docsOpts.InputBudgetTokens, fixedChars: 1_000);
        if (fit.Trimmed)
            _logger.LogInformation(
                "Module {Module}: input budget trimmed {Extras} source slice(s) and dropped {Dropped} routine(s)",
                moduleName, fit.ExtrasDropped, fit.Dropped);

        var routines = fit.Kept
            .Select(p => p.IncludeExtra ? p.Value : p.Value with { Source = null })
            .ToList();
        var userMessage = JsonSerializer.Serialize(new
        {
            module_name = moduleName,
            file_path = group.RelativePath,
            file_line_count = lines?.Length ?? 0,
            routine_count = group.Routines.Count,
            routines,
            omitted = new
            {
                source_removed = fit.ExtrasDropped,
                dropped_routines = fit.DroppedValues.Select(v => v.Name).ToList(),
            },
        }, JsonOpts);

        var request = new DocWriteRequest(
            ModulePromptId, ModulePromptVersion,
            _assets.SystemBlocks(_modulePrompt, "module"),
            userMessage,
            "emit_module_document", "Emit the module document as markdown plus its structured meta.",
            ModuleToolSchema,
            hasHeadline ? _docsOpts.OpusModel : _docsOpts.SonnetModel,
            _docsOpts.ModuleMaxOutputTokens,
            "docs:module:" + ModulePromptVersion,
            () => MockModuleEnvelope(moduleName, group.RelativePath, group.Routines));

        var result = await _writer.WriteAsync(request, ct);
        var envelope = DocEnvelope.Parse(result.ToolInputJson);
        var title = envelope.MetaString("title") is { Length: > 0 } t ? t : $"{moduleName} — module";
        envelope = envelope.WithMarkdown(DocMarkdown.EnsureTitle(envelope.Markdown, title, 1));

        var review = await _critic.ReviewAsync(new DocCriticPass.Input(
            "module", title, envelope, request, index,
            group.Routines.Select(r => r.Name).ToList(),
            IsCatalog: false, RequireCitations: true), ct);

        var final = review.Envelope;
        title = final.MetaString("title") is { Length: > 0 } t2 ? t2 : title;
        final = final.WithMarkdown(DocMarkdown.EnsureTitle(final.Markdown, title, 1));
        var rendered = InjectDependencyDiagram(final.Markdown, moduleName, diagrams);

        using var innerScope = _scopeFactory.CreateScope();
        var innerDb = innerScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var writerCall = DocLlmCalls.Record(innerDb, _writer, request, result);
        DocLlmCalls.RecordAll(innerDb, _writer, review.Calls);

        var extras = new Dictionary<string, object?>
        {
            ["moduleName"] = moduleName,
            ["filePath"] = group.RelativePath,
            ["title"] = title,
            ["routineCount"] = group.Routines.Count,
        };
        var section = new DocSection
        {
            Id = Guid.NewGuid(),
            CorpusId = corpusId,
            SourceVersionId = sourceVersionId,
            SectionKind = "module",
            Scope = "module",
            ModuleName = moduleName,
            State = "DRAFT",
            PayloadJson = JsonDocument.Parse(final.ToPayloadJson(extras)),
            RenderedMarkdown = rendered,
            LlmCallId = writerCall.Id,
            QualityJson = review.Report.ToJson(),
            GenerationRunId = runId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        innerDb.DocSections.Add(section);
        await innerDb.SaveChangesAsync(ct);
    }

    // ── Overview stage ──────────────────────────────────────────────────

    /// <summary>Run corpus-overview synthesis. Requires module sections to exist.</summary>
    public async Task<(int succeeded, int failed)> RunOverviewStageAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var classifier = scope.ServiceProvider.GetRequiredService<RoutineTierClassifier>();

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
            var subPaths = await db.Subroutines
                .AsNoTracking()
                .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
                .Select(s => new { s.Id, Path = s.SourceFile!.RelativePath })
                .ToListAsync(ct);
            var tiers = await classifier.ClassifyForCorpusAsync(corpusId, sourceVersionId, ct);
            var stats = subPaths
                .GroupBy(s => ModuleNameOf(s.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (Path: g.First().Path, Count: g.Count(),
                          Headline: g.Count(s => tiers.TryGetValue(s.Id, out var t) && t.Tier == "headline")),
                    StringComparer.OrdinalIgnoreCase);
            var index = await DocCitations.CorpusIndex.LoadAsync(db, sourceVersionId, ct);

            var items = new List<DocInputBudget.Item<ModuleInput>>(modules.Count);
            foreach (var m in modules)
            {
                var name = m.ModuleName ?? "(unknown)";
                var root = m.PayloadJson.RootElement;
                var markdown = Str(root, "markdown") ?? Str(root, "purpose") ?? "";
                var summary = Str(root, "summary") ?? DocMarkdown.FirstParagraph(markdown);
                stats.TryGetValue(name, out var st);
                var input = new ModuleInput(
                    name, Str(root, "filePath") ?? st.Path, st.Count, st.Headline, summary,
                    Arr(root, "publicSurface"), Arr(root, "architecturalNotes"), Arr(root, "knownRisks"),
                    Str(root, "touchWhen"),
                    string.IsNullOrWhiteSpace(markdown) ? null : markdown);
                var coreChars = JsonSerializer.Serialize(input with { Markdown = null }, JsonOpts).Length;
                items.Add(new DocInputBudget.Item<ModuleInput>(
                    input, st.Headline > 0 ? "headline" : "standard", Math.Max(1, st.Count), coreChars, input.Markdown?.Length ?? 0));
            }

            var fit = DocInputBudget.Fit(items, _docsOpts.InputBudgetTokens, fixedChars: 1_000);
            if (fit.Trimmed)
                _logger.LogInformation(
                    "Overview for {Corpus}: input budget degraded {Extras} module(s) to summary and omitted {Dropped}",
                    corpus.Name, fit.ExtrasDropped, fit.Dropped);

            var moduleInputs = fit.Kept
                .Select(p => p.IncludeExtra ? p.Value : p.Value with { Markdown = null })
                .ToList();
            var userMessage = JsonSerializer.Serialize(new
            {
                corpus_name = corpus.Name,
                module_count = modules.Count,
                routine_count = subPaths.Count,
                modules = moduleInputs,
                omitted_modules = fit.DroppedValues.Select(v => v.ModuleName).ToList(),
            }, JsonOpts);

            var request = new DocWriteRequest(
                OverviewPromptId, OverviewPromptVersion,
                _assets.SystemBlocks(_overviewPrompt, "overview"),
                userMessage,
                "emit_system_overview", "Emit the system overview as markdown plus its structured meta.",
                OverviewToolSchema,
                _docsOpts.OpusModel,
                _docsOpts.OverviewMaxOutputTokens,
                "docs:overview:" + OverviewPromptVersion,
                () => MockOverviewEnvelope(corpus.Name, moduleInputs, index));

            var result = await _writer.WriteAsync(request, ct);
            var envelope = DocEnvelope.Parse(result.ToolInputJson);
            var title = envelope.MetaString("title") is { Length: > 0 } t ? t : corpus.Name;
            envelope = envelope.WithMarkdown(DocMarkdown.EnsureTitle(envelope.Markdown, title, 1));

            var review = await _critic.ReviewAsync(new DocCriticPass.Input(
                "overview", title, envelope, request, index,
                Array.Empty<string>(), IsCatalog: false, RequireCitations: true), ct);

            var final = review.Envelope;
            title = final.MetaString("title") is { Length: > 0 } t2 ? t2 : title;
            final = final.WithMarkdown(DocMarkdown.EnsureTitle(final.Markdown, title, 1));

            var writerCall = DocLlmCalls.Record(db, _writer, request, result);
            DocLlmCalls.RecordAll(db, _writer, review.Calls);

            var extras = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["corpusName"] = corpus.Name,
                ["moduleCount"] = modules.Count,
            };
            var section = new DocSection
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                SectionKind = "overview",
                Scope = "corpus",
                State = "DRAFT",
                PayloadJson = JsonDocument.Parse(final.ToPayloadJson(extras)),
                RenderedMarkdown = final.Markdown,
                LlmCallId = writerCall.Id,
                QualityJson = review.Report.ToJson(),
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

    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<IReadOnlyDictionary<string, string>> LoadDependencyDiagramsAsync(
        AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        var rows = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "diagram"
                     && s.ModuleName != null)
            .Select(s => new { s.ModuleName, s.PayloadJson })
            .ToListAsync(ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var root = r.PayloadJson.RootElement;
            if (Str(root, "diagramKind") != "dependency") continue;
            var mermaid = Str(root, "mermaidSource");
            if (!string.IsNullOrWhiteSpace(mermaid)) map[r.ModuleName!] = mermaid;
        }
        return map;
    }

    /// <summary>Append the module's deterministic dependency diagram (a
    /// MermaidBlock payload the frontend already renders) when one exists
    /// and the writer did not emit a diagram of its own.</summary>
    public static string InjectDependencyDiagram(string markdown, string moduleName, IReadOnlyDictionary<string, string> diagrams)
    {
        if (!diagrams.TryGetValue(moduleName, out var mermaid) || string.IsNullOrWhiteSpace(mermaid)) return markdown;
        if (markdown.Contains("```mermaid", StringComparison.Ordinal)) return markdown;
        return DocMarkdown.AppendSection(markdown, "## Call structure", "```mermaid\n" + mermaid.Trim() + "\n```");
    }

    private static string ModuleNameOf(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath);

    private static string? Str(JsonElement root, string prop) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string> Arr(JsonElement root, string prop)
    {
        var list = new List<string>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }

    // ── Mock envelopes (Docs__Generator__Provider=mock) ─────────────────

    private static string MockModuleEnvelope(string moduleName, string path, IReadOnlyList<RoutineRow> routines)
    {
        var names = routines.Select(r => r.Name).ToList();
        var map = string.Join("\n", routines.Select(r =>
            $"- `{r.Name}` — routine at lines {r.LineStart}–{r.LineEnd} — {DocCitations.Format(path, r.LineStart, r.LineEnd)}"));
        var markdown =
            $"# {moduleName} — module (mock)\n\n" +
            $"`{path}` holds {routines.Count} routine(s). This document was produced offline by the mock documentation provider; it records structure only and interprets no source.\n\n" +
            "## What it provides\n\n" +
            $"The file exposes {string.Join(", ", names.Select(n => "`" + n + "`"))}.\n\n" +
            "## Routine map\n\n" + map + "\n";
        return JsonSerializer.Serialize(new
        {
            markdown,
            meta = new
            {
                title = $"{moduleName} — module (mock)",
                summary = $"{path} holds {routines.Count} routine(s); documented offline by the mock provider.",
                publicSurface = names,
                architecturalNotes = Array.Empty<string>(),
                knownRisks = Array.Empty<string>(),
                touchWhen = $"When any routine in {path} changes.",
                citations = routines.Select(r => new { path, lines = $"{r.LineStart}-{r.LineEnd}" }).ToList(),
                sections = new[] { "What it provides", "Routine map" },
            },
        });
    }

    private static string MockOverviewEnvelope(string corpusName, IReadOnlyList<ModuleInput> modules, DocCitations.CorpusIndex index)
    {
        var firstPath = modules.FirstOrDefault()?.FilePath;
        var span = firstPath is null ? null : index.RoutinesIn(firstPath).FirstOrDefault();
        var citation = span is null ? "" : " " + DocCitations.Format(span.Path, span.LineStart, span.LineEnd);
        var pitch = $"{corpusName} is a corpus of {modules.Count} module(s) documented offline by the mock provider; no source was interpreted.";
        var markdown =
            $"# {corpusName}\n\n{pitch}\n\n" +
            "## What the system does\n\n" +
            "The corpus contains the modules listed under Subsystems; their purpose is unknown until a model-backed run replaces this placeholder.\n\n" +
            "## Subsystems\n\n### All modules\n\n" +
            string.Join(", ", modules.Select(m => "`" + m.ModuleName + "`")) +
            (firstPath is null ? ".\n\n" : $". Read first: `{firstPath}`.\n\n") +
            "## Load-bearing concepts\n\n" +
            $"Every routine is cited by path and line range in its module document{citation}.\n";
        return JsonSerializer.Serialize(new
        {
            markdown,
            meta = new
            {
                title = corpusName,
                summary = pitch,
                subsystems = new[] { "All modules" },
                citations = span is null
                    ? Array.Empty<object>()
                    : new object[] { new { path = span.Path, lines = $"{span.LineStart}-{span.LineEnd}" } },
                sections = new[] { "What the system does", "Subsystems", "Load-bearing concepts" },
            },
        });
    }

    // ── Tool input schemas (structured-output contracts) ────────────────

    private static Dictionary<string, object?> StringArray(string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
    };

    private static Dictionary<string, object?> CitationsArray() => new()
    {
        ["type"] = "array",
        ["description"] = "Every [path:L…] citation used in the markdown.",
        ["items"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["required"] = new[] { "path", "lines" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["lines"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "\"<start>-<end>\"" },
            },
        },
    };

    public static readonly object ModuleToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["required"] = new[] { "markdown", "meta" },
        ["properties"] = new Dictionary<string, object?>
        {
            ["markdown"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The complete module document in markdown, starting with the # title. Length follows content.",
            },
            ["meta"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["required"] = new[] { "title", "summary", "publicSurface" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["title"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["summary"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "One or two sentences for lists and cards." },
                    ["publicSurface"] = StringArray("Routine names an outside caller uses."),
                    ["architecturalNotes"] = StringArray("Design observations visible only at file scope, one sentence each."),
                    ["knownRisks"] = StringArray("The bullets of Risks and traps, one sentence each."),
                    ["touchWhen"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["citations"] = CitationsArray(),
                    ["sections"] = StringArray("The ## headings, in order."),
                },
            },
        },
    };

    public static readonly object OverviewToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["required"] = new[] { "markdown", "meta" },
        ["properties"] = new Dictionary<string, object?>
        {
            ["markdown"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The complete system overview in markdown, starting with the # title. Length follows content.",
            },
            ["meta"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["required"] = new[] { "title", "summary", "subsystems" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["title"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["summary"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The pitch paragraph, verbatim." },
                    ["subsystems"] = StringArray("The ### subsystem titles exactly as written, in order."),
                    ["citations"] = CitationsArray(),
                    ["sections"] = StringArray("The ## headings, in order."),
                },
            },
        },
    };
}
