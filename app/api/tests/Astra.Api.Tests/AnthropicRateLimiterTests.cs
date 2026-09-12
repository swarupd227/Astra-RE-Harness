using System.Net;
using System.Net.Http.Headers;
using Astra.Api.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Astra.Api.Tests;

public class AnthropicRateLimiterTests
{
    private static AnthropicRateLimiter Make(int max = 8, int min = 2, int successes = 3) =>
        new(Options.Create(new AnthropicRateLimiterOptions
        {
            MaxInFlight = max, MinInFlight = min, SuccessesBeforeIncrease = successes, DefaultPauseSeconds = 1,
        }), NullLogger<AnthropicRateLimiter>.Instance);

    [Fact]
    public void A429_HalvesTheLimit_AndPausesForRetryAfter()
    {
        var limiter = Make(max: 8);
        var resp = new HttpResponseMessage((HttpStatusCode)429);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));

        limiter.Observe(resp);

        var snap = limiter.Current;
        Assert.Equal(4, snap.Limit);
        Assert.True(snap.PauseUntil > DateTimeOffset.UtcNow.AddSeconds(2));
    }

    [Fact]
    public void RepeatedSuccesses_GrowTheLimit_UpToMax()
    {
        var limiter = Make(max: 8, successes: 3);
        limiter.Observe(new HttpResponseMessage((HttpStatusCode)429)); // 8 → 4
        for (var i = 0; i < 3; i++) limiter.Observe(new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(5, limiter.Current.Limit);
        for (var i = 0; i < 30; i++) limiter.Observe(new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Equal(8, limiter.Current.Limit);
    }

    [Fact]
    public void LowRemainingWindow_HalvesProactively()
    {
        var limiter = Make(max: 8);
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Headers.Add("anthropic-ratelimit-tokens-limit", "100000");
        resp.Headers.Add("anthropic-ratelimit-tokens-remaining", "5000");
        limiter.Observe(resp);
        Assert.Equal(4, limiter.Current.Limit);
    }

    [Fact]
    public async Task Acquire_WaitsWhilePaused_ThenProceeds()
    {
        var limiter = Make(max: 8);
        var resp = new HttpResponseMessage((HttpStatusCode)429);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
        limiter.Observe(resp);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var lease = await limiter.AcquireAsync(null, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(700), $"waited only {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1, limiter.Current.InFlight);
    }

    [Fact]
    public async Task InFlight_NeverExceedsLimit()
    {
        var limiter = Make(max: 2, min: 2);
        using var l1 = await limiter.AcquireAsync(null, CancellationToken.None);
        using var l2 = await limiter.AcquireAsync(null, CancellationToken.None);
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(null, cts.Token));
        l1.Dispose();
        using var l3 = await limiter.AcquireAsync(null, CancellationToken.None);
        Assert.Equal(2, limiter.Current.InFlight);
    }

    [Fact]
    public async Task WarmupLeader_HoldsFollowersUntilItCompletes()
    {
        var limiter = Make(max: 8);
        var leader = await limiter.AcquireAsync("survey:delphi", CancellationToken.None);

        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync("survey:delphi", cts.Token));

        leader.Dispose();
        using var follower = await limiter.AcquireAsync("survey:delphi", CancellationToken.None);
        Assert.Equal(1, limiter.Current.InFlight);
    }
}
