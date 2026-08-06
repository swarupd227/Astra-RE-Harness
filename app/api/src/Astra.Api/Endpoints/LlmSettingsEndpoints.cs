using System.Diagnostics;
using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

namespace Astra.Api.Endpoints;

/// <summary>
/// Task #178 — LLM key management from the UI.
///
///   GET    /api/v1/settings/llm        → provider status (key always masked)
///   PUT    /api/v1/settings/llm/key    (admin) → store key, apply live
///   DELETE /api/v1/settings/llm/key    (admin) → drop override, revert to env
///   POST   /api/v1/settings/llm/test   (admin) → live call to Anthropic /v1/models
///
/// The key set here is stored in platform_configs (key "llm.anthropic.apiKey")
/// and applied to the process-wide <see cref="AnthropicOptions"/> instance,
/// which every Anthropic consumer captured at construction — so a key change
/// takes effect immediately, without a restart, whenever the API booted with
/// the anthropic provider. If the API booted in mock fallback (no key at
/// startup), provider singletons are already bound to mock and a restart is
/// required; the status endpoint reports that honestly instead of pretending.
///
/// The key value itself is never returned by any endpoint and never logged.
/// </summary>
public static class LlmSettingsEndpoints
{
    private const string ConfigKey = "llm.anthropic.apiKey";

    // ── startup: apply a previously stored key ───────────────────────
    public static async Task ApplyStoredLlmKeyAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Database.CanConnectAsync()) return;

        string? key = null;
        try
        {
            var row = await db.PlatformConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Key == ConfigKey);
            if (row is null) return;
            key = ParseKeyJson(row.ValueJson);
        }
        catch (Exception ex)
        {
            // Table missing on a pre-bootstrap DB, malformed JSON, … — the
            // env key still works, so warn and carry on.
            Log.Warning(ex, "Could not load stored LLM key override; using environment key.");
            return;
        }
        if (string.IsNullOrWhiteSpace(key)) return;

        var opts = app.Services.GetRequiredService<IOptions<AnthropicOptions>>().Value;
        opts.ApiKey = key;
        app.Services.GetRequiredService<LlmKeyState>().OverrideActive = true;
        Log.Information("LLM API key override loaded from platform config (source=database).");
    }

    // ── endpoints ────────────────────────────────────────────────────
    public static IEndpointRouteBuilder MapLlmSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/settings/llm", (
            IOptions<AnthropicOptions> opts,
            LlmKeyState state,
            ILlmProvider provider,
            IConfiguration config) =>
        {
            return Results.Ok(BuildStatus(opts.Value, state, provider, config));
        });

        app.MapPut("/api/v1/settings/llm/key", async (
            LlmKeyRequest body,
            AppDbContext db,
            IOptions<AnthropicOptions> opts,
            LlmKeyState state,
            ILlmProvider provider,
            IConfiguration config,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            var key = (body.ApiKey ?? "").Trim();
            if (key.Length == 0)
                return BadRequest("llm_key.empty", "API key is required.");
            if (key.Any(char.IsWhiteSpace))
                return BadRequest("llm_key.invalid", "API key must not contain whitespace.");
            if (!key.StartsWith("sk-ant-", StringComparison.Ordinal) || key.Length < 30)
                return BadRequest("llm_key.invalid",
                    "That does not look like an Anthropic API key (expected to start with sk-ant-).");

            var row = await db.PlatformConfigs.FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
            if (row is null)
            {
                row = new PlatformConfig { Key = ConfigKey };
                db.PlatformConfigs.Add(row);
            }
            row.ValueJson = JsonSerializer.Serialize(new { apiKey = key });
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedByDisplay = actor.DisplayName;
            await db.SaveChangesAsync(ct);

            // Apply live — every Anthropic consumer shares this instance.
            opts.Value.ApiKey = key;
            state.OverrideActive = true;
            Log.Information("LLM API key updated via settings UI by {Actor} (value not logged).", actor.DisplayName);

            return Results.Ok(BuildStatus(opts.Value, state, provider, config));
        });

        app.MapDelete("/api/v1/settings/llm/key", async (
            AppDbContext db,
            IOptions<AnthropicOptions> opts,
            LlmKeyState state,
            ILlmProvider provider,
            IConfiguration config,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            var row = await db.PlatformConfigs.FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
            if (row is not null)
            {
                db.PlatformConfigs.Remove(row);
                await db.SaveChangesAsync(ct);
            }
            opts.Value.ApiKey = state.BootKey;
            state.OverrideActive = false;
            Log.Information("LLM API key override cleared by {Actor}; reverted to environment key.", actor.DisplayName);

            return Results.Ok(BuildStatus(opts.Value, state, provider, config));
        });

        app.MapPost("/api/v1/settings/llm/test", async (
            IOptions<AnthropicOptions> opts,
            IHttpClientFactory httpFactory,
            DevPersonaContext actor,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin) return Forbid();

            var key = opts.Value.ApiKey;
            if (string.IsNullOrWhiteSpace(key))
                return BadRequest("llm_test.no_key", "No API key is configured to test.");

            var baseUrl = string.IsNullOrWhiteSpace(opts.Value.BaseUrl)
                ? "https://api.anthropic.com"
                : opts.Value.BaseUrl.TrimEnd('/');

            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models?limit=5");
            req.Headers.Add("x-api-key", key);
            req.Headers.Add("anthropic-version", "2023-06-01");

            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await http.SendAsync(req, ct);
                sw.Stop();
                var bodyText = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    var models = new List<string>();
                    try
                    {
                        using var doc = JsonDocument.Parse(bodyText);
                        if (doc.RootElement.TryGetProperty("data", out var data))
                            foreach (var m in data.EnumerateArray().Take(3))
                                if (m.TryGetProperty("id", out var id))
                                    models.Add(id.GetString() ?? "");
                    }
                    catch { /* body shape drift — connection still proven */ }

                    return Results.Ok(new
                    {
                        ok = true,
                        latencyMs = sw.ElapsedMilliseconds,
                        endpoint = baseUrl,
                        models,
                    });
                }

                var friendly = (int)resp.StatusCode switch
                {
                    401 => "Anthropic rejected the key (HTTP 401) — it is invalid or has been revoked.",
                    403 => "The key was recognised but is not permitted (HTTP 403).",
                    429 => "Rate limited (HTTP 429) — the key works but the account is throttled.",
                    _ => $"Anthropic returned HTTP {(int)resp.StatusCode}.",
                };
                return Results.Ok(new
                {
                    ok = false,
                    latencyMs = sw.ElapsedMilliseconds,
                    endpoint = baseUrl,
                    error = friendly,
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                sw.Stop();
                return Results.Ok(new
                {
                    ok = false,
                    latencyMs = sw.ElapsedMilliseconds,
                    endpoint = baseUrl,
                    error = $"Could not reach {baseUrl}: {ex.Message}",
                });
            }
        });

        return app;
    }

    // ── helpers ──────────────────────────────────────────────────────
    private static object BuildStatus(
        AnthropicOptions opts, LlmKeyState state, ILlmProvider provider, IConfiguration config)
    {
        var configuredProvider = (config.GetValue("Llm:Provider", "mock") ?? "mock").ToLowerInvariant();
        var activeProvider = provider switch
        {
            AnthropicLlmProvider => "anthropic",
            FailMockLlmProvider => "fail-mock",
            _ => "mock",
        };
        var keyConfigured = !string.IsNullOrWhiteSpace(opts.ApiKey);
        var keySource = state.OverrideActive
            ? "database"
            : string.IsNullOrWhiteSpace(state.BootKey) ? "none" : "environment";

        return new
        {
            configuredProvider,
            activeProvider,
            model = opts.Model,
            baseUrl = string.IsNullOrWhiteSpace(opts.BaseUrl) ? "https://api.anthropic.com" : opts.BaseUrl,
            keyConfigured,
            keySource,
            keyHint = Mask(opts.ApiKey),
            // Booted into mock fallback: singletons are bound to mock, so a
            // key set now only takes effect after an API restart.
            requiresRestart = keyConfigured && configuredProvider == "anthropic" && activeProvider != "anthropic",
        };
    }

    private static string? ParseKeyJson(string valueJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            return doc.RootElement.TryGetProperty("apiKey", out var k) ? k.GetString() : null;
        }
        catch { return null; }
    }

    private static string Mask(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        if (key.Length < 14) return "configured";
        return $"{key[..7]}…{key[^4..]}";
    }

    private static IResult Forbid() =>
        Results.Json(new { error = new { code = "auth.admin_required" } }, statusCode: 403);

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new { error = new { code, message } });
}

/// <summary>
/// Remembers the API key the process booted with (from env/config) so the
/// DELETE endpoint can revert a database override without a restart.
/// </summary>
public sealed class LlmKeyState
{
    public LlmKeyState(string bootKey) => BootKey = bootKey;
    public string BootKey { get; }
    public bool OverrideActive { get; set; }
}

public sealed record LlmKeyRequest(string? ApiKey);
