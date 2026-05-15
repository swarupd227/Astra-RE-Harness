using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Seeds the canonical CONSUME_ROLL corpus from spec Appendix B.
/// Idempotent: skips if a corpus with the same name already exists.
/// </summary>
public sealed class ConsumeRollSeed
{
    // Public so tests / docs can refer to a single source of truth. Pure
    // synthetic demo content — no client names, no real product strings.
    public const string CorpusName = "Roll-stock inventory demo (Fortran F77)";
    public const string FilePath = "src/CONSUME_ROLL.FOR";

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ConsumeRollSeed> _logger;

    public ConsumeRollSeed(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IAuditLogger audit,
        ILogger<ConsumeRollSeed> logger)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Corpora.AnyAsync(c => c.Name == CorpusName, ct))
        {
            _logger.LogInformation("Seed skipped — corpus '{Name}' already present", CorpusName);
            return;
        }

        _logger.LogInformation("Seeding {Name}", CorpusName);

        // 1. Upload the canonical CONSUME_ROLL.FOR to MinIO.
        var sourceText = ConsumeRollSource.Fortran;
        var fileHash = Sha256(sourceText);
        var versionId = Guid.NewGuid();
        var objectName = $"{versionId}/{FilePath}";
        var blobUri = await _blob.PutTextAsync(
            _storage.Buckets.Sources,
            objectName,
            sourceText,
            "text/x-fortran",
            ct);

        // 2. Persist the corpus + version + file + subroutine as one transaction.
        var now = DateTimeOffset.UtcNow;
        var corpus = new Corpus
        {
            Id = Guid.NewGuid(),
            Name = CorpusName,
            SourceType = "upload",
            State = "PARSED",
            FileCount = 1,
            TotalLoc = sourceText.Count(c => c == '\n') + 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var version = new SourceVersion
        {
            Id = versionId,
            CorpusId = corpus.Id,
            IngestedAt = now,
            FileManifestBlobUri = $"minio://{_storage.Buckets.Sources}/{versionId}/manifest.json",
        };

        var file = new SourceFile
        {
            Id = Guid.NewGuid(),
            SourceVersionId = version.Id,
            RelativePath = FilePath,
            FileHash = fileHash,
            LineCount = sourceText.Split('\n').Length,
            BlobUri = blobUri,
        };

        var sub = new Subroutine
        {
            Id = Guid.NewGuid(),
            SourceFileId = file.Id,
            Name = "CONSUME_ROLL",
            Signature = "CONSUME_ROLL(ROLL_ID, USED_LF, OPER_ID, RESULT_CD)",
            LineStart = 1,
            LineEnd = file.LineCount,
            CommonBlockRefs = JsonDocument.Parse("[\"INVCMN\",\"AUDMSG\"]"),
            CalledSubroutines = JsonDocument.Parse("[\"INV_READ\",\"INV_WRITE\",\"EMIT_EVENT\"]"),
            IoPatterns = JsonDocument.Parse("""
                {"isam_reads":["INV_READ"],"isam_writes":["INV_WRITE"],"notifications":["EMIT_EVENT"]}
                """),
            State = "PARSED",
        };

        corpus.LatestVersionId = version.Id;

        await _db.Corpora.AddAsync(corpus, ct);
        await _db.SourceVersions.AddAsync(version, ct);
        await _db.SourceFiles.AddAsync(file, ct);
        await _db.Subroutines.AddAsync(sub, ct);
        await _db.SaveChangesAsync(ct);

        // Synthetic audit events so the timeline isn't empty on first load.
        await _audit.LogAsync("corpus.ingested", "corpus", corpus.Id, actor: null,
            payload: new { name = corpus.Name, fileCount = corpus.FileCount, totalLoc = corpus.TotalLoc },
            ct: ct);
        await _audit.LogAsync("source.parsed", "subroutine", sub.Id, actor: null,
            payload: new { name = sub.Name, file = file.RelativePath, lines = $"{sub.LineStart}-{sub.LineEnd}" },
            ct: ct);

        _logger.LogInformation("Seed complete: corpus={Corpus} subroutine={Sub}", corpus.Id, sub.Id);
    }

    private static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
