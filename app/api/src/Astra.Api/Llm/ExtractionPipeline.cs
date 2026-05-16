using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm.Prompts;
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
    private readonly ILogger<ExtractionPipeline> _logger;

    public ExtractionPipeline(
        ILlmProvider provider,
        AppDbContext db,
        IBlobClient blob,
        IAuditLogger audit,
        DevPersonaContext persona,
        PromptLibrary prompts,
        ILogger<ExtractionPipeline> logger)
    {
        _provider = provider;
        _db = db;
        _blob = blob;
        _audit = audit;
        _persona = persona;
        _prompts = prompts;
        _logger = logger;
    }

    /// <summary>Resolve the active prompt's id+version for the audit trail.</summary>
    /// <param name="sourceLanguage">Subroutine.SourceLanguage — "fortran-f77" or "cobol".</param>
    /// <param name="targetStack">Project's target stack (default "dotnet8"; "java-spring" once Phase 5.4 lands).</param>
    private (string Id, string Version) ResolvePromptMeta(string sourceLanguage, string targetStack)
    {
        // Phase 5.2 — route by sourceLanguage. The prompt-library lookup
        // returns null when no matching prompt ships for the
        // (schema × target × kind) tuple; fall back to the Fortran
        // defaults so audit trails stay informative even on misroute.
        var loaded = _prompts.GetLatest(sourceLanguage, targetStack, "extract");
        return loaded is not null
            ? (loaded.PromptId, loaded.Version)
            : (DefaultPromptTemplateId, DefaultPromptTemplateVersion);
    }

    public async IAsyncEnumerable<ExtractionEvent> RunAsync(
        Guid subroutineId,
        [EnumeratorCancellation] CancellationToken ct)
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

        // State-machine guard: re-extract is allowed only from PARSED or DRAFT.
        // From IN_REVIEW / SIGNED, replacing the spec would invalidate every
        // existing claim review and signature. Supersession is the proper
        // forward path (Phase C). EXTRACTING means another run is in flight.
        if (sub.State is not ("PARSED" or "DRAFT"))
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
        // Target stack stays "dotnet8" for now; Phase 5.4 introduces the
        // engineer-chosen target-stack override on the scaffold path.
        var sourceLanguage = string.IsNullOrEmpty(sub.SourceLanguage) ? "fortran-f77" : sub.SourceLanguage;
        const string defaultTargetStack = "dotnet8";
        var (promptId, promptVersion) = ResolvePromptMeta(sourceLanguage, defaultTargetStack);
        yield return new("provider_info", new
        {
            name = _provider.Info.Name,
            model = _provider.Info.Model,
            configVersion = _provider.Info.ConfigVersion,
            promptTemplateId = promptId,
            promptTemplateVersion = promptVersion,
        });

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
            TargetStack: defaultTargetStack);

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
        var (specJsonText, inputTokens, outputTokens, latencyMs) = UnpackFinalPayload(finalPayload);

        var llmCall = new LlmCall
        {
            Id = Guid.NewGuid(),
            Provider = _provider.Info.Name,
            Model = _provider.Info.Model,
            PromptTemplateId = promptId,
            PromptTemplateVersion = promptVersion,
            ProviderConfigVersion = _provider.Info.ConfigVersion,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            LatencyMs = latencyMs,
            CostUsd = EstimateCost(_provider.Info.Name, inputTokens, outputTokens),
            Status = "success",
            CalledAt = DateTimeOffset.UtcNow,
        };
        await _db.LlmCalls.AddAsync(llmCall, ct);

        var existing = await _db.Specs.FirstOrDefaultAsync(s => s.SubroutineId == sub.Id, ct);
        var specDoc = JsonDocument.Parse(specJsonText);
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
                model = _provider.Info.Model,
                promptTemplate = $"{promptId}@{promptVersion}",
                inputTokens, outputTokens, latencyMs,
                llmCallId = llmCall.Id,
                providerConfigVersion = _provider.Info.ConfigVersion,
            },
            ct: ct);

        yield return new("done", new
        {
            specId = spec.Id,
            callId = llmCall.Id,
            inputTokens,
            outputTokens,
            latencyMs,
            costUsd = llmCall.CostUsd,
        });

        _logger.LogInformation(
            "Extraction complete: subroutine={Sub} spec={Spec} call={Call} {InTok}/{OutTok} tokens in {Ms}ms",
            sub.Id, spec.Id, llmCall.Id, inputTokens, outputTokens, latencyMs);
    }

    private static (string json, int inTok, int outTok, long ms) UnpackFinalPayload(object payload)
    {
        // Round-trip via JsonElement to avoid reflecting on anonymous types.
        var el = JsonSerializer.SerializeToElement(payload);
        var specJson = el.GetProperty("specJson").GetRawText();
        var inTok = el.TryGetProperty("inputTokens", out var i) ? i.GetInt32() : 0;
        var outTok = el.TryGetProperty("outputTokens", out var o) ? o.GetInt32() : 0;
        var ms = el.TryGetProperty("latencyMs", out var l) ? l.GetInt64() : 0;
        return (specJson, inTok, outTok, ms);
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

    private static decimal EstimateCost(string provider, int inputTokens, int outputTokens) =>
        provider == "mock"
            ? 0m
            : Math.Round((decimal)inputTokens * 0.000003m + (decimal)outputTokens * 0.000015m, 4);
}
