using System.Text.Json;
using System.Text.Json.Serialization;
using Astra.Api.Persistence.Entities;

namespace Astra.Api.Conversations;

/// <summary>Wire shapes for the conversation spine (mirrors
/// <c>frontend/src/lib/conversations.ts</c>). Records serialise camelCase
/// through the minimal-API defaults; the jsonb columns hold the same
/// shapes so a row round-trips without translation.</summary>
public static class ConversationJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToElement(object? value) =>
        JsonSerializer.SerializeToElement(value, Web);

    public static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Web); }
        catch { return null; }
    }

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Web);
}

public sealed record ArtifactDto(string Kind, string? RefId, JsonElement Props);

public sealed record SuggestionDto(string Label, string Intent);

public sealed record ToolCallDto(string Name, JsonElement Input, string Summary, bool Ok, long DurationMs);

public sealed record PendingActionDto(
    string ToolName, JsonElement Input, string Summary, string? RequiredPersona, string State);

public sealed record ProgrammeSummaryDto(string Name, string? SourceLanguage, int RoutineCount, int FileCount);

public sealed record ConversationDto(
    Guid Id,
    Guid? CorpusId,
    string Kind,
    Guid? RefId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastMessageAt,
    string? LastMessagePreview,
    int MessageCount,
    ProgrammeSummaryDto? Programme);

public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    string Role,
    string? Agent,
    string? Persona,
    string? AuthorDisplay,
    string Markdown,
    IReadOnlyList<ArtifactDto> Artifacts,
    IReadOnlyList<SuggestionDto> Suggestions,
    IReadOnlyList<ToolCallDto> ToolCalls,
    PendingActionDto? PendingAction,
    Guid? RunId,
    DateTimeOffset CreatedAt)
{
    public static MessageDto From(ConversationMessage m) => new(
        m.Id,
        m.ConversationId,
        m.Role,
        m.Agent,
        m.Persona,
        m.AuthorDisplay,
        m.Markdown,
        ConversationJson.Parse<List<ArtifactDto>>(m.ArtifactsJson) ?? new(),
        ConversationJson.Parse<List<SuggestionDto>>(m.SuggestionsJson) ?? new(),
        ConversationJson.Parse<List<ToolCallDto>>(m.ToolCallsJson) ?? new(),
        ConversationJson.Parse<PendingActionDto>(m.PendingActionJson),
        m.RunId,
        m.CreatedAt);
}

public sealed record FunnelCounts(
    int Parsed, int Extracting, int Draft, int InReview, int Signed, int Scaffolded, int Committed, int Total)
{
    public static FunnelCounts Empty => new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Fold raw subroutine states into the seven funnel buckets.</summary>
    public static FunnelCounts FromStates(IEnumerable<(string State, int Count)> rows)
    {
        int parsed = 0, extracting = 0, draft = 0, inReview = 0, signed = 0, scaffolded = 0, committed = 0, total = 0;
        foreach (var (state, count) in rows)
        {
            total += count;
            switch (state.ToUpperInvariant())
            {
                case "PARSED": parsed += count; break;
                case "EXTRACTING": extracting += count; break;
                case "DRAFT": draft += count; break;
                case "IN_REVIEW": inReview += count; break;
                case "SIGNED": signed += count; break;
                case "SCAFFOLDING":
                case "SCAFFOLDED":
                case "VALIDATED": scaffolded += count; break;
                case "COMMITTED": committed += count; break;
                default: parsed += count; break;
            }
        }
        return new(parsed, extracting, draft, inReview, signed, scaffolded, committed, total);
    }

    public FunnelCounts Add(FunnelCounts o) => new(
        Parsed + o.Parsed, Extracting + o.Extracting, Draft + o.Draft, InReview + o.InReview,
        Signed + o.Signed, Scaffolded + o.Scaffolded, Committed + o.Committed, Total + o.Total);
}

public sealed record LatestRunDto(Guid Id, string Kind, string State, DateTimeOffset StartedAt, string Summary);

public sealed record OverviewProgrammeDto(
    Guid CorpusId, Guid ConversationId, string Name, string? SourceLanguage, FunnelCounts Counts, LatestRunDto? LatestRun);

public sealed record TelemetryDto(decimal CostTodayUsd, int CallsToday, double? P50LatencyMs, double? CacheHitRate);

public sealed record OverviewDto(IReadOnlyList<OverviewProgrammeDto> Programmes, FunnelCounts Totals, TelemetryDto Telemetry);
