using System.Text.Json;
using Astra.Api.Artefacts;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm;

namespace Astra.Api.Copilot;

/// <summary>
/// The project's migration target as a sentence: "migrate this project to Java Spring", "target .NET 10". The same
/// setting the Project settings page edits — specs are extracted for it and code is generated for it.
/// </summary>
public sealed partial class CopilotToolRegistry
{
    private void RegisterTargetTools()
    {
        _tools.Add(new CopilotTool
        {
            Name = "set_target_stack",
            Agent = "planning",
            Mutating = true,
            AllowedPersonas = Drafters,
            Description = "Set the stack a programme migrates to (java-spring, dotnet10, dotnet8, react-python, angular-java, angular-dotnet8), or clear it so each routine goes to its language's default (PHP and COBOL to Java Spring, the .NET-bound languages to .NET 10). Specs drafted afterwards are written for that stack and code is generated for it unless a routine asks for another. Use for 'migrate this project to Java', 'target .NET 10', 'use the default target'. Engineer or Admin.",
            InputSchema = Obj(
                ("corpusId", Str("Programme id or name. Optional in a programme thread.")),
                ("targetStack", Str("Target stack id, or 'default' to clear it."))),
            Describe = async (input, ctx) =>
            {
                var name = (await ResolveCorpusAsync(input, ctx))?.Name ?? "this programme";
                var target = NormaliseTarget(Read(input, "targetStack"));
                return target is null
                    ? $"migrate {name} to each language's default target"
                    : $"migrate {name} to {target}";
            },
            Execute = SetTargetStackAsync,
        });
    }

    /// <summary>"Java Spring", "java", ".NET 10" … → a stack id; "default"/empty → null.</summary>
    internal static string? NormaliseTarget(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        if (t is "" or "default" or "none" or "clear" or "language default") return null;
        return t switch
        {
            "java" or "spring" or "java spring" or "spring boot" or "java-spring" => "java-spring",
            ".net 10" or "net10" or "dotnet 10" or ".net" or "dotnet" => "dotnet10",
            ".net 8" or "net8" or "dotnet 8" => "dotnet8",
            "python" or "react" or "react python" or "react + python" => "react-python",
            _ => t.Replace(' ', '-'),
        };
    }

    private static async Task<ToolResult> SetTargetStackAsync(JsonElement input, ToolContext ctx)
    {
        var corpus = await ResolveCorpusAsync(input, ctx);
        if (corpus is null) return NoCorpus(input);
        var target = NormaliseTarget(Read(input, "targetStack"));
        if (target is not null && !TargetStacks.IsKnown(target))
            return ToolResult.Failure("settings.invalid",
                $"'{Read(input, "targetStack")}' is not a target stack. Choose java-spring, dotnet10, dotnet8, react-python, angular-java or angular-dotnet8.");

        var tracked = await ctx.Db.Corpora.FindAsync(new object[] { corpus.Id }, ctx.Ct);
        if (tracked is null) return NoCorpus(input);
        var before = ProjectSettings.Parse(tracked.SettingsJson);
        var after = before with { TargetStack = target };
        tracked.SettingsJson = after.ToJson();
        await ctx.Db.SaveChangesAsync(ctx.Ct);
        await ctx.Services.GetRequiredService<IAuditLogger>().LogAsync(
            "corpus.settings.updated", "corpus", corpus.Id, ctx.Actor,
            new { targetStack = target, previousTargetStack = before.TargetStack, via = "copilot" }, ct: ctx.Ct);

        var summary = target is null
            ? $"{corpus.Name} now migrates each code unit to its language's default target."
            : $"{corpus.Name} now migrates to {target}: new specs are written for it and code is generated for it.";
        return ToolResult.Success(new
        {
            corpusId = corpus.Id,
            corpusName = corpus.Name,
            targetStack = target,
            previousTargetStack = before.TargetStack,
        }, summary);
    }
}
