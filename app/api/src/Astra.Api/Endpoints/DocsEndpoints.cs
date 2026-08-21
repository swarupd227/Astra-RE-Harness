using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Docs;
using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 11.0 documentation endpoints. This commit ships only the vertical-slice
/// trigger (gated by Dev:ResetEnabled). Phase 11.0.a opens up the production
/// /api/v1/corpora/{id}/docs/generate flow.
/// </summary>
public static class DocsEndpoints
{
    public static IEndpointRouteBuilder MapDocsEndpoints(this IEndpointRouteBuilder app)
    {
        // Vertical-slice trigger. POST /api/v1/dev/docs/slice?corpusId=...&take=10
        // Runs N routine-summary extractions through the real DocSection
        // entity model, returns a JSON summary with section IDs + summaries.
        // Dev-only; production doc generation lives behind 11.0.a's endpoint.
        app.MapPost("/api/v1/dev/docs/slice", async (
            Guid corpusId,
            int? take,
            DocsExtractionService svc,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (!cfg.GetValue("Dev:ResetEnabled", false))
                return Results.NotFound(new { error = new { code = "dev.docs.disabled" } });

            var n = Math.Clamp(take ?? 10, 1, 50);
            var result = await svc.RunSliceAsync(corpusId, n, ct);
            return Results.Ok(new
            {
                generationRunId = result.GenerationRunId,
                requested = result.Requested,
                succeeded = result.Succeeded,
                failed = result.Failed,
                sections = result.Sections,
            });
        });

        // Phase 11.0.a/b — multi-stage documentation generator. Returns 202
        // with a generationRunId; work runs in the background and the
        // caller polls /status until State leaves RUNNING.
        //
        // Query parameters:
        //   ?stages=routine-summary,module,overview
        //     Comma-separated stage list. Default: all three, in order.
        //   ?take=N
        //     Caps work in the routine-summary stage to N routines.
        //   ?force=true
        //     Wipes any existing DocSections in the requested stages
        //     for the same SourceVersion before re-generating.
        app.MapPost("/api/v1/corpora/{corpusId:guid}/docs/generate", async (
            Guid corpusId,
            string? stages,
            int? take,
            bool? force,
            Astra.Api.Docs.DocsGenerationOrchestrator orchestrator,
            CancellationToken ct) =>
        {
            var stagesList = string.IsNullOrWhiteSpace(stages)
                ? Astra.Api.Docs.DocsGenerationOrchestrator.DefaultStages
                : stages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Guid runId;
            try
            {
                runId = await orchestrator.StartAsync(
                    corpusId,
                    new Astra.Api.Docs.DocsGenerationOrchestrator.GenerateOptions(
                        stagesList, take, force ?? false),
                    ct);
            }
            catch (ArgumentException ex)
            {
                // An unknown stage name used to surface as an unhandled 500
                // with no CORS headers, which reads in the browser as an
                // opaque network failure rather than "you typed it wrong".
                return Results.BadRequest(new
                {
                    error = new { code = "docs.generate.invalid_stage", message = ex.Message },
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "docs.generate.precondition_failed", message = ex.Message },
                });
            }
            return Results.Accepted($"/api/v1/docs/runs/{runId}", new
            {
                generationRunId = runId,
                stages = stagesList,
                statusUrl = $"/api/v1/docs/runs/{runId}",
            });
        });

        // Status poll for a generation run. Returns state, summary, metrics.
        app.MapGet("/api/v1/docs/runs/{runId:guid}", async (
            Guid runId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var run = await db.DocGenerationRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null)
                return Results.NotFound(new { error = new { code = "docs.run.not_found" } });
            return Results.Ok(new
            {
                id = run.Id,
                corpusId = run.CorpusId,
                state = run.State,
                summary = run.Summary,
                metrics = run.MetricsJson is null
                    ? null
                    : (System.Text.Json.JsonElement?)System.Text.Json.JsonDocument.Parse(run.MetricsJson).RootElement,
                startedAt = run.StartedAt,
                completedAt = run.CompletedAt,
                errorSummary = run.ErrorSummary,
            });
        });

        // Read-back: GET /api/v1/dev/docs/sections?corpusId=...
        // Returns the rendered markdown for every DocSection on the corpus
        // so the slice output can be read by eye.
        app.MapGet("/api/v1/dev/docs/sections", async (
            Guid corpusId,
            AppDbContext db,
            IConfiguration cfg,
            CancellationToken ct) =>
        {
            if (!cfg.GetValue("Dev:ResetEnabled", false))
                return Results.NotFound(new { error = new { code = "dev.docs.disabled" } });

            var sections = await db.DocSections
                .AsNoTracking()
                .Where(s => s.CorpusId == corpusId)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    id = s.Id,
                    kind = s.SectionKind,
                    state = s.State,
                    subroutineId = s.SubroutineId,
                    rendered = s.RenderedMarkdown,
                    createdAt = s.CreatedAt,
                })
                .ToListAsync(ct);

            return Results.Ok(new { count = sections.Count, sections });
        });

        // ── 11.0.e Review endpoints ────────────────────────────────────────────

        // GET /api/v1/corpora/{corpusId}/docs/sections
        //   ?kind=routine-summary  optional section-kind filter
        //   ?state=DRAFT           optional state filter (uppercase)
        //   ?page=1&pageSize=50    pagination
        app.MapGet("/api/v1/corpora/{corpusId:guid}/docs/sections", async (
            Guid corpusId,
            string? kind,
            string? state,
            int? page,
            int? pageSize,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var pg = Math.Max(1, page ?? 1);
            var sz = Math.Clamp(pageSize ?? 50, 1, 200);
            var q = db.DocSections.AsNoTracking()
                .Where(s => s.CorpusId == corpusId);
            if (!string.IsNullOrWhiteSpace(kind))
                q = q.Where(s => s.SectionKind == kind);
            if (!string.IsNullOrWhiteSpace(state))
                q = q.Where(s => s.State == state.ToUpperInvariant());
            var total = await q.CountAsync(ct);
            var items = await q
                .OrderBy(s => s.SectionKind).ThenBy(s => s.CreatedAt)
                .Skip((pg - 1) * sz).Take(sz)
                .Select(s => new
                {
                    id = s.Id,
                    kind = s.SectionKind,
                    scope = s.Scope,
                    state = s.State,
                    moduleName = s.ModuleName,
                    subroutineId = s.SubroutineId,
                    subroutineName = s.Subroutine != null ? s.Subroutine.Name : null,
                    generationRunId = s.GenerationRunId,
                    createdAt = s.CreatedAt,
                    updatedAt = s.UpdatedAt,
                })
                .ToListAsync(ct);
            return Results.Ok(new { total, page = pg, pageSize = sz, data = items });
        });

        // GET /api/v1/docs/sections/{id}  — full section detail including payload + markdown
        app.MapGet("/api/v1/docs/sections/{id:guid}", async (
            Guid id,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var s = await db.DocSections.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (s is null)
                return Results.NotFound(new { error = new { code = "docs.section.not_found" } });
            return Results.Ok(new
            {
                id = s.Id,
                corpusId = s.CorpusId,
                sourceVersionId = s.SourceVersionId,
                kind = s.SectionKind,
                scope = s.Scope,
                state = s.State,
                moduleName = s.ModuleName,
                subroutineId = s.SubroutineId,
                renderedMarkdown = s.RenderedMarkdown,
                payload = s.PayloadJson.RootElement,
                generationRunId = s.GenerationRunId,
                llmCallId = s.LlmCallId,
                createdAt = s.CreatedAt,
                updatedAt = s.UpdatedAt,
            });
        });

        // POST /api/v1/docs/sections/{id}/accept  — DRAFT/IN_REVIEW → SIGNED (Admin only)
        app.MapPost("/api/v1/docs/sections/{id:guid}/accept", async (
            Guid id,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            var section = await db.DocSections.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (section is null)
                return Results.NotFound(new { error = new { code = "docs.section.not_found" } });
            if (section.State == "SIGNED")
                return Results.Conflict(new { error = new { code = "docs.section.already_signed" } });
            if (section.State == "REJECTED")
                return Results.BadRequest(new { error = new { code = "docs.section.rejected", message = "Cannot sign a rejected section." } });

            var previousState = section.State;
            section.State = "SIGNED";
            section.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "doc_section.accepted", "doc_section", section.Id, actor,
                payload: new { previousState, kind = section.SectionKind, scope = section.Scope },
                ct: ct);

            return Results.Ok(new { id = section.Id, state = section.State, updatedAt = section.UpdatedAt });
        });

        // DELETE /api/v1/docs/sections/{id}  — reject: soft-delete → REJECTED (Admin only)
        // Returns 204 on success and on idempotent re-reject.
        app.MapDelete("/api/v1/docs/sections/{id:guid}", async (
            Guid id,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            var section = await db.DocSections.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (section is null)
                return Results.NotFound(new { error = new { code = "docs.section.not_found" } });
            if (section.State == "SIGNED")
                return Results.Conflict(new { error = new { code = "docs.section.already_signed", message = "Cannot reject a signed section." } });
            if (section.State == "REJECTED")
                return Results.NoContent(); // idempotent

            var previousState = section.State;
            section.State = "REJECTED";
            section.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync(
                "doc_section.rejected", "doc_section", section.Id, actor,
                payload: new { previousState, kind = section.SectionKind, scope = section.Scope },
                ct: ct);

            return Results.NoContent();
        });

        // GET /api/v1/corpora/{corpusId}/docs/summary  — counts by kind × state
        app.MapGet("/api/v1/corpora/{corpusId:guid}/docs/summary", async (
            Guid corpusId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var breakdown = await db.DocSections.AsNoTracking()
                .Where(s => s.CorpusId == corpusId)
                .GroupBy(s => new { s.SectionKind, s.State })
                .Select(g => new { kind = g.Key.SectionKind, state = g.Key.State, count = g.Count() })
                .OrderBy(r => r.kind).ThenBy(r => r.state)
                .ToListAsync(ct);
            return Results.Ok(new { corpusId, breakdown });
        });

        // GET /api/v1/corpora/{corpusId}/docs/export?format=mkdocs|docx|pdf|confluence
        //
        // Returns a file download.
        //   mkdocs     → <name>-docs.zip  (application/zip)
        //   docx       → <name>-docs.docx (requires pandoc in $PATH)
        //   pdf        → <name>-docs.pdf  (requires pandoc + weasyprint; includes professional CSS + TOC)
        //   confluence → <name>-confluence.json (Confluence REST API page payloads)
        //
        // SIGNED, STALE, and DRAFT sections are included. STALE and DRAFT
        // sections render with warning/note banners. Admin persona required.
        app.MapGet("/api/v1/corpora/{corpusId:guid}/docs/export", async (
            Guid corpusId,
            string? format,
            DocExportService exporter,
            IAuditLogger audit,
            DevPersonaContext actor,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            var fmt = (format ?? "mkdocs").ToLowerInvariant();
            try
            {
                var result = await exporter.ExportAsync(corpusId, fmt, ct);

                await audit.LogAsync(
                    "docs.exported", "corpus", corpusId, actor,
                    payload: new { format = fmt, fileName = result.FileName, bytes = result.Bytes.Length },
                    ctx, ct);

                return Results.File(result.Bytes, result.ContentType, fileDownloadName: result.FileName);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new
                {
                    error = new { code = "docs.export.unknown_format", message = ex.Message },
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Pandoc"))
            {
                return Results.Problem(
                    detail: ex.Message,
                    title: "Export tool unavailable — pandoc is not installed in this container.",
                    statusCode: 503);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
            {
                return Results.NotFound(new { error = new { code = "docs.corpus.not_found" } });
            }
        });

        // GET /api/v1/docs/runs/{runId}/logs  — SSE stream of log lines for an active run.
        // Streams text/event-stream until the run completes or the client disconnects.
        // Each event: data: {"message":"..."}\n\n
        // Final event: event: done\ndata: {}\n\n
        app.MapGet("/api/v1/docs/runs/{runId:guid}/logs", async (
            Guid runId,
            Astra.Api.Docs.DocRunLogger logger,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await ctx.Response.Body.FlushAsync(ct);

            await foreach (var line in logger.SubscribeAsync(runId, ct))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new { message = line });
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
            if (!ct.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync("event: done\ndata: {}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        });

        return app;
    }

    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);
}
