using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Astra.Api.Llm;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Copilot;

/// <summary>
/// Astra — the orchestrator. A Claude tool-use loop over
/// <see cref="CopilotToolRegistry"/>: read tools run immediately; a
/// state-changing tool pauses the turn and persists a pending action the
/// user confirms (or declines) from the thread. Every answer is grounded in
/// tool results, which are kept on the message as "sources"; every tool
/// result that carries a card becomes an artifact on the reply.
/// </summary>
public sealed class CopilotOrchestrator
{
    public const string FinishTool = "finish_turn";

    private readonly ConversationService _conversations;
    private readonly CopilotToolRegistry _registry;
    private readonly ICopilotBrain _brain;
    private readonly PromptLibrary _prompts;
    private readonly AppDbContext _db;
    private readonly IServiceProvider _services;
    private readonly CopilotOptions _opts;
    private readonly ILogger<CopilotOrchestrator> _logger;

    public CopilotOrchestrator(
        ConversationService conversations, CopilotToolRegistry registry, ICopilotBrain brain, PromptLibrary prompts,
        AppDbContext db, IServiceProvider services, IOptions<CopilotOptions> opts, ILogger<CopilotOrchestrator> logger)
    {
        _conversations = conversations;
        _registry = registry;
        _brain = brain;
        _prompts = prompts;
        _db = db;
        _services = services;
        _opts = opts.Value;
        _logger = logger;
    }

    private sealed class TurnState
    {
        public List<object> Turns { get; } = new();
        public List<ToolCallDto> ToolCalls { get; } = new();
        public List<ArtifactDto> Artifacts { get; } = new();
        public List<SuggestionDto> Suggestions { get; } = new();
        public string? FinalMarkdown { get; set; }
        public Guid? RunId { get; set; }
        public int Rounds { get; set; }
    }

    private sealed record StoredTurns(List<JsonElement> Turns, string? PendingToolUseId);

    // ── Entry points ─────────────────────────────────────────────────────

    public async Task HandleUserTurnAsync(
        Conversation conv, string text, DevPersonaContext actor, Func<CopilotEvent, Task> emit, CancellationToken ct)
    {
        var user = await _conversations.AppendAsync(new ConversationMessage
        {
            ConversationId = conv.Id,
            Role = "user",
            Persona = actor.Persona.ToString().ToLowerInvariant(),
            AuthorDisplay = actor.DisplayName,
            Markdown = text,
        }, ct);
        await emit(new CopilotEvent("user", user));

        var rows = await _conversations.RecentEntitiesAsync(conv.Id, _opts.HistoryMessages, ct);
        var history = BuildHistory(rows.Where(r => r.Id != user.Id));
        history.Add(UserText(text));

        var state = new TurnState();
        await RunLoopAsync(conv, actor, history, state, emit, ct);
    }

    public async Task ContinueAsync(
        Conversation conv, ConversationMessage pending, DevPersonaContext actor, Func<CopilotEvent, Task> emit, CancellationToken ct)
    {
        var action = ConversationJson.Parse<PendingActionDto>(pending.PendingActionJson);
        var stored = ConversationJson.Parse<StoredTurns>(pending.LlmTurnsJson);
        if (action is null || stored is null || action.State != "pending")
        {
            await emit(new CopilotEvent("error", new { code = "confirm.not_pending", message = "That action is no longer pending." }));
            return;
        }

        var tool = _registry.Find(action.ToolName);
        if (tool is null)
        {
            await emit(new CopilotEvent("error", new { code = "confirm.unknown_tool", message = $"Unknown tool {action.ToolName}." }));
            return;
        }
        if (!tool.AllowedFor(actor.Persona))
        {
            await emit(new CopilotEvent("error", new
            {
                code = "auth.persona_required",
                message = $"Confirming this needs the {tool.RequiredPersonaLabel} persona (you are {actor.Persona.ToString().ToLowerInvariant()}).",
            }));
            return;
        }

        // Mark confirmed first so a crash mid-execution can't re-run it.
        pending.PendingActionJson = ConversationJson.Serialize(action with { State = "confirmed" });
        await _conversations.UpdateAsync(pending, ct);

        var rows = await _conversations.RecentEntitiesAsync(conv.Id, _opts.HistoryMessages + 5, ct);
        var history = BuildHistory(rows.Where(r => r.CreatedAt < pending.CreatedAt && r.Id != pending.Id));
        foreach (var t in stored.Turns) history.Add(t);

        var state = new TurnState();
        state.ToolCalls.AddRange(ConversationJson.Parse<List<ToolCallDto>>(pending.ToolCallsJson) ?? new());
        state.Artifacts.AddRange(ConversationJson.Parse<List<ArtifactDto>>(pending.ArtifactsJson) ?? new());

        // Results for every tool_use in the paused assistant turn.
        var lastAssistant = stored.Turns.LastOrDefault();
        var results = new List<object>();
        if (lastAssistant.ValueKind == JsonValueKind.Object && lastAssistant.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var ctx = MakeContext(conv, actor, ct);
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var ty) || ty.GetString() != "tool_use") continue;
                var id = block.GetProperty("id").GetString() ?? "";
                var name = block.GetProperty("name").GetString() ?? "";
                var input = block.TryGetProperty("input", out var inp) ? inp : JsonDocument.Parse("{}").RootElement;
                var t = _registry.Find(name);
                if (id == stored.PendingToolUseId || (t is not null && !t.Mutating))
                {
                    var r = await ExecuteAsync(t!, input, ctx, state, emit);
                    results.Add(ToolResultBlock(id, r));
                }
                else if (name == FinishTool)
                {
                    results.Add(ToolResultBlock(id, ToolResult.Success(new { ok = true }, "finish")));
                }
                else
                {
                    results.Add(ToolResultBlock(id, ToolResult.Failure("not_confirmed", "This action was not confirmed by the user; ask again if it is still needed.")));
                }
            }
        }
        history.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = results });
        state.Turns.Add(history[^1]);

        await RunLoopAsync(conv, actor, history, state, emit, ct);
    }

    public async Task<MessageDto> DeclineAsync(ConversationMessage pending, CancellationToken ct)
    {
        var action = ConversationJson.Parse<PendingActionDto>(pending.PendingActionJson);
        if (action is not null && action.State == "pending")
            pending.PendingActionJson = ConversationJson.Serialize(action with { State = "declined" });
        return await _conversations.UpdateAsync(pending, ct);
    }

    // ── The loop ─────────────────────────────────────────────────────────

    private async Task RunLoopAsync(
        Conversation conv, DevPersonaContext actor, List<object> history, TurnState state,
        Func<CopilotEvent, Task> emit, CancellationToken ct)
    {
        var ctx = MakeContext(conv, actor, ct);
        var system = await BuildSystemPromptAsync(conv, actor, ct);
        var tools = BuildToolSpecs();

        try
        {
            while (state.Rounds++ < _opts.MaxToolRounds)
            {
                await emit(new CopilotEvent("status", new { phase = "thinking", label = state.Rounds == 1 ? "Thinking" : "Deciding what to do next" }));

                BrainResponse resp;
                try
                {
                    resp = await _brain.CompleteAsync(new BrainRequest(system, history, tools), ct);
                }
                catch (AnthropicRequestException ex)
                {
                    _logger.LogWarning(ex, "Copilot model call failed");
                    var detail = ExtractApiError(ex.Message);
                    await FinishAsync(conv, actor, state, emit, ct,
                        fallback: $"The model call failed ({ex.StatusCode}{(detail is null ? "" : $": {detail}")}). " +
                                  (ex.StatusCode is 429 or >= 500 ? "Please try again in a moment." : "This needs a code or configuration fix — the message above is the provider's own reason."));
                    return;
                }
                await RecordLlmCallAsync(resp, ct);

                var texts = resp.Blocks.Where(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text)).Select(b => b.Text!).ToList();
                var toolUses = resp.Blocks.Where(b => b.Type == "tool_use").ToList();

                if (toolUses.Count == 0)
                {
                    state.FinalMarkdown = StripTranscriptNotes(string.Join("\n\n", texts));
                    break;
                }

                // Rebuild the assistant turn from the parsed blocks rather than
                // echoing the raw response array: response-only fields and
                // whitespace-only text blocks are both rejected on input.
                var assistantTurn = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = AssistantContent(resp.Blocks) };
                history.Add(assistantTurn);
                state.Turns.Add(assistantTurn);

                var results = new List<object>();
                var finished = false;
                foreach (var tu in toolUses)
                {
                    var name = tu.ToolName ?? "";
                    var input = tu.Input ?? JsonDocument.Parse("{}").RootElement;

                    if (name == FinishTool)
                    {
                        state.FinalMarkdown = StripTranscriptNotes(CopilotToolRegistry.Read(input, "markdown") ?? string.Join("\n\n", texts));
                        state.Suggestions.Clear();
                        if (input.TryGetProperty("suggestions", out var sugg) && sugg.ValueKind == JsonValueKind.Array)
                            foreach (var s in sugg.EnumerateArray())
                            {
                                var label = CopilotToolRegistry.Read(s, "label");
                                var intent = CopilotToolRegistry.Read(s, "intent") ?? label;
                                if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(intent))
                                    state.Suggestions.Add(new SuggestionDto(label!, intent!));
                            }
                        results.Add(ToolResultBlock(tu.ToolUseId!, ToolResult.Success(new { ok = true }, "finish")));
                        finished = true;
                        continue;
                    }

                    var tool = _registry.Find(name);
                    if (tool is null)
                    {
                        results.Add(ToolResultBlock(tu.ToolUseId!, ToolResult.Failure("tool.unknown", $"No tool named {name}.")));
                        continue;
                    }
                    if (!tool.AllowedFor(actor.Persona))
                    {
                        var r = ToolResult.Failure("auth.persona_required",
                            $"{name} needs the {tool.RequiredPersonaLabel} persona; the current user is {actor.Persona.ToString().ToLowerInvariant()}. " +
                            "Tell the user which persona to switch to (persona menu, top right) and what will happen then.");
                        state.ToolCalls.Add(new ToolCallDto(name, input, r.Summary, false, 0));
                        await emit(new CopilotEvent("tool_result", new { name, ok = false, summary = r.Summary, durationMs = 0 }));
                        results.Add(ToolResultBlock(tu.ToolUseId!, r));
                        continue;
                    }

                    if (tool.Mutating)
                    {
                        await PauseForConfirmationAsync(conv, actor, tool, tu, input, texts, state, ctx, emit, ct);
                        return;
                    }

                    var result = await ExecuteAsync(tool, input, ctx, state, emit);
                    results.Add(ToolResultBlock(tu.ToolUseId!, result));
                }

                var userTurn = new Dictionary<string, object?> { ["role"] = "user", ["content"] = results };
                history.Add(userTurn);
                state.Turns.Add(userTurn);

                if (finished) break;
            }

            await FinishAsync(conv, actor, state, emit, ct,
                fallback: "I've hit my step limit for this turn — here's what I have so far.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Copilot turn cancelled for conversation {Conv}", conv.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copilot turn failed for conversation {Conv}", conv.Id);
            await emit(new CopilotEvent("error", new { code = "copilot.unhandled", message = ex.Message }));
        }
    }

    private async Task PauseForConfirmationAsync(
        Conversation conv, DevPersonaContext actor, CopilotTool tool, BrainBlock tu, JsonElement input,
        List<string> texts, TurnState state, ToolContext ctx, Func<CopilotEvent, Task> emit, CancellationToken ct)
    {
        string summary;
        try
        {
            summary = tool.Describe is not null ? await tool.Describe(input, ctx) : $"run {tool.Name}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Describe failed for {Tool}", tool.Name);
            summary = $"run {tool.Name}";
        }

        var markdown = texts.Count > 0 ? string.Join("\n\n", texts) : $"I'll {summary}.";
        if (!markdown.Contains("confirm", StringComparison.OrdinalIgnoreCase))
            markdown += "\n\nConfirm and I'll go ahead.";

        var pending = new PendingActionDto(tool.Name, input, summary, tool.RequiredPersonaLabel, "pending");
        var stored = new StoredTurns(
            state.Turns.Select(t => JsonSerializer.SerializeToElement(t, ConversationJson.Web)).ToList(),
            tu.ToolUseId);

        var message = await _conversations.AppendAsync(new ConversationMessage
        {
            ConversationId = conv.Id,
            Role = "agent",
            Agent = "orchestrator",
            Persona = actor.Persona.ToString().ToLowerInvariant(),
            AuthorDisplay = "Astra",
            Markdown = markdown,
            ArtifactsJson = ConversationJson.Serialize(DedupeArtifacts(state.Artifacts)),
            SuggestionsJson = "[]",
            ToolCallsJson = ConversationJson.Serialize(state.ToolCalls),
            PendingActionJson = ConversationJson.Serialize(pending),
            LlmTurnsJson = ConversationJson.Serialize(stored),
        }, ct);

        await emit(new CopilotEvent("message", message));
        await emit(new CopilotEvent("done", new { }));
    }

    private async Task FinishAsync(
        Conversation conv, DevPersonaContext actor, TurnState state, Func<CopilotEvent, Task> emit, CancellationToken ct, string fallback)
    {
        var markdown = string.IsNullOrWhiteSpace(state.FinalMarkdown) ? fallback : state.FinalMarkdown!;
        if (state.Suggestions.Count == 0 && state.RunId is not null)
            state.Suggestions.Add(new SuggestionDto("What's the status?", "What's the status of this programme?"));

        var message = await _conversations.AppendAsync(new ConversationMessage
        {
            ConversationId = conv.Id,
            Role = "agent",
            Agent = "orchestrator",
            Persona = actor.Persona.ToString().ToLowerInvariant(),
            AuthorDisplay = "Astra",
            Markdown = markdown,
            ArtifactsJson = ConversationJson.Serialize(DedupeArtifacts(state.Artifacts)),
            SuggestionsJson = ConversationJson.Serialize(state.Suggestions.Take(4)),
            ToolCallsJson = ConversationJson.Serialize(state.ToolCalls),
            RunId = state.RunId,
        }, ct);
        await emit(new CopilotEvent("message", message));
        await emit(new CopilotEvent("done", new { }));
    }

    private async Task<ToolResult> ExecuteAsync(
        CopilotTool tool, JsonElement input, ToolContext ctx, TurnState state, Func<CopilotEvent, Task> emit)
    {
        await emit(new CopilotEvent("status", new { phase = "tool", tool = tool.Name, label = Humanize(tool.Name, input) }));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await tool.Execute(input, ctx);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} threw", tool.Name);
            result = ToolResult.Failure("tool.exception", ex.Message);
        }
        sw.Stop();

        state.ToolCalls.Add(new ToolCallDto(tool.Name, input, result.Summary, result.Ok, sw.ElapsedMilliseconds));
        if (result.Artifacts is { Count: > 0 }) state.Artifacts.AddRange(result.Artifacts);
        if (result.RunId is { } rid) state.RunId = rid;
        await emit(new CopilotEvent("tool_result", new { name = tool.Name, ok = result.Ok, summary = result.Summary, durationMs = sw.ElapsedMilliseconds }));
        return result;
    }

    // ── Prompt + history ─────────────────────────────────────────────────

    private async Task<string> BuildSystemPromptAsync(Conversation conv, DevPersonaContext actor, CancellationToken ct)
    {
        var context = await BuildProgrammeContextAsync(conv, ct);
        var vars = new Dictionary<string, string?>
        {
            ["persona"] = actor.Persona.DisplayName(),
            ["personaKey"] = actor.Persona.ToString().ToLowerInvariant(),
            ["displayName"] = actor.DisplayName,
            ["threadKind"] = conv.Kind,
            ["threadTitle"] = conv.Title,
            ["programmeContext"] = context,
            ["today"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
            ["userMessage"] = "",
        };
        var loaded = _prompts.GetLatest("common", "dotnet8", "copilot-orchestrator");
        if (loaded is null)
        {
            _logger.LogWarning("copilot-orchestrator prompt missing; using the built-in fallback");
            return FallbackSystem(vars);
        }
        return _prompts.Render(loaded, vars).System;
    }

    private async Task<string> BuildProgrammeContextAsync(Conversation conv, CancellationToken ct)
    {
        var lines = new List<string>();
        if (conv.Kind == "spec" && conv.RefId is { } specId)
        {
            var spec = await _db.Specs.AsNoTracking().Include(s => s.Subroutine).ThenInclude(s => s!.SourceFile)
                .FirstOrDefaultAsync(s => s.Id == specId, ct);
            if (spec is not null)
            {
                var reviews = await _db.ClaimReviews.AsNoTracking().CountAsync(r => r.SpecId == specId, ct);
                lines.Add($"This thread is the review thread for the spec of `{spec.Subroutine?.Name}` (specId {spec.Id}, subroutineId {spec.SubroutineId}) " +
                          $"in `{spec.Subroutine?.SourceFile?.RelativePath}` lines {spec.Subroutine?.LineStart}–{spec.Subroutine?.LineEnd}.");
                lines.Add($"Spec state: {spec.State}; {reviews} claim decisions recorded so far. Tools that take a spec default to this one — " +
                          "call get_spec first when you need the claim ids, explain_claim for 'why', review_claim / review_all_claims / sign_spec to act.");
                lines.Add("");
            }
        }
        if (conv.CorpusId is { } cid)
        {
            var corpus = await _db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
            if (corpus is null) return "(programme not found)";
            var (routines, files, language) = await _conversations.ProgrammeStatsAsync(corpus, ct);
            var f = await _conversations.FunnelAsync(corpus, ct);
            var run = await _db.PatternAnalysisRuns.AsNoTracking().Where(r => r.CorpusId == cid)
                .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
            lines.Add($"This thread is the programme **{corpus.Name}** (corpusId {corpus.Id}).");
            lines.Add($"Source: {ConversationService.LanguageLabel(language)} ({language}); {routines:N0} routines in {files:N0} files.");
            lines.Add($"Funnel: parsed {f.Parsed}, extracting {f.Extracting}, draft {f.Draft}, in review {f.InReview}, signed {f.Signed}, built {f.Scaffolded}, committed {f.Committed} (total {f.Total}).");
            lines.Add(run is null
                ? "Pattern analysis: never run."
                : $"Latest pattern analysis: {run.State} (started {run.StartedAt:u}) — {run.Summary}");
        }
        else
        {
            lines.Add("This is the global thread (\"Ask Astra\") — questions may span programmes. When a programme matters and the user hasn't named one, ask or call list_programmes.");
            var corpora = await _db.Corpora.AsNoTracking().Where(c => c.LatestVersionId != null)
                .OrderByDescending(c => c.UpdatedAt).Take(12).ToListAsync(ct);
            foreach (var c in corpora)
            {
                var (routines, _, language) = await _conversations.ProgrammeStatsAsync(c, ct);
                var f = await _conversations.FunnelAsync(c, ct);
                lines.Add($"- {c.Name} (corpusId {c.Id}): {ConversationService.LanguageLabel(language)}, {routines:N0} routines; signed {f.Signed}, built {f.Scaffolded}, committed {f.Committed}.");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// History shows an earlier assistant turn's confirmed action as a
    /// bracketed note (see BuildHistory). Seen live: the model copied the
    /// note into its own answer — "[proposed action generate_scaffold:
    /// confirmed] Retrying…" — without calling the tool, so nothing ran
    /// while the thread read as if it had. The prompt now forbids it; this
    /// keeps a stray one out of what the user sees.
    /// </summary>
    public static string StripTranscriptNotes(string markdown) =>
        System.Text.RegularExpressions.Regex.Replace(markdown, @"\s*\[proposed action [^\]]*\]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    private static string FallbackSystem(Dictionary<string, string?> v) =>
        $"You are Astra, the orchestrator of a legacy-modernization programme at Artizent. Acting persona: {v["persona"]}.\n" +
        "Use tools for every fact; never assert status you didn't read. State-changing tools pause for user confirmation automatically. " +
        "Answer briefly in markdown and end by calling finish_turn with 2–4 suggestions.\n\n" + v["programmeContext"];

    private List<object> BuildHistory(IEnumerable<ConversationMessage> rows)
    {
        var history = new List<Dictionary<string, object?>>();
        foreach (var m in rows)
        {
            string role;
            string text;
            if (m.Role == "user")
            {
                role = "user";
                text = m.Markdown;
            }
            else if (m.Role == "agent" && m.Agent == "orchestrator")
            {
                role = "assistant";
                text = m.Markdown;
                var action = ConversationJson.Parse<PendingActionDto>(m.PendingActionJson);
                if (action is not null) text += $"\n\n[proposed action {action.ToolName}: {action.State}]";
            }
            else
            {
                role = "user";
                text = $"[{Narrator.AgentName(m.Agent ?? "system")} agent posted to the thread]\n{m.Markdown}";
            }
            text = ArtifactBuilders.Truncate(text, 2500);
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (history.Count > 0 && (string)history[^1]["role"]! == role)
                history[^1]["content"] = history[^1]["content"] + "\n\n" + text;
            else
                history.Add(new Dictionary<string, object?> { ["role"] = role, ["content"] = text });
        }
        while (history.Count > 0 && (string)history[0]["role"]! != "user") history.RemoveAt(0);
        return history.Cast<object>().ToList();
    }

    private static Dictionary<string, object?> UserText(string text) => new() { ["role"] = "user", ["content"] = text };

    private List<object> BuildToolSpecs()
    {
        var specs = _registry.Tools.Select(t => (object)new Dictionary<string, object?>
        {
            ["name"] = t.Name,
            ["description"] = t.Description + (t.Mutating ? " (State-changing: the user will be asked to confirm before it runs.)" : ""),
            ["input_schema"] = t.InputSchema,
        }).ToList();
        specs.Add(new Dictionary<string, object?>
        {
            ["name"] = FinishTool,
            ["description"] = "Finish the turn: your final answer as markdown plus 2–4 suggested next intents phrased exactly as the user would type them. Always call this last.",
            ["input_schema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["required"] = new[] { "markdown" },
                ["properties"] = new Dictionary<string, object?>
                {
                    ["markdown"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The reply, in markdown. Concise; routine names in backticks." },
                    ["suggestions"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["required"] = new[] { "label", "intent" },
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["label"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "≤ 5 words, shown on the chip." },
                                ["intent"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "The full sentence sent when clicked." },
                            },
                        },
                    },
                },
            },
        });
        return specs;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ToolContext MakeContext(Conversation conv, DevPersonaContext actor, CancellationToken ct) => new()
    {
        Services = _services,
        Db = _db,
        Actor = actor,
        ConversationId = conv.Id,
        CorpusId = conv.CorpusId,
        SpecId = conv.Kind == "spec" ? conv.RefId : null,
        Ct = ct,
    };

    private static List<object> AssistantContent(IReadOnlyList<BrainBlock> blocks)
    {
        var content = new List<object>();
        foreach (var b in blocks)
        {
            if (b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text))
                content.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = b.Text });
            else if (b.Type == "tool_use" && b.ToolUseId is not null && b.ToolName is not null)
                content.Add(new Dictionary<string, object?>
                {
                    ["type"] = "tool_use",
                    ["id"] = b.ToolUseId,
                    ["name"] = b.ToolName,
                    ["input"] = b.Input ?? JsonDocument.Parse("{}").RootElement,
                });
        }
        return content;
    }

    // `is_error` is only ever sent as `true`: a null dictionary value is NOT
    // dropped by WhenWritingNull (that applies to properties), and Anthropic
    // rejects `"is_error": null` with a 400 — which silently broke every
    // second model round in production while the mock brain never noticed.
    private static object ToolResultBlock(string toolUseId, ToolResult result)
    {
        var block = new Dictionary<string, object?>
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = toolUseId,
            ["content"] = JsonSerializer.Serialize(result.ModelPayload, ConversationJson.Web),
        };
        if (!result.Ok) block["is_error"] = true;
        return block;
    }

    /// <summary>The provider's own `error.message` out of an
    /// <see cref="AnthropicRequestException"/> text, when it carried a JSON body.</summary>
    private static string? ExtractApiError(string exceptionMessage)
    {
        var start = exceptionMessage.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(exceptionMessage[start..].TrimEnd('…'));
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch (JsonException)
        {
            // Truncated body — fall back to a regex over the raw text.
            var m = System.Text.RegularExpressions.Regex.Match(exceptionMessage, "\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (m.Success) return m.Groups[1].Value.Replace("\\\"", "\"");
        }
        return null;
    }

    private static List<ArtifactDto> DedupeArtifacts(IEnumerable<ArtifactDto> artifacts)
    {
        var seen = new Dictionary<string, ArtifactDto>();
        foreach (var a in artifacts) seen[$"{a.Kind}:{a.RefId}"] = a;
        return seen.Values.TakeLast(6).ToList();
    }

    private static string Humanize(string tool, JsonElement input) => tool switch
    {
        "list_programmes" => "Listing the programmes",
        "get_programme_status" => "Reading the programme status",
        "search_routines" => CopilotToolRegistry.Read(input, "query") is { Length: > 0 } q ? $"Searching routines for “{q}”" : "Listing routines",
        "read_routine" => $"Reading `{CopilotToolRegistry.Read(input, "subroutineId")}`",
        "get_spec" => "Loading the spec",
        "get_pattern_clusters" => "Loading the pattern clusters",
        "get_run_status" => "Checking the run",
        "query_graph" => "Walking the dependency graph",
        "get_migration_plan" => "Loading the migration plan",
        "get_validation_results" => "Loading the gate results",
        "survey_corpus" => "Starting the pattern survey",
        "extract_spec" => "Starting the spec draft",
        "route_for_review" => "Routing for review",
        "review_claim" => "Recording the claim decision",
        "review_all_claims" => "Recording the claim decisions",
        "sign_spec" => "Signing the spec",
        "generate_scaffold" => "Starting code generation",
        "run_gate" => "Starting the gate",
        "generate_docs" => "Starting documentation",
        "generate_migration_plan" => "Drafting the migration plan",
        _ => tool.Replace('_', ' '),
    };

    private async Task RecordLlmCallAsync(BrainResponse resp, CancellationToken ct)
    {
        if (resp.Usage is null) return;
        try
        {
            var u = resp.Usage;
            _db.LlmCalls.Add(new LlmCall
            {
                Id = Guid.NewGuid(),
                Provider = _brain.Name,
                Model = resp.Model,
                PromptTemplateId = "copilot-orchestrator",
                PromptTemplateVersion = "v1.0",
                ProviderConfigVersion = "copilot",
                InputTokens = u.InputTokens,
                OutputTokens = u.OutputTokens,
                CacheReadTokens = u.CacheReadTokens,
                CacheCreationTokens = u.CacheCreationTokens,
                LatencyMs = resp.LatencyMs,
                CostUsd = ModelPricing.Estimate(_brain.Name, resp.Model, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens),
                Status = "success",
                CalledAt = DateTimeOffset.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record copilot LlmCall");
        }
    }
}
