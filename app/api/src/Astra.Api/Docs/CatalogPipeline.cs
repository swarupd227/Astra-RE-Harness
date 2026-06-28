using System.Text;
using System.Text.Json;
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
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _apiVersion;
    private readonly ILogger<CatalogPipeline> _logger;

    public CatalogPipeline(
        IServiceScopeFactory scopeFactory,
        IOptions<DocsOptions> docsOpts,
        IConfiguration cfg,
        IWebHostEnvironment env,
        ILogger<CatalogPipeline> logger)
    {
        _scopeFactory = scopeFactory;
        _docsOpts = docsOpts.Value;
        _logger = logger;
        _apiKey = cfg.GetValue<string>("Llm:Anthropic:ApiKey") ?? "";
        _baseUrl = cfg.GetValue<string>("Llm:Anthropic:BaseUrl") ?? "https://api.anthropic.com";
        _apiVersion = cfg.GetValue<string>("Llm:Anthropic:ApiVersion") ?? "2023-06-01";

        _dataDictionaryPrompt = LoadPrompt(env.ContentRootPath, "doc-data-dictionary.v1.md");
        _glossaryPrompt = LoadPrompt(env.ContentRootPath, "doc-glossary.v1.md");
        _interfacePrompt = LoadPrompt(env.ContentRootPath, "doc-interface.v1.md");
        _businessRulesPrompt = LoadPrompt(env.ContentRootPath, "doc-business-rules.v1.md");
    }

    public sealed record StageOutcome(int Entries, int Failed);

    public Task<StageOutcome> RunDataDictionaryAsync(Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct) =>
        RunCatalogAsync("data-dictionary", _dataDictionaryPrompt, async db =>
        {
            var routines = await LoadRoutineSummariesAsync(db, corpusId, sourceVersionId, ct);
            var commonBlocks = await LoadCommonBlocksAsync(db, corpusId, sourceVersionId, ct);
            return JsonSerializer.Serialize(new
            {
                corpus_name = await GetCorpusNameAsync(db, corpusId, ct),
                routine_summaries = routines,
                common_blocks = commonBlocks,
            }, new JsonSerializerOptions { WriteIndented = true });
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

    // ─────────────────────────────────────────────────────────────────

    private async Task<StageOutcome> RunCatalogAsync(
        string sectionKind, string systemPrompt,
        Func<AppDbContext, Task<string>> buildUserMessage,
        Guid runId, Guid corpusId, Guid sourceVersionId, bool force, CancellationToken ct)
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
        if (existing.Count > 0)
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
            foreach (var entry in entries)
            {
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
                    RenderedMarkdown = RenderCatalogEntryMarkdown(sectionKind, entry),
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

    private string LoadPrompt(string contentRoot, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(contentRoot, "Llm", "Prompts", "fortran-f77", fileName),
            Path.Combine(contentRoot, "..", "..", "Llm", "Prompts", "fortran-f77", fileName),
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
        var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "" : "";

        // Find the OUTERMOST balanced array by depth-tracking rather than
        // LastIndexOf(']'), which the original implementation got wrong: a
        // truncated mid-array response still contains inner-array `]`
        // characters (e.g. from "writers": ["A","B"]) and the naive trim
        // would lock onto those, producing an unclosed-outer-array string
        // that fails to parse.
        var trimmed = TrimToOutermostArray(text);
        return (trimmed, stopReason, text.Length);
    }

    private static string TrimToOutermostArray(string text)
    {
        var open = text.IndexOf('[');
        if (open < 0)
            throw new InvalidOperationException("Not a JSON array (no '['): " + Truncate(text, 200));
        int depth = 0;
        int close = -1;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) { close = i; break; }
            }
        }
        if (close < 0)
        {
            // Output is truncated mid-array. Walk back to the last
            // complete top-level object to recover as many entries as we
            // can without dropping the whole batch.
            var recovered = TryRecoverArrayFromTruncated(text, open);
            if (recovered is not null) return recovered;
            throw new InvalidOperationException(
                $"Truncated JSON array (depth still {depth} at end). Output length: {text.Length}. " +
                "Tail: " + Truncate(text[Math.Max(0, text.Length - 200)..], 200));
        }
        return text[open..(close + 1)];
    }

    /// <summary>
    /// Best-effort recovery from a max_tokens-truncated array response.
    /// Walks the text from the opening '[' tracking depth; remembers the
    /// position of the last complete top-level element (depth went 1→0
    /// crossing a '}') and synthesises a closed array up to that point.
    /// Drops the partial final element. Returns null if no complete
    /// element was found.
    /// </summary>
    private static string? TryRecoverArrayFromTruncated(string text, int openIdx)
    {
        int depth = 0;
        int lastCompleteEnd = -1;
        for (var i = openIdx; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[' || c == '{') depth++;
            else if (c == ']' || c == '}')
            {
                depth--;
                if (depth == 1 && c == '}') lastCompleteEnd = i;
            }
        }
        if (lastCompleteEnd < 0) return null;
        return text[openIdx..(lastCompleteEnd + 1)] + "]";
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

    private static string RenderCatalogEntryMarkdown(string sectionKind, JsonElement entry)
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
        }
        return sb.ToString();
    }

    // ─── Local row records ───────────────────────────────────────────

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
