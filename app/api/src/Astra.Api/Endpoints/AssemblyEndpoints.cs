using System.Text.Json;
using Astra.Api.Assembly;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Copilot;
using Astra.Api.Persistence;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public sealed record StartAssemblyBody(string? PathPrefix, Guid? WaveId, List<Guid>? UnitIds, string? TargetStack, bool? RequireTests, string? Name);

/// <summary>
/// One application from a module's code units. POST starts a background run (202 with the assembly id, which is also
/// the run id to follow); GET reads it; package.zip downloads the assembled project.
/// </summary>
public static class AssemblyEndpoints
{
    public static IEndpointRouteBuilder MapAssemblyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/corpora/{id:guid}/assemblies", async (
            Guid id, StartAssemblyBody? body, AppDbContext db, AssemblyService assemblies, Narrator narrator,
            Astra.Api.Conversations.ConversationService conversations, DevPersonaContext actor, IAuditLogger audit, HttpContext http, CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Engineer)
                return Results.Json(new { error = new { code = "auth.engineer_required", message = "Only engineers assemble applications." } }, statusCode: 403);
            var corpus = await db.Corpora.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (corpus is null) return Results.NotFound(new { error = new { code = "corpus.not_found" } });
            var request = new AssemblyRequest(body?.PathPrefix, body?.WaveId, body?.UnitIds, body?.TargetStack, body?.RequireTests ?? false, body?.Name);
            if (ModuleAssemblers.For(request.TargetStack ?? "java-spring") is null)
                return Results.BadRequest(new { error = new { code = "assembly.stack_unsupported", message = $"Only {string.Join(", ", ModuleAssemblers.Supported)} code can be assembled so far." } });

            var row = await assemblies.StartAsync(db, corpus, request, actor.Persona, actor.DisplayName, ct);
            try
            {
                // Whichever way it starts (this button or the copilot), the Migration agent narrates it into the programme's thread.
                var thread = await conversations.EnsureProgrammeAsync(corpus.Id, ct);
                narrator.Track(row.Id, thread.Id, AssemblyService.Agent, "assembly", row.Name, corpus.Id,
                    actor.Persona.ToString().ToLowerInvariant(), actor.DisplayName);
            }
            catch (Exception) { /* narration is best-effort */ }
            await audit.LogAsync("assembly.started", "assembly", row.Id, actor, new { corpusId = corpus.Id, row.Name, row.TargetStack, request.PathPrefix, request.WaveId, units = request.UnitIds?.Count }, http, ct);
            return Results.Accepted($"/api/v1/assemblies/{row.Id}", new { assemblyId = row.Id, runId = row.Id, statusUrl = $"/api/v1/assemblies/{row.Id}", assembly = AssemblyService.Shape(row) });
        });

        app.MapGet("/api/v1/assemblies/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var a = await db.ModuleAssemblies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return a is null ? Results.NotFound(new { error = new { code = "assembly.not_found" } }) : Results.Ok(AssemblyService.Shape(a));
        });

        app.MapGet("/api/v1/corpora/{id:guid}/assemblies", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.ModuleAssemblies.AsNoTracking().Where(a => a.CorpusId == id)
                .OrderByDescending(a => a.CreatedAt).Take(50).ToListAsync(ct);
            return Results.Ok(new { data = rows.Select(AssemblyService.Shape) });
        });

        app.MapGet("/api/v1/assemblies/{id:guid}/log", async (Guid id, AppDbContext db, IBlobClient blob, CancellationToken ct) =>
        {
            var a = await db.ModuleAssemblies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a?.GateLogBlobUri is not { Length: > 0 } uri) return Results.NotFound(new { error = new { code = "assembly.no_log" } });
            return Results.Text(await blob.GetTextAsync(uri, ct), "text/plain");
        });

        app.MapGet("/api/v1/assemblies/{id:guid}/package.zip", async (Guid id, AppDbContext db, IBlobClient blob, CancellationToken ct) =>
        {
            var a = await db.ModuleAssemblies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a?.PackageBlobUri is not { Length: > 0 } uri) return Results.NotFound(new { error = new { code = "assembly.not_found" } });
            using var manifest = JsonDocument.Parse(await blob.GetTextAsync(uri, ct));
            var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var f in manifest.RootElement.GetProperty("files").EnumerateArray())
                {
                    var path = f.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains("..")) continue;
                    var content = f.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                    var entry = zip.CreateEntry(path, System.IO.Compression.CompressionLevel.Fastest);
                    await using var w = new StreamWriter(entry.Open(), new System.Text.UTF8Encoding(false));
                    await w.WriteAsync(content);
                }
            }
            ms.Position = 0;
            return Results.File(ms, "application/zip", $"{ModuleAssemblers.Slug(a.Name)}-service.zip");
        });

        return app;
    }
}
