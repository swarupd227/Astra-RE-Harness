using System.Runtime.CompilerServices;

namespace Astra.Api.Llm;

/// <summary>
/// Provider abstraction. Adapters MUST stream <see cref="ExtractionEvent"/>s
/// in the canonical order:
///   stage(priming) → stage(loading_source) → stage(streaming) →
///   patch* / token* / citation_pulse* / warning* →
///   stage(validating) → stage(persisting) →
///   done | error
/// </summary>
public interface ILlmProvider
{
    ProviderInfo Info { get; }

    IAsyncEnumerable<ExtractionEvent> ExtractAsync(
        ExtractionRequest request,
        [EnumeratorCancellation] CancellationToken ct);
}
