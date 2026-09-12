using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Astra.Api.Copilot;
using Astra.Api.Persistence;
using Astra.Api.Runs;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// WS2 — the conversation spine.
///
///   GET  /api/v1/conversations?corpusId=                 threads (global first, then one per programme)
///   GET  /api/v1/conversations/global                    the global thread
///   GET  /api/v1/conversations/{id}                      thread + last 200 messages
///   POST /api/v1/conversations/{id}/messages   {text}    SSE: user | status | tool_result | message | error | done
///   POST /api/v1/conversations/{id}/messages/{mid}/confirm  SSE (same events) — runs the pending action, continues the turn
///   POST /api/v1/conversations/{id}/messages/{mid}/decline  200 { message }
///   GET  /api/v1/conversations/{id}/stream?afterSeq=     live `message` events (narrator, other tabs)
///   GET  /api/v1/copilot/overview                        Mission Control data
///   GET  /api/v1/runs/{runId}/events?afterSeq=           generic run stream off the RunEventBus
/// </summary>
public static class ConversationEndpoints
{
    public sealed record PostMessageRequest(string Text);

    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/conversations", async (Guid? corpusId, ConversationService conversations, CancellationToken ct) =>
            Results.Ok(new { data = await conversations.ListAsync(corpusId, ct) }));

        app.MapGet("/api/v1/conversations/global", async (ConversationService conversations, CancellationToken ct) =>
        {
            var conv = await conversations.EnsureGlobalAsync(ct);
            return Results.Ok(await conversations.RenderAsync(conv, ct));
        });

        app.MapGet("/api/v1/conversations/{id:guid}", async (Guid id, ConversationService conversations, CancellationToken ct) =>
        {
            var conv = await conversations.FindAsync(id, ct);
            if (conv is null) return Results.NotFound(new { error = new { code = "conversation.not_found" } });
            return Results.Ok(new
            {
                conversation = await conversations.RenderAsync(conv, ct),
                messages = await conversations.MessagesAsync(id, 200, ct),
            });
        });

        app.MapPost("/api/v1/conversations/{id:guid}/messages", async (
            Guid id, PostMessageRequest body, HttpContext ctx, ConversationService conversations,
            CopilotOrchestrator orchestrator, DevPersonaContext actor, CancellationToken ct) =>
        {
            var conv = await conversations.FindAsync(id, ct);
            if (conv is null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = new { code = "conversation.not_found" } }, ct);
                return;
            }
            var text = body.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = new { code = "message.empty", message = "Say something." } }, ct);
                return;
            }
            if (text.Length > 8000) text = text[..8000];

            await StartSseAsync(ctx, ct);
            await orchestrator.HandleUserTurnAsync(conv, text, actor, evt => WriteEventAsync(ctx, evt.Type, evt.Data, ct), ct);
        });

        app.MapPost("/api/v1/conversations/{id:guid}/messages/{messageId:guid}/confirm", async (
            Guid id, Guid messageId, HttpContext ctx, ConversationService conversations,
            CopilotOrchestrator orchestrator, DevPersonaContext actor, CancellationToken ct) =>
        {
            var conv = await conversations.FindAsync(id, ct);
            var pending = await conversations.FindMessageAsync(messageId, ct);
            if (conv is null || pending is null || pending.ConversationId != id)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = new { code = "message.not_found" } }, ct);
                return;
            }
            await StartSseAsync(ctx, ct);
            await orchestrator.ContinueAsync(conv, pending, actor, evt => WriteEventAsync(ctx, evt.Type, evt.Data, ct), ct);
        });

        app.MapPost("/api/v1/conversations/{id:guid}/messages/{messageId:guid}/decline", async (
            Guid id, Guid messageId, ConversationService conversations, CopilotOrchestrator orchestrator, CancellationToken ct) =>
        {
            var pending = await conversations.FindMessageAsync(messageId, ct);
            if (pending is null || pending.ConversationId != id)
                return Results.NotFound(new { error = new { code = "message.not_found" } });
            return Results.Ok(new { message = await orchestrator.DeclineAsync(pending, ct) });
        });

        app.MapGet("/api/v1/conversations/{id:guid}/stream", async (
            Guid id, long? afterSeq, RunEventBus bus, HttpContext ctx, CancellationToken ct) =>
        {
            await StartSseAsync(ctx, ct);
            var since = ResolveSince(ctx, afterSeq);
            try
            {
                await foreach (var evt in bus.SubscribeAsync(id, since, ct))
                {
                    if (evt.Type != "message") continue;
                    var payload = JsonSerializer.Serialize(new { seq = evt.Seq, ts = evt.Ts, data = evt.Data }, ConversationJson.Web);
                    await ctx.Response.WriteAsync($"id: {evt.Seq}\nevent: message\ndata: {payload}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The browser closed the stream (tab switch, reconnect) — not an error.
            }
        });

        app.MapGet("/api/v1/runs/{runId:guid}/events", async (
            Guid runId, long? afterSeq, RunEventBus bus, HttpContext ctx, CancellationToken ct) =>
        {
            await StartSseAsync(ctx, ct);
            var since = ResolveSince(ctx, afterSeq);
            try
            {
                await foreach (var evt in bus.SubscribeAsync(runId, since, ct))
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        message = evt.Message, agent = evt.Agent, stage = evt.Stage, type = evt.Type, seq = evt.Seq, ts = evt.Ts, data = evt.Data,
                    }, ConversationJson.Web);
                    var frame = evt.Type == "log"
                        ? $"id: {evt.Seq}\ndata: {payload}\n\n"
                        : $"id: {evt.Seq}\nevent: {evt.Type}\ndata: {payload}\n\n";
                    await ctx.Response.WriteAsync(frame, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                if (!ct.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client went away mid-stream — not an error.
            }
        });

        app.MapGet("/api/v1/copilot/overview", async (AppDbContext db, ConversationService conversations, CancellationToken ct) =>
        {
            var corpora = await db.Corpora.AsNoTracking()
                .Where(c => c.LatestVersionId != null)
                .OrderByDescending(c => c.UpdatedAt)
                .ToListAsync(ct);
            var programmes = new List<OverviewProgrammeDto>();
            var totals = FunnelCounts.Empty;
            foreach (var c in corpora)
            {
                var thread = await conversations.EnsureProgrammeAsync(c.Id, ct);
                var (_, _, language) = await conversations.ProgrammeStatsAsync(c, ct);
                var counts = await conversations.FunnelAsync(c, ct);
                totals = totals.Add(counts);
                var run = await db.PatternAnalysisRuns.AsNoTracking()
                    .Where(r => r.CorpusId == c.Id).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
                programmes.Add(new OverviewProgrammeDto(c.Id, thread.Id, c.Name, language, counts,
                    run is null ? null : new LatestRunDto(run.Id, "pattern-analysis", run.State, run.StartedAt, run.Summary)));
            }

            var since = DateTimeOffset.UtcNow.Date;
            var today = await db.LlmCalls.AsNoTracking().Where(l => l.CalledAt >= since).ToListAsync(ct);
            var cost = today.Sum(l => l.CostUsd);
            var latencies = today.Select(l => l.LatencyMs).OrderBy(x => x).ToList();
            double? p50 = latencies.Count == 0 ? null : latencies[latencies.Count / 2];
            var cacheable = today.Sum(l => (long)l.InputTokens + l.CacheReadTokens);
            double? hit = cacheable == 0 ? null : (double)today.Sum(l => (long)l.CacheReadTokens) / cacheable;

            return Results.Ok(new OverviewDto(programmes, totals, new TelemetryDto(cost, today.Count, p50, hit)));
        });

        return app;
    }

    private static long ResolveSince(HttpContext ctx, long? afterSeq)
    {
        var since = afterSeq ?? 0;
        if (ctx.Request.Headers.TryGetValue("Last-Event-ID", out var lastId) && long.TryParse(lastId.ToString(), out var parsed))
            since = Math.Max(since, parsed);
        return since;
    }

    private static async Task StartSseAsync(HttpContext ctx, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-transform";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Response.Headers["Connection"] = "keep-alive";
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static async Task WriteEventAsync(HttpContext ctx, string type, object data, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(data, ConversationJson.Web);
        await ctx.Response.WriteAsync($"event: {type}\ndata: {payload}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
}
