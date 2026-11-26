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
    string OriginalSourceText = "",
    // What the previous attempt's gate said (GateFailureDigest.ToRepairHint):
    // appended to the prompt so the regeneration fixes those errors instead
    // of repeating them. Null for a first attempt.
    string? RepairHint = null,
    // WS3 Mode A — set only for a faithful 1:1 conversion: the whole unit
    // around the routine (every sibling routine, the signed specs among
    // them) that the provider converts as one file. See FaithfulConversion.
    FaithfulConversion.UnitContext? Unit = null,
    // The failed package's `files` array (manifest JSON) when this is a
    // repair: the model edits it instead of rewriting the unit from scratch,
    // which is what made repair rounds oscillate instead of converge.
    string? PreviousPackageFilesJson = null,
    // The workbook's lookup sheets, described (where each data file will be, its shape, a few real rows). The files
    // themselves are added to the package by the pipeline, not written by the model. Null when there are none.
    string? DataTablesNote = null,
    // The source of the routine and of the routines it calls (CallTreeSource), so a request it sends to an outside
    // service is reproduced from code rather than guessed from the spec. Null when it calls nothing of the project.
    string? CallTreeSourceNote = null,
    // For a workbook routine: its host workbook's view (labels, controls) and the current values and allowed values of
    // the sheets its module reads (WorkbookInputs), so the form starts where the workbook was and offers real choices.
    string? WorkbookInputsNote = null);

public sealed record ScaffoldFile(
    string Path,
    string Language,
    string Content,
    int LineCount,
    int TodoCount,
    string[] DerivedFromClaimIds);
