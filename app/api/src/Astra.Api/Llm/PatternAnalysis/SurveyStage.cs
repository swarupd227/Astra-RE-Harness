using System.Text;
using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Runs;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>
/// Stage 1 of pattern analysis: produce a <see cref="RoutineDigest"/> for
/// every routine in a source version as cheaply as possible.
///
///   1. Routines that already have a Spec are skipped — clustering reads
///      the spec directly.
///   2. Routines already digested are skipped (that is what makes a run
///      resumable), unless <c>force</c> wipes the version's digests.
///   3. Every remaining routine is structurally normalised; pure accessors
///      get a deterministic digest with no model call.
///   4. The rest are grouped by structural hash; one exemplar per group is
///      surveyed (Haiku, forced tool-use, cached system block, 32-wide
///      under the process rate limiter) and the digest is propagated to
///      the group's other members.
///
/// Every digest is persisted the moment it exists, so a crash or restart
/// loses nothing already paid for.
/// </summary>
public sealed class SurveyStage
{
    private const string Agent = "survey";
    private const int ProgressEvery = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISurveyProvider _provider;
    private readonly SurveyOptions _opts;
    private readonly RunEventBus _bus;
    private readonly ILogger<SurveyStage> _logger;

    public SurveyStage(
        IServiceScopeFactory scopeFactory,
        ISurveyProvider provider,
        IOptions<SurveyOptions> opts,
        RunEventBus bus,
        ILogger<SurveyStage> logger)
    {
        _scopeFactory = scopeFactory;
        _provider = provider;
        _opts = opts.Value;
        _bus = bus;
        _logger = logger;
    }

    public sealed record Result(
        int Total,
        int FromSpec,
        int AlreadyDigested,
        int Trivial,
        int Surveyed,
        int Propagated,
        int Failed,
        int StructuralBuckets,
        int Calls,
        int InputTokens,
        int OutputTokens,
        int CacheReadTokens,
        decimal CostUsd,
        long ElapsedMs);

    public async Task<Result> RunAsync(
        Guid runId, Guid sourceVersionId, bool force,
        Func<string, Task>? reportProgress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── 1-2. Load the version, skip what is already covered ──────────
        List<Subroutine> subs;
        HashSet<Guid> specSubIds;
        HashSet<Guid> digested;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            subs = await db.Subroutines
                .Include(s => s.SourceFile)
                .AsNoTracking()
                .Where(s => s.SourceFile != null && s.SourceFile.SourceVersionId == sourceVersionId)
                .OrderBy(s => s.SourceFile!.RelativePath).ThenBy(s => s.LineStart)
                .ToListAsync(ct);
            var subIds = subs.Select(s => s.Id).ToList();
            specSubIds = (await db.Specs.AsNoTracking()
                    .Where(sp => subIds.Contains(sp.SubroutineId))
                    .Select(sp => sp.SubroutineId)
                    .ToListAsync(ct))
                .ToHashSet();

            if (force)
            {
                await db.RoutineDigests
                    .Where(d => d.SourceVersionId == sourceVersionId)
                    .ExecuteDeleteAsync(ct);
                digested = new HashSet<Guid>();
            }
            else
            {
                digested = (await db.RoutineDigests.AsNoTracking()
                        .Where(d => d.SourceVersionId == sourceVersionId)
                        .Select(d => d.SubroutineId)
                        .ToListAsync(ct))
                    .ToHashSet();
            }
        }

        var todo = subs.Where(s => !specSubIds.Contains(s.Id) && !digested.Contains(s.Id)).ToList();
        var fromSpec = subs.Count(s => specSubIds.Contains(s.Id));
        var already = subs.Count - fromSpec - todo.Count;

        _bus.Log(runId, Agent, "survey",
            $"Survey: {subs.Count} routine(s) in version — {fromSpec} have specs, {already} already digested, {todo.Count} to survey.");
        if (todo.Count == 0)
        {
            sw.Stop();
            return new Result(subs.Count, fromSpec, already, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0m, sw.ElapsedMilliseconds);
        }

        // ── 3. Normalise; trivial accessors need no model ────────────────
        var slices = await SliceAllAsync(todo, ct);
        var trivial = 0;
        var buckets = new Dictionary<string, List<Subroutine>>(StringComparer.Ordinal);
        var normalized = new Dictionary<Guid, StructuralNormalizer.Result>();
        foreach (var sub in todo)
        {
            var slice = slices[sub.Id];
            var norm = StructuralNormalizer.Normalize(sub.SourceLanguage, slice.Raw);
            normalized[sub.Id] = norm;
            if (norm.TrivialShape is not null)
            {
                await PersistAsync(sub, sourceVersionId, TrivialDigest(norm.TrivialShape), "trivial", norm, null, null, ct);
                trivial++;
                continue;
            }
            if (!buckets.TryGetValue(norm.Hash, out var list)) buckets[norm.Hash] = list = new List<Subroutine>();
            list.Add(sub);
        }
        var exemplars = buckets.Values.Select(l => l[0]).ToList();
        var propagatable = buckets.Values.Sum(l => l.Count - 1);
        _bus.Log(runId, Agent, "survey",
            $"Structural pass: {trivial} trivial accessor(s) digested without a model, " +
            $"{buckets.Count} structural bucket(s) → {exemplars.Count} exemplar call(s), {propagatable} will be propagated.");

        // ── 4. Survey exemplars, propagate to their buckets ──────────────
        var sem = new SemaphoreSlim(Math.Max(1, _opts.Concurrency));
        var done = 0; var failed = 0; var surveyed = 0; var propagated = 0; var calls = 0;
        var inTok = 0; var outTok = 0; var cacheRead = 0;
        var cost = 0m;
        var stageSw = System.Diagnostics.Stopwatch.StartNew();
        var costLock = new object();

        await Task.WhenAll(exemplars.Select(async sub =>
        {
            await sem.WaitAsync(ct);
            var ok = false;
            try
            {
                var slice = slices[sub.Id];
                var norm = normalized[sub.Id];
                var req = new SurveyRequest(
                    sub.Id, sub.Name, sub.Signature, sub.SourceLanguage,
                    sub.SourceFile?.RelativePath ?? "", ReadNameList(sub.CalledSubroutines).Take(12).ToList(),
                    CallerCount: 0, sub.LineStart, sub.LineEnd, slice.Numbered);

                var result = await _provider.SurveyAsync(req, ct);
                var callCost = ModelPricing.Estimate(_provider.Name, result.Model,
                    result.InputTokens, result.OutputTokens, result.CacheReadTokens, result.CacheCreationTokens);
                var llmCallId = await PersistAsync(sub, sourceVersionId, result.Digest, "survey", norm, result, callCost, ct);

                var members = buckets[norm.Hash].Skip(1).ToList();
                foreach (var m in members)
                {
                    await PersistAsync(m, sourceVersionId, result.Digest, "propagated", normalized[m.Id], result, null, ct, exemplarId: sub.Id);
                }

                lock (costLock)
                {
                    calls++; surveyed++; propagated += members.Count;
                    inTok += result.InputTokens; outTok += result.OutputTokens; cacheRead += result.CacheReadTokens;
                    cost += callCost;
                }
                ok = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var bucketSize = buckets[normalized[sub.Id].Hash].Count;
                lock (costLock) { failed += bucketSize; }
                _logger.LogWarning(ex, "Survey failed for {Sub} (bucket of {Size})", sub.Name, bucketSize);
                _bus.Log(runId, Agent, "survey", $"  ✗ '{sub.Name}': {FirstLine(ex.Message)}");
            }
            finally
            {
                sem.Release();
            }

            var n = Interlocked.Increment(ref done);
            _bus.Log(runId, Agent, "survey", $"[{n}/{exemplars.Count}] {(ok ? "ok  " : "FAIL")} {sub.Name}");
            if (n % ProgressEvery == 0 || n == exemplars.Count)
            {
                var perItem = stageSw.Elapsed.TotalSeconds / Math.Max(1, n);
                var eta = perItem * (exemplars.Count - n);
                int f, p;
                lock (costLock) { f = failed; p = propagated; }
                _bus.Progress(runId, Agent, "survey", n, exemplars.Count, f, p, eta);
                if (reportProgress is not null)
                {
                    await reportProgress(
                        $"Stage: survey — {n}/{exemplars.Count} exemplar(s) digested" +
                        $"{(p > 0 ? $", {p} propagated" : "")}{(f > 0 ? $", {f} failed" : "")}" +
                        $" · ~{Math.Ceiling(eta / 60)} min left");
                }
            }
        }));

        sw.Stop();
        return new Result(
            subs.Count, fromSpec, already, trivial, surveyed, propagated, failed,
            buckets.Count, calls, inTok, outTok, cacheRead, Math.Round(cost, 4), sw.ElapsedMilliseconds);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed record Slice(string Raw, string Numbered);

    private async Task<Dictionary<Guid, Slice>> SliceAllAsync(List<Subroutine> subs, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Slice>();
        using var scope = _scopeFactory.CreateScope();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobClient>();
        foreach (var group in subs.GroupBy(s => s.SourceFileId))
        {
            var file = group.First().SourceFile!;
            string[] lines;
            try
            {
                var text = await blob.GetTextAsync(file.BlobUri, ct);
                lines = text.Replace("\r\n", "\n").Split('\n');
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read source for {Path}", file.RelativePath);
                lines = Array.Empty<string>();
            }
            foreach (var sub in group)
                result[sub.Id] = SliceOne(lines, sub.LineStart, sub.LineEnd);
        }
        return result;
    }

    private Slice SliceOne(string[] lines, int lineStart, int lineEnd)
    {
        var lo = Math.Max(0, lineStart - 1);
        var hi = Math.Min(lines.Length, Math.Max(lineEnd, lineStart));
        if (hi <= lo) return new Slice("", "");

        var raw = new StringBuilder();
        var numbered = new StringBuilder();
        var count = hi - lo;
        if (count <= _opts.LineCap)
        {
            for (var i = lo; i < hi; i++) Append(i);
        }
        else
        {
            for (var i = lo; i < lo + _opts.HeadLines; i++) Append(i);
            var elided = count - _opts.HeadLines - _opts.TailLines;
            raw.Append("\n/* … ").Append(elided).Append(" lines elided … */\n");
            numbered.Append("… (").Append(elided).Append(" lines elided) …\n");
            for (var i = hi - _opts.TailLines; i < hi; i++) Append(i);
        }
        return new Slice(raw.ToString(), numbered.ToString());

        void Append(int i)
        {
            raw.Append(lines[i]).Append('\n');
            numbered.Append(i + 1).Append(": ").Append(lines[i]).Append('\n');
        }
    }

    private static SurveyDigest TrivialDigest(string shape) => new(
        shape == "trivial-setter" ? "Sets a field from its argument." : "Returns a field value.",
        new[] { "propertyAccessor" },
        "trivial-accessor",
        Array.Empty<SurveyDataAccess>(),
        Array.Empty<string>(),
        "trivial");

    private async Task<Guid?> PersistAsync(
        Subroutine sub, Guid sourceVersionId, SurveyDigest digest, string source,
        StructuralNormalizer.Result norm, SurveyResult? llm, decimal? cost, CancellationToken ct,
        Guid? exemplarId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        Guid? llmCallId = null;
        if (llm is not null && source == "survey")
        {
            var call = new LlmCall
            {
                Id = Guid.NewGuid(),
                Provider = _provider.Name,
                Model = llm.Model,
                PromptTemplateId = llm.PromptId,
                PromptTemplateVersion = llm.PromptVersion,
                ProviderConfigVersion = _provider.Name == "mock" ? "mock:offline" : "anthropic:survey",
                InputTokens = llm.InputTokens,
                OutputTokens = llm.OutputTokens,
                CacheReadTokens = llm.CacheReadTokens,
                CacheCreationTokens = llm.CacheCreationTokens,
                LatencyMs = llm.LatencyMs,
                CostUsd = cost ?? 0m,
                Status = "success",
                CalledAt = now,
            };
            db.LlmCalls.Add(call);
            llmCallId = call.Id;
        }

        var existing = await db.RoutineDigests.FirstOrDefaultAsync(d => d.SubroutineId == sub.Id, ct);
        var row = existing ?? new RoutineDigest { Id = Guid.NewGuid(), SubroutineId = sub.Id, CreatedAt = now };
        row.SourceVersionId = sourceVersionId;
        row.Source = source;
        row.Purpose = digest.Purpose;
        row.ArchetypeHint = digest.ArchetypeHint;
        row.ClaimKindsJson = JsonSerializer.Serialize(digest.ClaimKinds);
        row.DataAccessJson = digest.DataAccess.Count == 0 ? null : JsonSerializer.Serialize(digest.DataAccess.Select(d => new { table = d.Table, op = d.Op }));
        row.ModernizationFlagsJson = digest.ModernizationFlags.Count == 0 ? null : JsonSerializer.Serialize(digest.ModernizationFlags);
        row.Complexity = digest.Complexity;
        row.StructuralHash = norm.Hash;
        row.NormalizedTokenCount = norm.TokenCount;
        row.ExemplarSubroutineId = exemplarId;
        row.PromptVersion = llm?.PromptVersion;
        row.Model = source == "survey" ? llm?.Model : null;
        row.LlmCallId = llmCallId;
        row.UpdatedAt = now;
        if (existing is null) db.RoutineDigests.Add(row);
        await db.SaveChangesAsync(ct);
        return llmCallId;
    }

    private static IReadOnlyList<string> ReadNameList(JsonDocument? doc)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }

    private static string FirstLine(string s)
    {
        var nl = s.IndexOf('\n');
        var line = nl < 0 ? s : s[..nl];
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
