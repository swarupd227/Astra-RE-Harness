using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Validation;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase 6.0 — Admin CRUD + scorer surface for the golden dataset.
///
///   GET    /api/v1/golden-dataset                       list all entries (read-only OK)
///   GET    /api/v1/golden-dataset/{entryId}             detail by string entry id
///   POST   /api/v1/golden-dataset                       admin-only, create a new entry
///   PUT    /api/v1/golden-dataset/{entryId}             admin-only, overwrite an entry
///   DELETE /api/v1/golden-dataset/{entryId}             admin-only, hard-delete an entry
///
///   POST   /api/v1/golden-dataset/{entryId}/score       admin-only, run the scorer
///   POST   /api/v1/golden-dataset/score-all             admin-only, run the scorer on every entry
///                                                       in one prompt's schema; useful pre-PR.
///
///   GET    /api/v1/golden-dataset/runs                  list scorer runs (most recent first)
///   GET    /api/v1/golden-dataset/runs/{id}             single run detail
/// </summary>
public static class GoldenDatasetEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEndpointRouteBuilder MapGoldenDatasetEndpoints(this IEndpointRouteBuilder app)
    {
        // ─── Read paths (any persona) ─────────────────────────────────────
        app.MapGet("/api/v1/golden-dataset", async (
            AppDbContext db,
            string? schemaId,
            string? status,
            CancellationToken ct) =>
        {
            var q = db.GoldenDatasetEntries.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(schemaId)) q = q.Where(e => e.SchemaId == schemaId);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);
            var rows = await q.OrderBy(e => e.SchemaId).ThenBy(e => e.EntryId).ToListAsync(ct);

            // For each entry, attach the latest run's score so the list
            // view can render a one-line summary without N+1 fetches.
            var entryDbIds = rows.Select(r => r.Id).ToList();
            var latestRuns = await db.GoldenDatasetRuns.AsNoTracking()
                .Where(r => entryDbIds.Contains(r.EntryId))
                .GroupBy(r => r.EntryId)
                .Select(g => g.OrderByDescending(r => r.CompletedAt).First())
                .ToListAsync(ct);
            var latestByEntry = latestRuns.ToDictionary(r => r.EntryId);

            return Results.Ok(new
            {
                data = rows.Select(e => new
                {
                    id = e.Id,
                    entryId = e.EntryId,
                    schemaId = e.SchemaId,
                    title = e.Title,
                    trapCategory = e.TrapCategory,
                    difficulty = e.Difficulty,
                    status = e.Status,
                    sourcePath = e.SourcePath,
                    sourceLines = e.SourceLines,
                    expectedClaimCount = CountExpectedClaims(e.ExpectedClaimsJson),
                    hasCanonicalInputs = HasCanonicalInputs(e.CanonicalInputsJson),
                    notes = e.Notes,
                    updatedAt = e.UpdatedAt,
                    updatedBy = e.UpdatedBy,
                    latestRun = latestByEntry.TryGetValue(e.Id, out var lr) ? new
                    {
                        id = lr.Id,
                        promptId = lr.PromptId,
                        promptVersion = lr.PromptVersion,
                        modelName = lr.ModelName,
                        score = lr.Score,
                        matched = lr.Matched,
                        total = lr.Total,
                        completedAt = lr.CompletedAt,
                    } : null,
                }),
            });
        });

        app.MapGet("/api/v1/golden-dataset/{entryId}", async (
            string entryId, AppDbContext db, CancellationToken ct) =>
        {
            var entry = await db.GoldenDatasetEntries.AsNoTracking()
                .FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
            if (entry is null) return NotFound("golden_dataset.not_found");
            return Results.Ok(Render(entry));
        });

        // ─── Admin CRUD ───────────────────────────────────────────────────
        app.MapPost("/api/v1/golden-dataset", async (
            UpsertEntryRequest body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            var error = ValidateBody(body);
            if (error is not null) return BadRequest(error.Value.Code, error.Value.Message);

            if (await db.GoldenDatasetEntries.AnyAsync(e => e.EntryId == body.EntryId, ct))
                return BadRequest("golden_dataset.exists", $"Entry {body.EntryId} already exists; use PUT to update.");

            var now = DateTimeOffset.UtcNow;
            var entry = new GoldenDatasetEntry
            {
                Id = Guid.NewGuid(),
                EntryId = body.EntryId!,
                SchemaId = body.SchemaId!,
                Title = body.Title ?? "",
                TrapCategory = body.TrapCategory ?? "uncategorised",
                Difficulty = body.Difficulty ?? "medium",
                SourcePath = body.SourcePath ?? "",
                SourceContent = body.SourceContent ?? "",
                SourceLines = body.SourceLines ?? "",
                ExpectedClaimsJson = body.ExpectedClaims is null
                    ? "[]"
                    : JsonSerializer.Serialize(body.ExpectedClaims, JsonOpts),
                CanonicalInputsJson = body.CanonicalInputs is null
                    ? "[]"
                    : JsonSerializer.Serialize(body.CanonicalInputs, JsonOpts),
                Notes = body.Notes ?? "",
                Status = body.Status ?? "draft",
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = actor.DisplayName,
            };
            db.GoldenDatasetEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("golden_dataset.created", "golden_dataset_entry", entry.Id, actor,
                payload: new { entryId = entry.EntryId, schemaId = entry.SchemaId }, ct: ct);
            return Results.Ok(Render(entry));
        });

        app.MapPut("/api/v1/golden-dataset/{entryId}", async (
            string entryId,
            UpsertEntryRequest body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            var entry = await db.GoldenDatasetEntries.FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
            if (entry is null) return NotFound("golden_dataset.not_found");

            // The URL path's entry id wins; body.entryId is ignored on update
            // so a typo there can't silently re-key the row.
            entry.SchemaId = body.SchemaId ?? entry.SchemaId;
            entry.Title = body.Title ?? entry.Title;
            entry.TrapCategory = body.TrapCategory ?? entry.TrapCategory;
            entry.Difficulty = body.Difficulty ?? entry.Difficulty;
            entry.SourcePath = body.SourcePath ?? entry.SourcePath;
            entry.SourceContent = body.SourceContent ?? entry.SourceContent;
            entry.SourceLines = body.SourceLines ?? entry.SourceLines;
            if (body.ExpectedClaims is not null)
                entry.ExpectedClaimsJson = JsonSerializer.Serialize(body.ExpectedClaims, JsonOpts);
            if (body.CanonicalInputs is not null)
                entry.CanonicalInputsJson = JsonSerializer.Serialize(body.CanonicalInputs, JsonOpts);
            if (body.Notes is not null) entry.Notes = body.Notes;
            if (body.Status is not null) entry.Status = body.Status;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            entry.UpdatedBy = actor.DisplayName;

            await db.SaveChangesAsync(ct);
            await audit.LogAsync("golden_dataset.updated", "golden_dataset_entry", entry.Id, actor,
                payload: new { entryId = entry.EntryId, status = entry.Status }, ct: ct);
            return Results.Ok(Render(entry));
        });

        app.MapDelete("/api/v1/golden-dataset/{entryId}", async (
            string entryId,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            var entry = await db.GoldenDatasetEntries.FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
            if (entry is null) return NotFound("golden_dataset.not_found");
            db.GoldenDatasetEntries.Remove(entry);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("golden_dataset.deleted", "golden_dataset_entry", entry.Id, actor,
                payload: new { entryId }, ct: ct);
            return Results.NoContent();
        });

        // ─── Scorer ───────────────────────────────────────────────────────
        app.MapPost("/api/v1/golden-dataset/{entryId}/score", async (
            string entryId,
            AppDbContext db,
            GoldenDatasetScorer scorer,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            var entry = await db.GoldenDatasetEntries.FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
            if (entry is null) return NotFound("golden_dataset.not_found");
            var outcome = await scorer.ScoreAsync(entry.Id, actor, ct);
            return Results.Ok(RenderRun(outcome.Run, outcome.Detail));
        });

        app.MapPost("/api/v1/golden-dataset/score-all", async (
            AppDbContext db,
            GoldenDatasetScorer scorer,
            DevPersonaContext actor,
            string? schemaId,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();
            var q = db.GoldenDatasetEntries.AsQueryable();
            if (!string.IsNullOrWhiteSpace(schemaId)) q = q.Where(e => e.SchemaId == schemaId);
            // Skip deprecated entries — they live for history but should
            // not contribute to the aggregate regression score.
            q = q.Where(e => e.Status != "deprecated");
            var entries = await q.OrderBy(e => e.SchemaId).ThenBy(e => e.EntryId).ToListAsync(ct);

            var runs = new List<object>();
            int totalMatched = 0, totalExpected = 0;
            foreach (var e in entries)
            {
                try
                {
                    var outcome = await scorer.ScoreAsync(e.Id, actor, ct);
                    totalMatched += outcome.Run.Matched;
                    totalExpected += outcome.Run.Total;
                    runs.Add(RenderRun(outcome.Run, outcome.Detail));
                }
                catch (Exception ex)
                {
                    runs.Add(new
                    {
                        entryId = e.EntryId,
                        error = ex.Message,
                        score = 0.0,
                        matched = 0,
                        total = 0,
                    });
                }
            }
            return Results.Ok(new
            {
                inputCount = entries.Count,
                aggregateMatched = totalMatched,
                aggregateTotal = totalExpected,
                aggregateScore = totalExpected == 0 ? 1.0 : (double)totalMatched / totalExpected,
                runs,
            });
        });

        // ─── Runs history ─────────────────────────────────────────────────
        app.MapGet("/api/v1/golden-dataset/runs", async (
            AppDbContext db,
            string? entryId,
            string? promptId,
            int? limit,
            CancellationToken ct) =>
        {
            var q = db.GoldenDatasetRuns.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(entryId))
            {
                var entryDbId = await db.GoldenDatasetEntries.AsNoTracking()
                    .Where(e => e.EntryId == entryId)
                    .Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
                if (entryDbId is null) return Results.Ok(new { data = Array.Empty<object>() });
                q = q.Where(r => r.EntryId == entryDbId);
            }
            if (!string.IsNullOrWhiteSpace(promptId)) q = q.Where(r => r.PromptId == promptId);
            var rows = await q.OrderByDescending(r => r.CompletedAt)
                .Take(Math.Clamp(limit ?? 100, 1, 500))
                .ToListAsync(ct);
            return Results.Ok(new { data = rows.Select(r => RenderRunSummary(r)) });
        });

        app.MapGet("/api/v1/golden-dataset/runs/{id:guid}", async (
            Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.GoldenDatasetRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return NotFound("golden_dataset_run.not_found");
            return Results.Ok(RenderRunFull(run));
        });

        return app;
    }

    private static int CountExpectedClaims(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    private static bool HasCanonicalInputs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch { return false; }
    }

    private static object Render(GoldenDatasetEntry e) => new
    {
        id = e.Id,
        entryId = e.EntryId,
        schemaId = e.SchemaId,
        title = e.Title,
        trapCategory = e.TrapCategory,
        difficulty = e.Difficulty,
        sourcePath = e.SourcePath,
        sourceLines = e.SourceLines,
        sourceContent = e.SourceContent,
        expectedClaims = ParseArray(e.ExpectedClaimsJson),
        canonicalInputs = ParseArray(e.CanonicalInputsJson),
        notes = e.Notes,
        status = e.Status,
        createdAt = e.CreatedAt,
        updatedAt = e.UpdatedAt,
        updatedBy = e.UpdatedBy,
    };

    private static object? ParseArray(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return Array.Empty<object>(); }
    }

    private static object RenderRun(GoldenDatasetRun run, IReadOnlyList<GoldenDatasetScorer.ClaimMatch> detail) => new
    {
        id = run.Id,
        entryId = run.EntryId,
        promptId = run.PromptId,
        promptVersion = run.PromptVersion,
        modelName = run.ModelName,
        score = run.Score,
        matched = run.Matched,
        total = run.Total,
        detail = detail.Select(d => new
        {
            expectedClaimId = d.ExpectedClaimId,
            kind = d.Kind,
            pattern = d.Pattern,
            matched = d.Matched,
            matchedAgainst = d.MatchedAgainst,
        }),
        startedAt = run.StartedAt,
        completedAt = run.CompletedAt,
        triggeredBy = run.TriggeredBy,
    };

    private static object RenderRunSummary(GoldenDatasetRun r) => new
    {
        id = r.Id,
        entryId = r.EntryId,
        promptId = r.PromptId,
        promptVersion = r.PromptVersion,
        modelName = r.ModelName,
        score = r.Score,
        matched = r.Matched,
        total = r.Total,
        completedAt = r.CompletedAt,
    };

    private static object RenderRunFull(GoldenDatasetRun r) => new
    {
        id = r.Id,
        entryId = r.EntryId,
        promptId = r.PromptId,
        promptVersion = r.PromptVersion,
        modelName = r.ModelName,
        score = r.Score,
        matched = r.Matched,
        total = r.Total,
        detail = ParseArray(r.DetailJson),
        startedAt = r.StartedAt,
        completedAt = r.CompletedAt,
        triggeredBy = r.TriggeredBy,
    };

    private static (string Code, string Message)? ValidateBody(UpsertEntryRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.EntryId))
            return ("golden_dataset.bad_id", "entryId is required.");
        if (string.IsNullOrWhiteSpace(body.SchemaId))
            return ("golden_dataset.bad_schema", "schemaId is required (e.g. 'fortran-f77' or 'cobol').");
        if (string.IsNullOrWhiteSpace(body.SourceContent))
            return ("golden_dataset.empty_source", "sourceContent is required.");
        return null;
    }

    private static IResult NotFound(string code) => Results.NotFound(new { error = new { code } });
    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });
    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);
}

public sealed record UpsertEntryRequest(
    string? EntryId,
    string? SchemaId,
    string? Title,
    string? TrapCategory,
    string? Difficulty,
    string? SourcePath,
    string? SourceLines,
    string? SourceContent,
    object[]? ExpectedClaims,
    object[]? CanonicalInputs,
    string? Notes,
    string? Status);
