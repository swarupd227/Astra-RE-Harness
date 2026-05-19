using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Llm.Prompts;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm;

/// <summary>
/// Real Anthropic Messages-API provider for Stage 3 extraction.
///
/// Streams tokens from Claude as <c>token</c> events for the live-extraction
/// UI feel, then — once the buffer parses as a JSON object — emits an ordered
/// burst of <c>patch</c> + <c>citation_pulse</c> events so the StreamingSpecPanel
/// builds up section by section. This deliberately matches the mock provider's
/// emission pattern so the same UI works for both, with no client changes.
///
/// ZDR posture: the configured endpoint is the enterprise no-train,
/// no-retention deployment; the <see cref="AnthropicOptions.ConfigVersion"/>
/// snapshot is recorded on every <c>LlmCall</c> row for residency evidence.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly AnthropicOptions _opts;
    private readonly PromptLibrary _prompts;
    private readonly ILogger<AnthropicLlmProvider> _logger;

    public AnthropicLlmProvider(
        HttpClient http,
        IOptions<AnthropicOptions> opts,
        PromptLibrary prompts,
        ILogger<AnthropicLlmProvider> logger)
    {
        _http = http;
        _opts = opts.Value;
        _prompts = prompts;
        _logger = logger;
        // Pin defaults so callers don't have to remember them. 5 minutes covers
        // mid-sized real-world subroutines (200-500 LOC); we'll switch to a
        // streaming idle-timeout in Phase D when prod-scale corpora land.
        _http.Timeout = TimeSpan.FromMinutes(5);
        if (_http.DefaultRequestHeaders.Accept.Count == 0)
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public ProviderInfo Info => new(
        Name: "anthropic",
        Model: _opts.Model,
        ConfigVersion: _opts.ConfigVersion);

    public async IAsyncEnumerable<ExtractionEvent> ExtractAsync(
        ExtractionRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        yield return Stage("priming", 1, "Preparing prompt + provider config");
        await Task.Delay(120, ct);

        yield return Stage("loading_source", 2, $"Reading {request.SourcePath}");
        await Task.Delay(120, ct);

        yield return Stage("streaming", 3, "Streaming behavioural spec from Claude");

        // Resolve the prompt from the externalised library (Phase #3b).
        // Phase 5.2 routes by the request's SourceLanguage + TargetStack
        // so COBOL corpora reach cobol/<target>/extract while Fortran
        // continues to reach fortran-f77/<target>/extract. Defaults on
        // the record keep pre-5.2 callers safe.
        var schemaId = string.IsNullOrEmpty(request.SourceLanguage) ? "fortran-f77" : request.SourceLanguage;
        var targetStack = string.IsNullOrEmpty(request.TargetStack) ? "dotnet8" : request.TargetStack;
        const string kind = "extract";
        var loaded = _prompts.GetLatest(schemaId, targetStack, kind)
            ?? throw new InvalidOperationException(
                $"No prompt registered for ({schemaId}, {targetStack}, {kind}). " +
                $"Check Llm/Prompts/{schemaId}/{targetStack}/{kind}.v*.md.");
        var prompt = AnthropicPromptTemplate.Build(_prompts, loaded, request);

        // Phase 7.0 — prompt caching.
        //   - System message is identical for every call within a given
        //     (sourceLanguage × targetStack × kind × promptVersion)
        //     tuple. It carries the persona, the rules, and the
        //     ~500-token schema definition. Marking it `ephemeral` gets
        //     a 5-minute cache with a 90% input-token discount on hit.
        //   - The user message is structured so the variable part
        //     (per-routine source + neighbourhood) sits at the END,
        //     allowing future expansion of cache breakpoints into the
        //     user message without re-engineering this call site.
        // Anthropic's cache_control schema requires content blocks
        // (array form), not bare strings.
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _opts.Model,
            ["max_tokens"] = _opts.MaxOutputTokens,
            ["stream"] = true,
            ["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = prompt.System,
                    ["cache_control"] = new { type = "ephemeral" },
                },
            },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = prompt.User },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v1/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Add("x-api-key", _opts.ApiKey);
        req.Headers.Add("anthropic-version", _opts.ApiVersion);

        HttpResponseMessage? resp = null;
        string? transportErrorMessage = null;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Anthropic transport error");
            transportErrorMessage = ex.Message;
        }
        if (transportErrorMessage is not null || resp is null)
        {
            yield return new("error", new
            {
                code = "provider.transport",
                message = $"Could not reach Anthropic: {transportErrorMessage ?? "no response"}",
                retryable = true,
            });
            yield break;
        }

        string? upstreamErrorBody = null;
        if (!resp.IsSuccessStatusCode)
        {
            try { upstreamErrorBody = await resp.Content.ReadAsStringAsync(ct); } catch { /* best-effort */ }
            yield return new("error", new
            {
                code = MapHttpStatusToCode(resp.StatusCode),
                message = $"Anthropic returned {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(upstreamErrorBody, 280)}",
                retryable = resp.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout,
            });
            resp.Dispose();
            yield break;
        }

        // ── Stream Anthropic SSE events ──────────────────────────────────
        var textBuffer = new StringBuilder(8 * 1024);
        var inputTokens = 0;
        var outputTokens = 0;
        // Phase 7.0 — surface prompt-caching savings.
        var cacheCreationTokens = 0;
        var cacheReadTokens = 0;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventType = null;
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) { eventType = null; continue; }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line[6..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(json) || json == "[DONE]") continue;

            JsonElement evRoot;
            try
            {
                using var doc = JsonDocument.Parse(json);
                evRoot = doc.RootElement.Clone();
            }
            catch (JsonException) { continue; }

            var t = evRoot.TryGetProperty("type", out var tEl) ? tEl.GetString() : eventType;
            switch (t)
            {
                case "message_start":
                    if (evRoot.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("usage", out var u0))
                    {
                        if (u0.TryGetProperty("input_tokens", out var inT))
                            inputTokens = inT.GetInt32();
                        if (u0.TryGetProperty("cache_creation_input_tokens", out var ccT))
                            cacheCreationTokens = ccT.GetInt32();
                        if (u0.TryGetProperty("cache_read_input_tokens", out var crT))
                            cacheReadTokens = crT.GetInt32();
                    }
                    break;

                case "content_block_delta":
                    if (evRoot.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("text", out var txt))
                    {
                        var chunk = txt.GetString() ?? "";
                        textBuffer.Append(chunk);
                        // Forward as a token event so the UI shows live progress
                        yield return new("token", new { path = "$", text = chunk });
                    }
                    break;

                case "message_delta":
                    if (evRoot.TryGetProperty("usage", out var u1) &&
                        u1.TryGetProperty("output_tokens", out var outT))
                    {
                        outputTokens = outT.GetInt32();
                    }
                    break;

                case "error":
                    var errMsg = evRoot.TryGetProperty("error", out var e) &&
                                 e.TryGetProperty("message", out var em)
                        ? em.GetString() ?? "Anthropic emitted an error event."
                        : "Anthropic emitted an error event.";
                    yield return new("error", new
                    {
                        code = "provider.stream_error",
                        message = errMsg,
                        retryable = true,
                    });
                    yield break;
            }
        }

        // ── Parse buffered text → spec JSON ─────────────────────────────
        yield return Stage("validating", 4, "Schema + citation post-validation");
        await Task.Delay(120, ct);

        var rawText = textBuffer.ToString();
        var specJsonText = ExtractJsonObject(rawText);
        JsonDocument? specDoc = null;
        string? parseErrorMessage = null;
        try
        {
            specDoc = JsonDocument.Parse(specJsonText);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Anthropic returned non-JSON / malformed JSON");
            parseErrorMessage = ex.Message;
        }
        if (parseErrorMessage is not null || specDoc is null)
        {
            yield return new("error", new
            {
                code = "provider.malformed_json",
                message =
                    $"Anthropic response was not valid JSON: {parseErrorMessage ?? "empty response"}. " +
                    "View raw response or retry with a tightened prompt.",
                retryable = true,
            });
            yield break;
        }

        // Emit patches + citation pulses in spec-section order so the
        // StreamingSpecPanel populates section by section, matching the mock.
        var root = specDoc.RootElement;
        await foreach (var evt in EmitPatchesAndPulsesAsync(root, ct))
            yield return evt;

        yield return Stage("persisting", 5, "Writing draft spec to Postgres");
        sw.Stop();

        // Clone before disposing the document — the pipeline will serialise
        // this value after our iterator yields.
        var specClone = root.Clone();
        specDoc.Dispose();

        yield return new ExtractionEvent("__final__", new Dictionary<string, object?>
        {
            ["specJson"] = specClone,
            ["inputTokens"] = inputTokens,
            ["outputTokens"] = outputTokens,
            // Phase 7.0 — caching stats; non-zero on cache hit. The audit
            // sink (ExtractionPipeline → LlmCall) records these so the
            // Admin UI can render hit-rate over time.
            ["cacheCreationInputTokens"] = cacheCreationTokens,
            ["cacheReadInputTokens"] = cacheReadTokens,
            ["latencyMs"] = sw.ElapsedMilliseconds,
        });
        _logger.LogInformation(
            "Anthropic extract: input={Input} cached_read={Read} cached_create={Create} output={Output} latency={Ms}ms",
            inputTokens, cacheReadTokens, cacheCreationTokens, outputTokens, sw.ElapsedMilliseconds);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ExtractionEvent Stage(string stage, int step, string label) =>
        new("stage", new { stage, step, of = 5, label });

    private static async IAsyncEnumerable<ExtractionEvent> EmitPatchesAndPulsesAsync(
        JsonElement root,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Header + scalars.
        foreach (var key in new[] { "$schema", "routine", "source_path", "source_lines", "summary" })
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Undefined)
                yield return Patch("/" + key, v.Clone());
        }

        // Inputs / outputs as whole arrays.
        foreach (var sec in new[] { "inputs", "outputs" })
        {
            if (root.TryGetProperty(sec, out var arr) && arr.ValueKind == JsonValueKind.Array)
                yield return Patch("/" + sec, arr.Clone());
        }
        await Task.Delay(150, ct);

        // Per-claim emission with citation pulses.
        foreach (var sec in new[] { "invariants", "side_effects", "edge_cases" })
        {
            if (!root.TryGetProperty(sec, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            yield return Patch("/" + sec, JsonDocument.Parse("[]").RootElement.Clone());
            for (int i = 0; i < arr.GetArrayLength(); i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = arr[i].Clone();
                yield return Patch($"/{sec}/{i}", item);

                if (item.TryGetProperty("citations", out var cites)
                    && cites.ValueKind == JsonValueKind.Array
                    && cites.GetArrayLength() > 0
                    && cites[0].TryGetProperty("lines", out var linesEl)
                    && linesEl.ValueKind == JsonValueKind.String)
                {
                    yield return new("citation_pulse", new
                    {
                        claimPath = $"$.{sec}[{i}]",
                        lines = linesEl.GetString() ?? "",
                    });
                }
                await Task.Delay(220, ct);
            }
        }

        // Open questions.
        if (root.TryGetProperty("open_questions", out var oq) && oq.ValueKind == JsonValueKind.Array)
        {
            yield return Patch("/open_questions", JsonDocument.Parse("[]").RootElement.Clone());
            for (int i = 0; i < oq.GetArrayLength(); i++)
            {
                yield return Patch($"/open_questions/{i}", oq[i].Clone());
                await Task.Delay(150, ct);
            }
        }
    }

    private static ExtractionEvent Patch(string path, object? value) =>
        new("patch", new { op = "add", path, value });

    /// <summary>
    /// Pull a single JSON object out of a Claude response that might include
    /// surrounding prose or accidental markdown fences, despite the prompt
    /// asking for clean JSON. Returns the original buffer if it already parses.
    /// </summary>
    private static string ExtractJsonObject(string raw)
    {
        var trimmed = raw.TrimStart('﻿', ' ', '\n', '\r', '\t');
        // Strip a leading ```json or ``` fence if present.
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
                var fenceClose = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceClose > 0) trimmed = trimmed[..fenceClose];
            }
        }
        // Find the first `{` and the last `}`; trim any surrounding prose.
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return trimmed[firstBrace..(lastBrace + 1)];
        return trimmed;
    }

    private static string MapHttpStatusToCode(HttpStatusCode s) => (int)s switch
    {
        429 => "provider.rate_limited",
        >= 500 and < 600 => "provider.5xx",
        401 or 403 => "provider.auth",
        400 => "provider.bad_request",
        _ => $"provider.{(int)s}",
    };

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
