using System.Text;
using System.Text.Json;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.c — cross-cutting catalogs. Four stage methods, one per
/// catalog kind: data-dictionary, glossary, interface, business-rules.
/// Each stage:
///
///   1. Loads the relevant inputs from existing DocSections (routine
///      summaries are the load-bearing source; module summaries when
///      they exist; parser-extracted COMMON-block + IO-pattern data
///      from Subroutine rows for the data dictionary + interface
///      catalog).
///   2. Builds a single user message JSON.
///   3. Calls Sonnet with the matching catalog prompt.
///   4. Persists the returned JSON array as N DocSection rows
///      (one per catalog entry).
///
/// An EMPTY catalog is a valid output and not a failure: math libraries
/// like BLAS legitimately have no business rules; pure-compute corpora
/// have no external interfaces. The conservative business-rules prompt
/// in particular WILL emit empty arrays on math-only corpora, which is
/// the correct behaviour.
/// </summary>
public sealed class CatalogPipeline
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DocsOptions _docsOpts;
    private readonly string _dataDictionaryPrompt;
    private readonly string _glossaryPrompt;
    private readonly string _interfacePrompt;
    private readonly string _businessRulesPrompt;
    private readonly string _capabilityMapPrompt;
    private readonly string _functionalPrompt;
    private readonly string _processFlowPrompt;
    private readonly string _nfrPrompt;
    private readonly AnthropicOptions _anthropic;
    private readonly string _baseUrl;
    private readonly string _apiVersion;
    private readonly ILogger<CatalogPipeline> _logger;

    public CatalogPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IOptions<AnthropicOptions> anthropicOpts,
        IConfiguration cfg,
        IWebHostEnvironment env,
        ILogger<CatalogPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _logger = logger;
        // Shared mutable instance — the settings UI (Task #178) can swap the
        // key at runtime, so read ApiKey per call, never cache the string.
        _anthropic = anthropicOpts.Value;
        _baseUrl = cfg.GetValue<string>("Llm:Anthropic:BaseUrl") ?? "https://api.anthropic.com";
        _apiVersion = cfg.GetValue<string>("Llm:Anthropic:ApiVersion") ?? "2023-06-01";

        _dataDictionaryPrompt = LoadPrompt(env.ContentRootPath, "doc-data-dictionary.v1.md");
        _glossaryPrompt = LoadPrompt(env.ContentRootPath, "doc-glossary.v1.md");
        _interfacePrompt = LoadPrompt(env.ContentRootPath, "doc-interface.v1.md");
        _businessRulesPrompt = LoadPrompt(env.ContentRootPath, "doc-business-rules.v1.md");
        // Phase B — requirements pack. Language-agnostic: these synthesise
        // from already-extracted docs, not from source, so they live in
        // their own folder rather than under a source-language one.
        _capabilityMapPrompt = LoadPrompt(env.ContentRootPath, "req-capability-map.v1.md", "requirements");
        _functionalPrompt = LoadPrompt(env.ContentRootPath, "req-functional.v1.md", "requirements");
        _processFlowPrompt = LoadPrompt(env.ContentRootPath, "req-process-flow.v1.md", "requirements");
        _nfrPrompt = LoadPrompt(env.ContentRootPath, "req-nfr.v1.md", "requirements");
    }

    public sealed record StageOutcome(int Entries, int Failed);

    public Task<StageOutcome> RunDataDictionaryAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("data-dictionary", _dataDictionaryPrompt, async db =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct);
            var commonBlocks = await LoadCommonBlocksAsync(db, corpusId, sourceVersionId, ct);
            var corpusName = await GetCorpusNameAsync(db, corpusId, ct);

            // Full rows for a 450-routine corpus overflow the call budget
            // (the name+summary stages succeeded on EnvestNet while this one
            // failed every run). Send truncated fields; fall back to
            // name+summary only if the payload is still oversized.
            var compact = routines.Select(r => new
            {
                r.name,
                summary = Truncate(r.summary, 280),
                inputs = CapList(r.inputs, 8, 100),
                outputs = CapList(r.outputs, 8, 100),
                side_effects = CapList(r.sideEffects, 6, 100),
            }).ToList();
            var json = JsonSerializer.Serialize(new
            {
                corpus_name = corpusName,
                routine_summaries = compact,
                common_blocks = commonBlocks,
            });
            if (json.Length > MaxCatalogInputChars)
            {
                json = JsonSerializer.Serialize(new
                {
                    corpus_name = corpusName,
                    routine_summaries = routines.Select(r => new { r.name, summary = Truncate(r.summary, 200) }),
                    common_blocks = commonBlocks,
                });
            }
            return json;
        }, runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunGlossaryAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("glossary", _glossaryPrompt, async db =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct);
            var modules = await LoadModuleSummariesAsync(db, corpusId, sourceVersionId, ct);
            return JsonSerializer.Serialize(new
            {
                corpus_name = await GetCorpusNameAsync(db, corpusId, ct),
                routine_summaries = routines.Select(r => new { r.name, r.summary }),
                module_summaries = modules,
            }, new JsonSerializerOptions { WriteIndented = true });
        }, runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunInterfaceAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("interface", _interfacePrompt, async db =>
        {
            var routines = await LoadRoutineSummariesWithIoAsync(db, corpusId, sourceVersionId, ct);
            return JsonSerializer.Serialize(new
            {
                corpus_name = await GetCorpusNameAsync(db, corpusId, ct),
                routine_summaries = routines,
            }, new JsonSerializerOptions { WriteIndented = true });
        }, runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunBusinessRulesAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("business-rule", _businessRulesPrompt, async db =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct);
            return JsonSerializer.Serialize(new
            {
                corpus_name = await GetCorpusNameAsync(db, corpusId, ct),
                routine_summaries = routines.Select(r => new { r.name, r.summary, r.lineRange }),
            }, new JsonSerializerOptions { WriteIndented = true });
        }, runId, corpusId, sourceVersionId, force, ct);

    // ── Phase B: requirements pack (AS-IS) ───────────────────────────
    // These four synthesise from artifacts the pipeline already produced
    // — routine summaries, module rollups, business rules, interfaces —
    // rather than re-reading source. Each is one call, so a whole pack
    // costs minutes, not the hours a per-routine pass takes.

    public Task<StageOutcome> RunCapabilityMapAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("capability-map", _capabilityMapPrompt, db =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, ct,
                includeRules: false, includeInterfaces: false, includeSideEffects: false),
            runId, corpusId, sourceVersionId, force, ct);

    public async Task<StageOutcome> RunFunctionalRequirementsAsync(
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
    {
        var first = await RunCatalogAsync("functional-requirement", _functionalPrompt, db =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, ct,
                includeRules: true, includeInterfaces: false, includeSideEffects: false,
                includeCapabilities: true),
            runId, corpusId, sourceVersionId, force, ct);

        // A failed first pass leaves nothing coherent to top up — filling
        // "gaps" against an empty set would just regenerate the whole pack
        // through the narrow gap-fill prompt.
        if (first.Failed > 0) return first;

        // Otherwise always top up, including when the first pass was skipped
        // because requirements already existed: re-running the stage should
        // converge on full coverage rather than doing nothing. Costs one
        // coverage computation and no model call when there are no gaps.
        var fill = await FillRequirementGapsAsync(runId, corpusId, sourceVersionId, ct);
        return new StageOutcome(first.Entries + fill.Entries, first.Failed + fill.Failed);
    }

    /// <summary>
    /// Second pass over the requirements, driven by the completeness check
    /// rather than by hope: it asks for requirements covering exactly the
    /// business rules and capabilities that no requirement currently
    /// represents. One extra call, and it converges because the gap list is
    /// computed from what was actually stored, not guessed at.
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

        return await RunCatalogAsync("functional-requirement", _functionalPrompt, async db =>
        {
            var modules = await LoadModuleSummariesAsync(db, corpusId, sourceVersionId, ct);
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct);
            var capabilities = await LoadCatalogEntriesAsync(db, corpusId, sourceVersionId, "capability-map", ct);
            return JsonSerializer.Serialize(new
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
                module_summaries = modules,
                routine_summaries = routines.Select(r => new { r.name, summary = Truncate(r.summary, 200) }),
            });
        }, runId, corpusId, sourceVersionId, force: false, ct, append: true);
    }

    public Task<StageOutcome> RunProcessFlowsAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("process-flow", _processFlowPrompt, db =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, ct,
                includeRules: true, includeInterfaces: true, includeSideEffects: false),
            runId, corpusId, sourceVersionId, force, ct);

    public Task<StageOutcome> RunNfrAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("nfr", _nfrPrompt, db =>
            BuildRequirementsInputAsync(db, corpusId, sourceVersionId, ct,
                includeRules: false, includeInterfaces: true, includeSideEffects: true),
            runId, corpusId, sourceVersionId, force, ct);

    /// <summary>
    /// Shared input builder for the requirements stages. Degrades the
    /// routine list if the payload would overflow the call budget, the
    /// same way the data dictionary does.
    /// </summary>
    private async Task<string> BuildRequirementsInputAsync(
        AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct,
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
        // The requirements stage previously could not see the capability map,
        // so it had no way to know which capabilities it was obliged to
        // cover — the reason a whole capability came out with no requirement.
        var capabilities = includeCapabilities
            ? await LoadCatalogEntriesAsync(db, corpusId, sourceVersionId, "capability-map", ct)
            : new List<string>();

        object BuildPayload(int summaryChars, bool withSideEffects) => new
        {
            corpus_name = corpusName,
            system_overview = Truncate(overview ?? "", 6000),
            coverage_obligation = capabilities.Count > 0
                ? "Every capability in capability_definitions and every rule in business_rules " +
                  "must be represented by at least one requirement below."
                : null,
            capability_definitions = capabilities,
            module_summaries = modules,
            business_rules = rules,
            interfaces,
            routine_summaries = routines.Select(r => withSideEffects
                ? (object)new { r.name, summary = Truncate(r.summary, summaryChars), side_effects = CapList(r.sideEffects, 4, 90) }
                : new { r.name, summary = Truncate(r.summary, summaryChars) }),
        };

        var json = JsonSerializer.Serialize(BuildPayload(280, includeSideEffects));
        if (json.Length > MaxCatalogInputChars)
            json = JsonSerializer.Serialize(BuildPayload(140, false));
        return json;
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

    // ─────────────────────────────────────────────────────────────────

    private async Task<StageOutcome> RunCatalogAsync(
        string sectionKind, string systemPrompt,
        Func<AppDbContext, Task<string>> buildUserMessage,
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct,
        bool append = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var httpFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        // Idempotency: skip if catalog already populated, unless force=true.
        var existing = await db.DocSections
            .Where(s => s.CorpusId == corpusId
                     && s.SourceVersionId == sourceVersionId
                     && s.SectionKind == sectionKind)
            .ToListAsync(ct);

        // Append mode (the requirements gap-fill) adds to what is already
        // there and continues the numbering, so FR-045 follows FR-044
        // instead of a second FR-001 appearing in the document.
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
            var userMessage = await buildUserMessage(db);
            var (payload, stopReason, rawTextLen) = await CallAnthropicArrayAsync(httpFactory, _docsOpts.SonnetModel, systemPrompt, userMessage, ct);
            _logger.LogInformation(
                "Catalog stage {Kind}: model returned {RawLen} chars (stop_reason={Stop}); trimmed payload {PayloadLen} chars",
                sectionKind, rawTextLen, stopReason, payload.Length);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException jx)
            {
                _logger.LogWarning(
                    "Catalog {Kind} JSON parse failed at pos {Pos}: raw output starts with: {Head}; ends with: {Tail}",
                    sectionKind, jx.BytePositionInLine,
                    payload.Length > 400 ? payload[..400] + "…" : payload,
                    payload.Length > 400 ? "…" + payload[^400..] : "");
                throw;
            }
            using var _disposeDoc = doc;
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"Catalog {sectionKind}: expected JSON array, got {arr.ValueKind}");

            var entries = arr.EnumerateArray().ToList();
            for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
            {
                var entry = entries[entryIndex];
                var entryJson = entry.GetRawText();
                var section = new DocSection
                {
                    Id = Guid.NewGuid(),
                    CorpusId = corpusId,
                    SourceVersionId = sourceVersionId,
                    SectionKind = sectionKind,
                    Scope = "corpus",
                    State = "DRAFT",
                    PayloadJson = JsonDocument.Parse(entryJson),
                    RenderedMarkdown = RenderCatalogEntryMarkdown(sectionKind, entry, ordinalOffset + entryIndex + 1),
                    GenerationRunId = runId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                db.DocSections.Add(section);
            }
            await db.SaveChangesAsync(ct);
            return new StageOutcome(entries.Count, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog stage {Kind} failed for corpus {Corpus}", sectionKind, corpusId);
            return new StageOutcome(0, 1);
        }
    }

    private async Task<List<RoutineSummaryRow>> LoadRoutineSummariesAsync(AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
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
                      Name = sub.Name,
                      Section = section,
                      LineStart = sub.LineStart,
                      LineEnd = sub.LineEnd,
                  })
            .ToListAsync(ct);

        var list = new List<RoutineSummaryRow>(rows.Count);
        foreach (var r in rows)
        {
            var root = r.Section.PayloadJson.RootElement;
            list.Add(new RoutineSummaryRow(
                name: r.Name,
                summary: root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                inputs: ExtractStringArray(root, "inputs"),
                outputs: ExtractStringArray(root, "outputs"),
                sideEffects: ExtractStringArray(root, "sideEffects"),
                lineRange: $"{r.LineStart}-{r.LineEnd}"));
        }
        return list;
    }

    private async Task<List<RoutineSummaryWithIoRow>> LoadRoutineSummariesWithIoAsync(AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
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
                  (section, sub) => new { sub.Name, sub.IoPatterns, Section = section })
            .ToListAsync(ct);

        var list = new List<RoutineSummaryWithIoRow>(rows.Count);
        foreach (var r in rows)
        {
            var root = r.Section.PayloadJson.RootElement;
            var io = r.IoPatterns is null
                ? new List<string>()
                : ExtractIoPatterns(r.IoPatterns);
            list.Add(new RoutineSummaryWithIoRow(
                name: r.Name,
                summary: root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                sideEffects: ExtractStringArray(root, "sideEffects"),
                ioPatterns: io));
        }
        return list;
    }

    private async Task<List<ModuleSummaryRow>> LoadModuleSummariesAsync(AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
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
            list.Add(new ModuleSummaryRow(
                moduleName: sec.ModuleName!,
                purpose: root.TryGetProperty("purpose", out var p) ? p.GetString() ?? "" : ""));
        }
        return list;
    }

    private async Task<List<CommonBlockRow>> LoadCommonBlocksAsync(AppDbContext db, Guid corpusId, Guid sourceVersionId, CancellationToken ct)
    {
        // The Subroutine.CommonBlockRefs jsonb structure varies across
        // parser versions; tolerate both flat ["BLKNAME"] and structured
        // [{name: "BLK", fields: ["X", "Y"]}] shapes. Aggregate touchedBy
        // across the corpus.
        var subs = await db.Subroutines
            .AsNoTracking()
            .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId
                     && s.CommonBlockRefs != null)
            .Select(s => new { s.Name, s.CommonBlockRefs })
            .ToListAsync(ct);

        var aggregate = new Dictionary<string, CommonBlockAggregate>(StringComparer.OrdinalIgnoreCase);
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
                        if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            blockName = n.GetString();
                        if (item.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array)
                            fields = f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                                .Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                    }
                    if (string.IsNullOrWhiteSpace(blockName)) continue;
                    if (!aggregate.TryGetValue(blockName, out var agg))
                    {
                        agg = new CommonBlockAggregate { BlockName = blockName, FieldNames = new(), TouchedBy = new() };
                        aggregate[blockName] = agg;
                    }
                    foreach (var fld in fields) agg.FieldNames.Add(fld);
                    agg.TouchedBy.Add(sub.Name);
                }
            }
            catch { /* per-row malformed jsonb is non-fatal */ }
        }

        return aggregate.Values
            .Select(a => new CommonBlockRow(a.BlockName, a.FieldNames.ToList(), a.TouchedBy.ToList()))
            .ToList();
    }

    private static List<string> ExtractStringArray(JsonElement root, string prop)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString() ?? "");
        return list;
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
                    if (item.ValueKind == JsonValueKind.String)
                        list.Add(item.GetString() ?? "");
                    else if (item.ValueKind == JsonValueKind.Object)
                        list.Add(item.GetRawText());
                }
            }
        }
        catch { /* shape varies; best-effort */ }
        return list;
    }

    private async Task<string> GetCorpusNameAsync(AppDbContext db, Guid corpusId, CancellationToken ct)
    {
        var name = await db.Corpora.AsNoTracking()
            .Where(c => c.Id == corpusId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);
        return name ?? "(unknown)";
    }

    private string LoadPrompt(string contentRoot, string fileName, string folder = "fortran-f77")
    {
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Llm", "Prompts", folder, fileName),
            Path.Combine(contentRoot, "..", "..", "Llm", "Prompts", folder, fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return StripFrontmatter(File.ReadAllText(Path.GetFullPath(c)));
        throw new FileNotFoundException($"Prompt {fileName} not found.");
    }

    private async Task<(string payload, string stopReason, int rawLen)> CallAnthropicArrayAsync(
        IHttpClientFactory httpFactory, string model, string systemPrompt, string userMessage, CancellationToken ct)
    {
        // Catalog outputs are unbounded by routine count — a 1000-routine
        // corpus can produce a 50-entry data dictionary. 16k matches the
        // global Llm__Anthropic__MaxOutputTokens default; the BLAS run
        // tripped the 4k ceiling on the data-dictionary and glossary
        // stages mid-array.
        // Structured output via tool use. The catalogue array is wrapped in an
        // `entries` object (tool input_schemas must be objects) and the Anthropic
        // API assembles the JSON — an unescaped quote or control character in an
        // entry can no longer break the whole batch. On a max_tokens truncation
        // the model simply emits fewer complete entries; the API still returns
        // valid JSON, which subsumes the manual balanced-array recovery the old
        // text path needed.
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = 16384,
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
                    ["name"] = "emit_catalogue",
                    ["description"] = "Emit the catalogue entries as structured data.",
                    ["input_schema"] = CatalogueToolSchema,
                },
            },
            ["tool_choice"] = new Dictionary<string, object?>
            {
                ["type"] = "tool",
                ["name"] = "emit_catalogue",
            },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userMessage },
            },
        };

        var http = httpFactory.CreateClient("docs-summary");
        // 3 minutes proved too tight for large corpora: EnvestNet's
        // data-dictionary call timed out at exactly 180s on every run.
        http.Timeout = TimeSpan.FromMinutes(15);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _anthropic.ApiKey);
        req.Headers.Add("anthropic-version", _apiVersion);

        using var resp = await http.SendAsync(req, ct);
        var bodyText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Anthropic {(int)resp.StatusCode}: {Truncate(bodyText, 400)}");

        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "" : "";

        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && block.TryGetProperty("input", out var input))
            {
                var rawLen = input.GetRawText().Length;
                if (input.TryGetProperty("entries", out var entries)
                    && entries.ValueKind == JsonValueKind.Array)
                    return (entries.GetRawText(), stopReason, rawLen);
                // Tool answered without a usable entries array — treat as empty.
                return ("[]", stopReason, rawLen);
            }
        }
        throw new InvalidOperationException(
            "No tool_use block in Anthropic response: " + Truncate(bodyText, 300));
    }

    private static readonly object CatalogueToolSchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["entries"] = new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["description"] = "One object per catalogue entry, each in the exact field shape the instructions specify.",
                ["items"] = new Dictionary<string, object?> { ["type"] = "object" },
            },
        },
        ["required"] = new[] { "entries" },
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // Catalog user-message budget: ≈150k tokens at ~3 chars/token, leaving
    // room for the system prompt and the 16k structured output.
    private const int MaxCatalogInputChars = 450_000;

    private static List<string> CapList(List<string> items, int maxItems, int maxChars)
    {
        var capped = items.Take(maxItems).Select(s => Truncate(s, maxChars)).ToList();
        if (items.Count > maxItems) capped.Add($"(+{items.Count - maxItems} more)");
        return capped;
    }

    private static string RenderCatalogEntryMarkdown(string sectionKind, JsonElement entry, int ordinal = 1)
    {
        var sb = new StringBuilder();
        switch (sectionKind)
        {
            case "data-dictionary":
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : "(unknown)";
                var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
                var meaning = entry.TryGetProperty("businessMeaning", out var bm) ? bm.GetString() : "";
                var units = entry.TryGetProperty("units", out var u) ? u.GetString() : null;
                var range = entry.TryGetProperty("validRange", out var v) ? v.GetString() : null;
                sb.Append("### ").Append(name);
                if (!string.IsNullOrWhiteSpace(type)) sb.Append("  — `").Append(type).Append("`");
                sb.Append("\n\n").Append(meaning).Append("\n\n");
                if (!string.IsNullOrWhiteSpace(units)) sb.Append("- **Units:** ").Append(units).Append('\n');
                if (!string.IsNullOrWhiteSpace(range)) sb.Append("- **Range:** ").Append(range).Append('\n');
                break;
            }
            case "glossary":
            {
                var term = entry.TryGetProperty("term", out var n) ? n.GetString() : "(unknown)";
                var def = entry.TryGetProperty("definition", out var d) ? d.GetString() : "";
                sb.Append("### ").Append(term).Append("\n\n").Append(def).Append("\n");
                break;
            }
            case "interface":
            {
                var name = entry.TryGetProperty("name", out var n) ? n.GetString() : "(unknown)";
                var kind = entry.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var dir = entry.TryGetProperty("direction", out var dr) ? dr.GetString() : null;
                var purpose = entry.TryGetProperty("purpose", out var p) ? p.GetString() : "";
                sb.Append("### ").Append(name);
                if (!string.IsNullOrWhiteSpace(kind) || !string.IsNullOrWhiteSpace(dir))
                    sb.Append("  — `").Append(kind).Append(' ').Append(dir).Append('`');
                sb.Append("\n\n").Append(purpose).Append('\n');
                break;
            }
            case "business-rule":
            {
                var text = entry.TryGetProperty("ruleText", out var t) ? t.GetString() : "(unknown)";
                var cat = entry.TryGetProperty("category", out var c) ? c.GetString() : null;
                sb.Append("- ").Append(text);
                if (!string.IsNullOrWhiteSpace(cat)) sb.Append("  _(category: ").Append(cat).Append(")_");
                sb.Append('\n');
                break;
            }
            // -- Phase B: requirements pack ---------------------------
            case "capability-map":
            {
                sb.Append("### ").Append(Str(entry, "name", "(unnamed capability)")).Append("\n\n");
                sb.Append(Str(entry, "description", "")).Append("\n\n");
                var outcome = Str(entry, "businessOutcome", "");
                if (outcome.Length > 0) sb.Append("**Business outcome:** ").Append(outcome).Append("\n\n");
                var constraints = Str(entry, "notableConstraints", "");
                if (constraints.Length > 0) sb.Append("**Must be preserved:** ").Append(constraints).Append("\n\n");
                AppendTrace(sb, entry, "supportingRoutines", "Implemented by");
                break;
            }
            case "functional-requirement":
            {
                sb.Append("### ").Append($"FR-{ordinal:000}").Append(" -- ")
                  .Append(Str(entry, "statement", "(no statement)")).Append("\n\n");
                var cap = Str(entry, "capability", "");
                var pri = Str(entry, "priority", "");
                if (cap.Length > 0 || pri.Length > 0)
                {
                    sb.Append('`');
                    if (cap.Length > 0) sb.Append(cap);
                    if (cap.Length > 0 && pri.Length > 0) sb.Append(" - ");
                    if (pri.Length > 0) sb.Append(pri);
                    sb.Append("`\n\n");
                }
                var detail = Str(entry, "detail", "");
                if (detail.Length > 0) sb.Append(detail).Append("\n\n");
                AppendBullets(sb, entry, "acceptanceCriteria", "Acceptance criteria");
                AppendBullets(sb, entry, "sourceRules", "Derived from business rules");
                AppendTrace(sb, entry, "sourceRoutines", "Traceability");
                break;
            }
            case "process-flow":
            {
                sb.Append("### ").Append(Str(entry, "name", "(unnamed flow)")).Append("\n\n");
                var actor = Str(entry, "actor", "");
                var trigger = Str(entry, "trigger", "");
                if (actor.Length > 0) sb.Append("**Actor:** ").Append(actor).Append("  \n");
                if (trigger.Length > 0) sb.Append("**Trigger:** ").Append(trigger).Append("  \n");
                if (actor.Length > 0 || trigger.Length > 0) sb.Append("\n");
                if (entry.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    var stepNo = 1;
                    foreach (var st in steps.EnumerateArray())
                        if (st.ValueKind == JsonValueKind.String)
                            sb.Append(stepNo++).Append(". ").Append(st.GetString()).Append("\n");
                    sb.Append("\n");
                }
                AppendBullets(sb, entry, "gatingRules", "Gating rules as they behave today");
                var flowOutcome = Str(entry, "outcome", "");
                if (flowOutcome.Length > 0) sb.Append("**Outcome:** ").Append(flowOutcome).Append("\n\n");
                AppendTrace(sb, entry, "supportingRoutines", "Traceability");
                break;
            }
            case "nfr":
            {
                sb.Append("### ").Append($"NFR-{ordinal:000}").Append(" -- ")
                  .Append(Str(entry, "statement", "(no statement)")).Append("\n\n");
                var nfrCat = Str(entry, "category", "");
                if (nfrCat.Length > 0) sb.Append('`').Append(nfrCat).Append("`\n\n");
                var nfrDetail = Str(entry, "detail", "");
                if (nfrDetail.Length > 0) sb.Append(nfrDetail).Append("\n\n");
                var risk = Str(entry, "riskIfPreserved", "");
                if (risk.Length > 0) sb.Append("**Risk if carried over unchanged:** ").Append(risk).Append("\n\n");
                AppendTrace(sb, entry, "evidence", "Evidence");
                break;
            }
        }
        return sb.ToString();
    }

    // ─── Local row records ───────────────────────────────────────────
    private static string Str(JsonElement e, string prop, string fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback)
            : fallback;

    private static void AppendBullets(StringBuilder sb, JsonElement entry, string prop, string heading)
    {
        if (!entry.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var items = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).ToList();
        if (items.Count == 0) return;
        sb.Append("**").Append(heading).Append("**\n\n");
        foreach (var i in items) sb.Append("- ").Append(i.GetString()).Append("\n");
        sb.Append("\n");
    }

    /// <summary>Traceability line - the audit link from a requirement back
    /// to the evidence it was derived from.</summary>
    private static void AppendTrace(StringBuilder sb, JsonElement entry, string prop, string label)
    {
        if (!entry.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var names = arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (names.Count == 0) return;
        sb.Append("*").Append(label).Append(":* ")
          .Append(string.Join(", ", names.Select(n => "`" + n + "`"))).Append("\n\n");
    }


    private sealed record RoutineSummaryRow(
        string name, string summary, List<string> inputs, List<string> outputs,
        List<string> sideEffects, string lineRange);

    private sealed record RoutineSummaryWithIoRow(
        string name, string summary, List<string> sideEffects, List<string> ioPatterns);

    private sealed record ModuleSummaryRow(string moduleName, string purpose);

    private sealed record CommonBlockRow(string blockName, List<string> fieldNames, List<string> touchedBy);

    private sealed class CommonBlockAggregate
    {
        public string BlockName { get; set; } = "";
        public HashSet<string> FieldNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TouchedBy { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
