using System.Text;
using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// Turns failed gate runs into the instruction a regeneration gets: each gate's digest (compile errors, failing tests,
/// quality findings) with the offending line quoted from the package that failed. One run → "Regenerate with the
/// fix"; several → the auto-fix loop's combined round, so a round fixes everything at once instead of one gate at a
/// time.
/// </summary>
public static class RepairHints
{
    public static async Task<string?> BuildAsync(AppDbContext db, IBlobClient blob, IReadOnlyList<ValidationRun> failed, CancellationToken ct)
    {
        if (failed.Count == 0) return null;
        var filesByScaffold = new Dictionary<Guid, Dictionary<string, string>>();
        var parts = new List<string>();
        foreach (var run in failed.OrderBy(r => StageOrder(r.Stage)))
        {
            string? log = null;
            if (run.LogBlobUri is { Length: > 0 } uri)
            {
                try { log = await blob.GetTextAsync(uri, ct); }
                catch (Exception) { /* the summary still describes the failure */ }
            }
            if (!filesByScaffold.TryGetValue(run.ScaffoldId, out var files))
                filesByScaffold[run.ScaffoldId] = files = await ReadPackageAsync(db, blob, run.ScaffoldId, ct);
            var hint = GateFailureDigest.Parse(run.Stage, log)
                .ToRepairHint(path => files.TryGetValue(path.Replace('\\', '/'), out var c) ? c : null);
            parts.Add(string.IsNullOrWhiteSpace(hint) ? $"The previous attempt failed the {run.Stage} gate: {run.Summary}" : hint);
        }
        if (parts.Count == 1) return parts[0];
        var sb = new StringBuilder();
        sb.AppendLine($"The previous attempt failed {parts.Count} gates. Fix all of it in this one attempt, without changing the behaviour the signed spec describes and without weakening any test.");
        foreach (var p in parts) sb.AppendLine().AppendLine(p);
        return sb.ToString().TrimEnd();
    }

    public static async Task<Dictionary<string, string>> ReadPackageAsync(AppDbContext db, IBlobClient blob, Guid scaffoldId, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scaffold = await db.Scaffolds.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scaffoldId, ct);
            if (scaffold?.PackageBlobUri is not { Length: > 0 } packageUri) return files;
            using var manifest = JsonDocument.Parse(await blob.GetTextAsync(packageUri, ct));
            if (manifest.RootElement.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var f in arr.EnumerateArray())
                    if (f.TryGetProperty("path", out var fp) && fp.ValueKind == JsonValueKind.String
                        && f.TryGetProperty("content", out var fc) && fc.ValueKind == JsonValueKind.String)
                        files[fp.GetString()!.Replace('\\', '/')] = fc.GetString()!;
        }
        catch (Exception) { /* the hint still lists the errors */ }
        return files;
    }

    private static int StageOrder(string stage) => stage switch
    {
        "COMPILE" => 0,
        "TEST_PACK" => 1,
        "QUALITY" => 2,
        _ => 3,
    };
}
