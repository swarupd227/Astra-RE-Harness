using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Astra.Api.Llm;

/// <summary>
/// Shared non-streaming send for the Anthropic Messages API: takes the
/// process-wide <see cref="AnthropicRateLimiter"/> lease, retries 429 /
/// 5xx / transport failures with Retry-After-aware jittered backoff, and
/// reports every response back to the limiter. Used by the bulk pipelines
/// (survey digests, clustering, reconciliation) where nobody is watching a
/// live stream and a rate-limit wall would otherwise burn whole
/// generations.
/// </summary>
public static class AnthropicHttp
{
    public const int DefaultMaxAttempts = 5;

    public static HttpRequestMessage BuildMessagesRequest(AnthropicOptions opts, string bodyJson)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{opts.BaseUrl}/v1/messages")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", opts.ApiKey);
        req.Headers.Add("anthropic-version", opts.ApiVersion);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    /// <summary>
    /// Send with retry. Returns the response body on 2xx; throws
    /// <see cref="AnthropicRequestException"/> once attempts are exhausted or
    /// on a non-retryable status. The request is rebuilt per attempt because
    /// an <see cref="HttpRequestMessage"/> cannot be resent.
    /// </summary>
    public static async Task<AnthropicResponse> SendWithRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> makeRequest,
        AnthropicRateLimiter limiter,
        string? cacheKey,
        ILogger logger,
        CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var lease = await limiter.AcquireAsync(cacheKey, ct);
            HttpResponseMessage? resp = null;
            try
            {
                using var req = makeRequest();
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                last = ex;
                logger.LogWarning(ex, "Anthropic transport failure (attempt {Attempt}/{Max})", attempt, maxAttempts);
                await Task.Delay(Backoff(attempt, 0), ct);
                continue;
            }

            using (resp)
            {
                limiter.Observe(resp);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                    return new AnthropicResponse(body, (int)resp.StatusCode);

                var status = (int)resp.StatusCode;
                var retryable = status == 429 || status >= 500;
                if (!retryable)
                    throw new AnthropicRequestException(status, Truncate(body, 400), retryable: false);

                last = new AnthropicRequestException(status, Truncate(body, 400), retryable: true);
                var retryAfter = RetryAfterSeconds(resp);
                var delay = Backoff(attempt, retryAfter);
                logger.LogWarning(
                    "Anthropic {Status} (attempt {Attempt}/{Max}); retrying in {Delay:N1}s",
                    status, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
        throw last ?? new AnthropicRequestException(0, "Anthropic request failed", retryable: true);
    }

    /// <summary>Seconds from a Retry-After header (delta or HTTP-date), or 0.</summary>
    public static int RetryAfterSeconds(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return 0;
        if (ra.Delta is { } d) return (int)Math.Ceiling(d.TotalSeconds);
        if (ra.Date is { } when)
        {
            var secs = (when - DateTimeOffset.UtcNow).TotalSeconds;
            return secs > 0 ? (int)Math.Ceiling(secs) : 0;
        }
        return 0;
    }

    private static TimeSpan Backoff(int attempt, int retryAfterSeconds)
    {
        // Jittered exponential: 1-2s, 2-4s, 4-8s, 8-16s, capped at 30s —
        // but never shorter than what the server asked for.
        var baseSecs = Math.Min(30, Math.Pow(2, attempt - 1));
        var jittered = baseSecs + Random.Shared.NextDouble() * baseSecs;
        var secs = Math.Max(jittered, retryAfterSeconds);
        return TimeSpan.FromSeconds(Math.Min(60, secs));
    }

    /// <summary>Concatenated text blocks of a non-streaming Messages response.</summary>
    public static string ReadText(JsonElement root)
    {
        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                    sb.Append(txt.GetString());
            }
        }
        return sb.ToString();
    }

    /// <summary>The `input` of the first tool_use block, or null.</summary>
    public static string? ReadToolInput(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && block.TryGetProperty("input", out var input))
                return input.GetRawText();
        }
        return null;
    }

    public static AnthropicUsage ReadUsage(JsonElement root)
    {
        int inT = 0, outT = 0, cr = 0, cc = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i)) inT = i.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var o)) outT = o.GetInt32();
            if (usage.TryGetProperty("cache_read_input_tokens", out var r)) cr = r.GetInt32();
            if (usage.TryGetProperty("cache_creation_input_tokens", out var c)) cc = c.GetInt32();
        }
        var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        return new AnthropicUsage(inT, outT, cr, cc, model);
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}

public sealed record AnthropicResponse(string Body, int StatusCode);

public sealed record AnthropicUsage(
    int InputTokens, int OutputTokens, int CacheReadTokens, int CacheCreationTokens, string? Model);

public sealed class AnthropicRequestException : Exception
{
    public int StatusCode { get; }
    public bool Retryable { get; }

    public AnthropicRequestException(int statusCode, string body, bool retryable)
        : base($"Anthropic returned {statusCode}: {body}")
    {
        StatusCode = statusCode;
        Retryable = retryable;
    }
}
