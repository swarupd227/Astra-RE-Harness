namespace Astra.Api.Persistence.Entities;

/// <summary>
/// WS2 — the conversation spine. Every programme (corpus) has a thread; the
/// global thread ("Ask Astra") spans programmes. Agents post into threads
/// (the Narrator, after a run finishes), users type intents into them, and
/// the orchestrator replies with markdown + artifact cards.
/// </summary>
public sealed class Conversation
{
    public Guid Id { get; set; }

    /// <summary>Null for the global thread.</summary>
    public Guid? CorpusId { get; set; }

    /// <summary>"global" | "programme" | "spec" | "blueprint" | "wave".</summary>
    public string Kind { get; set; } = "programme";

    /// <summary>What a non-programme thread is about: the spec id for
    /// <c>spec</c> threads (later: blueprint / wave ids). Null otherwise.</summary>
    public Guid? RefId { get; set; }

    public string Title { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }
    public string? LastMessagePreview { get; set; }
    public int MessageCount { get; set; }

    public Corpus? Corpus { get; set; }
}

/// <summary>
/// One turn in a thread. Agent turns carry the structured extras the
/// Workspace renders: artifact cards, suggestion chips (phrased as intents),
/// the tool calls that ground the answer, and — for a state-changing action
/// awaiting the user's go-ahead — the pending action.
/// </summary>
public sealed class ConversationMessage
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>"user" | "agent" | "system".</summary>
    public string Role { get; set; } = "user";

    /// <summary>orchestrator | discovery | architecture | planning | spec |
    /// migration | data | validation | release | programme. Null for users.</summary>
    public string? Agent { get; set; }

    /// <summary>Persona of the human author (user turns) or the persona the
    /// agent acted as (agent turns).</summary>
    public string? Persona { get; set; }
    public string? AuthorDisplay { get; set; }

    public string Markdown { get; set; } = "";

    /// <summary>JSON array of {kind, refId, props}.</summary>
    public string ArtifactsJson { get; set; } = "[]";
    /// <summary>JSON array of {label, intent}.</summary>
    public string SuggestionsJson { get; set; } = "[]";
    /// <summary>JSON array of {name, input, summary, ok, durationMs}.</summary>
    public string ToolCallsJson { get; set; } = "[]";
    /// <summary>{toolName, input, summary, requiredPersona, state} or null.</summary>
    public string? PendingActionJson { get; set; }

    /// <summary>The raw model-side turns (assistant tool_use + tool_result
    /// pairs) produced while generating this message, so a paused turn can
    /// be continued exactly after the user confirms.</summary>
    public string? LlmTurnsJson { get; set; }

    /// <summary>A run this message started or narrates (pattern analysis,
    /// extraction, scaffold, gate…). Same id space as RunEventBus.</summary>
    public Guid? RunId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Conversation? Conversation { get; set; }
}
