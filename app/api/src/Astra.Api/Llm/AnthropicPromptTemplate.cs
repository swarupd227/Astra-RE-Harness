using Astra.Api.Llm.Prompts;

namespace Astra.Api.Llm;

/// <summary>
/// Thin shim around <see cref="PromptLibrary"/>. The actual prompt body
/// lives on disk under <c>Llm/Prompts/&lt;schema&gt;/&lt;target&gt;/extract.v&lt;N&gt;.md</c>
/// as of Phase #3b — this class just resolves the right file and runs
/// template substitution. Kept as a static helper so the rest of the
/// codebase doesn't have to thread the library through every call site
/// (the library is a singleton; we resolve it at the boot of the
/// extraction pipeline and pass the library plus an explicit version
/// pin through to <see cref="Build"/>).
/// </summary>
public static class AnthropicPromptTemplate
{
    public sealed record Built(string System, string User, string PromptId, string Version);

    /// <summary>
    /// Build the (system, user) pair for an extraction request. The caller
    /// supplies the loaded prompt — typically resolved via
    /// <see cref="PromptLibrary.Get"/> or <see cref="PromptLibrary.GetLatest"/>
    /// in <c>ExtractionPipeline</c>.
    /// </summary>
    public static Built Build(PromptLibrary lib, PromptLibrary.LoadedPrompt prompt, ExtractionRequest req)
    {
        var rendered = lib.Render(prompt, new Dictionary<string, string?>
        {
            ["subroutineName"] = req.SubroutineName,
            ["sourcePath"] = req.SourcePath,
            ["lineCount"] = req.LineCount.ToString(),
            ["sourceText"] = req.SourceText,
        });
        return new(rendered.System, rendered.User, rendered.PromptId, rendered.Version);
    }
}
