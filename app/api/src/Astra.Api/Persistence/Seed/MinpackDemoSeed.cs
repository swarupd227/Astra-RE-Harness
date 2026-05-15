using Astra.Api.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Demo seed for a real third-party Fortran codebase. Pulls MINPACK from
/// GitHub via the existing Git-URL ingest pipeline so the demo doesn't
/// open on a synthetic 66-line corpus — stakeholders see 50 files /
/// 10,800 LOC / 52 subroutines extracted by fparser2.
///
/// Idempotent: skips if a corpus with the same name already exists.
/// Network-dependent: gated behind <c>Database:SeedMinpack</c> so CI
/// (where outbound Git is sometimes blocked) can opt out cleanly.
/// </summary>
public sealed class MinpackDemoSeed
{
    public const string CorpusName = "MINPACK (F77 nonlinear least squares)";
    public const string GitUrl = "https://github.com/certik/minpack.git";

    private readonly AppDbContext _db;
    private readonly IngestPipeline _pipeline;
    private readonly ILogger<MinpackDemoSeed> _logger;

    public MinpackDemoSeed(
        AppDbContext db,
        IngestPipeline pipeline,
        ILogger<MinpackDemoSeed> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Corpora.AnyAsync(c => c.Name == CorpusName, ct))
        {
            _logger.LogInformation("MINPACK demo seed skipped — corpus already present");
            return;
        }

        _logger.LogInformation("Seeding MINPACK demo corpus from {Url}", GitUrl);

        // We reuse the same Git-URL ingest the engineer-facing /api/v1/ingest/git
        // endpoint uses — same libgit2sharp clone, same fparser2 parse — so
        // anything that works for the demo seed works for real ingests too.
        var workdir = Path.Combine(Path.GetTempPath(), "astra-minpack-seed-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            LibGit2Sharp.Repository.Clone(GitUrl, workdir);
            var files = CollectFortranFiles(workdir);
            if (files.Count == 0)
            {
                _logger.LogWarning("MINPACK clone produced 0 Fortran files; skipping seed");
                return;
            }

            string? commitHash = null;
            try
            {
                using var repo = new LibGit2Sharp.Repository(workdir);
                commitHash = repo.Head?.Tip?.Sha;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read MINPACK HEAD sha"); }

            var result = await _pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                Name: CorpusName,
                SourceType: "git",
                SourceUrl: GitUrl,
                Branch: null,
                SourceRoot: null,
                Files: files), ct);

            _logger.LogInformation(
                "MINPACK demo seed complete: state={State} files={Files} loc={Loc} subs={Subs} commit={Commit}",
                result.State, result.FileCount, result.TotalLoc, result.SubroutineCount, commitHash ?? "?");
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            _logger.LogWarning(ex,
                "MINPACK demo seed failed at clone (Git URL unreachable from the API container?). " +
                "The seed is non-essential; demo continues with the synthetic seed only.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MINPACK demo seed failed; demo continues without it");
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
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!FortranExtensions.Contains(ext)) continue;
            var fi = new FileInfo(path);
            if (fi.Length > 8 * 1024 * 1024) continue;
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            result.Add(new IngestPipeline.IncomingFile(rel, File.ReadAllText(path)));
        }
        return result;
    }
}
