using Astra.Api.Ingest;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Phase 10.0.h — Demo seed for the headline VB6 corpus.
///
/// Loads the Nous-authored "VB6 Inventory Sample" from
/// <c>SeedData/vb6-inventory-sample/</c> (copied to the API binary at
/// build time) into the database. Unlike <see cref="IndyDemoSeed"/> and
/// <see cref="FmtDemoSeed"/>, this corpus is NOT cloned from a public
/// Git URL because OSS VB6 is sparse and low-quality (per ADR-035's
/// seed-corpus decision); we ship our own.
///
/// The Inventory Sample is calibrated to exercise every VB6-specific
/// claim kind (onErrorHandler, comInteropContract, eventHandlerContract,
/// defaultPropertyUsage, lateBindingCall) and every trap in the
/// Phase 10.0.e Golden Dataset, so the spec extractor + equivalence
/// sidecar have a stable target for calibration before engagement teams
/// point the harness at real customer code.
///
/// Idempotent: skips if a corpus with the same name already exists.
/// Local-only: no network access — the source files are part of the
/// API image. Gated behind <c>Database:SeedVb6Demo</c> so CI can opt out.
///
/// Files included (10 files, ~1k LOC):
/// <list type="bullet">
///   <item>frmLogin.frm — entry form; On Error Resume Next anti-pattern</item>
///   <item>frmMainMDI.frm — MDI parent + menu</item>
///   <item>frmOrderEntry.frm — headline routine btnSubmit_Click</item>
///   <item>frmCustomerLookup.frm — MoveLast/MoveFirst RecordCount idiom</item>
///   <item>frmInvoicePrint.frm — second CreateObject site (visible Excel)</item>
///   <item>modOrders.bas — UpdateOrderTotal + InsertOrderHeader</item>
///   <item>modPricing.bas — ApplyDiscount + SafeAverage + FormatLineLabel</item>
///   <item>modReports.bas — headless Excel.Application export</item>
///   <item>modGlobals.bas — shared state + OpenSharedDatabase</item>
///   <item>modAudit.bas — audit-log wrapper</item>
///   <item>modUsers.bas — Authenticate + WeakHash</item>
///   <item>clsCustomer.cls — typed value object</item>
/// </list>
/// </summary>
public sealed class Vb6DemoSeed
{
    public const string CorpusName = "VB6 Inventory Sample (Nous)";
    public const string SeedRelativePath = "SeedData/vb6-inventory-sample";

    private readonly AppDbContext _db;
    private readonly IngestPipeline _pipeline;
    private readonly ILogger<Vb6DemoSeed> _logger;

    public Vb6DemoSeed(
        AppDbContext db,
        IngestPipeline pipeline,
        ILogger<Vb6DemoSeed> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _db.Corpora.AnyAsync(c => c.Name == CorpusName, ct))
        {
            _logger.LogInformation("VB6 Inventory Sample seed skipped — corpus already present");
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        var seedRoot = Path.Combine(baseDir, SeedRelativePath);

        if (!Directory.Exists(seedRoot))
        {
            _logger.LogWarning(
                "VB6 Inventory Sample seed root not found at {Path} — was SeedData copied to output?",
                seedRoot);
            return;
        }

        var files = CollectVb6Files(seedRoot);
        if (files.Count == 0)
        {
            _logger.LogWarning("VB6 Inventory Sample seed produced 0 files at {Path}", seedRoot);
            return;
        }

        _logger.LogInformation(
            "Seeding VB6 Inventory Sample corpus from {Path} ({Count} files)",
            seedRoot, files.Count);

        try
        {
            var result = await _pipeline.IngestAsync(new IngestPipeline.IngestRequest(
                Name: CorpusName,
                SourceType: "synthetic",
                SourceUrl: "nous-internal://vb6-inventory-sample",
                Branch: null,
                SourceRoot: "",
                Files: files), ct);

            _logger.LogInformation(
                "VB6 Inventory Sample seed complete: state={State} files={Files} loc={Loc} subs={Subs}",
                result.State, result.FileCount, result.TotalLoc, result.SubroutineCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VB6 Inventory Sample seed failed; demo continues without it");
        }
    }

    private static List<IngestPipeline.IncomingFile> CollectVb6Files(string root)
    {
        var result = new List<IngestPipeline.IncomingFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SourceLanguageDetector.Vb6Extensions.Contains(ext)) continue;
            var fi = new FileInfo(path);
            if (fi.Length > 4 * 1024 * 1024) continue;

            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            result.Add(new IngestPipeline.IncomingFile(rel, File.ReadAllText(path)));
        }
        return result;
    }
}
