using System.IO.Compression;
using System.Text;
using Astra.Api.Ingest;
using LibGit2Sharp;
using Microsoft.AspNetCore.Mvc;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase C.1 ingest surface. Three shapes:
///
///   POST /api/v1/ingest/upload   multipart: name + files[]   (zip OR individual .for/.f90)
///   POST /api/v1/ingest/git      json: { name, url, branch?, sourceRoot? }
///   POST /api/v1/ingest/text     json: { name, files: [{ path, content }] }  (for tests + curl)
///
/// All three call into <see cref="IngestPipeline"/> synchronously and return
/// the resulting <see cref="Corpus"/> id + state when parse completes.
/// </summary>
public static class IngestEndpoints
{
    private const long MaxUploadBytes = 32L * 1024 * 1024;   // 32 MiB per multipart body
    private const int MaxFilesPerCorpus = 500;
    private const int MaxFileBytes = 8 * 1024 * 1024;        // 8 MiB per individual file

    private static readonly string[] FortranExtensions =
    {
        ".f", ".f77", ".for", ".fpp", ".ftn",
        ".f90", ".f95", ".f03", ".f08", ".f15", ".f18",
    };

    public static IEndpointRouteBuilder MapIngestEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/v1/ingest");

        // ────────────────────────────────────────────────────────────────
        // 1) multipart upload (.zip OR a set of .f / .for / .f90 files)
        // ────────────────────────────────────────────────────────────────
        grp.MapPost("upload", async (HttpRequest request, IngestPipeline pipeline, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return BadRequest("ingest.invalid_form", "Expected multipart/form-data.");

            var form = await request.ReadFormAsync(ct);
            var name = form["name"].ToString();
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest("ingest.name_required", "Corpus name is required.");

            if (form.Files.Count == 0)
                return BadRequest("ingest.no_files", "At least one file or a .zip archive is required.");

            var collected = new List<IngestPipeline.IncomingFile>();
            var totalBytes = 0L;
            foreach (var file in form.Files)
            {
                if (file.Length == 0) continue;
                totalBytes += file.Length;
                if (totalBytes > MaxUploadBytes)
                    return BadRequest("ingest.upload_too_large",
                        $"Total upload exceeds {MaxUploadBytes / (1024 * 1024)} MiB.");

                var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? "";
                if (ext == ".zip")
                {
                    await foreach (var entry in ReadZipEntriesAsync(file, ct))
                    {
                        collected.Add(entry);
                        if (collected.Count > MaxFilesPerCorpus)
                            return BadRequest("ingest.too_many_files",
                                $"Corpus exceeds {MaxFilesPerCorpus} files. Split or filter the archive.");
                    }
                }
                else if (FortranExtensions.Contains(ext))
                {
                    if (file.Length > MaxFileBytes)
                        return BadRequest("ingest.file_too_large",
                            $"{file.FileName} exceeds {MaxFileBytes / (1024 * 1024)} MiB.");
                    using var stream = file.OpenReadStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    var content = await reader.ReadToEndAsync(ct);
                    collected.Add(new IngestPipeline.IncomingFile(
                        RelativePath: Path.GetFileName(file.FileName),
                        Content: content));
                }
                else
                {
                    return BadRequest("ingest.unsupported_extension",
                        $"Unsupported file type: {file.FileName}. Use .f/.for/.f90/.f95 or a .zip archive.");
                }
            }

            if (collected.Count == 0)
                return BadRequest("ingest.no_fortran_files",
                    "Archive contained no Fortran source files (.f, .for, .f90, etc.).");

            try
            {
                var result = await pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                    Name: name,
                    SourceType: "upload",
                    SourceUrl: null,
                    Branch: null,
                    SourceRoot: null,
                    Files: collected), ct);

                return Ok(result);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                return BadRequest("ingest.duplicate_name", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest("ingest.invalid_request", ex.Message);
            }
        })
        .DisableAntiforgery()
        .WithName("ingestUpload");

        // ────────────────────────────────────────────────────────────────
        // 2) Git URL clone
        // ────────────────────────────────────────────────────────────────
        grp.MapPost("git", async (IngestGitRequest body, IngestPipeline pipeline, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("ingest.git");

            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest("ingest.name_required", "Corpus name is required.");
            if (string.IsNullOrWhiteSpace(body.Url))
                return BadRequest("ingest.url_required", "Git URL is required.");

            string workdir = Path.Combine(Path.GetTempPath(), "astra-git-" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                // libgit2sharp 0.30 does not expose --depth; the clone is
                // full but small repos (Phase C target ≤200 files) clone
                // fast and we throw the worktree away afterwards anyway.
                var cloneOpts = new CloneOptions { BranchName = body.Branch };
                Repository.Clone(body.Url, workdir, cloneOpts);
                log.LogInformation("Cloned {Url} → {Dir}", body.Url, workdir);

                var rootRel = (body.SourceRoot ?? "").Trim().Trim('/');
                var root = string.IsNullOrEmpty(rootRel) ? workdir : Path.Combine(workdir, rootRel);
                if (!Directory.Exists(root))
                    return BadRequest("ingest.source_root_missing",
                        $"sourceRoot '{rootRel}' was not found in the cloned repo.");

                var files = CollectFortranFiles(root, MaxFilesPerCorpus, MaxFileBytes, out var error);
                if (error is not null) return BadRequest("ingest.scan_failed", error);
                if (files.Count == 0)
                    return BadRequest("ingest.no_fortran_files",
                        "No Fortran source files (.f, .for, .f90, etc.) found in the clone.");

                string? commitHash = null;
                try
                {
                    using var repo = new Repository(workdir);
                    commitHash = repo.Head?.Tip?.Sha;
                }
                catch (Exception ex) { log.LogWarning(ex, "Could not read HEAD sha"); }

                var result = await pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                    Name: body.Name,
                    SourceType: "git",
                    SourceUrl: body.Url,
                    Branch: body.Branch,
                    SourceRoot: rootRel,
                    Files: files), ct);

                return Results.Ok(new
                {
                    corpusId = result.CorpusId,
                    state = result.State,
                    fileCount = result.FileCount,
                    totalLoc = result.TotalLoc,
                    subroutineCount = result.SubroutineCount,
                    warnings = result.Warnings,
                    errorMessage = result.ErrorMessage,
                    gitCommitHash = commitHash,
                });
            }
            catch (LibGit2SharpException ex)
            {
                log.LogError(ex, "Git clone failed for {Url}", body.Url);
                return BadRequest("ingest.git_clone_failed",
                    $"Git clone failed: {ex.Message}. Verify the URL, branch, and that the API container can reach the host.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                return BadRequest("ingest.duplicate_name", ex.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workdir))
                    {
                        // git clone leaves files read-only on Windows; force.
                        foreach (var f in Directory.EnumerateFiles(workdir, "*", SearchOption.AllDirectories))
                            File.SetAttributes(f, FileAttributes.Normal);
                        Directory.Delete(workdir, recursive: true);
                    }
                }
                catch (Exception ex) { log.LogWarning(ex, "Could not clean up {Dir}", workdir); }
            }
        })
        .WithName("ingestGit");

        // ────────────────────────────────────────────────────────────────
        // 3) text / JSON ingest — for tests and curl-driven workflows
        // ────────────────────────────────────────────────────────────────
        grp.MapPost("text", async (IngestTextRequest body, IngestPipeline pipeline, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
                return BadRequest("ingest.name_required", "Corpus name is required.");
            if (body.Files is null || body.Files.Count == 0)
                return BadRequest("ingest.no_files", "At least one file is required.");
            if (body.Files.Count > MaxFilesPerCorpus)
                return BadRequest("ingest.too_many_files", $"Maximum {MaxFilesPerCorpus} files per corpus.");

            var collected = body.Files.Select(f => new IngestPipeline.IncomingFile(
                RelativePath: f.Path,
                Content: f.Content ?? "")).ToList();

            try
            {
                var result = await pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                    Name: body.Name,
                    SourceType: "upload",
                    SourceUrl: null,
                    Branch: null,
                    SourceRoot: null,
                    Files: collected), ct);

                return Ok(result);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
            {
                return BadRequest("ingest.duplicate_name", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BadRequest("ingest.invalid_request", ex.Message);
            }
        })
        .WithName("ingestText");

        // ────────────────────────────────────────────────────────────────
        // 4) Re-sync: ingest a new SourceVersion for an existing corpus
        //    (Phase C.3). Same shape as the create endpoints, but the
        //    corpus id is in the URL.
        // ────────────────────────────────────────────────────────────────
        var reingest = app.MapGroup("/api/v1/corpora/{corpusId:guid}/reingest");

        reingest.MapPost("upload", async (Guid corpusId, HttpRequest request, IngestPipeline pipeline, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return BadRequest("ingest.invalid_form", "Expected multipart/form-data.");
            var form = await request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
                return BadRequest("ingest.no_files", "At least one file or a .zip archive is required.");

            var collected = await ReadFortranFilesFromForm(form);
            if (collected.Error is not null) return BadRequest(collected.ErrorCode!, collected.Error);
            if (collected.Files.Count == 0)
                return BadRequest("ingest.no_fortran_files",
                    "Upload contained no Fortran source files (.f, .for, .f90, etc.).");

            return await RunReingest(pipeline, new IngestPipeline.ReingestRequest(
                CorpusId: corpusId,
                SourceUrl: null, Branch: null, SourceRoot: null, GitCommitHash: null,
                Files: collected.Files), ct);
        })
        .DisableAntiforgery()
        .WithName("reingestUpload");

        reingest.MapPost("git", async (Guid corpusId, IngestEndpoints.IngestGitRequest body, IngestPipeline pipeline, ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("ingest.git.reingest");
            if (string.IsNullOrWhiteSpace(body.Url))
                return BadRequest("ingest.url_required", "Git URL is required.");

            var workdir = Path.Combine(Path.GetTempPath(), "astra-git-" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                var cloneOpts = new CloneOptions { BranchName = body.Branch };
                Repository.Clone(body.Url, workdir, cloneOpts);
                var rootRel = (body.SourceRoot ?? "").Trim().Trim('/');
                var root = string.IsNullOrEmpty(rootRel) ? workdir : Path.Combine(workdir, rootRel);
                if (!Directory.Exists(root))
                    return BadRequest("ingest.source_root_missing",
                        $"sourceRoot '{rootRel}' was not found in the cloned repo.");

                var files = CollectFortranFiles(root, MaxFilesPerCorpus, MaxFileBytes, out var error);
                if (error is not null) return BadRequest("ingest.scan_failed", error);
                if (files.Count == 0)
                    return BadRequest("ingest.no_fortran_files",
                        "No Fortran source files (.f, .for, .f90, etc.) found in the clone.");

                string? commitHash = null;
                try
                {
                    using var repo = new Repository(workdir);
                    commitHash = repo.Head?.Tip?.Sha;
                }
                catch (Exception ex) { log.LogWarning(ex, "Could not read HEAD sha"); }

                return await RunReingest(pipeline, new IngestPipeline.ReingestRequest(
                    CorpusId: corpusId,
                    SourceUrl: body.Url,
                    Branch: body.Branch,
                    SourceRoot: rootRel,
                    GitCommitHash: commitHash,
                    Files: files), ct);
            }
            catch (LibGit2SharpException ex)
            {
                log.LogError(ex, "Git clone failed for {Url}", body.Url);
                return BadRequest("ingest.git_clone_failed",
                    $"Git clone failed: {ex.Message}. Verify the URL, branch, and that the API container can reach the host.");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workdir))
                    {
                        foreach (var f in Directory.EnumerateFiles(workdir, "*", SearchOption.AllDirectories))
                            File.SetAttributes(f, FileAttributes.Normal);
                        Directory.Delete(workdir, recursive: true);
                    }
                }
                catch (Exception ex) { log.LogWarning(ex, "Could not clean up {Dir}", workdir); }
            }
        })
        .WithName("reingestGit");

        reingest.MapPost("text", async (Guid corpusId, IngestEndpoints.IngestTextRequest body, IngestPipeline pipeline, CancellationToken ct) =>
        {
            if (body.Files is null || body.Files.Count == 0)
                return BadRequest("ingest.no_files", "At least one file is required.");
            if (body.Files.Count > MaxFilesPerCorpus)
                return BadRequest("ingest.too_many_files", $"Maximum {MaxFilesPerCorpus} files per corpus.");

            var collected = body.Files.Select(f => new IngestPipeline.IncomingFile(
                RelativePath: f.Path,
                Content: f.Content ?? "")).ToList();

            return await RunReingest(pipeline, new IngestPipeline.ReingestRequest(
                CorpusId: corpusId,
                SourceUrl: null, Branch: null, SourceRoot: null, GitCommitHash: null,
                Files: collected), ct);
        })
        .WithName("reingestText");

        return app;
    }

    private static async Task<IResult> RunReingest(IngestPipeline pipeline, IngestPipeline.ReingestRequest req, CancellationToken ct)
    {
        try
        {
            var r = await pipeline.ReingestAsync(req, ct);
            return Results.Ok(new
            {
                corpusId = r.CorpusId,
                state = r.State,
                fileCount = r.FileCount,
                totalLoc = r.TotalLoc,
                subroutineCount = r.SubroutineCount,
                carriedForwardCount = r.CarriedForwardCount,
                supersededCount = r.SupersededCount,
                warnings = r.Warnings,
                errorMessage = r.ErrorMessage,
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return Results.NotFound(new { error = new { code = "corpus.not_found", message = ex.Message } });
        }
        catch (ArgumentException ex)
        {
            return BadRequest("ingest.invalid_request", ex.Message);
        }
    }

    private sealed record FormFiles(
        List<IngestPipeline.IncomingFile> Files,
        string? ErrorCode,
        string? Error);

    private static async Task<FormFiles> ReadFortranFilesFromForm(IFormCollection form)
    {
        var collected = new List<IngestPipeline.IncomingFile>();
        var totalBytes = 0L;
        foreach (var file in form.Files)
        {
            if (file.Length == 0) continue;
            totalBytes += file.Length;
            if (totalBytes > MaxUploadBytes)
                return new(collected, "ingest.upload_too_large",
                    $"Total upload exceeds {MaxUploadBytes / (1024 * 1024)} MiB.");

            var ext = Path.GetExtension(file.FileName)?.ToLowerInvariant() ?? "";
            if (ext == ".zip")
            {
                await foreach (var entry in ReadZipEntriesAsync(file, CancellationToken.None))
                {
                    collected.Add(entry);
                    if (collected.Count > MaxFilesPerCorpus)
                        return new(collected, "ingest.too_many_files",
                            $"Corpus exceeds {MaxFilesPerCorpus} files. Split or filter the archive.");
                }
            }
            else if (FortranExtensions.Contains(ext))
            {
                if (file.Length > MaxFileBytes)
                    return new(collected, "ingest.file_too_large",
                        $"{file.FileName} exceeds {MaxFileBytes / (1024 * 1024)} MiB.");
                using var stream = file.OpenReadStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var content = await reader.ReadToEndAsync();
                collected.Add(new IngestPipeline.IncomingFile(Path.GetFileName(file.FileName), content));
            }
            else
            {
                return new(collected, "ingest.unsupported_extension",
                    $"Unsupported file type: {file.FileName}. Use .f/.for/.f90/.f95 or a .zip archive.");
            }
        }
        return new(collected, null, null);
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<IngestPipeline.IncomingFile> ReadZipEntriesAsync(
        IFormFile file, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var stream = file.OpenReadStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;  // directory
            if (entry.Length > MaxFileBytes) continue;       // silently skip oversize
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (!FortranExtensions.Contains(ext)) continue;

            string text;
            using (var es = entry.Open())
            using (var reader = new StreamReader(es, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = await reader.ReadToEndAsync(ct);

            yield return new IngestPipeline.IncomingFile(
                RelativePath: entry.FullName.Replace('\\', '/'),
                Content: text);
        }
    }

    private static List<IngestPipeline.IncomingFile> CollectFortranFiles(
        string root, int maxFiles, int maxFileBytes, out string? error)
    {
        error = null;
        var result = new List<IngestPipeline.IncomingFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!FortranExtensions.Contains(ext)) continue;

            var fi = new FileInfo(path);
            if (fi.Length > maxFileBytes) continue;
            if (result.Count >= maxFiles)
            {
                error = $"Corpus exceeds {maxFiles} files; provide a more specific sourceRoot.";
                return result;
            }

            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            result.Add(new IngestPipeline.IncomingFile(rel, File.ReadAllText(path)));
        }
        return result;
    }

    private static IResult Ok(IngestPipeline.IngestResult r) =>
        Results.Ok(new
        {
            corpusId = r.CorpusId,
            state = r.State,
            fileCount = r.FileCount,
            totalLoc = r.TotalLoc,
            subroutineCount = r.SubroutineCount,
            warnings = r.Warnings,
            errorMessage = r.ErrorMessage,
        });

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });

    public sealed record IngestGitRequest(string Name, string Url, string? Branch, string? SourceRoot);

    public sealed record IngestTextRequest(string Name, List<IngestTextFile> Files);

    public sealed record IngestTextFile(string Path, string? Content);
}
