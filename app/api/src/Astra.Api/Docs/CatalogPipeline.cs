using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Cross-cutting catalogs — data-dictionary, glossary, interface,
/// business-rules — and the Phase B requirements pack (capability-map,
/// functional-requirement, process-flow, nfr). One writer call per stage,
/// N DocSection rows out (one per entry).
///
/// WS6 changes:
///   * **No pre-model truncation.** Every stage sends full routine
///     summaries (inputs, outputs, side effects, preconditions, edge cases,
///     path, line range). When a message would exceed the input budget,
///     whole routines are dropped — lowest tier, smallest first — and the
///     message says which; nothing is chopped to 140 characters.
///   * **Business rules see source.** Line-numbered slices go in under the
///     same budget, headline routines first.
///   * **Model-authored prose.** Business rules and the four requirements
///     kinds come back as <c>{entries:[{markdown, meta}]}</c>; the entry's
///     markdown is stored as written (C# only frames the numbered heading
///     so FR-045 follows FR-044 across gap-fill passes) and the meta keeps
///     the fields the coverage and delivery reports read. Data dictionary,
///     glossary and interface stay structured reference catalogs.
///   * Deterministic quality checks per entry (citations resolved for
///     business rules, banned phrases, empty sections) land in QualityJson.
///
/// An EMPTY catalog remains a valid output: math libraries have no business
/// rules; pure-compute corpora have no external interfaces.
/// </summary>
public sealed class CatalogPipeline
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Kinds whose entries the model writes as {markdown, meta}.</summary>
    private static readonly HashSet<string> ProseKinds = new(StringComparer.Ordinal)
    {
        "business-rule", "capability-map", "functional-requirement", "process-flow", "nfr",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly DocPromptAssets _assets;
    private readonly IDocWriter _writer;
    private readonly ILogger<CatalogPipeline> _logger;

    private readonly string _dataDictionaryPrompt;
    private readonly string _glossaryPrompt;
    private readonly string _interfacePrompt;
    private readonly string _businessRulesPrompt;
    private readonly string _capabilityMapPrompt;
    private readonly string _functionalPrompt;
    private readonly string _processFlowPrompt;
    private readonly string _nfrPrompt;

    public CatalogPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IWebHostEnvironment env,
        DocPromptAssets assets,
        IDocWriter writer,
        ILogger<CatalogPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _assets = assets;
        _writer = writer;
        _logger = logger;

        var root = env.ContentRootPath;
        _dataDictionaryPrompt = DocPromptAssets.ReadPrompt(root, "fortran-f77", "doc-data-dictionary.v1.md");
        _glossaryPrompt = DocPromptAssets.ReadPrompt(root, "fortran-f77", "doc-glossary.v1.md");
        _interfacePrompt = DocPromptAssets.ReadPrompt(root, "fortran-f77", "doc-interface.v1.md");
        _businessRulesPrompt = DocPromptAssets.ReadPrompt(root, "fortran-f77", "doc-business-rules.v1.md");
        // Phase B — requirements pack. Language-agnostic: these synthesise
        // from already-extracted docs, so they live in their own folder.
        _capabilityMapPrompt = DocPromptAssets.ReadPrompt(root, "requirements", "req-capability-map.v1.md");
        _functionalPrompt = DocPromptAssets.ReadPrompt(root, "requirements", "req-functional.v1.md");
        _processFlowPrompt = DocPromptAssets.ReadPrompt(root, "requirements", "req-process-flow.v1.md");
        _nfrPrompt = DocPromptAssets.ReadPrompt(root, "requirements", "req-nfr.v1.md");
    }

    public sealed record StageOutcome(int Entries, int Failed);

    private sealed record Built(string UserMessage, Func<string> MockPayload);

    // ── Stages ──────────────────────────────────────────────────────────

    public Task<StageOutcome> RunDataDictionaryAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("data-dictionary", _dataDictionaryPrompt, null, async (db, _, ct2) =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct2);
            var commonBlocks = await LoadCommonBlocksAsync(db, sourceVersionId, ct2);
            var corpusName = await GetCorpusNameAsync(db, corpusId, ct2);
            var fixedChars = JsonSerializer.Serialize(new { corpus_name = corpusName, common_blocks = commonBlocks }).Length;
            var (list, omitted) = FitRoutines(routines, null, fixedChars, r => new Dictionary<string, object?>
            {
                ["name"] = r.Name, ["path"] = r.Path, ["lineRange"] = r.LineRange, ["summary"] = r.Summary,
                ["inputs"] = r.Inputs, ["outputs"] = r.Outputs, ["sideEffects"] = r.SideEffects,
            });
            var user = JsonSerializer.Serialize(new
            {
                corpus_name = corpusName,
                routine_count = routines.Count,
                routine_summaries = list,
                common_blocks = commonBlocks,
                omitted,
            }, JsonOpts);
            return new Built(user, () => MockStructuredEntries("data-dictionary", routines));
        }, runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunGlossaryAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("glossary", _glossaryPrompt, null, async (db, _, ct2) =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct2);
            var modules = await LoadModuleSummariesAsync(db, corpusId, sourceVersionId, ct2);
            var corpusName = await GetCorpusNameAsync(db, corpusId, ct2);
            var moduleInputs = ModulesWithinBudget(modules, fraction: 0.4);
            var fixedChars = JsonSerializer.Serialize(new { corpus_name = corpusName, module_summaries = moduleInputs }, JsonOpts).Length;
            var (list, omitted) = FitRoutines(routines, null, fixedChars, r => new Dictionary<string, object?>
            {
                ["name"] = r.Name, ["summary"] = r.Summary,
            });
            var user = JsonSerializer.Serialize(new
            {
                corpus_name = corpusName,
                routine_summaries = list,
                module_summaries = moduleInputs,
                omitted,
            }, JsonOpts);
            return new Built(user, () => MockStructuredEntries("glossary", routines));
        }, runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunInterfaceAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("interface", _interfacePrompt, null, async (db, _, ct2) =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct2);
            var corpusName = await GetCorpusNameAsync(db, corpusId, ct2);
            var fixedChars = JsonSerializer.Serialize(new { corpus_name = corpusName }).Length;
            var (list, omitted) = FitRoutines(routines, null, fixedChars, r => new Dictionary<string, object?>
            {
                ["name"] = r.Name, ["summary"] = r.Summary, ["sideEffects"] = r.SideEffects, ["ioPatterns"] = r.IoPatterns,
            });
            var user = JsonSerializer.Serialize(new
            {
                corpus_name = corpusName,
                routine_summaries = list,
                omitted,
            }, JsonOpts);
            return new Built(user, () => MockStructuredEntries("interface", routines));
        }, runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunBusinessRulesAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("business-rule", _businessRulesPrompt, "business-rule", async (db, sp, ct2) =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct2);
            var corpusName = await GetCorpusNameAsync(db, corpusId, ct2);
            var blob = sp.GetRequiredService<IBlobClient>();
            var sources = await LoadSourceSlicesAsync(blob, routines, ct2);
            var fixedChars = JsonSerializer.Serialize(new { corpus_name = corpusName }).Length;
            var (list, omitted) = FitRoutines(routines, sources, fixedChars, r => new Dictionary<string, object?>
            {
                ["name"] = r.Name, ["path"] = r.Path, ["lineRange"] = r.LineRange, ["tier"] = r.Tier,
                ["summary"] = r.Summary, ["inputs"] = r.Inputs, ["outputs"] = r.Outputs, ["sideEffects"] = r.SideEffects,
                ["preconditions"] = r.Preconditions, ["edgeCases"] = r.EdgeCases,
            });
            var user = JsonSerializer.Serialize(new
            {
                corpus_name = corpusName,
                routine_count = routines.Count,
                routines = list,
                omitted,
            }, JsonOpts);
            return new Built(user, () => MockProseEntries("business-rule", routines));
        }, runId, corpusId, sourceVersionId, force, ct);

    // ── Phase B: requirements pack (AS-IS) ───────────────────────────

    public Task<StageOutcome> RunCapabilityMapAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("capability-map", _capabilityMapPrompt, null, (db, _, ct2) =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, "capability-map", ct2,
                includeRules: false, includeInterfaces: false, includeSideEffects: false),
            runId, corpusId, sourceVersionId, force, ct);

    public async Task<StageOutcome> RunFunctionalRequirementsAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        var first = await RunCatalogAsync("functional-requirement", _functionalPrompt, null, (db, _, ct2) =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, "functional-requirement", ct2,
                includeRules: true, includeInterfaces: false, includeSideEffects: false,
                includeCapabilities: true),
            runId, corpusId, sourceVersionId, force, ct);

        // A failed first pass leaves nothing coherent to top up.
        if (first.Failed > 0) return first;

        // Otherwise always top up, including when the first pass was skipped
        // because requirements already existed: re-running the stage should
        // converge on full coverage rather than doing nothing.
        var fill = await FillRequirementGapsAsync(runId, corpusId, sourceVersionId, ct);
        return new StageOutcome(first.Entries + fill.Entries, first.Failed + fill.Failed);
    }

    /// <summary>
    /// Second pass over the requirements, driven by the completeness check:
    /// asks for requirements covering exactly the business rules and
    /// capabilities no requirement currently represents.
    /// </summary>
    private async Task<StageOutcome> FillRequirementGapsAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        List<string> uncoveredRules;
        List<string> uncoveredCapabilities;
        using (var scope = _scopeFactory.CreateScope())
        {
            var coverageSvc = scope.ServiceProvider.GetRequiredService<RequirementsCoverageService>();
            var report = await coverageSvc.BuildAsync(corpusId, ct);
            if (report is null) return new StageOutcome(0, 0);
            uncoveredRules = report.BusinessRules.Uncovered.Select(u => u.Text).ToList();
            uncoveredCapabilities = report.Capabilities.Uncovered.Select(u => u.Text).ToList();
        }
        if (uncoveredRules.Count == 0 && uncoveredCapabilities.Count == 0)
            return new StageOutcome(0, 0);

        _logger.LogInformation(
            "Requirements gap-fill for corpus {Corpus}: {Rules} rule(s) and {Caps} capability(ies) uncovered",
            corpusId, uncoveredRules.Count, uncoveredCapabilities.Count);

        return await RunCatalogAsync("functional-requirement", _functionalPrompt, null, async (db, _, ct2) =>
        {
            var modules = await LoadModuleSummariesAsync(db, corpusId, sourceVersionId, ct2);
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct2);
            var capabilities = await LoadCatalogEntriesAsync(db, corpusId, sourceVersionId, "capability-map", ct2);
            var moduleInputs = ModulesWithinBudget(modules, fraction: 0.3);
            var head = new
            {
                task =
                    "GAP FILL. A first pass has already written requirements for this system. " +
                    "Write requirements ONLY for the uncovered items listed below: every uncovered " +
                    "business rule and every uncovered capability must end up behind at least one " +
                    "requirement. Do not restate requirements for anything else, and keep the same " +
                    "as-is stance — describe what the system does today, not what it should do.",
                uncovered_business_rules = uncoveredRules,
                uncovered_capabilities = uncoveredCapabilities,
                capability_definitions = capabilities,
                module_summaries = moduleInputs,
            };
            var fixedChars = JsonSerializer.Serialize(head, JsonOpts).Length;
            var (list, omitted) = FitRoutines(routines, null, fixedChars, r => new Dictionary<string, object?>
            {
                ["name"] = r.Name, ["summary"] = r.Summary,
            });
            var user = JsonSerializer.Serialize(new
            {
                head.task,
                head.uncovered_business_rules,
                head.uncovered_capabilities,
                head.capability_definitions,
                head.module_summaries,
                routine_summaries = list,
                omitted,
            }, JsonOpts);
            return new Built(user, () => MockProseEntries("functional-requirement", routines));
        }, runId, corpusId, sourceVersionId, force: false, ct, append: true);
    }

    public Task<StageOutcome> RunProcessFlowsAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("process-flow", _processFlowPrompt, null, (db, _, ct2) =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, "process-flow", ct2,
                includeRules: true, includeInterfaces: true, includeSideEffects: false),
            runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunNfrAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("nfr", _nfrPrompt, null, (db, _, ct2) =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, "nfr", ct2,
                includeRules: false, includeInterfaces: true, includeSideEffects: true),
            runId, corpusId, sourceVersionId, force, ct);

    /// <summary>
    /// Shared input builder for the requirements stages. The overview and
    /// module documents go in whole; routines are fitted under the remaining
    /// budget by dropping whole routines, lowest tier first.
    /// </summary>
    private async Task<Built> BuildRequirementsInputAsync(
        AppDbContext db, Guid corpusId, Guid sourceVersionId, string kind, CancellationToken ct,
        bool includeRules, bool includeInterfaces, bool includeSideEffects,
        bool includeCapabilities = false)
    {
        var corpusName = await GetCorpusNameAsync(db, corpusId, ct);
        var modules = await LoadModuleSummariesAsync(db, corpusId, sourceVersionId, ct);
        var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct);
        var overview = await LoadOverviewMarkdownAsync(db, corpusId, sourceVersionId, ct);
        var rules = includeRules
            ? await LoadCatalogEntriesAsync(db, corpusId, sourceVersionId, "business-rule", ct)
            : new List<string>();
        var interfaces = includeInterfaces
            ? await LoadCatalogEntriesAsync(db, corpusId, sourceVersionId, "interface", ct)
            : new List<string>();
        // The requirements stage must see the capability map to know which
        // capabilities it is obliged to cover.
        var capabilities = includeCapabilities
            ? await LoadCatalogEntriesAsync(db, corpusId, sourceVersionId, "capability-map", ct)
            : new List<string>();

        var moduleInputs = ModulesWithinBudget(modules, fraction: 0.3);
        var head = new
        {
            corpus_name = corpusName,
            system_overview = overview ?? "",
            coverage_obligation = capabilities.Count > 0
                ? "Every capability in capability_definitions and every rule in business_rules " +
                  "must be represented by at least one requirement below."
                : null,
            capability_definitions = capabilities,
            module_summaries = moduleInputs,
            business_rules = rules,
            interfaces,
        };
        var fixedChars = JsonSerializer.Serialize(head, JsonOpts).Length;
        var (list, omitted) = FitRoutines(routines, null, fixedChars, r =>
        {
            var d = new Dictionary<string, object?> { ["name"] = r.Name, ["summary"] = r.Summary };
            if (includeSideEffects) d["side_effects"] = r.SideEffects;
            return d;
        });
        var user = JsonSerializer.Serialize(new
        {
            head.corpus_name,
            head.system_overview,
            head.coverage_obligation,
            head.capability_definitions,
            head.module_summaries,
            head.business_rules,
            head.interfaces,
            routine_summaries = list,
            omitted,
        }, JsonOpts);
        return new Built(user, () => MockProseEntries(kind, routines));
    }

    // ── Budgeting ───────────────────────────────────────────────────────

    /// <summary>Fit routines under the input budget. Returns the projected
    /// routine objects (with a <c>source</c> field where a slice was kept)
    /// and an <c>omitted</c> summary for the prompt.</summary>
    private (List<Dictionary<string, object?>> Items, object Omitted) FitRoutines(
        IReadOnlyList<RoutineSummaryRow> rows,
        IReadOnlyDictionary<Guid, string>? sources,
        int fixedChars,
        Func<RoutineSummaryRow, Dictionary<string, object?>> project)
    {
        var items = rows.Select(r =>
        {
            var core = JsonSerializer.Serialize(project(r), JsonOpts).Length;
            var extra = sources is not null && sources.TryGetValue(r.SubroutineId, out var s) ? s.Length : 0;
            return new DocInputBudget.Item<RoutineSummaryRow>(r, r.Tier, r.Loc, core, extra);
        }).ToList();

        var fit = DocInputBudget.Fit(items, _docsOpts.InputBudgetTokens, fixedChars);
        if (fit.Trimmed)
            _logger.LogInformation(
                "Catalog input budget: removed {Extras} source slice(s), dropped {Dropped} of {Total} routine(s)",
                fit.ExtrasDropped, fit.Dropped, rows.Count);

        var list = new List<Dictionary<string, object?>>();
        foreach (var p in fit.Kept)
        {
            var obj = project(p.Value);
            if (p.IncludeExtra && sources is not null && sources.TryGetValue(p.Value.SubroutineId, out var src))
                obj["source"] = src;
            list.Add(obj);
        }
        var omitted = new
        {
            source_removed = fit.ExtrasDropped,
            dropped_routines = fit.DroppedValues.Select(r => r.Name).ToList(),
        };
        return (list, omitted);
    }

    /// <summary>Module documents in full unless they alone would take more
    /// than <paramref name="fraction"/> of the budget, in which case only the
    /// summaries go in.</summary>
    private List<object> ModulesWithinBudget(IReadOnlyList<ModuleSummaryRow> modules, double fraction)
    {
        var budgetChars = DocInputBudget.TokensToChars(_docsOpts.InputBudgetTokens) * fraction;
        var full = modules.Select(m => (object)new { moduleName = m.ModuleName, summary = m.Summary, markdown = m.Markdown }).ToList();
        if (JsonSerializer.Serialize(full, JsonOpts).Length <= budgetChars) return full;
        return modules.Select(m => (object)new { moduleName = m.ModuleName, summary = m.Summary }).ToList();
    }

    private async Task<Dictionary<Guid, string>> LoadSourceSlicesAsync(
        IBlobClient blob, IReadOnlyList<RoutineSummaryRow> rows, CancellationToken ct)
    {
        var cap = Math.Max(20, _docsOpts.ModuleSourceLinesPerRoutine);
        var result = new Dictionary<Guid, string>();
        foreach (var file in rows.GroupBy(r => r.BlobUri))
        {
            string[] lines;
            try
            {
                lines = DocSourceSlices.SplitLines(await blob.GetTextAsync(file.Key, ct));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source unavailable for {Blob}; business rules written from summaries only for its routines", file.Key);
                continue;
            }
            foreach (var r in file)
            {
                var slice = DocSourceSlices.Slice(lines, r.LineStart, r.LineEnd, cap);
                if (slice.Length > 0) result[r.SubroutineId] = slice;
            }
        }
        return result;
    }

    // ── Core stage runner ───────────────────────────────────────────────

    private async Task<StageOutcome> RunCatalogAsync(
        string sectionKind, string systemPrompt, string? exemplarKind,
        Func<AppDbContext, IServiceProvider, CancellationToken, Task<Built>> build,
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct,
        bool append = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.DocSections
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == sectionKind)
            .ToListAsync(ct);

        // Append mode (the requirements gap-fill) continues the numbering.
        var ordinalOffset = 0;
        if (append)
        {
            ordinalOffset = existing.Count;
        }
        else if (existing.Count > 0)
        {
            if (!force) return new StageOutcome(0, 0);
            db.DocSections.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            var built = await build(db, scope.ServiceProvider, ct);
            var prose = ProseKinds.Contains(sectionKind);
            var request = new DocWriteRequest(
                PromptIdFor(sectionKind), prose ? "v2.0" : "v1.0",
                _assets.SystemBlocks(systemPrompt, exemplarKind),
                built.UserMessage,
                "emit_catalogue", "Emit the catalogue entries.",
                prose ? ProseCatalogueToolSchema : StructuredCatalogueToolSchema,
                _docsOpts.SonnetModel,
                _docsOpts.CatalogMaxOutputTokens,
                $"docs:catalog:{sectionKind}",
                built.MockPayload);

            var result = await _writer.WriteAsync(request, ct);
            _logger.LogInformation(
                "Catalog stage {Kind}: model returned {Len} chars (stop_reason={Stop})",
                sectionKind, result.ToolInputJson.Length, result.StopReason);
            var call = DocLlmCalls.Record(db, _writer, request, result);

            int count;
            if (prose)
            {
                var index = sectionKind == "business-rule"
                    ? await DocCitations.CorpusIndex.LoadAsync(db, sourceVersionId, ct)
                    : null;
                count = PersistProseEntries(db, sectionKind, result.ToolInputJson, ordinalOffset, index, runId, corpusId, sourceVersionId, call.Id);
            }
            else
            {
                count = PersistStructuredEntries(db, sectionKind, result.ToolInputJson, runId, corpusId, sourceVersionId, call.Id);
            }
            await db.SaveChangesAsync(ct);
            return new StageOutcome(count, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog stage {Kind} failed for corpus {Corpus}", sectionKind, corpusId);
            return new StageOutcome(0, 1);
        }
    }

    private int PersistProseEntries(
        AppDbContext db, string sectionKind, string toolInputJson, int ordinalOffset,
        DocCitations.CorpusIndex? index, Guid runId, Guid corpusId, Guid sourceVersionId, Guid llmCallId)
    {
        var (entries, skipped) = DocEnvelope.ParseEntries(toolInputJson);
        if (skipped > 0)
            _logger.LogWarning("Catalog {Kind}: {Skipped} entry(ies) had no markdown and were dropped", sectionKind, skipped);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var ordinal = ordinalOffset + i + 1;
            var (markdown, extras) = FrameEntry(sectionKind, entry, ordinal);
            var checks = DocCriticPass.RunChecks(
                markdown, index, Array.Empty<string>(),
                isCatalog: true, requireCitations: sectionKind == "business-rule");
            var report = DocCriticPass.Report.ChecksOnly(checks, _writer.ProviderName, "catalog entry: deterministic checks only");
            var final = entry.WithMarkdown(markdown);

            db.DocSections.Add(new DocSection
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                SectionKind = sectionKind,
                Scope = "corpus",
                State = "DRAFT",
                PayloadJson = JsonDocument.Parse(final.ToPayloadJson(extras)),
                RenderedMarkdown = markdown,
                LlmCallId = llmCallId,
                QualityJson = report.ToJson(),
                GenerationRunId = runId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        return entries.Count;
    }

    /// <summary>Frame an entry's markdown: numbered heading for requirements
    /// and NFRs (numbering is a pipeline concern so it continues across
    /// gap-fill passes), the entry's own name as a ### heading otherwise.</summary>
    public static (string Markdown, Dictionary<string, object?> Extras) FrameEntry(string sectionKind, DocEnvelope entry, int ordinal)
    {
        var extras = new Dictionary<string, object?>();
        switch (sectionKind)
        {
            case "functional-requirement":
            {
                var reference = $"FR-{ordinal:000}";
                var statement = entry.MetaString("statement") ?? "(no statement)";
                extras["reference"] = reference;
                return ($"### {reference} — {statement}\n\n" + StripLeadingHeading(entry.Markdown), extras);
            }
            case "nfr":
            {
                var reference = $"NFR-{ordinal:000}";
                var statement = entry.MetaString("statement") ?? "(no statement)";
                extras["reference"] = reference;
                return ($"### {reference} — {statement}\n\n" + StripLeadingHeading(entry.Markdown), extras);
            }
            case "business-rule":
            {
                var title = entry.MetaString("title")
                    ?? Shorten(entry.MetaString("ruleText"), 80)
                    ?? "(unnamed rule)";
                extras["title"] = title;
                return (DocMarkdown.EnsureTitle(DocMarkdown.ShiftHeadings(entry.Markdown, 3), title, 3), extras);
            }
            case "capability-map":
            {
                var name = entry.MetaString("name") ?? "(unnamed capability)";
                return (DocMarkdown.EnsureTitle(DocMarkdown.ShiftHeadings(entry.Markdown, 3), name, 3), extras);
            }
            case "process-flow":
            {
                var name = entry.MetaString("name") ?? "(unnamed flow)";
                return (DocMarkdown.EnsureTitle(DocMarkdown.ShiftHeadings(entry.Markdown, 3), name, 3), extras);
            }
            default:
                return (DocMarkdown.Normalize(entry.Markdown), extras);
        }
    }

    private static string StripLeadingHeading(string markdown)
    {
        var md = DocMarkdown.Normalize(markdown);
        var lines = md.Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first >= 0 && DocMarkdown.HeadingDepth(lines[first]) > 0)
            return DocMarkdown.Normalize(string.Join('\n', lines.Skip(first + 1)));
        return md;
    }

    private static string? Shorten(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "…";
    }

    private int PersistStructuredEntries(
        AppDbContext db, string sectionKind, string toolInputJson,
        Guid runId, Guid corpusId, Guid sourceVersionId, Guid llmCallId)
    {
        using var doc = JsonDocument.Parse(toolInputJson);
        var root = doc.RootElement;
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array) arr = e;
        else throw new InvalidOperationException($"Catalog {sectionKind}: expected an entries array, got {root.ValueKind}");

        var count = 0;
        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var markdown = RenderStructuredEntryMarkdown(sectionKind, entry);
            var checks = DocCriticPass.RunChecks(markdown, null, Array.Empty<string>(), isCatalog: true, requireCitations: false);
            var report = DocCriticPass.Report.ChecksOnly(checks, _writer.ProviderName, "reference catalog entry: deterministic checks only");
            db.DocSections.Add(new DocSection
            {
                Id = Guid.NewGuid(),
                CorpusId = corpusId,
                SourceVersionId = sourceVersionId,
                SectionKind = sectionKind,
                Scope = "corpus",
                State = "DRAFT",
                PayloadJson = JsonDocument.Parse(entry.GetRawText()),
                RenderedMarkdown = markdown,
                LlmCallId = llmCallId,
                QualityJson = report.ToJson(),
                GenerationRunId = runId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            count++;
        }
        return count;
    }

    // ── Loaders ─────────────────────────────────────────────────────────

    private sealed record RoutineSummaryRow(
        Guid SubroutineId, string Name, string Path, string BlobUri, int LineStart, int LineEnd, string Tier,
        string Summary, List<string> Inputs, List<string> Outputs, List<string> SideEffects,
        List<string> Preconditions, List<string> EdgeCases, List<string> IoPatterns)
    {
        public string LineRange => $"{LineStart}-{LineEnd}";
        public int Loc => Math.Max(1, LineEnd - LineStart + 1);
    }

    private sealed record ModuleSummaryRow(string ModuleName, string Summary, string Markdown);

    private sealed record CommonBlockRow(string blockName, List<string> fieldNames, List<string> touchedBy);

    private static async Task<List<RoutineSummaryRow>> LoadRoutineSummariesAsync(
        AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
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
                      sub.Id,
                      sub.Name,
                      Path = sub.SourceFile!.RelativePath,
                      BlobUri = sub.SourceFile!.BlobUri,
                      sub.LineStart,
                      sub.LineEnd,
                      sub.IoPatterns,
                      section.PayloadJson,
                  })
            .OrderBy(r => r.Path).ThenBy(r => r.LineStart)
            .ToListAsync(ct);

        var list = new List<RoutineSummaryRow>(rows.Count);
        foreach (var r in rows)
        {
            var root = r.PayloadJson.RootElement;
            list.Add(new RoutineSummaryRow(
                r.Id, r.Name, r.Path, r.BlobUri, r.LineStart, r.LineEnd,
                Str(root, "tier") ?? "standard",
                Str(root, "summary") ?? "",
                Arr(root, "inputs"), Arr(root, "outputs"), Arr(root, "sideEffects"),
                Arr(root, "preconditions"), Arr(root, "edgeCases"),
                r.IoPatterns is null ? new List<string>() : ExtractIoPatterns(r.IoPatterns)));
        }
        return list;
    }

    private static async Task<List<ModuleSummaryRow>> LoadModuleSummariesAsync(
        AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        var sections = await db.DocSections
            .AsNoTracking()
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == "module"
                     && s.ModuleName != null)
            .OrderBy(s => s.ModuleName)
            .Select(s => new { s.ModuleName, s.PayloadJson })
            .ToListAsync(ct);

        var list = new List<ModuleSummaryRow>(sections.Count);
        foreach (var sec in sections)
        {
            var root = sec.PayloadJson.RootElement;
            // v3 module payloads carry markdown + summary; older rows carry purpose.
            var markdown = Str(root, "markdown") ?? Str(root, "purpose") ?? "";
            var summary = Str(root, "summary") ?? DocMarkdown.FirstParagraph(markdown);
            list.Add(new ModuleSummaryRow(sec.ModuleName!, summary, markdown));
        }
        return list;
    }

    /// <summary>Raw payload JSON of every catalog entry of one kind — the
    /// evidence the requirements stages cite back to.</summary>
    private static async Task<List<string>> LoadCatalogEntriesAsync(
        AppDbContext db, Guid corpusId, Guid sourceVersionId, string kind, CancellationToken ct)
    {
        var rows = await db.DocSections.AsNoTracking()
            .Where(s => s.CorpusId == corpusId && s.SourceVersionId == sourceVersionId && s.SectionKind == kind)
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.PayloadJson)
            .ToListAsync(ct);
        return rows.Select(r => r.RootElement.GetRawText()).ToList();
    }

    private static async Task<string?> LoadOverviewMarkdownAsync(
        AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
        => await db.DocSections.AsNoTracking()
            .Where(s => s.CorpusId == corpusId && s.SourceVersionId == sourceVersionId && s.SectionKind == "overview")
            .Select(s => s.RenderedMarkdown)
            .FirstOrDefaultAsync(ct);

    private static async Task<List<CommonBlockRow>> LoadCommonBlocksAsync(AppDbContext db, Guid sourceVersionId, CancellationToken ct)
    {
        // Subroutine.CommonBlockRefs varies across parser versions; tolerate
        // both flat ["BLKNAME"] and structured [{name, fields}] shapes.
        var subs = await db.Subroutines
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId
                     && s.CommonBlockRefs != null)
            .Select(s => new { s.Name, s.CommonBlockRefs })
            .ToListAsync(ct);

        var aggregate = new Dictionary<string, (HashSet<string> Fields, HashSet<string> TouchedBy)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in subs)
        {
            if (sub.CommonBlockRefs is null) continue;
            try
            {
                var root = sub.CommonBlockRefs.RootElement;
                if (root.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in root.EnumerateArray())
                {
                    string? blockName = null;
                    var fields = new List<string>();
                    if (item.ValueKind == JsonValueKind.String)
                        blockName = item.GetString();
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        blockName = Str(item, "name");
                        fields = Arr(item, "fields");
                    }
                    if (string.IsNullOrWhiteSpace(blockName)) continue;
                    if (!aggregate.TryGetValue(blockName, out var agg))
                        aggregate[blockName] = agg = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    foreach (var f in fields) agg.Fields.Add(f);
                    agg.TouchedBy.Add(sub.Name);
                }
            }
            catch { /* per-row malformed jsonb is non-fatal */ }
        }

        return aggregate
            .Select(kv => new CommonBlockRow(kv.Key, kv.Value.Fields.ToList(), kv.Value.TouchedBy.ToList()))
            .ToList();
    }

    private static async Task<string> GetCorpusNameAsync(AppDbContext db, Guid corpusId, CancellationToken ct)
    {
        var name = await db.Corpora.AsNoTracking()
            .Where(c => c.Id == corpusId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);
        return name ?? "(unknown)";
    }

    private static List<string> ExtractIoPatterns(JsonDocument doc)
    {
        var list = new List<string>();
        try
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? "");
                    else if (item.ValueKind == JsonValueKind.Object) list.Add(item.GetRawText());
                }
            }
        }
        catch { /* shape varies; best-effort */ }
        return list;
    }

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

    private static string PromptIdFor(string sectionKind) => sectionKind switch
    {
        "data-dictionary" => "fortran-doc-data-dictionary",
        "glossary" => "fortran-doc-glossary",
        "interface" => "fortran-doc-interface",
        "business-rule" => "fortran-doc-business-rules",
        "capability-map" => "req-capability-map",
        "functional-requirement" => "req-functional",
        "process-flow" => "req-process-flow",
        "nfr" => "req-nfr",
        _ => "doc-catalog",
    };

    // ── Reference-catalog rendering (data dictionary, glossary, interface) ──

    private static string RenderStructuredEntryMarkdown(string sectionKind, JsonElement entry)
    {
        var sb = new StringBuilder();
        switch (sectionKind)
        {
            case "data-dictionary":
            {
                var name = Str(entry, "name") ?? "(unknown)";
                var type = Str(entry, "type");
                var meaning = Str(entry, "businessMeaning") ?? "";
                var units = Str(entry, "units");
                var range = Str(entry, "validRange");
                sb.Append("### ").Append(name);
                if (!string.IsNullOrWhiteSpace(type)) sb.Append("  — `").Append(type).Append('`');
                sb.Append("\n\n").Append(meaning).Append("\n\n");
                if (!string.IsNullOrWhiteSpace(units)) sb.Append("- **Units:** ").Append(units).Append('\n');
                if (!string.IsNullOrWhiteSpace(range)) sb.Append("- **Range:** ").Append(range).Append('\n');
                break;
            }
            case "glossary":
            {
                sb.Append("### ").Append(Str(entry, "term") ?? "(unknown)").Append("\n\n")
                  .Append(Str(entry, "definition") ?? "").Append('\n');
                break;
            }
            case "interface":
            {
                var kind = Str(entry, "kind");
                var dir = Str(entry, "direction");
                sb.Append("### ").Append(Str(entry, "name") ?? "(unknown)");
                if (!string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(dir))
                    sb.Append("  — `").Append(kind).Append(' ').Append(dir).Append('`');
                sb.Append("\n\n").Append(Str(entry, "purpose") ?? "").Append('\n');
                break;
            }
            default:
                sb.Append(entry.GetRawText()).Append('\n');
                break;
        }
        return sb.ToString();
    }

    // ── Tool input schemas ──────────────────────────────────────────────

    private static readonly object StructuredCatalogueToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["required"] = new[] { "entries" },
        ["properties"] = new Dictionary<string, object?>
        {
            ["entries"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "One object per catalogue entry, in the exact field shape the instructions specify.",
                ["items"] = new Dictionary<string, object?> { ["type"] = "object" },
            },
        },
    };

    private static readonly object ProseCatalogueToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["required"] = new[] { "entries" },
        ["properties"] = new Dictionary<string, object?>
        {
            ["entries"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "One entry per catalogue item. Empty when the evidence supports none.",
                ["items"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["required"] = new[] { "markdown", "meta" },
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["markdown"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                            ["description"] = "The entry as written for the reader, in the structure the instructions specify.",
                        },
                        ["meta"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["description"] = "The structured fields the instructions list for this kind.",
                        },
                    },
                },
            },
        },
    };

    // ── Mock payloads (Docs__Generator__Provider=mock) ──────────────────

    private static string MockStructuredEntries(string kind, IReadOnlyList<RoutineSummaryRow> routines)
    {
        var first = routines.FirstOrDefault();
        object entry = kind switch
        {
            "data-dictionary" => new
            {
                id = "dd.mock_item.v1", name = "MOCK_ITEM", container = (string?)null, type = "INTEGER",
                businessMeaning = "Placeholder data item produced offline by the mock documentation provider.",
                units = (string?)null, validRange = (string?)null,
                readers = first is null ? Array.Empty<string>() : new[] { first.Name },
                writers = Array.Empty<string>(), confidence = "low",
                citations = first is null ? Array.Empty<object>() : new object[] { new { lines = first.LineRange } },
            },
            "glossary" => new
            {
                id = "gl.mock.v1", term = "MOCK", definition = "Placeholder term produced offline by the mock documentation provider.",
                examples = Array.Empty<string>(), confidence = "low",
                citations = Array.Empty<object>(),
            },
            _ => new
            {
                id = "if.mock.v1", name = "mock-provider", kind = "service", direction = "both",
                purpose = "Placeholder interface produced offline by the mock documentation provider.",
                format = (string?)null,
                touchpoints = first is null ? Array.Empty<string>() : new[] { first.Name },
                citations = Array.Empty<object>(),
            },
        };
        return JsonSerializer.Serialize(new { entries = new[] { entry } });
    }

    private static string MockProseEntries(string kind, IReadOnlyList<RoutineSummaryRow> routines)
    {
        var first = routines.FirstOrDefault();
        var names = first is null ? Array.Empty<string>() : new[] { first.Name };
        object entry;
        switch (kind)
        {
            case "business-rule":
            {
                if (first is null) return JsonSerializer.Serialize(new { entries = Array.Empty<object>() });
                var cite = DocCitations.Format(first.Path, first.LineStart, first.LineEnd);
                entry = new
                {
                    markdown = $"### Placeholder rule for {first.Name}\n\nIF the mock documentation provider is configured THEN this entry stands in for a rule extracted from `{first.Name}` {cite}; on the other branch a model-backed run replaces it. A replacement must not preserve this placeholder.\n",
                    meta = new
                    {
                        title = $"Placeholder rule for {first.Name}",
                        ruleText = "IF the mock documentation provider is configured THEN a placeholder rule is produced.",
                        category = "other", extractionMode = "conservative", confidence = "low",
                        citations = new[] { new { path = first.Path, lines = first.LineRange } },
                        routines = names,
                    },
                };
                break;
            }
            case "capability-map":
                entry = new
                {
                    markdown = "### Mock capability\n\nThe system produces placeholder documentation when the mock provider is configured. This entry exists so the offline path exercises the same persistence as a model-backed run.\n\n**Business outcome:** the pipeline can be exercised without a model.\n\n" +
                               (first is null ? "" : $"*Implemented by:* `{first.Name}`\n"),
                    meta = new
                    {
                        name = "Mock capability",
                        description = "The system produces placeholder documentation when the mock provider is configured.",
                        businessOutcome = "The pipeline can be exercised without a model.",
                        supportingRoutines = names, notableConstraints = "",
                    },
                };
                break;
            case "functional-requirement":
                entry = new
                {
                    markdown = "The system produces one placeholder requirement per stage when the mock provider is configured, and records it with the same fields a model-backed requirement carries.\n\n**Acceptance criteria**\n\n- A requirements run under the mock provider stores at least one functional requirement.\n\n" +
                               (first is null ? "" : $"*Traceability:* `{first.Name}`\n"),
                    meta = new
                    {
                        capability = "Mock capability",
                        statement = "The system shall produce placeholder documentation when the mock provider is configured.",
                        detail = "The system produces one placeholder requirement per stage when the mock provider is configured.",
                        acceptanceCriteria = new[] { "A requirements run under the mock provider stores at least one functional requirement." },
                        sourceRoutines = names, sourceRules = Array.Empty<string>(), priority = "supporting",
                    },
                };
                break;
            case "process-flow":
                entry = new
                {
                    markdown = "### Mock flow\n\n**Actor:** documentation pipeline\n**Trigger:** a docs run with the mock provider configured\n\n1. The pipeline builds the same input a model would receive.\n2. The mock writer returns a placeholder entry.\n3. The entry is stored with deterministic quality checks.\n\n**Outcome:** the requirements pack contains placeholder entries.\n\n" +
                               (first is null ? "" : $"*Traceability:* `{first.Name}`\n"),
                    meta = new
                    {
                        name = "Mock flow", actor = "documentation pipeline", trigger = "a docs run with the mock provider configured",
                        steps = new[] { "The pipeline builds the same input a model would receive.", "The mock writer returns a placeholder entry.", "The entry is stored with deterministic quality checks." },
                        gatingRules = Array.Empty<string>(),
                        outcome = "The requirements pack contains placeholder entries.",
                        supportingRoutines = names,
                    },
                };
                break;
            default:
                entry = new
                {
                    markdown = "The system produces placeholder documentation when the mock provider is configured, with no model call and zero recorded cost.\n\n**Risk if carried over unchanged:** a deployment left on the mock provider ships placeholder documents.\n\n" +
                               (first is null ? "" : $"*Evidence:* `{first.Name}`\n"),
                    meta = new
                    {
                        category = "configuration",
                        statement = "The system produces placeholder documentation when the mock provider is configured.",
                        detail = "No model call is made and zero cost is recorded.",
                        riskIfPreserved = "A deployment left on the mock provider ships placeholder documents.",
                        evidence = names,
                    },
                };
                break;
        }
        return JsonSerializer.Serialize(new { entries = new[] { entry } });
    }
}
