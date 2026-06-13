using System.Collections.Concurrent;

namespace Astra.Api.Validation;

/// <summary>
/// Phase 9.5.a — Per-validation-run binary cache for the 4th gate's
/// live-mode equivalence callback (per ADR-032).
///
/// <para>
/// The <see cref="PropertyTestValidator"/> pre-compiles both the
/// reference binary (via the appropriate <c>*-sidecar</c>) and the
/// candidate binary (via <c>dotnet publish</c>) BEFORE asking the
/// property-test sidecar to start its Hypothesis loop. Both handles
/// are stashed here keyed by validation-run id. The
/// <c>/internal/equivalence-callback</c> endpoint reads <c>runId</c>
/// from the sidecar's POST and uses this cache to drive both
/// binaries per generated input without re-compiling.
/// </para>
///
/// <para>
/// Lifecycle: populated by the validator before <c>/falsify</c>,
/// removed after <c>/falsify</c> returns (success OR error). When a
/// pod restart kills the cache mid-run, callbacks return non-200 and
/// the sidecar treats them as <c>agree:true</c> (per ADR-029) — i.e.
/// the run degrades gracefully into shadow-mode-equivalent behaviour
/// for the remainder of the run.
/// </para>
///
/// <para>
/// Concurrency: a <see cref="ConcurrentDictionary{Guid, Entry}"/>
/// lets callbacks for different validation runs fire in parallel.
/// Per-run callback serialisation (so the ref+cand spawn pair for
/// one input doesn't race the next input) lives on the entry's own
/// <see cref="SemaphoreSlim"/>.
/// </para>
/// </summary>
public sealed class PropertyTestRunCache : IDisposable
{
    /// <summary>
    /// Single per-run cache entry. The validator constructs it before
    /// /falsify; the callback consumes it per generated input.
    /// </summary>
    public sealed record Entry(
        /// <summary>"shadow" | "live". Shadow runs don't populate
        /// RefSidecar / RefArtifactId / CandidateExePath; the
        /// callback returns the canned shadow-mode response.</summary>
        string Mode,
        /// <summary>Sidecar name driving the reference binary —
        /// "gfortran" | "fpc" | "gpp" | "gnucobol". Null in shadow mode.</summary>
        string? RefSidecar,
        /// <summary>Sidecar-side handle for /run. Null in shadow mode.</summary>
        string? RefArtifactId,
        /// <summary>Absolute filesystem path to the candidate executable.
        /// Null until 9.5.b lands.</summary>
        string? CandidateExePath,
        /// <summary>"dotnet" | "java". Null until 9.5.b lands.</summary>
        string? CandidateRunner,
        /// <summary>Tempdir to clean up when the run is evicted. The
        /// validator owns this; the cache just remembers the path so
        /// eviction can release it.</summary>
        string? CandidateTempDir,
        /// <summary>Ordered list of input field names from the spec —
        /// matches spec.inputs[*].name. The callback uses these to
        /// rename JSON keys into the wire format the reference shim
        /// driver expects.</summary>
        IReadOnlyList<string> InputNames)
    {
        /// <summary>Per-run guard so ref+cand spawns for one input
        /// don't race the next input's spawns. Lazily created.</summary>
        public SemaphoreSlim CallbackGuard { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ILogger<PropertyTestRunCache> _log;

    public PropertyTestRunCache(ILogger<PropertyTestRunCache> log)
    {
        _log = log;
    }

    /// <summary>Insert an entry; idempotent on the run id.</summary>
    public void Put(Guid runId, Entry entry)
    {
        _entries[runId] = entry;
        _log.LogDebug(
            "PropertyTestRunCache · Put runId={Run} mode={Mode} refSidecar={RefSidecar}",
            runId, entry.Mode, entry.RefSidecar ?? "<none>");
    }

    /// <summary>Look up an entry. Returns null when no row is present
    /// — the callback should treat that as shadow-mode fallback per
    /// ADR-029.</summary>
    public Entry? TryGet(Guid runId)
        => _entries.TryGetValue(runId, out var e) ? e : null;

    /// <summary>Remove an entry and release its callback guard. The
    /// caller is responsible for any candidate-tempdir cleanup
    /// (validator owns the filesystem path; cache just remembers it
    /// for visibility).</summary>
    public void Remove(Guid runId)
    {
        if (_entries.TryRemove(runId, out var entry))
        {
            entry.CallbackGuard.Dispose();
            _log.LogDebug(
                "PropertyTestRunCache · Remove runId={Run} mode={Mode}",
                runId, entry.Mode);
        }
    }

    /// <summary>Number of live cache entries — surfaced on a debug
    /// endpoint so SREs can spot leaks at a glance.</summary>
    public int Count => _entries.Count;

    /// <summary>Evict every entry; called from the
    /// IHostApplicationLifetime.ApplicationStopping hook so SIGTERM
    /// during a 4th-gate run doesn't leak.</summary>
    public void Dispose()
    {
        foreach (var (runId, entry) in _entries)
        {
            entry.CallbackGuard.Dispose();
            _log.LogDebug("PropertyTestRunCache disposed entry runId={Run}", runId);
        }
        _entries.Clear();
    }
}
