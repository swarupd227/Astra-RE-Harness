using Astra.Api.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Phase 9.1.g — Demo seed for the headline C++ corpus.
///
/// Pulls a curated subset of fmtlib/fmt from GitHub so the demo can showcase
/// a real C++ codebase alongside MINPACK / LAPACK-BLAS / Indy / the COBOL
/// corpus. We don't clone the entire fmt tree — even a "small" header-only
/// library has tests, benchmarks, and CMake glue that bulks the count up
/// to ~150 files. The Phase 9.1 demo only needs the public API surface plus
/// detail/ — about a dozen headers.
///
/// Idempotent: skips if a corpus with the same name already exists.
/// Network-dependent: gated behind <c>Database:SeedFmtDemo</c> so CI (where
/// outbound Git is sometimes blocked) can opt out cleanly.
///
/// Files included:
/// <list type="bullet">
///   <item>include/fmt/core.h — format_context, format_to, format</item>
///   <item>include/fmt/format.h — the format engine</item>
///   <item>include/fmt/base.h — basic types and traits (modern fmt)</item>
///   <item>include/fmt/args.h — dynamic_format_arg_store</item>
///   <item>include/fmt/format-inl.h — inline implementations</item>
///   <item>include/fmt/std.h — std::filesystem / std::variant formatters</item>
///   <item>include/fmt/chrono.h — chrono formatters</item>
///   <item>src/format.cc — non-inline definitions</item>
/// </list>
/// Anything outside this set is skipped so the ingest stays under ~6 MB /
/// ~12 files / a few minutes of parse time.
/// </summary>
public sealed class FmtDemoSeed
{
    public const string CorpusName = "fmt (C++ format library)";
    public const string GitUrl = "https://github.com/fmtlib/fmt.git";

    /// <summary>Path prefixes we accept inside the upstream repo.</summary>
    private static readonly string[] WhitelistPrefixes =
    {
        "include/fmt/core.h",
        "include/fmt/format.h",
        "include/fmt/base.h",
        "include/fmt/args.h",
        "include/fmt/format-inl.h",
        "include/fmt/std.h",
        "include/fmt/chrono.h",
        "include/fmt/ranges.h",
        "include/fmt/printf.h",
        "include/fmt/ostream.h",
        "src/format.cc",
    };

    private readonly AppDbContext _db;
    private readonly IngestPipeline _pipeline;
    private readonly ILogger<FmtDemoSeed> _logger;

    public FmtDemoSeed(
        AppDbContext db,
        IngestPipeline pipeline,
        ILogger<FmtDemoSeed> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Corpora.AnyAsync(c => c.Name == CorpusName, ct))
        {
            _logger.LogInformation("fmt demo seed skipped — corpus already present");
            return;
        }

        _logger.LogInformation("Seeding fmt demo corpus from {Url}", GitUrl);

        var workdir = Path.Combine(Path.GetTempPath(), "astra-fmt-seed-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            LibGit2Sharp.Repository.Clone(GitUrl, workdir);
            var files = CollectCppFiles(workdir);
            if (files.Count == 0)
            {
                _logger.LogWarning(
                    "fmt clone produced 0 whitelisted C++ files; upstream layout may have changed");
                return;
            }

            string? commitHash = null;
            try
            {
                using var repo = new LibGit2Sharp.Repository(workdir);
                commitHash = repo.Head?.Tip?.Sha;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read fmt HEAD sha"); }

            var result = await _pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                Name: CorpusName,
                SourceType: "git",
                SourceUrl: GitUrl,
                Branch: null,
                SourceRoot: "include/",
                Files: files), ct);

            _logger.LogInformation(
                "fmt demo seed complete: state={State} files={Files} loc={Loc} subs={Subs} commit={Commit}",
                result.State, result.FileCount, result.TotalLoc, result.SubroutineCount, commitHash ?? "?");
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            _logger.LogWarning(ex,
                "fmt demo seed failed at clone (Git URL unreachable from the API container?). " +
                "The seed is non-essential; demo continues with the other seeds only.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "fmt demo seed failed; demo continues without it");
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

    private static List<IngestPipeline.IncomingFile> CollectCppFiles(string root)
    {
        var result = new List<IngestPipeline.IncomingFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SourceLanguageDetector.CppExtensions.Contains(ext)) continue;
            var fi = new FileInfo(path);
            if (fi.Length > 8 * 1024 * 1024) continue;

            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!WhitelistPrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(new IngestPipeline.IncomingFile(rel, File.ReadAllText(path)));
        }
        return result;
    }
}
