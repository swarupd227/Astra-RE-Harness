using System.Runtime.CompilerServices;

namespace Astra.Api.Llm;

/// <summary>
/// Stage-5 (scaffold) provider abstraction. In Phase B.4 only the offline
/// <see cref="MockScaffoldProvider"/> is wired; the Azure OpenAI adapter ships
/// in B.4.x once the user has tenant + key configured. The interface stays
/// stable so the registration is the only swap.
/// </summary>
public interface IScaffoldProvider
{
    ProviderInfo Info { get; }

    IAsyncEnumerable<ExtractionEvent> GenerateAsync(
        ScaffoldRequest request,
        [EnumeratorCancellation] CancellationToken ct);
}

public sealed record ScaffoldRequest(
    Guid SpecId,
    string SubroutineName,
    string SourcePath,
    string SignedSpecJson,
    string TargetPlatform,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string SourceSchema = "",
    string OriginalSourceText = "");

public sealed record ScaffoldFile(
    string Path,
    string Language,
    string Content,
    int LineCount,
    int TodoCount,
    string[] DerivedFromClaimIds);
