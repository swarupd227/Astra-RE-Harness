using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Astra.Api.Persistence;

namespace Astra.Api.Copilot;

/// <summary>What a tool needs to run: the request's scoped services (so it
/// calls the same pipelines the REST endpoints do), who is asking, and the
/// thread it runs in.</summary>
public sealed class ToolContext
{
    public required IServiceProvider Services { get; init; }
    public required AppDbContext Db { get; init; }
    public required DevPersonaContext Actor { get; init; }
    public required Guid ConversationId { get; init; }
    public Guid? CorpusId { get; init; }
    /// <summary>Set in a spec thread: tools that take a spec default to it.</summary>
    public Guid? SpecId { get; init; }
    public required CancellationToken Ct { get; init; }
}

/// <summary>A tool's outcome. <see cref="ModelPayload"/> goes back to the
/// model verbatim (keep it compact); <see cref="Artifacts"/> become cards on
/// the final message; <see cref="Summary"/> is the one-line "source" chip.</summary>
public sealed record ToolResult(
    bool Ok,
    object ModelPayload,
    string Summary,
    IReadOnlyList<ArtifactDto>? Artifacts = null,
    Guid? RunId = null)
{
    public static ToolResult Success(object payload, string summary, params ArtifactDto[] artifacts) =>
        new(true, payload, summary, artifacts);

    public static ToolResult Failure(string code, string message, object? extra = null) =>
        new(false, new { error = code, message, details = extra }, message);

    public static ArtifactDto Artifact(string kind, string? refId, object props) =>
        new(kind, refId, ConversationJson.ToElement(props));
}

/// <summary>One entry of the orchestrator's tool registry.</summary>
public sealed class CopilotTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON-Schema object for the tool's input (Anthropic `input_schema`).</summary>
    public required object InputSchema { get; init; }
    /// <summary>Which agent "speaks" when this tool's result is narrated.</summary>
    public required string Agent { get; init; }
    /// <summary>Personas allowed to run it; empty = anyone.</summary>
    public IReadOnlyList<Persona> AllowedPersonas { get; init; } = Array.Empty<Persona>();
    /// <summary>State-changing tools pause for an explicit confirmation turn.</summary>
    public bool Mutating { get; init; }
    /// <summary>Human sentence for the confirmation card, e.g. "sign the spec for `X` as SME".</summary>
    public Func<JsonElement, ToolContext, Task<string>>? Describe { get; init; }
    public required Func<JsonElement, ToolContext, Task<ToolResult>> Execute { get; init; }

    public bool AllowedFor(Persona p) => AllowedPersonas.Count == 0 || AllowedPersonas.Contains(p);

    public string? RequiredPersonaLabel => AllowedPersonas.Count == 0
        ? null
        : string.Join(" or ", AllowedPersonas.Select(p => p.ToString().ToLowerInvariant()));
}

/// <summary>Streaming events the orchestrator emits while handling a turn
/// (mirrors the SSE contract: user | status | tool_result | message | error | done).</summary>
public sealed record CopilotEvent(string Type, object Data);

public sealed class CopilotOptions
{
    /// <summary>Model for the orchestrator loop; null = <c>Llm:Anthropic:Model</c>.</summary>
    public string? Model { get; set; }
    public int MaxOutputTokens { get; set; } = 2048;
    /// <summary>Upper bound on model⇄tool rounds per user turn.</summary>
    public int MaxToolRounds { get; set; } = 8;
    /// <summary>How many prior thread messages are replayed to the model.</summary>
    public int HistoryMessages { get; set; } = 30;
}
