using System;

namespace Demo.MinPack;

/// <summary>
/// Powell hybrid Newton/Broyden nonlinear-equation solver — user-facing
/// entry point. Translated 1:1 from MINPACK's HYBRD1.F (Argonne National
/// Laboratory, public domain). The Fortran original is a thin wrapper
/// around HYBRD that supplies sensible defaults for the lower-level
/// tunables; the same separation is preserved here so engineers can
/// inject a custom <see cref="HybrdCore"/> for unit testing.
///
/// Behaviour map (every guard cites its signed-spec invariant):
///   INV-1  N &gt; 0
///   INV-2  TOL ≥ 0
///   INV-3  default MAXFEV = 200 · (N + 1)
///   INV-4  ML = MU = N − 1 (full Jacobian band)
///   INV-5  EPSFCN = 0 ⇒ rely on the machine epsilon inside HYBRD
/// </summary>
public sealed class Hybrd1Service
{
    /// <summary>
    /// Bandwidth-multiplier driving the maximum function-evaluation budget.
    /// Matches the Fortran source: MAXFEV = HybrD1MaxFevMultiplier · (N + 1).
    /// </summary>
    public const int HybrD1MaxFevMultiplier = 200;

    /// <summary>
    /// FACTOR controls the initial step bound (||DELTA||₀ = FACTOR · ||X₀||).
    /// Source ships FACTOR = 100.0 — kept identical so the iteration path
    /// matches the reference implementation bit-for-bit on the calibration
    /// problems in the SignedSpecPack.
    /// </summary>
    public const double DefaultStepBoundFactor = 100.0;

    private readonly HybrdCore _core;

    public Hybrd1Service() : this(new HybrdCore()) { }

    /// <summary>Test-time entry point — inject a stubbed core to isolate
    /// HYBRD1's parameter-validation + defaults logic from the iterative
    /// kernel.</summary>
    public Hybrd1Service(HybrdCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
    }

    /// <summary>
    /// Solve F(x) = 0 starting from <paramref name="x0"/>. The input is
    /// not mutated; the result carries the final iterate so the call
    /// site is pure. Diagnostic information matches the Fortran INFO
    /// contract (see <see cref="HybrdInfo"/>).
    /// </summary>
    public HybrdResult Solve(IVectorFunction fcn, ReadOnlySpan<double> x0, double tol)
    {
        ArgumentNullException.ThrowIfNull(fcn);

        var n = x0.Length;

        // INV-1: improper input — N must be at least 1.
        if (n <= 0)
            return Invalid(x0);

        // INV-2: improper input — TOL must be non-negative.
        // Source returns INFO=0 (and untouched X) for this case.
        if (tol < 0 || double.IsNaN(tol))
            return Invalid(x0);

        // INV-3: MAXFEV ramps with problem size so larger systems get
        // proportionally more iterations before declaring failure.
        var maxfev = HybrD1MaxFevMultiplier * (n + 1);

        // INV-4: band-width = N − 1 (full dense Jacobian).
        // INV-5: EPSFCN = 0 → core defaults to sqrt(eps_machine).
        var options = new HybrdOptions(
            MaxFev: maxfev,
            BandwidthLower: n - 1,
            BandwidthUpper: n - 1,
            EpsFcn: 0.0,
            StepBoundFactor: DefaultStepBoundFactor,
            Tolerance: tol);

        // Copy x0 into a working buffer — HYBRD overwrites X in place.
        var x = x0.ToArray();
        var fvec = new double[n];

        return _core.Iterate(fcn, x, fvec, options);
    }

    private static HybrdResult Invalid(ReadOnlySpan<double> x0) =>
        new(HybrdInfo.InvalidArguments, x0.ToArray(), new double[x0.Length], FunctionEvaluations: 0);
}
