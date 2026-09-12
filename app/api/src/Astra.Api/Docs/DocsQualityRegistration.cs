using Astra.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Docs;

/// <summary>
/// WS6 — documentation quality wiring. Kept out of Program.cs so the two
/// call sites there stay one line each:
///
/// <code>
/// builder.Services.AddDocsQuality();                       // after the Docs pipeline registrations
/// await Astra.Api.Docs.DocsQualityRegistration.ApplyDocsSchemaAsync(db);   // inside the startup DDL block, after doc_sections
/// </code>
/// </summary>
public static class DocsQualityRegistration
{
    public static IServiceCollection AddDocsQuality(this IServiceCollection services)
    {
        // Style guide, exemplars, critic/revise prompts — read once, cached
        // as system text on every writer call.
        services.AddSingleton<DocPromptAssets>(sp =>
            DocPromptAssets.Load(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath));

        // Both writers are registered; Docs:Generator:Provider picks the one
        // the pipelines see. "mock" is the compose default so a fresh clone
        // never calls out; set DOCS_GENERATOR_PROVIDER=anthropic for real runs.
        services.AddSingleton<AnthropicDocWriter>();
        services.AddSingleton<MockDocWriter>();
        services.AddSingleton<IDocWriter>(sp =>
            sp.GetRequiredService<IOptions<DocsOptions>>().Value.IsMock
                ? sp.GetRequiredService<MockDocWriter>()
                : sp.GetRequiredService<AnthropicDocWriter>());

        services.AddSingleton<DocCriticPass>();
        return services;
    }

    /// <summary>Additive DDL for the quality column. Idempotent; mirrors the
    /// <c>[Column("quality_json", TypeName = "jsonb")]</c> mapping on
    /// <see cref="Persistence.Entities.DocSection.QualityJson"/>.</summary>
    public static Task ApplyDocsSchemaAsync(AppDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE doc_sections ADD COLUMN IF NOT EXISTS quality_json jsonb NULL;");
}
