using System.Runtime.CompilerServices;

namespace Astra.Api.Llm;

/// <summary>
/// Chaos-rehearsal provider. Streams the first three stages cleanly, emits a
/// few patches, then throws a simulated provider failure. Used in §9.4 of the
/// demo dress-rehearsal protocol to verify the client's error UX renders
/// correctly under a mid-stream provider 5xx.
///
/// Activate by setting <c>Llm:Provider=fail-mock</c>.
/// </summary>
public sealed class FailMockLlmProvider : ILlmProvider
{
    public ProviderInfo Info { get; } = new(
        Name: "fail-mock",
        Model: "fail-mock-1",
        ConfigVersion: "fail-mock:offline:errors-by-design");

    public async IAsyncEnumerable<ExtractionEvent> ExtractAsync(
        ExtractionRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new("stage", new { stage = "priming", step = 1, of = 5, label = "Loading prompt template" });
        await Task.Delay(180, ct);
        yield return new("stage", new { stage = "loading_source", step = 2, of = 5, label = $"Reading {request.SourcePath}" });
        await Task.Delay(220, ct);
        yield return new("stage", new { stage = "streaming", step = 3, of = 5, label = "Drafting behavioural specification" });
        await Task.Delay(180, ct);

        // Emit a small amount of plausible content before failing so the UI
        // has something to render before the error block appears.
        yield return new("patch", new { op = "add", path = "/$schema", value = "https://nous.dev/schemas/re-harness/spec/v1.json" });
        yield return new("patch", new { op = "add", path = "/routine", value = request.SubroutineName });
        await Task.Delay(280, ct);

        yield return new("warning", new
        {
            code = "provider.degraded",
            message = "Provider latency above warning threshold; preparing to retry.",
        });
        await Task.Delay(220, ct);

        // Boom — surface as the same shape the SSE pipeline emits for real
        // provider 5xxs. The client's error block + retry CTA exercises here.
        yield return new("error", new
        {
            code = "provider.5xx",
            message = "Provider returned 503 Service Unavailable. Retry, or switch to fallback provider.",
            retryable = true,
        });
    }
}
