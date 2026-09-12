---
id: docs-exemplar-routine-summary
version: v1.0
owner: Nous · Documentation generation
notes: |
  Exemplar of a rendered routine summary, grounded in MINPACK's enorm.f.
  The routine-summary writer emits structured JSON; this shows the prose
  its fields should read as once rendered. Shown as a cached system
  block after the style guide. The model is told not to copy paths,
  lines, or claims.
---

# Exemplar — routine summary

### ENORM

Computes the Euclidean norm of a vector without overflowing on large components or losing small ones to underflow. Callers throughout the library use it wherever a norm feeds a tolerance test — the residual norm that drives convergence, the column norms that set parameter scaling, the step norm that bounds the trust region — so its accuracy at the extremes decides whether those tests behave near the limits of double precision. It sorts each component into one of three accumulators by magnitude: components above `agiant` (the overflow guard `rgiant` divided by the vector length) are squared after dividing by the largest such component; components below `rdwarf` are squared after dividing by the largest of those; everything in between is summed directly. The final value combines whichever accumulators are non-empty, giving priority to the large-component sum when it exists and folding the small sum in only when the intermediate sum is empty [minpack/enorm.f:L20–L58].

Source: [minpack/enorm.f:L1–L108]

**Inputs.** the number of components; the vector whose norm is required.

**Outputs.** the Euclidean norm, returned as the double-precision function value.

**Side effects.** None; the routine reads its arguments and modifies nothing.

#### Preconditions

- The vector length must be at least 1 — the overflow guard `agiant` is computed as `rgiant / n` and a zero length divides by zero [minpack/enorm.f:L40].
- The vector must hold `n` initialised values; an uninitialised component in the large range poisons the scaled sum.

#### Edge cases

- An all-zero vector returns exactly zero; no accumulator is touched [minpack/enorm.f:L44–L52].
- A single component near `rgiant` (about 1.3e19) takes the scaled path and returns its magnitude rather than overflowing [minpack/enorm.f:L46–L50].
- Components between `rdwarf` (about 3.8e-20) and `agiant` are summed unscaled, so for ordinary data the routine reduces to a plain sum of squares and a square root [minpack/enorm.f:L53–L56].
- When only small components exist, the result is `x3max * sqrt(s3)`, preserving the norm's relative accuracy where a naive sum would return zero [minpack/enorm.f:L60–L68].
