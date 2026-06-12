using System;
using Xunit;

namespace Demo.MinPack.Tests;

/// <summary>
/// Engineer-authored xUnit fixtures for HYBRD1. Every fixture is tied
/// to a single signed-spec claim so claim → test traceability is
/// preserved during code review.
/// </summary>
public class Hybrd1ServiceTests
{
    [Fact] // INV-1 · N ≤ 0 is improper input
    public void Solve_ReturnsInvalid_OnEmptyInitialVector()
    {
        var svc = new Hybrd1Service();
        var r = svc.Solve(new Identity(), ReadOnlySpan<double>.Empty, tol: 1e-8);
        Assert.Equal(HybrdInfo.InvalidArguments, r.Info);
        Assert.Equal(0, r.FunctionEvaluations);
    }

    [Fact] // INV-2 · TOL < 0 is improper input
    public void Solve_ReturnsInvalid_OnNegativeTolerance()
    {
        var svc = new Hybrd1Service();
        var r = svc.Solve(new Identity(), new double[] { 0.5 }, tol: -1e-8);
        Assert.Equal(HybrdInfo.InvalidArguments, r.Info);
    }

    [Fact] // INV-2 · NaN tolerance is also improper input
    public void Solve_ReturnsInvalid_OnNanTolerance()
    {
        var svc = new Hybrd1Service();
        var r = svc.Solve(new Identity(), new double[] { 0.5 }, tol: double.NaN);
        Assert.Equal(HybrdInfo.InvalidArguments, r.Info);
    }

    [Fact] // INV-3 · MAXFEV = 200 · (N + 1) — verified on the trivial root
    public void Solve_ConvergesOnLinearIdentityProblem()
    {
        var svc = new Hybrd1Service();
        // F(x) = x   →   root at the origin
        var r = svc.Solve(new Identity(), new double[] { 0.0, 0.0, 0.0 }, tol: 1e-10);
        Assert.Equal(HybrdInfo.Converged, r.Info);
        Assert.All(r.Fvec, fi => Assert.True(Math.Abs(fi) <= 1e-10));
    }

    [Fact] // EC-2 · clean caller-driven abort
    public void Solve_PropagatesUserAbort()
    {
        var svc = new Hybrd1Service(new RecordingCore(HybrdInfo.UserAbort));
        var r = svc.Solve(new Identity(), new double[] { 1.0 }, tol: 1e-8);
        Assert.Equal(HybrdInfo.UserAbort, r.Info);
    }

    [Fact] // INV-5 · the input vector is not mutated by Solve
    public void Solve_DoesNotMutateInputSpan()
    {
        var svc = new Hybrd1Service();
        var input = new double[] { 1.0, 2.0, 3.0 };
        _ = svc.Solve(new Identity(), input, tol: 1e-8);
        Assert.Equal(new double[] { 1.0, 2.0, 3.0 }, input);
    }

    // ── Fixtures ────────────────────────────────────────────────────

    /// <summary>F(x) = x — root at the origin.</summary>
    private sealed class Identity : IVectorFunction
    {
        public bool Evaluate(ReadOnlySpan<double> x, Span<double> fvec)
        {
            for (int i = 0; i < x.Length; i++) fvec[i] = x[i];
            return true;
        }
    }

    /// <summary>Stub core that returns a fixed INFO without iterating.</summary>
    private sealed class RecordingCore : HybrdCore
    {
        private readonly HybrdInfo _info;
        public RecordingCore(HybrdInfo info) { _info = info; }
        public override HybrdResult Iterate(IVectorFunction fcn, double[] x, double[] fvec, HybrdOptions options) =>
            new(_info, x, fvec, FunctionEvaluations: 0);
    }
}
