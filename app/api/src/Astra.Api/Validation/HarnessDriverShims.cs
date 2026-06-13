namespace Astra.Api.Validation;

/// <summary>
/// Phase 9.5.a — Hand-rolled reference-side HarnessDriver source
/// shims, one per spec schemaId / routine pair the 4th gate supports
/// in live mode.
///
/// <para>
/// Per ADR-032, every reference driver reads exactly one line from
/// stdin, parses its space-separated values as the routine's
/// argument list (ordered by spec.inputs), calls the routine, and
/// writes the routine's result as a space-separated line to stdout.
/// Exit 0 on success; non-zero on any thrown exception.
/// </para>
///
/// <para>
/// v1.1 ships ONE shim — a tiny "A + B" Fortran routine — purely to
/// prove the round-trip works end-to-end without committing to a
/// HarnessDriver for HYBRD1 yet. The real demo routines'
/// HarnessDrivers (HYBRD1, fmt::format, TIdSMTP.Connect, DEPTPAY)
/// land alongside the candidate side in Phase 9.5.b / v1.2 because
/// they need spec-specific scaffolding to bind variables to
/// arguments.
/// </para>
/// </summary>
public static class HarnessDriverShims
{
    /// <summary>
    /// Canonical "A + B" Fortran ref-driver used for round-trip
    /// validation in 9.5.a. Reads one line of "a b" from stdin,
    /// prints "(a + b)" to stdout.
    ///
    /// <para>
    /// This shim deliberately lives in the platform code (not in a
    /// scaffold archetype) because v1.1 doesn't yet ship a
    /// HarnessDriver-aware archetype; once Phase 9.5.b lands the
    /// canonical-minpack HarnessDriver, validators with a
    /// HarnessDriver-bearing scaffold use the archetype's shim
    /// instead of this canned one.
    /// </para>
    /// </summary>
    public const string FortranSumShimSource = """
          PROGRAM ECHO_HARNESS
    C     Phase 9.5.a — minimal reference HarnessDriver shim.
    C     Reads one integer from stdin and echoes it to stdout.
    C     ADR-032 contract:
    C       - stdin:   "<x>"   one integer, optionally followed by
    C                          additional whitespace-separated values
    C                          that we silently ignore (so the same
    C                          shim works for specs with any inputs[*]
    C                          arity, not just N=1).
    C       - stdout:  "<x>"   the echoed integer.
    C       - exit 0 on success, non-zero on any READ failure.
    C
    C     v1.1 uses an echo because the round-trip's job is to prove
    C     the cache + dispatch + ref-binary execution work, NOT to
    C     compute anything spec-specific. Real per-spec drivers (HYBRD1,
    C     fmt::format, TIdSMTP, DEPTPAY) ship in Phase 9.5.b alongside
    C     the candidate side.
          IMPLICIT NONE
          INTEGER X
          READ(*, *) X
          WRITE(*, '(I0)') X
          STOP
          END
    """;

    /// <summary>Path the gfortran sidecar will see the shim at.</summary>
    public const string FortranSumShimPath = "echo_harness.f";

    // ──────────────────────────────────────────────────────────────────
    // Phase 9.5.b — candidate-side C# shim
    //
    // The same stdin → result on stdout contract as the Fortran ref
    // shim. The validator compiles this into a tiny self-contained .NET
    // executable per validation run and the equivalence-callback runs it
    // per generated input with a subprocess pipe. Comparison vs the ref
    // binary's stdout determines `agree`.
    //
    // Two variants:
    //   - DotnetEchoCandidateSource: behaviourally identical to the
    //     Fortran ref (echo input → output). Used by the green-path
    //     test where the gate stays PASSED.
    //   - DotnetBrokenCandidateSource: returns x + 1 instead of x. Used
    //     by the intentional-break test (Δ-9.5 hard gate) where the
    //     gate must report FAILED with a minimal counterexample.
    //
    // Variant selection lives behind a config flag
    // (Validation:LiveMode4thGate:CandidateVariant) so the existing
    // demo flow + the broken-variant test can co-exist without code
    // changes.
    // ──────────────────────────────────────────────────────────────────

    public const string DotnetEchoCandidateSource = """
        // Phase 9.5.b — minimal candidate HarnessDriver shim (echo).
        // Reads one integer from stdin, writes it back to stdout, exit 0.
        // Behaviourally identical to the Fortran ref shim — the gate
        // stays PASSED with no falsifying example.
        using System;
        class HarnessDriver {
            static int Main() {
                var line = Console.In.ReadLine();
                if (line is null) return 1;
                var token = line.Split(new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries)[0];
                if (!int.TryParse(token, out var x)) return 2;
                Console.WriteLine(x);
                return 0;
            }
        }
        """;

    public const string DotnetBrokenCandidateSource = """
        // Phase 9.5.b — intentionally-broken candidate (returns x + 1).
        // Used by the Δ-9.5 hard-gate criterion that the 4th gate must
        // report FAILED with a minimal counterexample when the
        // candidate disagrees with the reference. The mutation is
        // deliberately small — every Hypothesis-generated x falsifies
        // the contract, so the search terminates quickly on the first
        // generated input.
        using System;
        class HarnessDriver {
            static int Main() {
                var line = Console.In.ReadLine();
                if (line is null) return 1;
                var token = line.Split(new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries)[0];
                if (!int.TryParse(token, out var x)) return 2;
                Console.WriteLine(x + 1);   // INTENTIONAL BUG
                return 0;
            }
        }
        """;

    public const string DotnetCandidateProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <RootNamespace>AstraHarness</RootNamespace>
            <AssemblyName>HarnessDriver</AssemblyName>
            <Nullable>enable</Nullable>
            <ImplicitUsings>disable</ImplicitUsings>
            <InvariantGlobalization>true</InvariantGlobalization>
          </PropertyGroup>
        </Project>
        """;
}

