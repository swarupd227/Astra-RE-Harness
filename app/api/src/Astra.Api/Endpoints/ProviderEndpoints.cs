using Astra.Api.Llm;
using Astra.Api.Llm.Prompts;
using Microsoft.Extensions.Options;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #4 / value-add #1 — Provider Settings surface.
///
///   GET /api/v1/providers/settings
///
/// Promotes the residency/cost flags that were previously buried in the
/// audit-trail debug strip into a structured trust signal the frontend
/// can render as a visible card on every Spec/Scaffold/Validation page.
///
/// The flags themselves come from <see cref="AnthropicOptions.ConfigVersion"/>,
/// which is the canonical string stamped onto every <c>LlmCall</c> row;
/// this endpoint just parses it into booleans so the UI can render chips
/// rather than a single opaque token.
/// </summary>
public static class ProviderEndpoints
{
    public static IEndpointRouteBuilder MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/providers/settings", (
            ILlmProvider provider,
            IOptions<AnthropicOptions> anthropic,
            PromptLibrary prompts) =>
        {
            var info = provider.Info;
            var flags = ParseFlags(info.ConfigVersion);
            var anth = anthropic.Value;
            var endpointHost = ExtractHostname(anth.BaseUrl);

            // Surface the most recent extract prompt for the canonical
            // Fortran → .NET 8 path. When per-project pinning lands this
            // becomes per-project.
            const string schemaId = "fortran-f77";
            const string targetStack = "dotnet8";
            const string kind = "extract";
            var extractPrompt = prompts.GetLatest(schemaId, targetStack, kind);

            return Results.Ok(new
            {
                provider = new
                {
                    name = info.Name,
                    displayName = DisplayName(info.Name),
                    model = info.Model,
                    endpointHostname = info.Name == "anthropic" ? endpointHost : null,
                    apiVersion = info.Name == "anthropic" ? anth.ApiVersion : null,
                    maxOutputTokens = info.Name == "anthropic" ? anth.MaxOutputTokens : (int?)null,
                },
                residency = new
                {
                    configVersion = info.ConfigVersion,
                    zdr = flags.Contains("zdr=true") || flags.Contains("zdr"),
                    noTraining = flags.Contains("no-train"),
                    noRetention = flags.Contains("no-retention"),
                    enterpriseEndpoint = flags.Contains("enterprise-endpoint"),
                    offline = flags.Contains("offline"),
                },
                promptLibrary = new
                {
                    schemaId,
                    targetStack,
                    extractPromptId = extractPrompt?.PromptId,
                    extractPromptVersion = extractPrompt?.Version,
                },
            });
        });

        return app;
    }

    private static HashSet<string> ParseFlags(string configVersion) =>
        configVersion
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string ExtractHostname(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) ? u.Host : baseUrl;
    }

    private static string DisplayName(string name) => name switch
    {
        "anthropic" => "Anthropic Claude",
        "azure_openai" => "Azure OpenAI",
        "mock" => "Mock (offline)",
        "fail-mock" => "Mock (errors-by-design)",
        _ => name,
    };
}
