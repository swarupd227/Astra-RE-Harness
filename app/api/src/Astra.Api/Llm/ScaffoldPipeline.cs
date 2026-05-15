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
    public const string PromptTemplateId = "dotnet-scaffold";
    public const string PromptTemplateVersion = "v2.0";
    public const string TargetPlatform = "dotnet8";

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
        [EnumeratorCancellation] CancellationToken ct)
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

        yield return new("provider_info", new
        {
            name = _provider.Info.Name,
            model = _provider.Info.Model,
            configVersion = _provider.Info.ConfigVersion,
            promptTemplateId = PromptTemplateId,
            promptTemplateVersion = PromptTemplateVersion,
            targetPlatform = targetStack,
        });

        var req = new ScaffoldRequest(
            spec.Id,
            spec.Subroutine?.Name ?? "",
            spec.Subroutine?.SourceFile?.RelativePath ?? "",
            spec.SpecJson.RootElement.GetRawText(),
            targetStack,
            PromptTemplateId,
            PromptTemplateVersion);

        object? finalPayload = null;
        await foreach (var evt in _provider.GenerateAsync(req, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (evt.Type == "__final__") { finalPayload = evt.Data; continue; }
            yield return evt;
        }

        if (finalPayload is null)
        {
            yield return new("error", new
            {
                code = "provider.no_final_payload",
                message = "Provider stream ended without producing a scaffold package.",
                retryable = true,
            });
            if (spec.Subroutine is not null) spec.Subroutine.State = "SIGNED";
            await _db.SaveChangesAsync(ct);
            yield break;
        }

        var (filesJson, inputTokens, outputTokens, latencyMs, fileCount, totalLines, todoCount) =
            UnpackPayload(finalPayload);

        // Persist the LlmCall row.
        var llmCall = new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = _provider.Info.Name,
            Model = _provider.Info.Model,
            PromptTemplateId = PromptTemplateId,
            PromptTemplateVersion = PromptTemplateVersion,
            ProviderConfigVersion = _provider.Info.ConfigVersion,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = latencyMs,
            CostUsd = EstimateCost(_provider.Info.Name, inputTokens, outputTokens),
            Status = "success",
            CalledAt = DateTimeOffset.UtcNow,
        };
        await _db.LlmCalls.AddAsync(llmCall, ct);

        // Write the manifest to the scaffolds bucket.
        var existing = await _db.Scaffolds.FirstOrDefaultAsync(s => s.SpecId == specId, ct);
        var scaffoldId = existing?.Id ?? Guid.NewGuid();
        var manifest = new
        {
            scaffoldId,
            specId = spec.Id,
            subroutineId = spec.SubroutineId,
            targetPlatform = targetStack,
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
                fileCount, totalLines, todoCount,
                inputTokens, outputTokens, latencyMs,
                blobUri,
            },
            ct: ct);

        yield return new("done", new
        {
            scaffoldId = scaffold.Id,
            specId = spec.Id,
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
                    int fileCount, int totalLines, int todoCount)
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
        return (
            files.GetRawText(),
            el.GetProperty("inputTokens").GetInt32(),
            el.GetProperty("outputTokens").GetInt32(),
            el.GetProperty("latencyMs").GetInt64(),
            fileCount,
            totalLines,
            todoCount);
    }

    private static decimal EstimateCost(string provider, int inputTokens, int outputTokens) =>
        provider == "mock"
            ? 0m
            : Math.Round((decimal)inputTokens * 0.000003m + (decimal)outputTokens * 0.000015m, 4);
}
