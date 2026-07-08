using Astra.Api.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Phase 12.0.g — Demo seed for the headline C# corpus.
///
/// Pulls a curated subset of Jon Galloway's MVC Music Store reference app
/// (https://github.com/jongalloway/MVCMusicStore) — the canonical
/// ASP.NET MVC 5 / .NET Framework line-of-business tutorial that illustrates
/// common migration traps: Global.asax wiring, EF6 DbContext,
/// OWIN-based identity, sync controllers, and Http* types.
///
/// Idempotent: skips if a corpus with the same name already exists.
/// Network-dependent: gated behind <c>Database:SeedMvcMusicStoreDemo</c>.
///
/// Files included (from src/MVC5/MvcMusicStore/):
/// <list type="bullet">
///   <item>Controllers/ — all MVC controllers</item>
///   <item>Models/ — entity and view-model classes</item>
///   <item>ViewModels/ — shopping cart and store view models</item>
///   <item>App_Start/ — route, bundle, filter, and auth wiring</item>
/// </list>
/// Views (.cshtml), migrations, and config XML are excluded — the
/// parser works on .cs files only.
/// </summary>
public sealed class MvcMusicStoreSeed
{
    public const string CorpusName = "MVC Music Store (C# / ASP.NET MVC 5)";
    public const string GitUrl = "https://github.com/jongalloway/MVCMusicStore.git";

    /// <summary>Path prefixes inside the repo that we include.</summary>
    private static readonly string[] WhitelistPrefixes =
    {
        "src/MVC5/MvcMusicStore/Controllers/",
        "src/MVC5/MvcMusicStore/Models/",
        "src/MVC5/MvcMusicStore/ViewModels/",
        "src/MVC5/MvcMusicStore/App_Start/",
    };

    private readonly AppDbContext _db;
    private readonly IngestPipeline _pipeline;
    private readonly ILogger<MvcMusicStoreSeed> _logger;

    public MvcMusicStoreSeed(
        AppDbContext db,
        IngestPipeline pipeline,
        ILogger<MvcMusicStoreSeed> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Corpora.AnyAsync(c => c.Name == CorpusName, ct))
        {
            _logger.LogInformation("MVC Music Store demo seed skipped — corpus already present");
            return;
        }

        _logger.LogInformation("Seeding MVC Music Store demo corpus from {Url}", GitUrl);

        var workdir = Path.Combine(
            Path.GetTempPath(),
            "astra-mvcmusicstore-seed-" + Guid.NewGuid().ToString("N")[..12]);

        try
        {
            // Use the system git CLI instead of LibGit2Sharp for this repo.
            // The microsoft/ GitHub org triggers redirect-auth loops in LibGit2Sharp
            // even for public repos. git CLI handles it correctly.
            await CloneWithGitCliAsync(workdir, ct);
            var files = CollectCsharpFiles(workdir);
            if (files.Count == 0)
            {
                _logger.LogWarning(
                    "ASP.NET MusicStore clone produced 0 whitelisted C# files — upstream layout may have changed");
                return;
            }

            string? commitHash = null;
            try
            {
                using var repo = new LibGit2Sharp.Repository(workdir);
                commitHash = repo.Head?.Tip?.Sha;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read MvcMusicStore HEAD sha"); }

            var result = await _pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                Name: CorpusName,
                SourceType: "git",
                SourceUrl: GitUrl,
                Branch: null,
                SourceRoot: "src/MVC5/MvcMusicStore/",
                Files: files), ct);

            _logger.LogInformation(
                "MVC Music Store demo seed complete: state={State} files={Files} loc={Loc} subs={Subs} commit={Commit}",
                result.State, result.FileCount, result.TotalLoc, result.SubroutineCount,
                commitHash ?? "?");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ASP.NET MusicStore demo seed failed at clone (Git URL unreachable from the API container?). " +
                "The seed is non-essential; demo continues with the other seeds only.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(workdir))
                {
                    // Git objects are read-only on Windows — must clear attributes before delete.
                    foreach (var f in Directory.EnumerateFiles(workdir, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(workdir, recursive: true);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not clean up {Dir}", workdir); }
        }
    }

    private async Task CloneWithGitCliAsync(string targetDir, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            ArgumentList =
            {
                // Clear any credential helper so git does not attempt Basic auth.
                // Public repos on GitHub work without credentials; a helper injecting
                // stale tokens causes 'Authentication failed' errors.
                "-c", "credential.helper=",
                "clone",
                "--depth", "1",
                GitUrl,
                targetDir,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment =
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
            },
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git clone exited {proc.ExitCode}: {stderr.Trim()}");
    }

    private static List<IngestPipeline.IncomingFile> CollectCsharpFiles(string root)
    {
        var result = new List<IngestPipeline.IncomingFile>();

        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');

            // Only accept files under a whitelisted prefix.
            if (!WhitelistPrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Skip designer-generated files and migrations — they add noise without
            // meaningful migration guidance.
            var name = Path.GetFileName(path);
            if (name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)) continue;

            var fi = new FileInfo(path);
            if (fi.Length > 4 * 1024 * 1024) continue;  // 4 MiB ceiling per file

            result.Add(new IngestPipeline.IncomingFile(rel, File.ReadAllText(path)));
        }

        return result;
    }
}
