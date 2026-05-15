using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #4 / value-add #5 — Roles &amp; Permissions surface.
///
///   GET    /api/v1/personas            list the four personas + their charter
///   GET    /api/v1/personas/matrix     who-can-do-what action matrix
///
///   GET    /api/v1/users               list users (admin-only)
///   POST   /api/v1/users               create a user (admin-only)
///   PUT    /api/v1/users/{id}/persona  re-assign a user's persona (admin-only)
///   DELETE /api/v1/users/{id}          remove a user (admin-only)
///
/// The persona/matrix endpoints stay static — they're the canonical
/// charter and capability table. The /users surface is the operational
/// CRUD an Admin uses to grant access.
/// </summary>
public static class RolesEndpoints
{
    private static readonly string[] ValidPersonas = { "engineer", "sme", "observer", "admin" };

    public static IEndpointRouteBuilder MapRolesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/personas", () =>
        {
            return Results.Ok(new
            {
                data = Personas,
            });
        });

        app.MapGet("/api/v1/personas/matrix", () =>
        {
            return Results.Ok(new
            {
                personas = Personas.Select(p => new { id = p.id, displayName = p.displayName }),
                actions = Actions.Select(a => new
                {
                    id = a.id,
                    label = a.label,
                    description = a.description,
                    category = a.category,
                    allowedPersonas = a.allowedPersonas,
                }),
            });
        });

        // ─── User CRUD (admin-only) ───────────────────────────────────────

        app.MapGet("/api/v1/users", async (
            AppDbContext db,
            DevPersonaContext persona,
            CancellationToken ct) =>
        {
            if (persona.Persona != Persona.Admin)
                return Results.StatusCode(403);
            var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
            return Results.Ok(new
            {
                data = users.Select(u => new
                {
                    id = u.Id,
                    email = u.Email,
                    displayName = u.DisplayName,
                    persona = u.Persona,
                    createdAt = u.CreatedAt,
                    updatedAt = u.UpdatedAt,
                }),
            });
        });

        app.MapPost("/api/v1/users", async (
            CreateUserRequest body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.DisplayName))
                return Results.BadRequest(new { error = new { code = "user.invalid", message = "email and displayName are required." } });
            if (!ValidPersonas.Contains(body.Persona))
                return Results.BadRequest(new { error = new { code = "user.invalid_persona", message = $"persona must be one of: {string.Join(", ", ValidPersonas)}." } });

            var email = body.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
                return Results.Conflict(new { error = new { code = "user.email_exists", message = "A user with that email already exists." } });

            var now = DateTimeOffset.UtcNow;
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = email,
                DisplayName = body.DisplayName.Trim(),
                Persona = body.Persona,
                IdpSubject = "dev:" + email,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await db.Users.AddAsync(user, ct);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync("user.created", "user", user.Id, actor, payload: new
            {
                email = user.Email,
                displayName = user.DisplayName,
                persona = user.Persona,
            }, ctx, ct);

            return Results.Created($"/api/v1/users/{user.Id}", new
            {
                id = user.Id,
                email = user.Email,
                displayName = user.DisplayName,
                persona = user.Persona,
                createdAt = user.CreatedAt,
                updatedAt = user.UpdatedAt,
            });
        });

        app.MapPut("/api/v1/users/{id:guid}/persona", async (
            Guid id,
            UpdatePersonaRequest body,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            if (!ValidPersonas.Contains(body.Persona))
                return Results.BadRequest(new { error = new { code = "user.invalid_persona", message = $"persona must be one of: {string.Join(", ", ValidPersonas)}." } });

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound(new { error = new { code = "user.not_found" } });

            var previous = user.Persona;
            if (previous == body.Persona)
                return Results.Ok(new { id = user.Id, persona = user.Persona, unchanged = true });

            user.Persona = body.Persona;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.LogAsync("user.persona_changed", "user", user.Id, actor, payload: new
            {
                from = previous,
                to = body.Persona,
                email = user.Email,
            }, ctx, ct);

            return Results.Ok(new { id = user.Id, persona = user.Persona, previousPersona = previous });
        });

        app.MapDelete("/api/v1/users/{id:guid}", async (
            Guid id,
            AppDbContext db,
            DevPersonaContext actor,
            IAuditLogger audit,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (actor.Persona != Persona.Admin)
                return Results.StatusCode(403);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound(new { error = new { code = "user.not_found" } });

            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);

            await audit.LogAsync("user.deleted", "user", user.Id, actor, payload: new
            {
                email = user.Email,
                displayName = user.DisplayName,
                persona = user.Persona,
            }, ctx, ct);

            return Results.NoContent();
        });

        return app;
    }

    private sealed record CreateUserRequest(string Email, string DisplayName, string Persona);
    private sealed record UpdatePersonaRequest(string Persona);

    private record PersonaDef(string id, string displayName, string charter, string[] ownsStages);

    private static readonly PersonaDef[] Personas = new[]
    {
        new PersonaDef(
            "engineer",
            "Engineer",
            "Operates the pipeline. Triggers ingest, extract, scaffold, validation, commit.",
            new[] { "Stage 1 · Ingest", "Stage 3 · Extract", "Stage 5 · Scaffold", "Phase #2 · Validation" }),
        new PersonaDef(
            "sme",
            "SME",
            "Reviews every Claude-produced claim and signs the spec when satisfied. The signature is the audit-grade gate.",
            new[] { "Stage 4 · Spec review", "Stage 4 · Sign-off" }),
        new PersonaDef(
            "observer",
            "Observer",
            "Read-only oversight. Audits the trail and exports compliance evidence; cannot trigger pipeline actions.",
            new[] { "Audit trail", "Compliance feed" }),
        new PersonaDef(
            "admin",
            "Admin",
            "Platform configuration. Manages prompts, schemas, archetypes, providers, validation policy, and user role assignments.",
            new[] { "Platform · Prompts", "Platform · Languages", "Platform · Validation Policy", "Platform · Signature Health", "Platform · Roles" }),
    };

    private record ActionDef(
        string id,
        string label,
        string description,
        string category,
        string[] allowedPersonas);

    private static readonly ActionDef[] Actions = new[]
    {
        new ActionDef("ingest_project",       "Ingest a project",                  "Upload or Git-clone a legacy source corpus and parse it.",                        "Pipeline",    new[] { "engineer", "admin" }),
        new ActionDef("extract_spec",         "Extract a behavioural spec",        "Trigger a Claude call against a parsed subroutine to produce a structured spec.", "Pipeline",    new[] { "engineer", "admin" }),
        new ActionDef("review_claim",         "Review claims (accept / edit / reject)", "Walk every claim in a DRAFT spec and apply a decision.",                  "Review",      new[] { "sme", "admin" }),
        new ActionDef("sign_spec",            "Sign a spec",                       "Cryptographically bind every signed claim to the exact source revision.",         "Review",      new[] { "sme", "admin" }),
        new ActionDef("generate_scaffold",    "Generate a scaffold",               "Stream target-stack code from the signed spec (.NET 8, Java Spring, …).",         "Pipeline",    new[] { "engineer", "admin" }),
        new ActionDef("run_validation",       "Run validation gates",              "Trigger compile / test pack / equivalence checks for a scaffold.",                "Pipeline",    new[] { "engineer", "admin" }),
        new ActionDef("commit_scaffold",      "Commit a scaffold to Git",          "Record a Git commit + URL on the scaffold; downstream pipeline picks it up.",     "Pipeline",    new[] { "engineer", "admin" }),
        new ActionDef("export_compliance",    "Export SOX / HIPAA / PCI feed",     "Download the audit log as an evidence bundle; the export itself is audited.",     "Audit",       new[] { "engineer", "sme", "observer", "admin" }),
        new ActionDef("read_audit_trail",     "Read the audit trail",              "Browse the immutable append-only log of every state transition.",                 "Audit",       new[] { "engineer", "sme", "observer", "admin" }),
        new ActionDef("manage_prompts",       "Configure prompts & archetypes",    "Pin prompt versions, register new archetypes, edit calibration metadata.",        "Platform",    new[] { "admin" }),
        new ActionDef("manage_validation",    "Configure validation policy",       "Toggle gates per project, set test-coverage thresholds, retry policy.",            "Platform",    new[] { "admin" }),
        new ActionDef("manage_roles",         "Manage roles & permissions",        "Assign personas to users; edit the capability matrix.",                            "Platform",    new[] { "admin" }),
    };
}
