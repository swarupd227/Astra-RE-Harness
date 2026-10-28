using Astra.Api.Artefacts;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

public static class ArtefactEndpoints
{
    // The transport ceiling in Program.cs (640 MiB) sits above this, so the friendly
    // check below runs before any bare 413.
    private const long MaxUploadBytes = 128L * 1024 * 1024;
    private const int MaxFilesPerCall = 500;

    public static IEndpointRouteBuilder MapArtefactEndpoints(this IEndpointRouteBuilder app)
    {
        // The registry of artefact kinds a project can take. Read-only and ungated:
        // the New-project page and the settings page build their choices from it.
        app.MapGet("/api/v1/artefact-kinds", () =>
            Results.Ok(new { data = ArtefactKinds.All.Select(Describe).ToList() }));

        // What the current version holds, per kind. Ungated.
        app.MapGet("/api/v1/corpora/{id:guid}/artefacts", async (Guid id, ArtefactAttachService svc, CancellationToken ct) =>
        {
            var summary = await svc.SummaryAsync(id, ct);
            return summary is null
                ? Results.NotFound(new { error = new { code = "corpus.not_found" } })
                : Results.Ok(summary);
        });

        // Add or replace artefacts on the current version. multipart: files[] plus, optionally,
        // `path` (repeated, aligned with the files — keeps folder structure) and `kind` (one kind
        // for the whole drop). Always answers per file; a refusal is a result, not an error.
        app.MapPost("/api/v1/corpora/{id:guid}/artefacts", async (
            Guid id,
            HttpRequest request,
            ArtefactAttachService svc,
            DevPersonaContext actor,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!CanWrite(actor)) return WriterRequired();
            if (!request.HasFormContentType)
                return BadRequest("attach.invalid_form", "Expected multipart/form-data.");

            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return BadRequest("attach.no_files", "At least one file is required.");
            if (form.Files.Count > MaxFilesPerCall)
                return BadRequest("attach.too_many_files", $"At most {MaxFilesPerCall} files per upload.");

            var paths = form["path"];
            var kind = form["kind"].ToString();
            var incoming = new List<IncomingArtefact>(form.Files.Count);
            var total = 0L;
            for (var i = 0; i < form.Files.Count; i++)
            {
                var file = form.Files[i];
                total += file.Length;
                if (total > MaxUploadBytes)
                    return BadRequest("attach.upload_too_large", $"The upload is larger than {MaxUploadBytes / (1024 * 1024)} MiB.");

                var rawPath = i < paths.Count && !string.IsNullOrWhiteSpace(paths[i]) ? paths[i]! : file.FileName;
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                incoming.Add(new IncomingArtefact(rawPath, ms.ToArray(), string.IsNullOrWhiteSpace(kind) ? null : kind));
            }

            var result = await svc.AttachAsync(id, incoming, actor, http, ct);
            if (result.Outcome is null)
            {
                return result.ErrorCode switch
                {
                    "corpus.not_found" => Results.NotFound(new { error = new { code = "corpus.not_found" } }),
                    "attach.no_version" => Results.Json(
                        new { error = new { code = "attach.no_version", message = "This project has no source version yet. Ingest its source code first." } },
                        statusCode: StatusCodes.Status409Conflict),
                    _ => Results.Json(
                        new { error = new { code = result.ErrorCode, message = "Another change to this project landed at the same time. Try again." } },
                        statusCode: StatusCodes.Status409Conflict),
                };
            }

            var o = result.Outcome;
            return Results.Ok(new
            {
                corpusId = o.CorpusId,
                versionId = o.VersionId,
                counts = new { added = o.Count("added"), replaced = o.Count("replaced"), unchanged = o.Count("unchanged"), rejected = o.Count("rejected") },
                files = o.Files,
            });
        })
        .DisableAntiforgery();

        // Remove one artefact from the current version. Source code is removed by re-syncing.
        app.MapDelete("/api/v1/corpora/{id:guid}/artefacts", async (
            Guid id,
            string path,
            ArtefactAttachService svc,
            DevPersonaContext actor,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!CanWrite(actor)) return WriterRequired();
            return await svc.RemoveAsync(id, path, actor, http, ct) switch
            {
                "removed" => Results.Ok(new { removed = true, path }),
                "is_code" => BadRequest("artefact.is_code", "That is a source-code file; remove it by re-syncing the project without it."),
                "invalid_path" => BadRequest("artefact.invalid_path", "The path is not valid."),
                _ => Results.NotFound(new { error = new { code = "artefact.not_found" } }),
            };
        });

        // The stored bytes of an artefact, with Range support so an <audio> element can seek.
        // Source code is served by the routine and source routes, not here. Ungated, like every read.
        app.MapGet("/api/v1/artefacts/{fileId:guid}/content", async (
            Guid fileId, AppDbContext db, IBlobClient blob, HttpResponse response, CancellationToken ct) =>
        {
            var file = await db.SourceFiles.AsNoTracking()
                .Where(f => f.Id == fileId && f.Kind != ArtefactKinds.Code)
                .Select(f => new { f.BlobUri, f.ContentType, f.FileHash })
                .FirstOrDefaultAsync(ct);
            if (file is null)
                return Results.NotFound(new { error = new { code = "artefact.not_found", message = "No such artefact." } });

            var bytes = await blob.GetBytesAsync(file.BlobUri, ct);
            response.Headers.CacheControl = "private, max-age=300";
            return Results.File(
                new MemoryStream(bytes, writable: false),
                file.ContentType ?? "application/octet-stream",
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"" + file.FileHash + "\""),
                enableRangeProcessing: true);
        });

        return app;
    }

    // Engineers and SMEs add evidence (the demo's presenter is an SME); Observers read.
    private static bool CanWrite(DevPersonaContext actor) =>
        actor.Persona is Persona.Engineer or Persona.Sme or Persona.Admin;

    private static IResult WriterRequired() =>
        Results.Json(
            new { error = new { code = "auth.writer_required", message = "Adding or removing artefacts needs an Engineer, SME or Admin." } },
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });

    internal static object Describe(ArtefactKind k) => new
    {
        id = k.Id,
        label = k.Label,
        description = k.Description,
        extensions = k.Extensions,
        trustTier = k.Trust.ToString().ToLowerInvariant(),
        maxBytes = k.MaxBytes,
        text = k.IsText,
        core = k.Core,
    };
}
