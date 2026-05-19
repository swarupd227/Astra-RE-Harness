using System.Text;
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
            // Phase 7.0 — structured cross-routine context. Empty
            // string when no neighbourhood was supplied, so the
            // template's "## Neighbourhood" section collapses cleanly.
            ["neighbourhood"] = RenderNeighbourhood(req.Neighbourhood),
        });
        return new(rendered.System, rendered.User, rendered.PromptId, rendered.Version);
    }

    /// <summary>
    /// Render the structured <see cref="Neighbourhood"/> as a markdown
    /// block the LLM can parse. We use a deterministic layout so the
    /// portion of the user message that comes BEFORE the per-routine
    /// source remains cache-stable across calls for the same
    /// neighbourhood — important when prompt caching lands here later.
    /// </summary>
    public static string RenderNeighbourhood(Neighbourhood? nbh)
    {
        if (nbh is null || nbh.IsEmpty) return "";
        var sb = new StringBuilder();
        sb.AppendLine("## Neighbourhood (structured cross-routine context from the parser's call graph + COMMON refs)");
        sb.AppendLine();
        sb.AppendLine("This subroutine does NOT exist in isolation. The following information was extracted at parse time from the rest of the corpus. Use it as INPUT to your extraction: callees become `side_effects` / `io_side_effects` candidates; callers establish how this routine is invoked; COMMON-block declarers point at shared-state contracts.");
        sb.AppendLine();
        if (nbh.Callees.Count > 0)
        {
            sb.AppendLine("### Callees (routines this one CALLs)");
            foreach (var c in nbh.Callees) AppendRoutine(sb, c);
            sb.AppendLine();
        }
        if (nbh.Callers.Count > 0)
        {
            sb.AppendLine("### Callers (routines that CALL this one)");
            foreach (var c in nbh.Callers) AppendRoutine(sb, c);
            sb.AppendLine();
        }
        if (nbh.CommonBlocks.Count > 0)
        {
            sb.AppendLine("### COMMON blocks referenced (shared storage)");
            foreach (var b in nbh.CommonBlocks)
            {
                sb.Append("- `/").Append(b.Name).Append("/`");
                if (!string.IsNullOrEmpty(b.DeclaringRoutine))
                {
                    sb.Append(" — first declared in `").Append(b.DeclaringRoutine).Append("`");
                    if (!string.IsNullOrEmpty(b.DeclaringRoutineSourcePath))
                        sb.Append(" (").Append(b.DeclaringRoutineSourcePath).Append(")");
                    if (!string.IsNullOrEmpty(b.DeclaringRoutineSignature))
                        sb.AppendLine().Append("    signature: `").Append(b.DeclaringRoutineSignature).Append("`");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        if (nbh.SiblingsInFile.Count > 0)
        {
            sb.AppendLine("### Siblings in the same source file (likely tightly coupled)");
            foreach (var s in nbh.SiblingsInFile) AppendRoutine(sb, s);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendRoutine(StringBuilder sb, RoutineRef r)
    {
        sb.Append("- `").Append(r.Name).Append("`");
        if (r.Resolution == "external") sb.Append(" *(external — not in this corpus)*");
        if (!string.IsNullOrEmpty(r.SourcePath)) sb.Append(" (").Append(r.SourcePath).Append(")");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(r.Signature)) sb.Append("    signature: `").Append(r.Signature).AppendLine("`");
        if (!string.IsNullOrEmpty(r.SpecSummary)) sb.Append("    spec: ").AppendLine(r.SpecSummary);
        if (r.KnownSideEffects.Count > 0)
        {
            sb.AppendLine("    side effects (from spec):");
            foreach (var se in r.KnownSideEffects.Take(3)) sb.Append("      - ").AppendLine(se);
        }
    }
}
