using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm.Prompts;
using Astra.Api.Office;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm;

/// <summary>
/// Orchestrates one extraction call: invokes the configured provider, validates
/// citations against the source, persists the LlmCall + Spec rows, and yields
/// SSE events to the caller.
/// </summary>
public sealed class ExtractionPipeline
{
    // Fallback constants — used when the prompt library doesn't have a
    // matching (schema × target × kind) entry. With #3b shipped, the
    // resolved values come from Llm/Prompts/<schema>/<target>/<kind>.v<N>.md.
    public const string DefaultPromptTemplateId = "fortran-extract";
    public const string DefaultPromptTemplateVersion = "v3.2";

    private readonly ILlmProvider _provider;
    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly IAuditLogger _audit;
    private readonly DevPersonaContext _persona;
    private readonly PromptLibrary _prompts;
    private readonly NeighbourhoodBuilder _neighbourhood;
    private readonly ILogger<ExtractionPipeline> _logger;
    private readonly Evidence.EvidenceService? _evidence;
    private readonly Evidence.GraphService? _graph;
    private readonly Schemas.SpecSchemaProvider? _schemas;

    /// <summary>
    /// May this routine's spec be drafted (again)? From PARSED or DRAFT, and from IN_REVIEW only when the spec there
    /// has no claims: nothing has been reviewed and nothing can be signed, so drafting it again is the way forward.
    /// </summary>
    public static async Task<bool> MayDraftAsync(
        AppDbContext db, Persistence.Entities.Subroutine sub, Schemas.SpecSchema? schema, CancellationToken ct)
    {
        if (sub.State is "PARSED" or "DRAFT") return true;
        if (sub.State != "IN_REVIEW") return false;
        var spec = await db.Specs.AsNoTracking().Where(s => s.SubroutineId == sub.Id)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        return spec is { State: "IN_REVIEW" } && !SpecShape.HasClaims(spec.SpecJson.RootElement, schema);
    }

    public ExtractionPipeline(
        ILlmProvider provider,
        AppDbContext db,
        IBlobClient blob,
        IAuditLogger audit,
        DevPersonaContext persona,
        PromptLibrary prompts,
        NeighbourhoodBuilder neighbourhood,
        ILogger<ExtractionPipeline> logger,
        Evidence.EvidenceService? evidence = null,
        Evidence.GraphService? graph = null,
        Schemas.SpecSchemaProvider? schemas = null)
    {
        _schemas = schemas;
        _provider = provider;
        _db = db;
        _blob = blob;
        _audit = audit;
        _persona = persona;
        _prompts = prompts;
        _neighbourhood = neighbourhood;
        _logger = logger;
        _evidence = evidence;
        _graph = graph;
    }

    /// <summary>The target stack the project names in its settings, or null.</summary>
    private async Task<string?> ProjectTargetAsync(Guid corpusId, CancellationToken ct)
    {
        var json = await _db.Corpora.AsNoTracking().Where(c => c.Id == corpusId).Select(c => c.SettingsJson).FirstOrDefaultAsync(ct);
        return Artefacts.ProjectSettings.Parse(json).TargetStack;
    }

    /// <summary>
    /// The routes the routine serves, read from the version's entity graph (built on first use, as every graph
    /// reader does). Best effort: extraction never fails or waits on a graph that cannot be read.
    /// </summary>
    private async Task<IReadOnlyList<ServedRoute>> ServedRoutesAsync(Subroutine sub, CancellationToken ct)
    {
        if (_evidence is null || _graph is null) return Array.Empty<ServedRoute>();
        try
        {
            var versionId = sub.SourceFile!.SourceVersionId;
            await _evidence.EnsureIndexedAsync(versionId, ct);
            await _graph.EnsureBuiltAsync(versionId, ct);
            var triggers = await _graph.RouteTriggersAsync(versionId, ct);
            if (triggers.Count == 0) return Array.Empty<ServedRoute>();
            var types = Dependency.DependencyGraphBuilder.ReadNameList(sub.CommonBlockRefs);
            return ServedRoutes.For(sub.Name, sub.SourceFile.RelativePath, types, triggers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the routes served by {Subroutine}; extracting without them", sub.Id);
            return Array.Empty<ServedRoute>();
        }
    }

    /// <summary>Resolve the active prompt's id+version for the audit trail.</summary>
    /// <param name="sourceLanguage">Subroutine.SourceLanguage — "fortran-f77", "cobol", "delphi", "cpp", "vb6".</param>
    /// <param name="targetStack">Project's target stack (default "dotnet8"; "java-spring" / "dotnet10" once those land).</param>
    private (string Id, string Version) ResolvePromptMeta(string sourceLanguage, string targetStack)
    {
        // Phase 5.2 — route by sourceLanguage.
        // Phase 10.1.c — use the target-stack fallback chain so the audit
        // trail records the prompt that AnthropicLlmProvider actually
        // resolved, not the legacy Fortran-default. The fallback target
        // is logged at the provider; here we just need the metadata.
        var loaded = _prompts.GetLatestWithFallback(sourceLanguage, targetStack, "extract", out _);
        return loaded is not null
            ? (loaded.PromptId, loaded.Version)
            : (DefaultPromptTemplateId, DefaultPromptTemplateVersion);
    }

    /// <summary>The prompt-library target holding the extract prompt for VBA in Office documents.</summary>
    public const string OfficeExtractTarget = "office-vba";

    /// <summary>
    /// For a routine whose file sits under an Office document (<c>Rating.xlsm/Pricing.bas</c>), the host
    /// workbook's view of it, read from the workbook kept as evidence on the same version; a short note when
    /// that workbook is not there. Null for ordinary source. Best effort: extraction never fails on it.
    /// </summary>
    private async Task<string?> BuildHostContextAsync(Persistence.Entities.Subroutine sub, CancellationToken ct)
    {
        if (OfficeHostContext.SplitPath(sub.SourceFile!.RelativePath) is not { } split) return null;
        try
        {
            var doc = await _db.SourceFiles.AsNoTracking()
                .Where(f => f.SourceVersionId == sub.SourceFile.SourceVersionId && f.RelativePath == split.Document)
                .Select(f => new { f.BlobUri, f.Kind })
                .FirstOrDefaultAsync(ct);
            if (doc is null || doc.Kind != Artefacts.ArtefactKinds.Workbook) return OfficeHostContext.Minimal(split.Document);
            var bytes = await _blob.GetBytesAsync(doc.BlobUri, ct);
            var surface = WorkbookSurface.Read(bytes, Path.GetFileName(split.Document));
            return surface.Sheets.Count == 0
                ? OfficeHostContext.Minimal(split.Document)
                : OfficeHostContext.Build(surface, split.ModuleFile, sub.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the host document of {Subroutine}; extracting without it", sub.Id);
            return OfficeHostContext.Minimal(split.Document);
        }
    }

    public async IAsyncEnumerable<ExtractionEvent> RunAsync(
        Guid subroutineId,
        [EnumeratorCancellation] CancellationToken ct,
        ExtractionTuning? tuning = null)
    {
        // 1. Load context
        var sub = await _db.Subroutines
            .Include(s => s.SourceFile)
                .ThenInclude(f => f!.SourceVersion)
            .FirstOrDefaultAsync(s => s.Id == subroutineId, ct);
        if (sub?.SourceFile?.SourceVersion is null)
        {
            yield return new("error", new
            {
                code = "subroutine.not_found",
                message = $"Subroutine {subroutineId} not found",
                retryable = false,
            });
            yield break;
        }

        // State-machine guard: re-extract is allowed from PARSED or DRAFT.
        // From IN_REVIEW / SIGNED, replacing the spec would invalidate every
        // existing claim review and signature. Supersession is the proper
        // forward path (Phase C). EXTRACTING means another run is in flight.
        // The one exception: a spec in review with no claims at all, which
        // holds no review and cannot be signed, may be drafted again.
        var schema = _schemas?.GetById(string.IsNullOrEmpty(sub.SourceLanguage) ? "fortran-f77" : sub.SourceLanguage);
        if (!await MayDraftAsync(_db, sub, schema, ct))
        {
            yield return new("error", new
            {
                code = "extract.invalid_state",
                message = $"Cannot extract from state {sub.State}. Allowed: PARSED, DRAFT.",
                retryable = false,
                currentState = sub.State,
            });
            yield break;
        }

        var sourceText = await _blob.GetTextAsync(sub.SourceFile.BlobUri, ct);

        // Mark EXTRACTING. Reverted to PARSED/DRAFT on cancellation by the
        // SSE handler's finally-block (see ExtractionEndpoints.MapPost).
        var priorState = sub.State;
        sub.State = "EXTRACTING";
        await _db.SaveChangesAsync(ct);

        // 2. Provider context (visible to the user).
        // Phase 5.2 — pick the prompt by the subroutine's parsed language.
        // The spec is written for the stack the routine is going to: the project's target when it names one,
        // else the language's default (TargetStacks), else .NET 10 — the first of those with an extract prompt.
        var sourceLanguage = string.IsNullOrEmpty(sub.SourceLanguage) ? "fortran-f77" : sub.SourceLanguage;
        var projectTarget = await ProjectTargetAsync(sub.SourceFile.SourceVersion.CorpusId, ct);
        var defaultTargetStack = TargetStacks.Resolve(projectTarget, sourceLanguage,
            t => _prompts.GetLatest(sourceLanguage, t, "extract") is not null);
        // VBA taken from an Office document is extracted with its own prompt and the host workbook's
        // view of the routine: named cells, sheets, what starts it, its form.
        var hostContext = await BuildHostContextAsync(sub, ct);
        var extractTarget = hostContext is not null && _prompts.GetLatest(sourceLanguage, OfficeExtractTarget, "extract") is not null
            ? OfficeExtractTarget : defaultTargetStack;
        var (promptId, promptVersion) = ResolvePromptMeta(sourceLanguage, extractTarget);
        yield return new("provider_info", new
        {
            name = _provider.Info.Name,
            model = _provider.Info.Model,
            configVersion = _provider.Info.ConfigVersion,
            promptTemplateId = promptId,
            promptTemplateVersion = promptVersion,
        });

        // Phase 7.0 — build the structured cross-routine context from
        // parser-extracted metadata already in Postgres. If the build
        // throws (e.g. orphaned subroutine), we proceed without a
        // neighbourhood — extraction must keep working in degraded mode.
        Neighbourhood? neighbourhood = null;
        try
        {
            neighbourhood = await _neighbourhood.BuildForSubroutineAsync(sub.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not build neighbourhood for {Subroutine}; proceeding without",
                sub.Id);
        }
        // The routes this routine serves, from the project's route table (a Laravel routes file, a #[Route]
        // attribute, a front controller's switch). The method and path of an HTTP contract are stated there, not
        // in the handler's own source.
        var served = await ServedRoutesAsync(sub, ct);
        if (served.Count > 0)
        {
            neighbourhood = (neighbourhood ?? Neighbourhood.Empty) with { Routes = served };
            yield return new("routes_attached", new { routes = served.Select(r => r.Name).ToArray() });
        }
        if (hostContext is not null)
            yield return new("host_context_attached", new { document = OfficeHostContext.SplitPath(sub.SourceFile.RelativePath)?.Document, chars = hostContext.Length });
        if (neighbourhood is not null && !neighbourhood.IsEmpty)
        {
            // Surface to the UI so reviewers can see what context the
            // extract had access to.
            yield return new("neighbourhood_attached", new
            {
                callees = neighbourhood.Callees.Count,
                callers = neighbourhood.Callers.Count,
                commonBlocks = neighbourhood.CommonBlocks.Count,
                siblingsInFile = neighbourhood.SiblingsInFile.Count,
            });
        }

        // 3. Stream events from the provider, capturing the final payload
        var req = new ExtractionRequest(
            sub.Id,
            sub.Name,
            sub.SourceFile.RelativePath,
            sourceText,
            sub.SourceFile.LineCount,
            promptId,
            promptVersion,
            SourceLanguage: sourceLanguage,
            TargetStack: extractTarget,
            Neighbourhood: neighbourhood,
            ModelOverride: tuning?.ModelOverride,
            MaxOutputTokensOverride: tuning?.MaxOutputTokensOverride,
            BulkMode: tuning?.BulkMode ?? false,
            HostContext: hostContext);

        object? finalPayload = null;
        var providerEmittedError = false;
        await foreach (var evt in _provider.ExtractAsync(req, ct))
        {
            ct.ThrowIfCancellationRequested();

            // Capture the final payload sentinel; do not forward it to the client.
            if (evt.Type == "__final__")
            {
                finalPayload = evt.Data;
                continue;
            }

            // Track whether the provider already emitted an error event so we
            // don't follow up with our own "no final payload" message that
            // would overwrite the provider's user-facing error in the UI.
            if (evt.Type == "error") providerEmittedError = true;

            // Citation post-validation: warn if the cited line range
            // is outside the source file. Non-blocking.
            if (evt.Type == "citation_pulse" && evt.Data is not null)
            {
                var dynData = evt.Data;
                var json = JsonSerializer.SerializeToElement(dynData);
                if (json.TryGetProperty("lines", out var linesEl))
                {
                    var (ok, range) = ParseRange(linesEl.GetString());
                    if (ok && (range.start < 1 || range.end > sub.SourceFile.LineCount))
                    {
                        yield return new("warning", new
                        {
                            code = "citation_unresolved",
                            message = $"Citation '{linesEl.GetString()}' is outside source range 1-{sub.SourceFile.LineCount}.",
                        });
                    }
                }
            }

            yield return evt;
        }

        if (finalPayload is null)
        {
            // Suppress the follow-up error if the provider already surfaced
            // one — its message is more specific (e.g. "Provider returned
            // 503...") and shouldn't be clobbered by the generic fallback.
            if (!providerEmittedError)
            {
                yield return new("error", new
                {
                    code = "provider.no_final_payload",
                    message = "Provider stream ended without producing a final spec.",
                    retryable = true,
                });
            }
            // Revert subroutine state to its pre-EXTRACTING value
            sub.State = priorState;
            await _db.SaveChangesAsync(ct);
            yield break;
        }

        // 4. Persist LlmCall + Spec
        var payload = UnpackFinalPayload(finalPayload);
        var (specJsonText, inputTokens, outputTokens, latencyMs) =
            (payload.SpecJson, payload.InputTokens, payload.OutputTokens, payload.LatencyMs);
        var modelUsed = string.IsNullOrWhiteSpace(payload.Model) ? _provider.Info.Model : payload.Model;

        // A reply is a draft only if there is something in it to review. The call is paid for either way, so it is
        // recorded; a draft the routine already had is left as it was.
        var specElement = SpecShape.Unwrap(JsonDocument.Parse(specJsonText).RootElement, schema);
        var reviewable = SpecShape.HasClaims(specElement, schema);

        var llmCall = new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = _provider.Info.Name,
            Model = modelUsed,
            PromptTemplateId = promptId,
            PromptTemplateVersion = promptVersion,
            ProviderConfigVersion = _provider.Info.ConfigVersion,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = payload.CacheReadTokens,
            CacheCreationTokens = payload.CacheCreationTokens,
            LatencyMs = latencyMs,
            CostUsd = ModelPricing.Estimate(
                _provider.Info.Name, modelUsed, inputTokens, outputTokens,
                payload.CacheReadTokens, payload.CacheCreationTokens),
            Status = reviewable ? "success" : "rejected",
            CalledAt = DateTimeOffset.UtcNow,
        };
        await _db.LlmCalls.AddAsync(llmCall, ct);

        if (!reviewable)
        {
            sub.State = priorState;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(
                "spec.extraction_rejected",
                "subroutine", sub.Id,
                _persona,
                new { subroutineName = sub.Name, reason = "no_claims", model = modelUsed, promptTemplate = $"{promptId}@{promptVersion}", llmCallId = llmCall.Id },
                ct: ct);
            _logger.LogWarning(
                "Extraction rejected: the reply for subroutine {Sub} holds no claims (call {Call}, {OutTok} output tokens)",
                sub.Id, llmCall.Id, outputTokens);
            yield return new("error", new
            {
                code = "provider.empty_spec",
                message = "The model's reply held no claims to review, so no draft was saved. Extract the spec again.",
                retryable = true,
            });
            yield break;
        }

        var existing = await _db.Specs.FirstOrDefaultAsync(s => s.SubroutineId == sub.Id, ct);
        var specDoc = JsonDocument.Parse(specElement.GetRawText());
        var now = DateTimeOffset.UtcNow;
        Spec spec;
        if (existing is null)
        {
            spec = new Spec
            {
                Id = Guid.NewGuid(),
                SubroutineId = sub.Id,
                SourceVersionId = sub.SourceFile.SourceVersionId,
                State = "DRAFT",
                SpecJson = specDoc,
                LlmCallId = llmCall.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _db.Specs.AddAsync(spec, ct);
        }
        else
        {
            existing.SpecJson = specDoc;
            existing.LlmCallId = llmCall.Id;
            existing.State = "DRAFT";
            existing.SourceVersionId = sub.SourceFile.SourceVersionId;
            existing.UpdatedAt = now;
            spec = existing;
        }

        sub.State = "DRAFT";
        await _db.SaveChangesAsync(ct);

        // 5. Audit + done
        await _audit.LogAsync(
            "spec.extracted",
            "spec", spec.Id,
            _persona,
            new
            {
                subroutineId = sub.Id,
                subroutineName = sub.Name,
                provider = _provider.Info.Name,
                model = modelUsed,
                promptTemplate = $"{promptId}@{promptVersion}",
                inputTokens, outputTokens, latencyMs,
                cacheReadTokens = payload.CacheReadTokens,
                cacheCreationTokens = payload.CacheCreationTokens,
                llmCallId = llmCall.Id,
                providerConfigVersion = _provider.Info.ConfigVersion,
            },
            ct: ct);

        yield return new("done", new
        {
            specId = spec.Id,
            callId = llmCall.Id,
            model = modelUsed,
            inputTokens,
            outputTokens,
            cacheReadTokens = payload.CacheReadTokens,
            cacheCreationTokens = payload.CacheCreationTokens,
            latencyMs,
            costUsd = llmCall.CostUsd,
        });

        _logger.LogInformation(
            "Extraction complete: subroutine={Sub} spec={Spec} call={Call} {InTok}/{OutTok} tokens in {Ms}ms",
            sub.Id, spec.Id, llmCall.Id, inputTokens, outputTokens, latencyMs);
    }

    private sealed record FinalPayload(
        string SpecJson,
        string? Model,
        int InputTokens,
        int OutputTokens,
        int CacheReadTokens,
        int CacheCreationTokens,
        long LatencyMs);

    private static FinalPayload UnpackFinalPayload(object payload)
    {
        // Round-trip via JsonElement to avoid reflecting on anonymous types.
        var el = JsonSerializer.SerializeToElement(payload);
        var specJson = el.GetProperty("specJson").GetRawText();
        var model = el.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var inTok = el.TryGetProperty("inputTokens", out var i) ? i.GetInt32() : 0;
        var outTok = el.TryGetProperty("outputTokens", out var o) ? o.GetInt32() : 0;
        var cacheRead = el.TryGetProperty("cacheReadInputTokens", out var cr) ? cr.GetInt32() : 0;
        var cacheCreate = el.TryGetProperty("cacheCreationInputTokens", out var cc) ? cc.GetInt32() : 0;
        var ms = el.TryGetProperty("latencyMs", out var l) ? l.GetInt64() : 0;
        return new FinalPayload(specJson, model, inTok, outTok, cacheRead, cacheCreate, ms);
    }

    private static (bool ok, (int start, int end) range) ParseRange(string? lines)
    {
        if (string.IsNullOrWhiteSpace(lines)) return (false, (0, 0));
        var s = lines.Trim();
        if (s.Contains('-'))
        {
            var parts = s.Split('-', 2);
            if (int.TryParse(parts[0].Trim(), out var a) && int.TryParse(parts[1].Trim(), out var b))
                return (true, (a, b));
        }
        // Single line "5" or comma list "1, 14" — treat first numeric as both ends
        var first = new string(s.TakeWhile(c => char.IsDigit(c)).ToArray());
        if (int.TryParse(first, out var n)) return (true, (n, n));
        return (false, (0, 0));
    }
}
