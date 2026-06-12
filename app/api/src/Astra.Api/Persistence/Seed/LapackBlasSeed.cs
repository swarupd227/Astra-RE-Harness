using Astra.Api.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Demo seed for a second real third-party Fortran codebase. Pulls the
/// LAPACK Reference BLAS sources from GitHub via the existing Git-URL
/// ingest pipeline so the demo can showcase a second corpus alongside
/// MINPACK — ~140 routines / ~25k LOC of canonical reference BLAS Fortran
/// extracted by fparser2.
///
/// Idempotent: skips if a corpus with the same name already exists.
/// Network-dependent: gated behind <c>Database:SeedLapackBlas</c> so CI
/// (where outbound Git is sometimes blocked) can opt out cleanly.
///
/// Only files under the upstream repo's <c>BLAS/SRC/</c> subdirectory
/// are ingested — the full LAPACK tree is much larger and out of scope
/// for the BLAS-focused demo corpus.
/// </summary>
public sealed class LapackBlasSeed
{
    public const string CorpusName = "LAPACK Reference BLAS";
    public const string GitUrl = "https://github.com/Reference-LAPACK/lapack.git";
    private const string SrcSubdir = "BLAS/SRC";

    private readonly AppDbContext _db;
    private readonly IngestPipeline _pipeline;
    private readonly ILogger<LapackBlasSeed> _logger;

    public LapackBlasSeed(
        AppDbContext db,
        IngestPipeline pipeline,
        ILogger<LapackBlasSeed> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Corpora.AnyAsync(c => c.Name == CorpusName, ct))
        {
            _logger.LogInformation("LAPACK Reference BLAS demo seed skipped — corpus already present");
            return;
        }

        _logger.LogInformation("Seeding LAPACK Reference BLAS demo corpus from {Url}", GitUrl);

        // We reuse the same Git-URL ingest the engineer-facing /api/v1/ingest/git
        // endpoint uses — same libgit2sharp clone, same fparser2 parse — so
        // anything that works for the demo seed works for real ingests too.
        var workdir = Path.Combine(Path.GetTempPath(), "astra-lapack-blas-seed-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            LibGit2Sharp.Repository.Clone(GitUrl, workdir);
            var files = CollectFortranFiles(workdir);
            if (files.Count == 0)
            {
                _logger.LogWarning("LAPACK Reference BLAS clone produced 0 Fortran files; skipping seed");
                return;
            }

            string? commitHash = null;
            try
            {
                using var repo = new LibGit2Sharp.Repository(workdir);
                commitHash = repo.Head?.Tip?.Sha;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read LAPACK HEAD sha"); }

            var result = await _pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                Name: CorpusName,
                SourceType: "git",
                SourceUrl: GitUrl,
                Branch: null,
                SourceRoot: SrcSubdir,
                Files: files), ct);

            _logger.LogInformation(
                "LAPACK Reference BLAS demo seed complete: state={State} files={Files} loc={Loc} subs={Subs} commit={Commit}",
                result.State, result.FileCount, result.TotalLoc, result.SubroutineCount, commitHash ?? "?");
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            _logger.LogWarning(ex,
                "LAPACK Reference BLAS demo seed failed at clone (Git URL unreachable from the API container?). " +
                "The seed is non-essential; demo continues with the other seeds only.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LAPACK Reference BLAS demo seed failed; demo continues without it");
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
            catch (Exception ex) { _logger.LogWarning(ex, "Could not clean up {Dir}", workdir); }
        }
    }

    private static readonly string[] FortranExtensions =
    {
        ".f", ".f77", ".for", ".fpp", ".ftn",
        ".f90", ".f95", ".f03", ".f08", ".f15", ".f18",
    };

    private static List<IngestPipeline.IncomingFile> CollectFortranFiles(string root)
    {
        var result = new List<IngestPipeline.IncomingFile>();
        var srcRoot = Path.Combine(root, "BLAS", "SRC");
        if (!Directory.Exists(srcRoot))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(srcRoot, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!FortranExtensions.Contains(ext)) continue;
            var fi = new FileInfo(path);
            if (fi.Length > 8 * 1024 * 1024) continue;
            // Relative path is BLAS/SRC/<file> so the corpus's file tree
            // mirrors the upstream repo layout (rather than collapsing
            // it to just <file>), which keeps the UI source-tree column
            // honest about provenance.
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!rel.StartsWith("BLAS/SRC/", StringComparison.Ordinal)) continue;
            result.Add(new IngestPipeline.IncomingFile(rel, File.ReadAllText(path)));
        }
        return result;
    }
}
