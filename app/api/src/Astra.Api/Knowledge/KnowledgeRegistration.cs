namespace Astra.Api.Knowledge;

/// <summary>
/// Knowledge-capture wiring, kept out of Program.cs so its call site there is one line:
/// <code>builder.Services.AddKnowledge(builder.Configuration);</code>
/// Everything is read once at startup from content that ships with the binary (<c>Llm/Packs</c>,
/// <c>Llm/Prompts/knowledge</c>); the writer goes through the same <c>IDocWriter</c> as the other documentation
/// pipelines, so there is no HTTP client in this folder and a mock run needs no key.
/// </summary>
public static class KnowledgeRegistration
{
    public static IServiceCollection AddKnowledge(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<KnowledgeOptions>(configuration.GetSection("Knowledge"));

        services.AddSingleton(sp => PackRegistry.Load(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
        services.AddSingleton(sp => StatementPrompts.Load(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
        services.AddSingleton<IStatementFixtures>(sp =>
        {
            var packs = sp.GetRequiredService<PackRegistry>();
            var folders = packs.All
                .Select(p => packs.DirectoryOf(p.Id))
                .Where(d => d is not null)
                .Select(d => Path.Combine(d!, "fixtures"));
            return new DirectoryStatementFixtures(folders);
        });
        services.AddSingleton<StatementWriter>();
        services.AddSingleton<ClaimExtractor>();
        services.AddScoped<ClaimService>();
        return services;
    }
}
