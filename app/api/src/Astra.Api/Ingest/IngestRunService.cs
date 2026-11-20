using System.Collections.Concurrent;
using Astra.Api.Artefacts;
using Astra.Api.Auth;
using Astra.Api.Conversations;
using Astra.Api.Copilot;
using Astra.Api.Persistence;
using Astra.Api.Runs;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Ingest;

/// <summary>
/// Runs an ingest or re-sync detached from the HTTP request that asked for
/// it. Azure App Service cuts a request at 230 s and a whole-corpus parse
/// can take longer; the pipeline already survives the client going away,
/// but the client used to learn nothing. Now every ingest is a run on the
/// <see cref="RunEventBus"/> (stages, file progress, a final item with the
/// result) that the Discovery agent narrates into the programme's thread,
/// and the endpoint can hand back a run id to poll when the work outlives
/// its deadline. Each run gets its own DI scope with the requesting
/// persona copied in, like <see cref="BackgroundRunService"/>.
/// </summary>
public sealed class IngestRunService
{
    public const string Agent = "discovery";

    public sealed record RunStatus(
        Guid RunId,
        Guid? CorpusId,
        string Kind,                 // "ingest" | "reingest"
        string State,                // RUNNING | SUCCEEDED | FAILED
        object? Result,              // IngestPipeline.IngestResult | ReingestResult
        string? ErrorCode,           // "ingest.duplicate_name" | "ingest.invalid_request" | null
        string? Error,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt);

    private sealed class Entry
    {
        public RunStatus Status = null!;
        public readonly TaskCompletionSource<RunStatus> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunEventBus _bus;
    private readonly Narrator _narrator;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IngestRunService> _logger;
    private readonly ConcurrentDictionary<Guid, Entry> _runs = new();
    private static readonly TimeSpan Retention = TimeSpan.FromHours(2);

    public IngestRunService(
        IServiceScopeFactory scopeFactory, RunEventBus bus, Narrator narrator,
        IHostApplicationLifetime lifetime, ILogger<IngestRunService> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _narrator = narrator;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Guid StartIngest(IngestPipeline.IngestRequest req, Persona persona, string displayName)
    {
        var corpusId = req.CorpusId ?? Guid.NewGuid();
        req = req with { CorpusId = corpusId };
        var runId = Guid.NewGuid();
        Register(runId, corpusId, "ingest");
        _bus.State(runId, Agent, "RUNNING", $"Ingesting **{req.Name}** — {req.Files.Count:N0} files from {req.SourceType}…");
        var request = req;
        _ = Task.Run(() => RunAsync(runId, request.Name, corpusId, persona, displayName, isNew: true,
            work: async pipeline => await pipeline.IngestAsync(request, _lifetime.ApplicationStopping), request.Documents));
        return runId;
    }

    public async Task<Guid> StartReingestAsync(IngestPipeline.ReingestRequest req, Persona persona, string displayName, CancellationToken ct)
    {
        string corpusName;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            corpusName = await db.Corpora.AsNoTracking().Where(c => c.Id == req.CorpusId).Select(c => c.Name).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException($"Corpus {req.CorpusId} not found.");
        }
        var runId = Guid.NewGuid();
        Register(runId, req.CorpusId, "reingest");
        _bus.State(runId, Agent, "RUNNING", $"Re-syncing **{corpusName}** — {req.Files.Count:N0} files…");
        _ = Task.Run(() => RunAsync(runId, corpusName, req.CorpusId, persona, displayName, isNew: false,
            work: async pipeline => await pipeline.ReingestAsync(req, _lifetime.ApplicationStopping), req.Documents));
        return runId;
    }

    public RunStatus? Get(Guid runId) => _runs.TryGetValue(runId, out var e) ? e.Status : null;

    /// <summary>Wait until the run ends or the deadline passes; either way
    /// the current status comes back.</summary>
    public async Task<RunStatus> WaitAsync(Guid runId, TimeSpan deadline, CancellationToken ct)
    {
        if (!_runs.TryGetValue(runId, out var entry)) throw new KeyNotFoundException($"Unknown ingest run {runId}");
        var finished = await Task.WhenAny(entry.Done.Task, Task.Delay(deadline, ct));
        return finished == entry.Done.Task ? await entry.Done.Task : entry.Status;
    }

    private void Register(Guid runId, Guid corpusId, string kind)
    {
        Evict();
        _runs[runId] = new Entry
        {
            Status = new RunStatus(runId, corpusId, kind, "RUNNING", null, null, null, DateTimeOffset.UtcNow, null),
        };
    }

    private async Task RunAsync(
        Guid runId, string label, Guid corpusId, Persona persona, string displayName, bool isNew,
        Func<IngestPipeline, Task<object>> work, IReadOnlyList<IncomingArtefact>? documents = null)
    {
        var entry = _runs[runId];
        try
        {
            await TrackAsync(runId, label, corpusId, persona, displayName, isNew);

            using var scope = _scopeFactory.CreateScope();
            var actor = scope.ServiceProvider.GetRequiredService<DevPersonaContext>();
            actor.Persona = persona;
            actor.DisplayName = displayName;
            var pipeline = scope.ServiceProvider.GetRequiredService<IngestPipeline>();
            pipeline.Progress = p =>
            {
                // A tick with nothing done yet opens a stage (the Narrator
                // may post its label); later ticks are progress.
                if (p.Done == 0)
                    _bus.Publish(runId, Agent, p.Stage, "stage", new { stage = p.Stage, label = p.Message, total = p.Total }, p.Message);
                else
                    _bus.Progress(runId, Agent, p.Stage, p.Done, p.Total);
            };

            var result = await work(pipeline);
            if (documents is { Count: > 0 } && IsParsed(result))
                result = WithNotes(result, await AttachDocumentsAsync(scope.ServiceProvider, corpusId, documents, actor));
            var (state, summary, item) = Describe(result, label, isNew);
            _bus.Publish(runId, Agent, "", "item", item);
            Finish(entry, state, result, null, null);
            _bus.State(runId, Agent, state, summary);
        }
        catch (OperationCanceledException)
        {
            Finish(entry, "FAILED", null, null, "Interrupted by an API restart — run it again.");
            _bus.State(runId, Agent, "FAILED", "Interrupted by an API restart — run it again.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already exists"))
        {
            Finish(entry, "FAILED", null, "ingest.duplicate_name", ex.Message);
            _bus.State(runId, Agent, "FAILED", ex.Message);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            Finish(entry, "FAILED", null, "corpus.not_found", ex.Message);
            _bus.State(runId, Agent, "FAILED", ex.Message);
        }
        catch (ArgumentException ex)
        {
            Finish(entry, "FAILED", null, "ingest.invalid_request", ex.Message);
            _bus.State(runId, Agent, "FAILED", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingest run {RunId} for {Label} failed", runId, label);
            Finish(entry, "FAILED", null, null, $"Ingest failed unexpectedly. Check the server log for corpus {corpusId}.");
            _bus.State(runId, Agent, "FAILED", $"Ingest failed unexpectedly. Check the server log for corpus {corpusId}.");
        }
        finally
        {
            _bus.Complete(runId);
        }
    }

    private static bool IsParsed(object result) => result switch
    {
        IngestPipeline.IngestResult r => r.State == "PARSED",
        IngestPipeline.ReingestResult r => r.State == "PARSED",
        _ => false,
    };

    private static object WithNotes(object result, IReadOnlyList<string> notes) => notes.Count == 0 ? result : result switch
    {
        IngestPipeline.IngestResult r => r with { Warnings = r.Warnings.Concat(notes).ToList() },
        IngestPipeline.ReingestResult r => r with { Warnings = r.Warnings.Concat(notes).ToList() },
        _ => result,
    };

    /// <summary>
    /// The workbooks the code came out of are kept as evidence on the new version, so their sheets,
    /// inputs and buttons can be cited and joined to the macros in the entity graph. The spreadsheet
    /// kind is switched on for the project first (attaching follows the project's kinds). Best effort:
    /// the code is already in, so a failure here is a note on the result, never a failed ingest.
    /// </summary>
    private async Task<IReadOnlyList<string>> AttachDocumentsAsync(
        IServiceProvider services, Guid corpusId, IReadOnlyList<IncomingArtefact> documents, DevPersonaContext actor)
    {
        try
        {
            var db = services.GetRequiredService<AppDbContext>();
            var corpus = await db.Corpora.FirstAsync(c => c.Id == corpusId, _lifetime.ApplicationStopping);
            var settings = ProjectSettings.Parse(corpus.SettingsJson);
            var kinds = documents.Select(d => d.Kind).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var missing = kinds.Where(k => !settings.Has(k)).ToList();
            if (missing.Count > 0)
            {
                var all = settings.ArtefactKinds.Concat(missing).OrderBy(ArtefactKinds.OrderOf).ToList();
                corpus.SettingsJson = (settings with { ArtefactKinds = all }).ToJson();
                await db.SaveChangesAsync(_lifetime.ApplicationStopping);
            }

            var attach = services.GetRequiredService<ArtefactAttachService>();
            var result = await attach.AttachAsync(corpusId, documents, actor, null, _lifetime.ApplicationStopping);
            if (result.Outcome is not { } outcome)
                return new[] { $"The workbooks were not kept as evidence ({result.ErrorCode})." };
            var notes = new List<string>();
            var kept = outcome.Files.Where(f => f.Status is "added" or "replaced" or "unchanged").ToList();
            if (kept.Count > 0)
                notes.Add($"Kept {(kept.Count == 1 ? $"{kept[0].Path}" : $"{kept.Count} workbooks")} as evidence: " +
                          $"{kept.Sum(f => f.Chunks):N0} citable pieces (sheets, named ranges, inputs, buttons), joined to the macros in the entity graph.");
            foreach (var f in outcome.Files.Where(f => f.Status == "rejected"))
                notes.Add($"{f.Path} was not kept as evidence: {f.Message}");
            foreach (var f in kept)
                notes.AddRange(f.Notes.Select(n => $"{f.Path}: {n}"));
            return notes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Attaching workbooks to {CorpusId} failed", corpusId);
            return new[] { "The workbooks were not kept as evidence; attach them from the project's Sources panel." };
        }
    }

    private static (string State, string Summary, object Item) Describe(object result, string label, bool isNew)
    {
        switch (result)
        {
            case IngestPipeline.IngestResult r:
            {
                var ok = r.State == "PARSED";
                var summary = ok
                    ? $"Ingest done for **{label}**: {r.FileCount:N0} files, {r.TotalLoc:N0} lines, {r.SubroutineCount:N0} routines."
                    : $"Ingest of **{label}** ended in {r.State}: {r.ErrorMessage ?? "see the corpus warnings"}.";
                return (ok ? "SUCCEEDED" : "FAILED", summary, new
                {
                    kind = "ingest", corpusId = r.CorpusId, reingest = false, state = r.State,
                    fileCount = r.FileCount, totalLoc = r.TotalLoc, subroutineCount = r.SubroutineCount,
                    carriedForwardCount = 0, supersededCount = 0, warningCount = r.Warnings.Count, errorMessage = r.ErrorMessage,
                });
            }
            case IngestPipeline.ReingestResult r:
            {
                var ok = r.State == "PARSED";
                var summary = ok
                    ? $"Re-sync done for **{label}**: {r.FileCount:N0} files, {r.SubroutineCount:N0} routines, {r.CarriedForwardCount:N0} specs carried forward, {r.SupersededCount:N0} superseded."
                    : $"Re-sync of **{label}** ended in {r.State}: {r.ErrorMessage ?? "see the corpus warnings"}.";
                return (ok ? "SUCCEEDED" : "FAILED", summary, new
                {
                    kind = "ingest", corpusId = r.CorpusId, reingest = true, state = r.State,
                    fileCount = r.FileCount, totalLoc = r.TotalLoc, subroutineCount = r.SubroutineCount,
                    carriedForwardCount = r.CarriedForwardCount, supersededCount = r.SupersededCount,
                    warningCount = r.Warnings.Count, errorMessage = r.ErrorMessage,
                });
            }
            default:
                return ("FAILED", $"{(isNew ? "Ingest" : "Re-sync")} of {label} produced no result.", new { kind = "ingest" });
        }
    }

    /// <summary>A re-sync narrates into the programme's thread. A brand-new
    /// corpus has no thread yet, so its run is followed in the global thread
    /// (Mission Control); the Narrator posts the summary to the programme
    /// thread as well once the corpus exists.</summary>
    private async Task TrackAsync(Guid runId, string label, Guid corpusId, Persona persona, string displayName, bool isNew)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var exists = !isNew && await db.Corpora.AsNoTracking().AnyAsync(c => c.Id == corpusId, _lifetime.ApplicationStopping);
            var thread = exists
                ? await conversations.EnsureProgrammeAsync(corpusId, _lifetime.ApplicationStopping)
                : await conversations.EnsureGlobalAsync(_lifetime.ApplicationStopping);
            _narrator.Track(runId, thread.Id, Agent, "ingest", label, corpusId,
                persona.ToString().ToLowerInvariant(), displayName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not attach the narrator to ingest run {RunId}", runId);
        }
    }

    private static void Finish(Entry entry, string state, object? result, string? errorCode, string? error)
    {
        entry.Status = entry.Status with
        {
            State = state, Result = result, ErrorCode = errorCode, Error = error, CompletedAt = DateTimeOffset.UtcNow,
        };
        entry.Done.TrySetResult(entry.Status);
    }

    private void Evict()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var (id, e) in _runs)
            if (e.Status.CompletedAt is { } done && done < cutoff) _runs.TryRemove(id, out _);
    }
}
