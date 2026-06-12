namespace Astra.Api.Llm;

/// <summary>
/// Pre-canned spec/v1 for MINPACK's HYBRD1 subroutine — the user-facing
/// entry point to the Powell hybrid Newton/Broyden nonlinear-equation
/// solver. Used by <see cref="MockLlmProvider"/> to produce a
/// deterministic, demo-ready stream against any MINPACK source file.
/// Line citations are deliberately approximate; the original HYBRD1.F
/// is ~280 lines and the exact line numbers vary between mirrors.
/// </summary>
public static class CanonicalSpec
{
    public sealed record Citation(string Lines);
    public sealed record Input(string Id, string Name, string Type, string Semantic, Citation[] Citations);
    public sealed record Output(string Id, string Name, string Type, string Semantic, Citation[] Citations);
    public sealed record Invariant(string Id, string Claim, Citation[] Citations, string Confidence);
    public sealed record SideEffect(string Id, string Description, Citation[] Citations);
    public sealed record EdgeCase(string Id, string Description, Citation[] Citations, string Behavior, string Confidence);
    public sealed record OpenQuestion(string Id, string Question, string Status);

    public const string Summary =
        "User-facing entry point to Powell's hybrid Newton/Broyden nonlinear-equation solver. " +
        "Wraps the lower-level HYBRD with sensible defaults: MAXFEV = 200·(N+1), full-bandwidth " +
        "Jacobian, forward-difference EPSFCN, FACTOR = 100. Iterates F(x)=0 from the caller's " +
        "starting estimate, overwriting X with the final iterate and reporting outcome via INFO.";

    public static readonly Input[] Inputs =
    {
        new("in.FCN", "FCN",   "EXTERNAL",     "User-supplied subroutine for evaluating F(X). Caller signs the contract that FCN is pure (no hidden state) and returns IFLAG ≥ 0 on success.", new[]{new Citation("38-44")}),
        new("in.N",   "N",     "INTEGER",      "Number of variables = number of equations. Must be positive.",                                                                              new[]{new Citation("46")}),
        new("in.X",   "X",     "REAL*8(N)",    "Initial estimate of the root on entry; overwritten in place with the final iterate on exit.",                                              new[]{new Citation("48")}),
        new("in.TOL", "TOL",   "REAL*8",       "Termination threshold on the relative error between two consecutive iterates. Must be non-negative.",                                     new[]{new Citation("52")}),
        new("in.WA",  "WA",    "REAL*8(LWA)",  "Caller-allocated work array. Length must satisfy LWA ≥ N·(3·N+13)/2.",                                                                    new[]{new Citation("56-58")}),
    };

    public static readonly Output[] Outputs =
    {
        new("out.X",    "X",    "REAL*8(N)", "Final iterate (overwrites input X).",                                                                                       new[]{new Citation("48")}),
        new("out.FVEC", "FVEC", "REAL*8(N)", "Residual F(X) at the final iterate.",                                                                                       new[]{new Citation("50")}),
        new("out.INFO", "INFO", "INTEGER",   "Result code: 0=invalid args, 1=converged, 2=maxfev exceeded, 3=tol too small, 4=no progress, 5=user abort (IFLAG<0).",       new[]{new Citation("54")}),
    };

    public static readonly Invariant[] Invariants =
    {
        new("INV-1",
            "INFO is set to 0 (invalid arguments) and the routine returns without calling FCN when N ≤ 0.",
            new[]{new Citation("63-65")},
            "high"),
        new("INV-2",
            "INFO is set to 0 (invalid arguments) and the routine returns without calling FCN when TOL < 0 or LWA < N·(3·N+13)/2.",
            new[]{new Citation("66-68")},
            "high"),
        new("INV-3",
            "MAXFEV is initialised to 200·(N+1) before delegation to HYBRD — the function-evaluation budget scales linearly with problem size.",
            new[]{new Citation("78")},
            "high"),
        new("INV-4",
            "Bandwidth parameters ML and MU are set to N−1, signalling a fully dense Jacobian (no exploitable band structure).",
            new[]{new Citation("80-81")},
            "high"),
        new("INV-5",
            "FACTOR is fixed at 100.0 — the initial trust-region radius is 100 × ‖X₀‖ (or 100.0 if ‖X₀‖ = 0).",
            new[]{new Citation("84")},
            "high"),
        new("INV-6",
            "On normal return INFO ∈ {1, 2, 3, 4}; INFO=5 only when the user's FCN sets IFLAG to a negative value.",
            new[]{new Citation("96-98")},
            "high"),
    };

    public static readonly SideEffect[] SideEffects =
    {
        new("SE-1", "Overwrites the caller's X array with the final iterate.",                              new[]{new Citation("48")}),
        new("SE-2", "Populates FVEC with the residual F(X) at termination.",                                 new[]{new Citation("50")}),
        new("SE-3", "Mutates the caller's WA work array as scratch space (no output meaning post-return).", new[]{new Citation("56-58")}),
    };

    public static readonly EdgeCase[] EdgeCases =
    {
        new("EC-1",
            "Zero starting iterate (‖X₀‖ = 0). FACTOR · ‖X₀‖ would be zero — source falls back to using FACTOR alone as the initial step bound.",
            new[]{new Citation("84-86")},
            "Trust-region initialises to 100.0; iteration proceeds normally.",
            "high"),
        new("EC-2",
            "User FCN signals abort by setting IFLAG to a negative value mid-iteration.",
            new[]{new Citation("96-98")},
            "Routine returns immediately with INFO=5; X and FVEC reflect the last successful evaluation.",
            "high"),
        new("EC-3",
            "TOL is below machine precision — the convergence test is unsatisfiable in finite arithmetic.",
            new[]{new Citation("100-102")},
            "HYBRD returns INFO=3 (TOL too small); HYBRD1 propagates it.",
            "high"),
        new("EC-4",
            "Stagnation: five consecutive iterations with ratio of actual to predicted reduction below 0.001.",
            new[]{new Citation("104-106")},
            "Returns INFO=4 — iteration is not making good progress; X holds the best iterate found.",
            "medium"),
    };

    public static readonly OpenQuestion[] OpenQuestions =
    {
        new("Q-1",
            "Source ships FACTOR=100.0 as a compile-time constant. Is this value validated against modern problem sets, or should it be exposed for caller override in the C# port?",
            "unresolved"),
        new("Q-2",
            "EPSFCN is hard-coded to 0 (→ sqrt(machine-epsilon) inside HYBRD). For problems where F is itself computed by an inexact iterative solver, the implicit EPSFCN is wrong — should the C# port expose it?",
            "unresolved"),
        new("Q-3",
            "The work array WA mixes scratch space for the QR factorisation, Broyden update, and dogleg direction. The original packs them into a single REAL*8(LWA) — should we split into typed buffers in the C# port for clarity, even at small memory cost?",
            "unresolved"),
    };
}
