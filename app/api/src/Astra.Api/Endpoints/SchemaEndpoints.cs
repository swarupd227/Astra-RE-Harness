using System.Text.Json;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm.Schemas;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #3a — schema discovery surface, plus Phase #4.4 admin overrides
/// for enable/disable, displayName, description, status, and platform
/// readiness.
///
///   GET    /api/v1/spec-schemas                        list every schema (with merged overrides)
///   GET    /api/v1/spec-schemas/{id}                   full schema body
///   PUT    /api/v1/spec-schemas/{id}/override          admin-only override
///   DELETE /api/v1/spec-schemas/{id}/override          admin-only revert
///
/// Overrides live in platform_configs keyed "languages.overrides" as a
/// single JSON map { [schemaId]: SchemaOverride }. This avoids one row
/// per language while keeping the override authoritative through a
/// single audit row per change.
/// </summary>
public static class SchemaEndpoints
{
    private const string ConfigKey = "languages.overrides";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapSchemaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/spec-schemas", async (SpecSchemaProvider schemas, AppDbContext db, CancellationToken ct) =>
        {
            var overrides = await LoadOverridesAsync(db, ct);
            return Results.Ok(new
            {
                data = schemas.All().Select(s => Project(s, overrides)),
            });
        });

        app.MapGet("/api/v1/spec-schemas/{id}", async (string id, SpecSchemaProvider schemas, AppDbContext db, CancellationToken ct) =>
        {
            var s = schemas.GetById(id);
            if (s is null) return Results.NotFound(new { error = new { code = "schema.not_found" } });
            var overrides = await LoadOverridesAsync(db, ct);
            return Results.Ok(Project(s, overrides));
        });

        // ─── Phase #4.4 admin mutations ───────────────────────────────────

        app.MapPut("/api/v1/spec-schemas/{id}/override", async (
            string id,
            SchemaOverride body,
            SpecSchemaProvider schemas,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            if (schemas.GetById(id) is null)
                return Results.NotFound(new { error = new { code = "schema.not_found" } });

            var overrides = await LoadOverridesAsync(db, ct);
            overrides[id] = body;
            await SaveOverridesAsync(db, actor, overrides, ct);

            await audit.LogAsync("schema.override_updated", "spec_schema", null, actor,
                payload: new { id, override_ = body }, ctx, ct);

            return Results.Ok(Project(schemas.GetById(id)!, overrides));
        });

        app.MapDelete("/api/v1/spec-schemas/{id}/override", async (
            string id,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            var overrides = await LoadOverridesAsync(db, ct);
            if (!overrides.Remove(id)) return Results.NoContent();
            await SaveOverridesAsync(db, actor, overrides, ct);
            await audit.LogAsync("schema.override_reverted", "spec_schema", null, actor,
                payload: new { id }, ctx, ct);
            return Results.NoContent();
        });

        return app;
    }

    // ─── Override storage + projection ──────────────────────────────────

    public sealed class SchemaOverride
    {
        public bool? Enabled { get; set; }
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Status { get; set; }
        public string? PlatformReadiness { get; set; }
    }

    private static async Task<Dictionary<string, SchemaOverride>> LoadOverridesAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.PlatformConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
        if (row is null) return new Dictionary<string, SchemaOverride>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, SchemaOverride>>(row.ValueJson, JsonOpts);
            return map is null
                ? new Dictionary<string, SchemaOverride>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, SchemaOverride>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, SchemaOverride>(StringComparer.OrdinalIgnoreCase); }
    }

    private static async Task SaveOverridesAsync(AppDbContext db, DevPersonaContext actor, Dictionary<string, SchemaOverride> overrides, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(overrides, JsonOpts);
        var existing = await db.PlatformConfigs.FirstOrDefaultAsync(c => c.Key == ConfigKey, ct);
        if (existing is null)
        {
            await db.PlatformConfigs.AddAsync(new PlatformConfig
            {
                Key = ConfigKey,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = null,
                UpdatedByDisplay = actor.DisplayName,
            }, ct);
        }
        else
        {
            existing.ValueJson = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.UpdatedByDisplay = actor.DisplayName;
        }
        await db.SaveChangesAsync(ct);
    }

    private static object Project(SpecSchema s, Dictionary<string, SchemaOverride> overrides)
    {
        overrides.TryGetValue(s.Id, out var o);
        var enabled = o?.Enabled ?? true;
        return new
        {
            id = s.Id,
            displayName = o?.DisplayName ?? s.DisplayName,
            description = o?.Description ?? s.Description,
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
            status = o?.Status ?? s.Status,
            platformReadiness = o?.PlatformReadiness ?? s.PlatformReadiness,
            enabled,
            overrideActive = o is not null,
        };
    }
}
