using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Api.Preview;

/// <summary>Where and how previews run. Off unless <c>Preview:Enabled</c> is true and the Azure ids are set.</summary>
public sealed class PreviewOptions
{
    public bool Enabled { get; set; }
    public string SubscriptionId { get; set; } = "";
    /// <summary>A resource group used for previews and nothing else; the API's role is scoped to it.</summary>
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "centralus";
    /// <summary>Full resource id of the Container Apps environment the previews join.</summary>
    public string EnvironmentId { get; set; } = "";
    public string Image { get; set; } = "";
    public string RegistryServer { get; set; } = "";
    /// <summary>Full resource id of the user-assigned identity that has AcrPull on the registry.</summary>
    public string PullIdentityId { get; set; } = "";
    /// <summary>The address the preview container uses to fetch the package from this API.</summary>
    public string PublicApiBase { get; set; } = "";
    public int TtlMinutes { get; set; } = 60;
    public int MaxConcurrent { get; set; } = 3;
}

public sealed record PreviewInfo(string Name, string State, string? Url, DateTimeOffset? ExpiresAt, string? Detail);

/// <summary>
/// Runs a generated React + Python package as a short-lived Azure Container App (the preview-runner image),
/// apart from the API. State lives on the container app itself (name from the scaffold id, tags for expiry), so
/// there is no table. The API's identity may only create, read and delete container apps in the preview group.
/// </summary>
public sealed class PreviewService
{
    private const string Arm = "https://management.azure.com";
    private const string ApiVersion = "2024-03-01";
    private const string Tag = "astra-preview";

    private readonly PreviewOptions _o;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<PreviewService> _log;

    public PreviewService(Microsoft.Extensions.Options.IOptions<PreviewOptions> o, IHttpClientFactory http, ILogger<PreviewService> log)
    {
        _o = o.Value; _http = http; _log = log;
    }

    public bool Enabled => _o.Enabled && !string.IsNullOrWhiteSpace(_o.SubscriptionId) && !string.IsNullOrWhiteSpace(_o.ResourceGroup)
                           && !string.IsNullOrWhiteSpace(_o.EnvironmentId) && !string.IsNullOrWhiteSpace(_o.Image);

    /// <summary>pv-&lt;12 hex of the scaffold id&gt;: valid as a container app name, stable for one scaffold.</summary>
    public static string NameFor(Guid scaffoldId) => "pv-" + scaffoldId.ToString("N")[..12];

    private string AppUrl(string name) =>
        $"{Arm}/subscriptions/{_o.SubscriptionId}/resourceGroups/{_o.ResourceGroup}/providers/Microsoft.App/containerApps/{name}?api-version={ApiVersion}";

    private string ListUrl() =>
        $"{Arm}/subscriptions/{_o.SubscriptionId}/resourceGroups/{_o.ResourceGroup}/providers/Microsoft.App/containerApps?api-version={ApiVersion}";

    /// <summary>App Service managed-identity token for Azure Resource Manager.</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(header))
            throw new InvalidOperationException("No managed identity is available to this API (previews run only on Azure).");
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?resource={Uri.EscapeDataString(Arm + "/")}&api-version=2019-08-01");
        req.Headers.Add("X-IDENTITY-HEADER", header);
        var res = await _http.CreateClient().SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Could not get an Azure token: {(int)res.StatusCode} {Trim(text)}");
        return JsonDocument.Parse(text).RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var res = await _http.CreateClient().SendAsync(req, ct);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    /// <summary>The preview for a scaffold, or null when none exists.</summary>
    public async Task<PreviewInfo?> GetAsync(Guid scaffoldId, CancellationToken ct)
    {
        var name = NameFor(scaffoldId);
        var (status, body) = await SendAsync(HttpMethod.Get, AppUrl(name), null, ct);
        if (status == 404) return null;
        if (status >= 300) throw new InvalidOperationException($"Azure refused the status request: {status} {Trim(body)}");
        return await ToInfoAsync(JsonNode.Parse(body)!, ct);
    }

    public async Task<PreviewInfo> StartAsync(Guid scaffoldId, CancellationToken ct)
    {
        var existing = await GetAsync(scaffoldId, ct);
        if (existing is not null && existing.State != "FAILED") return existing;

        var running = (await ListAsync(ct)).Count(a => a.State != "FAILED");
        if (running >= _o.MaxConcurrent)
            throw new InvalidOperationException($"{running} previews are already running (the limit is {_o.MaxConcurrent}). Stop one first.");

        var name = NameFor(scaffoldId);
        var expires = DateTimeOffset.UtcNow.AddMinutes(_o.TtlMinutes);
        var body = new JsonObject
        {
            ["location"] = _o.Location,
            ["tags"] = new JsonObject { [Tag] = "1", ["scaffoldId"] = scaffoldId.ToString(), ["expiresAt"] = expires.ToString("o") },
            ["identity"] = new JsonObject
            {
                ["type"] = "UserAssigned",
                ["userAssignedIdentities"] = new JsonObject { [_o.PullIdentityId] = new JsonObject() },
            },
            ["properties"] = new JsonObject
            {
                ["environmentId"] = _o.EnvironmentId,
                ["configuration"] = new JsonObject
                {
                    ["ingress"] = new JsonObject { ["external"] = true, ["targetPort"] = 8080, ["transport"] = "auto", ["allowInsecure"] = false },
                    ["registries"] = new JsonArray(new JsonObject { ["server"] = _o.RegistryServer, ["identity"] = _o.PullIdentityId }),
                },
                ["template"] = new JsonObject
                {
                    ["containers"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "preview",
                        ["image"] = _o.Image,
                        ["env"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "PACKAGE_URL",
                            ["value"] = $"{_o.PublicApiBase.TrimEnd('/')}/api/v1/scaffolds/{scaffoldId}/package.zip",
                        }),
                        ["resources"] = new JsonObject { ["cpu"] = 1.0, ["memory"] = "2Gi" },
                    }),
                    ["scale"] = new JsonObject { ["minReplicas"] = 1, ["maxReplicas"] = 1 },
                },
            },
        };
        var (status, text) = await SendAsync(HttpMethod.Put, AppUrl(name), body, ct);
        if (status >= 300) throw new InvalidOperationException($"Azure refused to start the preview: {status} {Trim(text)}");
        _log.LogInformation("Preview {Name} started for scaffold {Scaffold}, expires {Expires}", name, scaffoldId, expires);
        return new PreviewInfo(name, "STARTING", null, expires, "Creating the container");
    }

    public async Task<bool> StopAsync(Guid scaffoldId, CancellationToken ct)
    {
        var (status, text) = await SendAsync(HttpMethod.Delete, AppUrl(NameFor(scaffoldId)), null, ct);
        if (status == 404) return false;
        if (status >= 300) throw new InvalidOperationException($"Azure refused to stop the preview: {status} {Trim(text)}");
        _log.LogInformation("Preview for scaffold {Scaffold} stopped", scaffoldId);
        return true;
    }

    /// <summary>Delete previews past their expiry (run by the sweeper). Only apps carrying the preview tag are touched.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, ListUrl(), null, ct);
        if (status >= 300) throw new InvalidOperationException($"Azure refused the list request: {status} {Trim(body)}");
        var removed = 0;
        foreach (var app in JsonNode.Parse(body)!["value"]!.AsArray())
        {
            var tags = app!["tags"];
            if (tags?[Tag]?.GetValue<string>() != "1") continue;
            var name = app["name"]!.GetValue<string>();
            var expires = DateTimeOffset.TryParse(tags["expiresAt"]?.GetValue<string>(), out var e) ? e : DateTimeOffset.MinValue;
            if (expires > DateTimeOffset.UtcNow) continue;
            var (s, _) = await SendAsync(HttpMethod.Delete, AppUrl(name), null, ct);
            if (s < 300) { removed++; _log.LogInformation("Preview {Name} expired and was removed", name); }
        }
        return removed;
    }

    private async Task<List<PreviewInfo>> ListAsync(CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, ListUrl(), null, ct);
        if (status >= 300) throw new InvalidOperationException($"Azure refused the list request: {status} {Trim(body)}");
        var list = new List<PreviewInfo>();
        foreach (var app in JsonNode.Parse(body)!["value"]!.AsArray())
            if (app!["tags"]?[Tag]?.GetValue<string>() == "1")
                list.Add(Describe(app!));
        return list;
    }

    private static PreviewInfo Describe(JsonNode app)
    {
        var name = app["name"]!.GetValue<string>();
        var provisioning = app["properties"]?["provisioningState"]?.GetValue<string>() ?? "";
        var fqdn = app["properties"]?["configuration"]?["ingress"]?["fqdn"]?.GetValue<string>();
        var expires = DateTimeOffset.TryParse(app["tags"]?["expiresAt"]?.GetValue<string>(), out var e) ? e : (DateTimeOffset?)null;
        var state = provisioning switch { "Failed" => "FAILED", "Succeeded" => "STARTING", _ => "STARTING" };
        return new PreviewInfo(name, state, fqdn is null ? null : "https://" + fqdn, expires,
            provisioning == "Failed" ? "Azure could not create the container" : null);
    }

    /// <summary>Container created is not the same as app ready: the runner builds for a couple of minutes first.</summary>
    private async Task<PreviewInfo> ToInfoAsync(JsonNode app, CancellationToken ct)
    {
        var info = Describe(app);
        if (info.State == "FAILED" || info.Url is null) return info;
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, info.Url);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var res = await _http.CreateClient().SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // The runner answers 200 only once the front end is built; before that nothing listens (502/503/404).
            return res.IsSuccessStatusCode
                ? info with { State = "READY", Detail = null }
                : info with { Detail = "Building the app (installing dependencies, building the form)" };
        }
        catch
        {
            return info with { Detail = "Building the app (installing dependencies, building the form)" };
        }
    }
}

/// <summary>Removes expired previews every few minutes. Does nothing when previews are off.</summary>
public sealed class PreviewSweeper : BackgroundService
{
    private readonly PreviewService _svc;
    private readonly ILogger<PreviewSweeper> _log;
    public PreviewSweeper(PreviewService svc, ILogger<PreviewSweeper> log) { _svc = svc; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_svc.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _svc.SweepAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Preview sweep failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
