using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm;

public sealed class AnthropicRateLimiterOptions
{
    /// <summary>Hard ceiling on concurrent in-flight Anthropic requests across
    /// every pipeline in the process.</summary>
    public int MaxInFlight { get; set; } = 32;

    /// <summary>Floor the AIMD halving never goes below.</summary>
    public int MinInFlight { get; set; } = 2;

    /// <summary>Consecutive 2xx responses before the limit grows by one.</summary>
    public int SuccessesBeforeIncrease { get; set; } = 20;

    /// <summary>Fraction of a rate-limit window remaining below which the
    /// limiter proactively backs off instead of waiting for a 429.</summary>
    public double LowRemainingFraction { get; set; } = 0.10;

    /// <summary>Pause applied on a 429 that carries no Retry-After.</summary>
    public int DefaultPauseSeconds { get; set; } = 10;

    /// <summary>In-flight slots kept free for the Interactive lane: batch work
    /// (a wave's queued per-routine calls) never takes the last N, so a person
    /// typing into the copilot is not stuck behind a hundred queued extractions.</summary>
    public int InteractiveReserve { get; set; } = 4;

    /// <summary>Soft budget on model tokens (input + output) per rolling minute
    /// for the Batch lane; 0 = unlimited. Batch callers wait while the last
    /// 60 s are over it. Interactive calls are never held back by it.</summary>
    public int BatchTokensPerMinute { get; set; } = 0;
}

/// <summary>
/// Process-wide adaptive concurrency gate for the Anthropic Messages API.
/// Additive-increase / multiplicative-decrease driven by the response
/// headers Anthropic already sends (<c>anthropic-ratelimit-*</c>,
/// <c>Retry-After</c>): a 429 or a nearly-exhausted window halves the
/// in-flight limit and pauses until the window resets; a run of successes
/// grows it back one at a time up to <see cref="AnthropicRateLimiterOptions.MaxInFlight"/>.
///
/// Also owns the prompt-cache warm-up gate: the first request for a given
/// cache key runs alone, so the N-1 followers read the cache entry it just
/// wrote instead of all paying to write it.
///
/// Without this, raising bulk concurrency from 8 converted a rate-limit wall
/// into three wasted full generations per routine.
/// </summary>
public sealed class AnthropicRateLimiter
{
    private readonly AnthropicRateLimiterOptions _opts;
    private readonly ILogger<AnthropicRateLimiter> _logger;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, WarmupState> _warmups = new(StringComparer.Ordinal);

    private int _limit;
    private int _inflight;
    private int _inflightBatch;
    private int _interactiveWaiting;
    private readonly Queue<(DateTimeOffset At, int Tokens)> _tokenWindow = new();
    private long _tokensInWindow;
    private int _consecutiveSuccesses;
    private DateTimeOffset _pauseUntil = DateTimeOffset.MinValue;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public AnthropicRateLimiter(IOptions<AnthropicRateLimiterOptions> opts, ILogger<AnthropicRateLimiter> logger)
    {
        _opts = opts.Value;
        _logger = logger;
        _limit = Math.Max(_opts.MinInFlight, _opts.MaxInFlight);
        LlmUsage.Recorded += OnUsage;
    }

    public sealed record Snapshot(int Limit, int InFlight, DateTimeOffset PauseUntil, int InFlightBatch = 0, int InteractiveWaiting = 0, long TokensLastMinute = 0);

    public Snapshot Current
    {
        get
        {
            lock (_lock)
            {
                TrimWindow(DateTimeOffset.UtcNow);
                return new Snapshot(_limit, _inflight, _pauseUntil, _inflightBatch, _interactiveWaiting, _tokensInWindow);
            }
        }
    }

    // ── token-per-minute window (fed by every saved LlmCall) ─────────────

    private void OnUsage(Persistence.Entities.LlmCall call) =>
        RecordTokens(call.InputTokens + call.CacheCreationTokens + call.OutputTokens);

    /// <summary>Count tokens against the rolling minute (public for tests).</summary>
    public void RecordTokens(int tokens)
    {
        if (tokens <= 0) return;
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            _tokenWindow.Enqueue((now, tokens));
            _tokensInWindow += tokens;
            TrimWindow(now);
        }
    }

    private void TrimWindow(DateTimeOffset now)
    {
        while (_tokenWindow.Count > 0 && now - _tokenWindow.Peek().At > TimeSpan.FromMinutes(1))
            _tokensInWindow -= _tokenWindow.Dequeue().Tokens;
    }

    /// <summary>May a call in this lane take a slot right now? Caller holds <c>_lock</c>.</summary>
    private bool LaneAllows(LlmLane lane, DateTimeOffset now)
    {
        if (lane == LlmLane.Interactive) return true;
        // Batch yields to anyone interactive that is waiting, never takes the
        // slots reserved for them, and respects the token-per-minute budget.
        if (_interactiveWaiting > 0) return false;
        var cap = _limit <= _opts.InteractiveReserve ? 1 : _limit - _opts.InteractiveReserve;
        if (_inflightBatch >= cap) return false;
        if (_opts.BatchTokensPerMinute > 0)
        {
            TrimWindow(now);
            if (_tokensInWindow >= _opts.BatchTokensPerMinute) return false;
        }
        return true;
    }

    /// <summary>
    /// Wait for a slot. <paramref name="cacheKey"/> identifies a cached
    /// system prompt (e.g. <c>survey:delphi</c>); when supplied, the first
    /// caller for that key holds every other caller for the same key until
    /// its response arrives, so the followers hit the cache it wrote.
    /// </summary>
    public async Task<Lease> AcquireAsync(string? cacheKey, CancellationToken ct)
    {
        var scope = LlmWorkScope.Current;
        var lane = scope?.Lane ?? LlmLane.Interactive;
        if (lane == LlmLane.Interactive)
        {
            lock (_lock) _interactiveWaiting++;
            try { return await AcquireCoreAsync(cacheKey, lane, scope, ct); }
            finally { lock (_lock) _interactiveWaiting--; }
        }
        return await AcquireCoreAsync(cacheKey, lane, scope, ct);
    }

    private async Task<Lease> AcquireCoreAsync(string? cacheKey, LlmLane lane, LlmWorkScope? scope, CancellationToken ct)
    {
        WarmupState? warmup = cacheKey is null ? null : _warmups.GetOrAdd(cacheKey, _ => new WarmupState());
        var isWarmupLeader = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (scope?.Budget is { Exhausted: true, CeilingUsd: { } ceiling } budget)
                throw new LlmBudgetExceededException(budget.RunId, ceiling, budget.SpentUsd);
            TimeSpan wait;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                var paused = now < _pauseUntil;
                var warmupBlocks = false;
                if (warmup is not null)
                {
                    lock (warmup)
                    {
                        var stale = now - warmup.LastCompleted > CacheTtl;
                        if (!warmup.Warmed || stale)
                        {
                            if (!warmup.LeaderInFlight)
                            {
                                warmup.LeaderInFlight = true;
                                warmup.Warmed = false;
                                isWarmupLeader = true;
                            }
                            else
                            {
                                warmupBlocks = true;
                            }
                        }
                    }
                }

                if (!paused && !warmupBlocks && _inflight < _limit && LaneAllows(lane, now))
                {
                    _inflight++;
                    if (lane == LlmLane.Batch) _inflightBatch++;
                    return new Lease(this, warmup, isWarmupLeader, lane);
                }

                if (isWarmupLeader)
                {
                    // We claimed leadership but the global gate is closed;
                    // hand leadership back so we don't deadlock followers.
                    lock (warmup!) warmup.LeaderInFlight = false;
                    isWarmupLeader = false;
                }

                wait = paused ? _pauseUntil - now : TimeSpan.FromMilliseconds(150);
                if (wait > TimeSpan.FromMilliseconds(500)) wait = TimeSpan.FromMilliseconds(500);
                if (wait < TimeSpan.FromMilliseconds(25)) wait = TimeSpan.FromMilliseconds(25);
            }
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// When Anthropic last refused the API key (401/403), or null once a call succeeds again. Every provider feeds
    /// its responses through <see cref="Observe"/>, so this is the one place that knows the key stopped working —
    /// the settings status reports it and the UI shows a banner instead of letting runs fail one by one.
    /// </summary>
    public DateTimeOffset? LastAuthFailureAt { get; private set; }

    /// <summary>A new key was saved: forget the old key's refusal until a call says otherwise.</summary>
    public void ClearAuthFailure() => LastAuthFailureAt = null;

    /// <summary>Feed a response's status + rate-limit headers back into the gate.</summary>
    public void Observe(HttpResponseMessage resp)
    {
        var status = (int)resp.StatusCode;
        if (status is 401 or 403) LastAuthFailureAt = DateTimeOffset.UtcNow;
        else if (status is >= 200 and < 300) LastAuthFailureAt = null;
        var retryAfter = AnthropicHttp.RetryAfterSeconds(resp);
        var lowWindow = TryDetectLowRemaining(resp, out var resetAt);

        lock (_lock)
        {
            if (status == 429)
            {
                Halve("429");
                var pause = retryAfter > 0 ? retryAfter : _opts.DefaultPauseSeconds;
                _pauseUntil = DateTimeOffset.UtcNow.AddSeconds(pause);
                _consecutiveSuccesses = 0;
                _logger.LogWarning("Anthropic 429 — in-flight limit now {Limit}, paused {Pause}s", _limit, pause);
                return;
            }
            if (status >= 500)
            {
                _consecutiveSuccesses = 0;
                return;
            }
            if (lowWindow)
            {
                Halve("rate-limit window nearly exhausted");
                if (resetAt is { } reset && reset > DateTimeOffset.UtcNow)
                    _pauseUntil = reset;
                _consecutiveSuccesses = 0;
                return;
            }
            if (status is >= 200 and < 300)
            {
                _consecutiveSuccesses++;
                if (_consecutiveSuccesses >= _opts.SuccessesBeforeIncrease && _limit < _opts.MaxInFlight)
                {
                    _limit++;
                    _consecutiveSuccesses = 0;
                    _logger.LogDebug("Anthropic limiter: in-flight limit raised to {Limit}", _limit);
                }
            }
        }
    }

    private void Halve(string reason)
    {
        var next = Math.Max(_opts.MinInFlight, _limit / 2);
        if (next != _limit)
            _logger.LogInformation("Anthropic limiter: {Reason} — in-flight limit {From} → {To}", reason, _limit, next);
        _limit = next;
    }

    private bool TryDetectLowRemaining(HttpResponseMessage resp, out DateTimeOffset? resetAt)
    {
        resetAt = null;
        var low = false;
        foreach (var kind in new[] { "requests", "tokens", "input-tokens", "output-tokens" })
        {
            var remaining = HeaderLong(resp, $"anthropic-ratelimit-{kind}-remaining");
            var limit = HeaderLong(resp, $"anthropic-ratelimit-{kind}-limit");
            if (remaining is null || limit is null || limit <= 0) continue;
            if ((double)remaining.Value / limit.Value < _opts.LowRemainingFraction)
            {
                low = true;
                var reset = HeaderString(resp, $"anthropic-ratelimit-{kind}-reset");
                if (reset is not null && DateTimeOffset.TryParse(reset, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var when))
                {
                    resetAt = resetAt is null || when > resetAt ? when : resetAt;
                }
            }
        }
        return low;
    }

    private static long? HeaderLong(HttpResponseMessage resp, string name) =>
        long.TryParse(HeaderString(resp, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? HeaderString(HttpResponseMessage resp, string name) =>
        resp.Headers.TryGetValues(name, out var vals) ? vals.FirstOrDefault() : null;

    private void Release(WarmupState? warmup, bool wasLeader, LlmLane lane)
    {
        lock (_lock)
        {
            _inflight = Math.Max(0, _inflight - 1);
            if (lane == LlmLane.Batch) _inflightBatch = Math.Max(0, _inflightBatch - 1);
        }
        if (warmup is not null && wasLeader)
        {
            lock (warmup)
            {
                warmup.LeaderInFlight = false;
                warmup.Warmed = true;
                warmup.LastCompleted = DateTimeOffset.UtcNow;
            }
        }
        else if (warmup is not null)
        {
            lock (warmup) warmup.LastCompleted = DateTimeOffset.UtcNow;
        }
    }

    internal sealed class WarmupState
    {
        public bool Warmed;
        public bool LeaderInFlight;
        public DateTimeOffset LastCompleted = DateTimeOffset.MinValue;
    }

    public sealed class Lease : IDisposable
    {
        private readonly AnthropicRateLimiter _owner;
        private readonly WarmupState? _warmup;
        private readonly bool _leader;
        private readonly LlmLane _lane;
        private int _released;

        internal Lease(AnthropicRateLimiter owner, WarmupState? warmup, bool leader, LlmLane lane)
        {
            _owner = owner;
            _warmup = warmup;
            _leader = leader;
            _lane = lane;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Release(_warmup, _leader, _lane);
        }
    }
}
