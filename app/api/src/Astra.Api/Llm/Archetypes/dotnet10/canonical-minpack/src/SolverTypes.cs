using System;

namespace Demo.MinPack;

/// <summary>
/// Contract the caller supplies to evaluate the system of nonlinear
/// equations F(x). Replaces the Fortran user-defined SUBROUTINE FCN
/// callback used by HYBRD1.
/// </summary>
public interface IVectorFunction
{
    /// <summary>
    /// Evaluate F at x. Caller-allocated <paramref name="fvec"/> of the
    /// same length as <paramref name="x"/> receives the residual.
    /// Return <c>false</c> to terminate the iteration cleanly
    /// (mapped to <see cref="HybrdInfo.UserAbort"/> on the result).
    /// </summary>
    bool Evaluate(ReadOnlySpan<double> x, Span<double> fvec);
}

/// <summary>Result code, mirrors the Fortran INFO parameter.</summary>
public enum HybrdInfo
{
    /// <summary>Improper input parameters (N ≤ 0, TOL &lt; 0, LWA too small).</summary>
    InvalidArguments = 0,

    /// <summary>Relative error between two consecutive iterates is at most TOL.</summary>
    Converged = 1,

    /// <summary>Number of FCN calls reached MAXFEV without convergence.</summary>
    MaxEvaluationsExceeded = 2,

    /// <summary>TOL is too small. No further improvement in the approximate solution X is possible.</summary>
    ToleranceTooSmall = 3,

    /// <summary>The iteration is not making good progress.</summary>
    StagnatedNoProgress = 4,

    /// <summary>The user's FCN delegate returned false (clean abort).</summary>
    UserAbort = 5,
}

/// <summary>
/// Outcome of a HYBRD1 call. <see cref="X"/> contains the final iterate
/// (the input vector is overwritten in-place by the Fortran original).
/// <see cref="Fvec"/> is F(X) at termination. <see cref="FunctionEvaluations"/>
/// is the count of FCN calls actually made.
/// </summary>
public sealed record HybrdResult(
    HybrdInfo Info,
    double[] X,
    double[] Fvec,
    int FunctionEvaluations);
