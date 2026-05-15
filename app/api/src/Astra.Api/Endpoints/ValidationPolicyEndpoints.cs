namespace Astra.Api.Endpoints;

/// <summary>
/// Phase #4 / value-add #7 — Validation policy surface.
///
///   GET /api/v1/validation/policy            global policy
///
/// Read-only today. Returns the canonical gate set, the coverage
/// thresholds we enforce, and the retry/flake-handling policy. Phase D
/// adds the per-project override + write surface; this lays down the
/// shape so the FE can render the "three independent gates with these
/// thresholds" story for buyers.
/// </summary>
public static class ValidationPolicyEndpoints
{
    public static IEndpointRouteBuilder MapValidationPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/validation/policy", () =>
        {
            return Results.Ok(Policy);
        });

        return app;
    }

    private record Gate(
        string id,
        string label,
        string description,
        bool required,
        string? coverageThreshold,
        string retryPolicy,
        string blockingCommitOnFailure);

    private static readonly object Policy = new
    {
        scope = "global",
        version = "v1.0",
        ownedBy = "Nous · Validation engineering",
        appliesTo = "Every scaffold artifact before commit.",
        commitGate = new
        {
            requireAllGreen = true,
            description = "A scaffold cannot be committed to Git until every required gate reports PASSED on the same scaffold revision.",
        },
        gates = new[]
        {
            new Gate(
                id: "compile",
                label: "Compile",
                description: "Run `dotnet build` (or the target-stack equivalent) against the generated package. Zero warnings required at project-level error treatment.",
                required: true,
                coverageThreshold: null,
                retryPolicy: "manual",
                blockingCommitOnFailure: "yes"),
            new Gate(
                id: "test_pack",
                label: "Test pack",
                description: "Run every claim-mapped fixture in the generated test project. Every signed claim must produce at least one fixture and that fixture must pass.",
                required: true,
                coverageThreshold: "100% of signed claims must be covered by at least one fixture; coverage gap fails the gate.",
                retryPolicy: "regenerate-then-run",
                blockingCommitOnFailure: "yes"),
            new Gate(
                id: "equivalence",
                label: "Cross-runtime equivalence",
                description: "Drive the original Fortran (via gfortran sidecar) and the generated C# with the same canonical inputs. Outputs must match field-for-field on every recorded input.",
                required: true,
                coverageThreshold: "Smoke at minimum (one canonical input per CONSUME_ROLL family); per-routine equivalence as that lands per Phase #5.",
                retryPolicy: "manual",
                blockingCommitOnFailure: "yes"),
        },
        retryDefaults = new
        {
            transientFlakeWindow = "PT15M",
            autoRetryCount = 0,
            note = "Transient failures (compile-server crash, gfortran timeout) require a manual re-run by the engineer so flake doesn't mask a real regression.",
        },
    };
}
