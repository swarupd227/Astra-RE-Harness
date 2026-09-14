using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm;

public sealed class ScaffoldPipeline
{
    // Fallback only — used to populate ScaffoldRequest (which no provider
    // actually consults for prompt lookup; each resolves its own prompt
    // via PromptLibrary keyed on the real target stack) and as a safety
    // net if a provider's __final__ payload omits promptTemplateId/
    // Version. The real, per-stack values a provider actually used come
    // back in that payload — see UnpackPayload — and that's what gets
    // persisted to the LlmCall row below, not these constants.
    public const string DefaultPromptTemplateId = "dotnet-scaffold";
    public const string DefaultPromptTemplateVersion = "v2.0";
    public const string TargetPlatform = "dotnet10";

    private readonly IScaffoldProvider _provider;
    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly DevPersonaContext _persona;
    private readonly ILogger<ScaffoldPipeline> _logger;

    public ScaffoldPipeline(
        IScaffoldProvider provider,
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IAuditLogger audit,
        DevPersonaContext persona,
        ILogger<ScaffoldPipeline> logger)
    {
        _provider = provider;
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _persona = persona;
        _logger = logger;
    }

    public IAsyncEnumerable<ExtractionEvent> RunAsync(
        Guid specId,
        CancellationToken ct) => RunAsync(specId, TargetPlatform, ct);

    public async IAsyncEnumerable<ExtractionEvent> RunAsync(
        Guid specId,
        string targetStack,
        [EnumeratorCancellation] CancellationToken ct,
        string? repairHint = null)
    {
        // Phase #4 / value-add #3 — engineer-chosen target stack.
        // The pipeline currently delegates to a single scaffold provider, but
        // recording the requested stack on the LlmCall + Scaffold row gives
        // us provenance for the day Java-Spring (or any preview archetype)
        // gets its own provider wired in.
        var spec = await _db.Specs
            .Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile)
            .FirstOrDefaultAsync(s => s.Id == specId, ct);
        if (spec is null)
        {
            yield return new("error", new { code = "spec.not_found", message = $"Spec {specId} not found", retryable = false });
            yield break;
        }
        if (spec.State != "SIGNED")
        {
            yield return new("error", new
            {
                code = "spec.not_signed",
                message = $"Scaffold requires SIGNED spec (was {spec.State}).",
                retryable = false,
            });
            yield break;
        }

        // Subroutine state → SCAFFOLDING for the duration.
        if (spec.Subroutine is not null)
        {
            spec.Subroutine.State = "SCAFFOLDING";
            await _db.SaveChangesAsync(ct);
        }

        // Which prompt actually runs is resolved inside the provider (each
        // picks its own per-target-stack prompt via PromptLibrary), so it
        // isn't known yet at this point — the provider emits its own
        // "provider_info" moments later with the real prompt kind, and the
        // __final__ payload carries the real id/version for persistence
        // below. Omitted here rather than guessed, to avoid ever showing a
        // wrong value (this event previously always claimed
        // DefaultPromptTemplateId/Version regardless of target stack).
        yield return new("provider_info", new
        {
            name = _provider.Info.Name,
            model = _provider.Info.Model,
            configVersion = _provider.Info.ConfigVersion,
            targetPlatform = targetStack,
        });

        // Phase 16.0 — in-place modernization schemas (see
        // AnthropicScaffoldProvider.GenerateInPlaceAsync) transform the
        // routine's own file instead of an archetype, so the pipeline
        // always has the original text on hand to pass through.
        var originalSourceText = spec.Subroutine?.SourceFile?.BlobUri is { Length: > 0 } sourceBlobUri
            ? await _blob.GetTextAsync(sourceBlobUri, ct)
            : "";

        // WS3 Mode A — a faithful 1:1 conversion works on the whole unit the
        // routine lives in: every sibling routine in line order, and the
        // signed specs among them as guardrails. The anchor spec is signed
        // (checked above); siblings without a signed spec are converted from
        // source and marked so in the output.
        FaithfulConversion.UnitContext? unit = null;
        if (FaithfulConversion.IsFaithful(targetStack) && spec.Subroutine is not null)
        {
            var fileId = spec.Subroutine.SourceFileId;
            var siblings = await _db.Subroutines.AsNoTracking()
                .Where(s => s.SourceFileId == fileId)
                .ToListAsync(ct);
            var siblingIds = siblings.Select(s => s.Id).ToList();
            var siblingSpecs = await _db.Specs.AsNoTracking()
                .Where(sp => siblingIds.Contains(sp.SubroutineId))
                .ToListAsync(ct);
            unit = FaithfulConversion.Build(spec.Subroutine.SourceFile?.RelativePath ?? "", siblings, siblingSpecs);
        }

        var req = new ScaffoldRequest(
            spec.Id,
            spec.Subroutine?.Name ?? "",
            spec.Subroutine?.SourceFile?.RelativePath ?? "",
            spec.SpecJson.RootElement.GetRawText(),
            targetStack,
            DefaultPromptTemplateId,
            DefaultPromptTemplateVersion,
            spec.Subroutine?.SourceLanguage ?? "",
            originalSourceText,
            repairHint,
            unit);

        object? finalPayload = null;
        var providerReportedError = false;
        await foreach (var evt in _provider.GenerateAsync(req, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (evt.Type == "__final__") { finalPayload = evt.Data; continue; }
            if (evt.Type == "error") providerReportedError = true;
            yield return evt;
        }

        if (finalPayload is null)
        {
            // The provider's own error already said why; a second, generic
            // error would only replace it in the run's summary.
            if (!providerReportedError)
            {
                yield return new("error", new
                {
                    code = "provider.no_final_payload",
                    message = "Provider stream ended without producing a scaffold package.",
                    retryable = true,
                });
            }
            if (spec.Subroutine is not null)
                spec.Subroutine.State = await _db.Scaffolds.AnyAsync(s => s.SpecId == specId, ct) ? "SCAFFOLDED" : "SIGNED";
            await _db.SaveChangesAsync(ct);
            yield break;
        }

        var (filesJson, inputTokens, outputTokens, latencyMs, fileCount, totalLines, todoCount,
                promptTemplateId, promptTemplateVersion) = UnpackPayload(finalPayload);

        // Persist the LlmCall row — using the prompt id/version the
        // provider ACTUALLY resolved (from its __final__ payload), not
        // this class's own DefaultPromptTemplateId/Version. Those defaults
        // only apply when a provider's payload omits the fields entirely
        // (see UnpackPayload) — every current provider supplies them.
        var llmCall = new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = _provider.Info.Name,
            Model = _provider.Info.Model,
            PromptTemplateId = promptTemplateId,
            PromptTemplateVersion = promptTemplateVersion,
            ProviderConfigVersion = _provider.Info.ConfigVersion,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = latencyMs,
            CostUsd = ModelPricing.Estimate(_provider.Info.Name, _provider.Info.Model, inputTokens, outputTokens),
            Status = "success",
            CalledAt = DateTimeOffset.UtcNow,
        };
        await _db.LlmCalls.AddAsync(llmCall, ct);

        // Write the manifest to the scaffolds bucket. Keyed by (spec, target)
        // — a spec can carry an independent scaffold per target stack, so
        // generating a NEW target must not find (and overwrite) a scaffold
        // that was built for a DIFFERENT one.
        var existing = await _db.Scaffolds.FirstOrDefaultAsync(
            s => s.SpecId == specId && s.TargetPlatform == targetStack, ct);
        var scaffoldId = existing?.Id ?? Guid.NewGuid();
        var manifest = new
        {
            scaffoldId,
            specId = spec.Id,
            subroutineId = spec.SubroutineId,
            targetPlatform = targetStack,
            mode = unit is null ? "archetype" : FaithfulConversion.Mode,
            unit = unit is null ? null : new
            {
                name = unit.UnitName,
                path = unit.UnitPath,
                routineCount = unit.Routines.Count,
                signedSpecCount = unit.SignedCount,
            },
            generatedAt = DateTimeOffset.UtcNow,
            generatedBy = _persona.DisplayName,
            files = JsonDocument.Parse(filesJson).RootElement,
        };
        var manifestText = JsonSerializer.Serialize(manifest);
        var blobUri = await _blob.PutTextAsync(
            _storage.Buckets.Scaffolds,
            $"{scaffoldId}/manifest.json",
            manifestText,
            "application/json",
            ct);

        var now = DateTimeOffset.UtcNow;
        Scaffold scaffold;
        if (existing is null)
        {
            scaffold = new Scaffold
            {
                Id = scaffoldId,
                SpecId = spec.Id,
                State = "SCAFFOLDED",
                LlmCallId = llmCall.Id,
                TargetPlatform = targetStack,
                PackageBlobUri = blobUri,
                FileCount = fileCount,
                TotalLines = totalLines,
                TodoCount = todoCount,
                GeneratedAt = now,
            };
            await _db.Scaffolds.AddAsync(scaffold, ct);
        }
        else
        {
            existing.State = "SCAFFOLDED";
            existing.LlmCallId = llmCall.Id;
            existing.TargetPlatform = targetStack;
            existing.PackageBlobUri = blobUri;
            existing.FileCount = fileCount;
            existing.TotalLines = totalLines;
            existing.TodoCount = todoCount;
            existing.GeneratedAt = now;
            existing.GitBranch = null;
            existing.GitCommitHash = null;
            existing.GitCommitUrl = null;
            scaffold = existing;
        }

        if (spec.Subroutine is not null) spec.Subroutine.State = "SCAFFOLDED";
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            "scaffold.generated",
            "spec", spec.Id,
            _persona,
            new
            {
                scaffoldId = scaffold.Id,
                provider = _provider.Info.Name,
                model = _provider.Info.Model,
                targetPlatform = targetStack,
                mode = unit is null ? "archetype" : FaithfulConversion.Mode,
                unit = unit?.UnitPath,
                fileCount, totalLines, todoCount,
                inputTokens, outputTokens, latencyMs,
                blobUri,
            },
            ct: ct);

        yield return new("done", new
        {
            scaffoldId = scaffold.Id,
            specId = spec.Id,
            mode = unit is null ? "archetype" : FaithfulConversion.Mode,
            unitPath = unit?.UnitPath,
            unitRoutineCount = unit?.Routines.Count,
            fileCount, totalLines, todoCount,
            inputTokens, outputTokens, latencyMs,
            costUsd = llmCall.CostUsd,
            packageBlobUri = blobUri,
        });

        _logger.LogInformation(
            "Scaffold complete: spec={Spec} scaffold={Scaffold} {Files} files {TodoCount} TODOs in {Ms}ms",
            spec.Id, scaffold.Id, fileCount, todoCount, latencyMs);
    }

    private static (string filesJson, int inputTokens, int outputTokens, long latencyMs,
                    int fileCount, int totalLines, int todoCount,
                    string promptTemplateId, string promptTemplateVersion)
        UnpackPayload(object payload)
    {
        var el = JsonSerializer.SerializeToElement(payload);
        var files = el.GetProperty("files");
        var fileCount = files.GetArrayLength();
        var totalLines = 0;
        var todoCount = 0;
        foreach (var f in files.EnumerateArray())
        {
            if (f.TryGetProperty("lineCount", out var lc)) totalLines += lc.GetInt32();
            if (f.TryGetProperty("todoCount", out var tc)) todoCount += tc.GetInt32();
        }
        // Every current provider (Anthropic + Mock) supplies these — the
        // fallback only guards a future provider that forgets to.
        var promptTemplateId = el.TryGetProperty("promptTemplateId", out var pid) && pid.ValueKind == JsonValueKind.String
            ? pid.GetString()!
            : DefaultPromptTemplateId;
        var promptTemplateVersion = el.TryGetProperty("promptTemplateVersion", out var pver) && pver.ValueKind == JsonValueKind.String
            ? pver.GetString()!
            : DefaultPromptTemplateVersion;
        return (
            files.GetRawText(),
            el.GetProperty("inputTokens").GetInt32(),
            el.GetProperty("outputTokens").GetInt32(),
            el.GetProperty("latencyMs").GetInt64(),
            fileCount,
            totalLines,
            todoCount,
            promptTemplateId,
            promptTemplateVersion);
    }
}
