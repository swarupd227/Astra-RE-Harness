using Astra.Api.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Phase 9.0.h — Demo seed for the headline Delphi corpus.
///
/// Pulls a curated subset of the Indy networking library from GitHub
/// (IndySockets/Indy) so the demo can showcase a real Delphi codebase
/// alongside MINPACK / LAPACK-BLAS / the COBOL corpus. We don't clone
/// the entire Indy tree — that's ~80 MB and most of it (the protocol
/// implementations) is out of scope for the Phase 9.0 demo, which
/// targets <c>TIdSMTP.Connect</c> and its direct dependencies.
///
/// Idempotent: skips if a corpus with the same name already exists.
/// Network-dependent: gated behind <c>Database:SeedIndyDemo</c> so CI
/// (where outbound Git is sometimes blocked) can opt out cleanly.
///
/// Files included:
/// <list type="bullet">
///   <item>Lib/Core/IdComponent.pas — base component class</item>
///   <item>Lib/Core/IdTCPClient.pas — TCP client base</item>
///   <item>Lib/Core/IdTCPConnection.pas — connection lifecycle</item>
///   <item>Lib/Core/IdGlobal.pas — RTL adapter constants</item>
///   <item>Lib/Core/IdStack*.pas — socket-stack abstraction</item>
///   <item>Lib/Protocols/IdSMTP.pas — the demo's headline routine</item>
///   <item>Lib/Protocols/IdSMTPBase.pas — SMTP base / command pipeline</item>
///   <item>Lib/Protocols/IdMessage*.pas — message construction</item>
/// </list>
/// Anything outside that list is skipped so the ingest stays under
/// ~5 MB / ~30 files / a few minutes of fparser-equivalent parse time.
/// </summary>
public sealed class IndyDemoSeed
{
    public const string CorpusName = "Indy Sockets (Delphi)";
    public const string GitUrl = "https://github.com/IndySockets/Indy.git";

    /// <summary>Path prefixes we accept inside the upstream repo.</summary>
    private static readonly string[] WhitelistPrefixes =
    {
        "Lib/Core/IdComponent",
        "Lib/Core/IdTCPClient",
        "Lib/Core/IdTCPConnection",
        "Lib/Core/IdGlobal",
        "Lib/Core/IdStack",
        "Lib/Core/IdIOHandler",
        "Lib/Core/IdScheduler",
        "Lib/Core/IdThread",
        "Lib/Core/IdSocketHandle",
        "Lib/Protocols/IdSMTP",
        "Lib/Protocols/IdMessage",
        "Lib/Protocols/IdReplySMTP",
        "Lib/Protocols/IdExplicitTLSClientServerBase",
    };

    private readonly AppDbContext _db;
    private readonly IngestPipeline _pipeline;
    private readonly ILogger<IndyDemoSeed> _logger;

    public IndyDemoSeed(
        AppDbContext db,
        IngestPipeline pipeline,
        ILogger<IndyDemoSeed> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Corpora.AnyAsync(c => c.Name == CorpusName, ct))
        {
            _logger.LogInformation("Indy Sockets demo seed skipped — corpus already present");
            return;
        }

        _logger.LogInformation("Seeding Indy Sockets demo corpus from {Url}", GitUrl);

        var workdir = Path.Combine(Path.GetTempPath(), "astra-indy-seed-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            LibGit2Sharp.Repository.Clone(GitUrl, workdir);
            var files = CollectDelphiFiles(workdir);
            if (files.Count == 0)
            {
                _logger.LogWarning(
                    "Indy clone produced 0 whitelisted Delphi files; upstream layout may have changed");
                return;
            }

            string? commitHash = null;
            try
            {
                using var repo = new LibGit2Sharp.Repository(workdir);
                commitHash = repo.Head?.Tip?.Sha;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read Indy HEAD sha"); }

            var result = await _pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                Name: CorpusName,
                SourceType: "git",
                SourceUrl: GitUrl,
                Branch: null,
                SourceRoot: "Lib/",
                Files: files), ct);

            _logger.LogInformation(
                "Indy Sockets demo seed complete: state={State} files={Files} loc={Loc} subs={Subs} commit={Commit}",
                result.State, result.FileCount, result.TotalLoc, result.SubroutineCount, commitHash ?? "?");
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            _logger.LogWarning(ex,
                "Indy demo seed failed at clone (Git URL unreachable from the API container?). " +
                "The seed is non-essential; demo continues with the other seeds only.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Indy demo seed failed; demo continues without it");
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

    private static List<IngestPipeline.IncomingFile> CollectDelphiFiles(string root)
    {
        var result = new List<IngestPipeline.IncomingFile>();
        var lib = Path.Combine(root, "Lib");
        if (!Directory.Exists(lib)) return result;

        foreach (var path in Directory.EnumerateFiles(lib, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SourceLanguageDetector.DelphiExtensions.Contains(ext)) continue;
            var fi = new FileInfo(path);
            if (fi.Length > 8 * 1024 * 1024) continue;

            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!WhitelistPrefixes.Any(p => rel.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            result.Add(new IngestPipeline.IncomingFile(rel, File.ReadAllText(path)));
        }
        return result;
    }
}
