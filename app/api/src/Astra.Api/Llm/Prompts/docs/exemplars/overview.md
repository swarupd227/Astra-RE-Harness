---
id: docs-exemplar-overview
version: v1.0
owner: Nous · Documentation generation
notes: |
  Exemplar of the system overview form, grounded in the shape of MINPACK
  (Fortran 77, one routine per file). Shown to the model as a cached
  system block after the style guide. Models tone, density, sectioning,
  and citation placement; the model is told not to copy its paths,
  lines, or claims.
---

# Exemplar — system overview

# MINPACK — nonlinear least squares and nonlinear equation solvers

MINPACK solves two problems: find the parameters that make a model fit observed data as closely as possible (nonlinear least squares), and find the point at which a system of nonlinear equations is zero. It does this with two families of trust-region algorithms, Levenberg–Marquardt and Powell's hybrid method, wrapped in drivers that a caller invokes with a single function pointer and a handful of tolerances. Everything else in the library exists to keep those two families numerically safe.

## What the system does

A caller supplies a function that computes residuals — the differences between model and data — and optionally a function that computes their Jacobian. The least-squares drivers `lmder`, `lmdif`, and `lmstr` minimise the sum of squared residuals [minpack/lmder.f:L1–L52]; the equation drivers `hybrd` and `hybrj` find a zero of the residual vector [minpack/hybrd.f:L1–L48]. Each has a simplified `…1` variant that fixes the tuning parameters to defaults, so most callers never see a scaling vector or a trust-region factor [minpack/lmder1.f:L1–L30].

## Subsystems

### Nonlinear least squares

Three drivers share one algorithm and differ in where the Jacobian comes from: `lmder` takes it from the caller, `lmdif` estimates it by forward differences, and `lmstr` accepts it one row at a time to keep memory at O(n²) for large residual counts [minpack/lmstr.f:L12–L40]. All three compute a QR factorisation of the Jacobian with column pivoting, then solve the trust-region subproblem in `lmpar` on every iteration [minpack/lmder.f:L242–L270]. Read first: `lmder.f`, then `lmpar.f`.

### Nonlinear equations

`hybrd` and `hybrj` implement Powell's hybrid method: a dogleg step between the Gauss–Newton direction and steepest descent, with a Broyden rank-one update of the Jacobian between refactorisations [minpack/hybrd.f:L188–L262]. `hybrd` estimates the Jacobian by finite differences with `fdjac1`; `hybrj` takes it from the caller. Read first: `hybrd.f`, then `dogleg.f`.

### Linear algebra kernels

`qrfac` performs the pivoted Householder QR that both families depend on [minpack/qrfac.f:L1–L60]; `qrsolv` solves the augmented least-squares system inside `lmpar`; `r1updt` and `r1mpyq` apply the rank-one updates the hybrid method needs without refactorising [minpack/r1updt.f:L1–L38]. None of these are meant to be called directly.

### Support

`enorm` computes a Euclidean norm with three-range scaling so that neither overflow nor destructive underflow occurs [minpack/enorm.f:L20–L58]; `dpmpar` returns the machine constants every tolerance test is expressed in [minpack/dpmpar.f:L1–L45]; `chkder` lets a caller verify a hand-written Jacobian against finite differences before trusting it [minpack/chkder.f:L1–L40].

## Load-bearing concepts

**Termination is a three-way test.** Every driver stops on the first of: the relative reduction in the sum of squares falls below `ftol`, the relative change in the parameters falls below `xtol`, or the cosine of the angle between the residual vector and any Jacobian column falls below `gtol` [minpack/lmder.f:L296–L318]. The `info` return code says which one fired, and a value of 4 or higher means a limit was hit rather than a convergence criterion.

**Scaling is explicit.** The `diag` vector rescales the parameter space so the trust region is round in scaled coordinates; with `mode = 1` the driver sets it from the Jacobian's column norms on the first iteration and never lowers it [minpack/lmder.f:L204–L214]. A caller who passes badly scaled parameters with `mode = 2` gets slow convergence, not an error.

**The caller's function controls abort.** The residual function receives `iflag`; setting it negative makes the driver return immediately with `info = iflag`. This is the only way to stop a run from inside the callback [minpack/lmder.f:L150–L158].

**Machine constants come from one place.** `dpmpar` hard-codes the precision, smallest, and largest magnitudes for the compiler's double type, and a port to a different floating-point model must change those three constants before anything else [minpack/dpmpar.f:L30–L45].

## Known risks

- `dpmpar` is compiled with constants for IEEE double; a build on a platform with a different representation silently uses the wrong epsilon [minpack/dpmpar.f:L30–L45].
- `lmdif`'s finite-difference step `epsfcn` defaults to machine precision; a residual function with lower precision than that produces a Jacobian of noise, and the driver reports slow convergence rather than a fault [minpack/lmdif.f:L60–L72].
