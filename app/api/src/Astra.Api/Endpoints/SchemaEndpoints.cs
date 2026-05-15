using Astra.Api.Llm.Schemas;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #3a — schema discovery surface.
///
///   GET /api/v1/spec-schemas              list every loaded schema (id, displayName, claim kinds)
///   GET /api/v1/spec-schemas/{id}         full schema body (for "what claim shape ships here")
///
/// Public read-only: a CIO walking the dashboard can see "we ship N
/// pre-built schemas" without engineer permissions.
/// </summary>
public static class SchemaEndpoints
{
    public static IEndpointRouteBuilder MapSchemaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/spec-schemas", (SpecSchemaProvider schemas) =>
        {
            return Results.Ok(new
            {
                data = schemas.All().Select(s => new
                {
                    id = s.Id,
                    displayName = s.DisplayName,
                    description = s.Description,
                    supportedSourceExtensions = s.SupportedSourceExtensions,
                    compatibleTargetStacks = s.CompatibleTargetStacks,
                    claimKindCount = s.ClaimKinds.Count,
                    claimKinds = s.ClaimKinds.Select(k => new
                    {
                        id = k.Id,
                        label = k.Label,
                        idPrefix = k.IdPrefix,
                        displayTone = k.DisplayTone,
                        description = k.Description,
                    }),
                    owner = s.Owner,
                    calibratedAgainst = s.CalibratedAgainst,
                    status = s.Status,
                    platformReadiness = s.PlatformReadiness,
                }),
            });
        });

        app.MapGet("/api/v1/spec-schemas/{id}", (string id, SpecSchemaProvider schemas) =>
        {
            var s = schemas.GetById(id);
            if (s is null) return Results.NotFound(new { error = new { code = "schema.not_found" } });
            return Results.Ok(s);
        });

        return app;
    }
}
