namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #4 / value-add #5 — Roles &amp; Permissions surface.
///
///   GET /api/v1/personas            list the four personas + their charter
///   GET /api/v1/personas/matrix     who-can-do-what action matrix
///
/// Static today — the matrix is hand-curated to match the
/// <c>if (persona.Persona != Persona.Engineer)</c> checks scattered
/// across the endpoint surface. When real RBAC arrives in Phase D the
/// shape stays the same and the source becomes a policy table rather
/// than this constant.
/// </summary>
public static class RolesEndpoints
{
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

        return app;
    }

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
