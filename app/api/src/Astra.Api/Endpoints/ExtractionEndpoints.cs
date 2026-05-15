using System.Text.Json;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class ExtractionEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapExtractionEndpoints(this IEndpointRouteBuilder app)
    {
        // SSE-over-POST: client posts to start an extraction, server keeps the
        // response open and streams events. Client uses fetch() + ReadableStream.
        app.MapPost("/api/v1/subroutines/{id:guid}/extract", async (
            Guid id,
            HttpContext ctx,
            ExtractionPipeline pipeline,
            IServiceScopeFactory scopeFactory,
            ILogger<ExtractionPipelineMarker> logger,
            CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Response.Headers["Connection"] = "keep-alive";
            // Flush immediately so the client knows the stream is open.
            await ctx.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var evt in pipeline.RunAsync(id, ct))
                {
                    await WriteEventAsync(ctx, evt, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected mid-stream. Best-effort revert any stuck
                // EXTRACTING state so the subroutine can be retried.
                await BestEffortRevertExtractingAsync(scopeFactory, id, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Extraction pipeline failed for subroutine {Id}", id);
                await BestEffortRevertExtractingAsync(scopeFactory, id, logger);
                try
                {
                    await WriteEventAsync(ctx, new ExtractionEvent("error", new
                    {
                        code = "extraction.unhandled_exception",
                        message = ex.Message,
                        retryable = true,
                    }), ct);
                }
                catch { /* response may already be torn down */ }
            }
        });

        // Read the persisted spec for a subroutine
        app.MapGet("/api/v1/subroutines/{id:guid}/spec", async (
            Guid id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var spec = await db.Specs
                .Include(s => s.LlmCall)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SubroutineId == id, ct);
            if (spec is null)
                return Results.NotFound(new { error = new { code = "spec.not_found" } });

            var reviews = await db.ClaimReviews.AsNoTracking()
                .Where(r => r.SpecId == spec.Id)
                .ToListAsync(ct);
            var signature = await db.Signatures.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SpecId == spec.Id, ct);

            using var doc = spec.SpecJson;
            return Results.Ok(new
            {
                id = spec.Id,
                subroutineId = spec.SubroutineId,
                sourceVersionId = spec.SourceVersionId,
                state = spec.State,
                createdAt = spec.CreatedAt,
                updatedAt = spec.UpdatedAt,
                spec = doc.RootElement.Clone(),
                claimReviews = reviews.Select(r => new
                {
                    id = r.Id,
                    claimPath = r.ClaimPath,
                    action = r.Action,
                    reason = r.Reason,
                    editedText = r.EditedText,
                    reviewedAt = r.ReviewedAt,
                }),
                signature = signature is null ? null : new
                {
                    id = signature.Id,
                    signedAt = signature.SignedAt,
                    signerDisplay = signature.SignerDisplay,
                    algorithm = signature.Algorithm,
                    keyId = signature.SignatureKeyId,
                    specCanonicalHash = signature.SpecCanonicalHash,
                    sourceVersionHash = signature.SourceVersionHash,
                    signedBlobUri = signature.SignedBlobUri,
                    signatureBase64 = Convert.ToBase64String(signature.SignatureBytes),
                },
                llmCall = spec.LlmCall is null ? null : new
                {
                    id = spec.LlmCall.Id,
                    provider = spec.LlmCall.Provider,
                    model = spec.LlmCall.Model,
                    promptTemplateId = spec.LlmCall.PromptTemplateId,
                    promptTemplateVersion = spec.LlmCall.PromptTemplateVersion,
                    providerConfigVersion = spec.LlmCall.ProviderConfigVersion,
                    inputTokens = spec.LlmCall.InputTokens,
                    outputTokens = spec.LlmCall.OutputTokens,
                    latencyMs = spec.LlmCall.LatencyMs,
                    costUsd = spec.LlmCall.CostUsd,
                    status = spec.LlmCall.Status,
                    calledAt = spec.LlmCall.CalledAt,
                },
            });
        });

        return app;
    }

    private static async Task WriteEventAsync(HttpContext ctx, ExtractionEvent evt, CancellationToken ct)
    {
        var dataJson = JsonSerializer.Serialize(evt.Data, JsonOpts);
        var payload = $"event: {evt.Type}\ndata: {dataJson}\n\n";
        await ctx.Response.WriteAsync(payload, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// If the subroutine is stuck in EXTRACTING because the client disconnected
    /// mid-stream, revert to PARSED so the demo flow can retry. Best-effort —
    /// uses a fresh DI scope because the request scope may already be torn down.
    /// </summary>
    private static async Task BestEffortRevertExtractingAsync(
        IServiceScopeFactory scopeFactory, Guid subroutineId,
        ILogger<ExtractionPipelineMarker> logger)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sub = await db.Subroutines.FirstOrDefaultAsync(s => s.Id == subroutineId);
            if (sub is null) return;
            if (sub.State == "EXTRACTING")
            {
                // No spec exists yet for a fresh PARSED → EXTRACTING transition;
                // if a Spec row is already present we left it from a prior DRAFT.
                var hasSpec = await db.Specs.AnyAsync(s => s.SubroutineId == sub.Id);
                sub.State = hasSpec ? "DRAFT" : "PARSED";
                await db.SaveChangesAsync();
                logger.LogInformation(
                    "Reverted stuck EXTRACTING → {NewState} for subroutine {Id}",
                    sub.State, sub.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Best-effort EXTRACTING revert failed for {Id}", subroutineId);
        }
    }

    private sealed class ExtractionPipelineMarker; // ILogger<T> category anchor
}
