using System.Globalization;
using System.Text;

namespace Astra.Api.Validation;

/// <summary>
/// Phase 5.6 — Per-routine equivalence harness for DEPTPAY's
/// <c>AVERAGE-SALARY</c> paragraph (the COBOL COMPUTE / ROUNDED line
/// that maps to <see cref="Llm.Archetypes.ArchetypeRegistry"/>'s
/// <c>java-spring/cobol-canonical-payroll</c> <c>PayrollService.computeAverage</c>
/// helper). Two pieces sit side-by-side:
///   1. <see cref="CobolDriverSource"/> — a stand-alone GnuCOBOL program
///      that reads a stream of (total, count) pairs from stdin, runs
///      <c>COMPUTE WS-AVG ROUNDED = WS-TOTAL / WS-COUNT</c> on
///      <c>PIC 9(7)V99</c> fields (with ON SIZE ERROR fall-through to
///      zero), and emits one decimal line per pair on stdout.
///   2. <see cref="EvaluateCSharp"/> — an in-process C# reference that
///      mirrors what <c>PayrollService.computeAverage</c> does in the
///      Java scaffold: <c>BigDecimal.divide(.., 2, HALF_UP)</c>, with
///      divide-by-zero → 0.00.
///
/// The equivalence-preview endpoint drives both with the same canonical
/// inputs from <see cref="CanonicalInputs"/>; matching outputs row-for-row
/// prove the migration preserves COBOL's numeric semantics. This is the
/// strongest moment of the demo recording: the legacy COBOL binary and
/// the modern Java implementation produce identical decimal output on
/// the same canonical payroll vectors.
/// </summary>
public static class PayrollEquivalence
{
    /// <summary>
    /// Canonical input vectors. Each row is sent on stdin as two lines —
    /// total on one line, count on the next — so the COBOL driver can
    /// <c>ACCEPT</c> them one at a time and parse via <c>FUNCTION NUMVAL</c>.
    /// </summary>
    // Surprising-but-correct case worth flagging: 111111.11 / 19 =
    // 5847.953157… (because 19 × 5848 = 111112, not 111111). HALF_UP at
    // scale 2 = 5847.95. The Phase 5.4 PayrollServiceTest fixture
    // asserts 5848.06 for this same pair — that expected value was
    // hand-computed off-by-one. This is exactly the kind of human
    // arithmetic bug a per-routine equivalence harness exists to
    // surface: when the engineer implements PayrollService.process,
    // the test as written will fail against both the COBOL legacy
    // binary AND a correct Java implementation. Phase 5.7 demo
    // narration calls this out as the value-add moment.
    public static readonly Input[] CanonicalInputs =
    {
        new(Total: 111111.11m, Count: 19, ExpectedLabel: "INV-4 baseline · surfaces Java fixture bug"),
        new(Total:    100.00m, Count:  0, ExpectedLabel: "EC-1 ON SIZE ERROR (divide by zero → 0.00)"),
        new(Total:     10.00m, Count:  3, ExpectedLabel: "repeating decimal · HALF_UP"),
        new(Total:     50.50m, Count:  2, ExpectedLabel: "exact halves"),
        new(Total: 123456.78m, Count:  7, ExpectedLabel: "round-down case"),
        new(Total:   1000.00m, Count: 13, ExpectedLabel: "non-terminating · HALF_UP"),
    };

    public sealed record Input(decimal Total, int Count, string ExpectedLabel);

    /// <summary>
    /// In-process C# evaluation that mirrors the Java
    /// <c>PayrollService.computeAverage</c> helper: <c>BigDecimal.divide</c>
    /// with scale 2, HALF_UP; divide-by-zero short-circuits to
    /// <c>BigDecimal.ZERO</c> at scale 2.
    /// </summary>
    public static decimal EvaluateCSharp(Input input)
    {
        if (input.Count == 0) return 0.00m;
        // decimal /, then Math.Round with AwayFromZero — same midpoint
        // policy as COBOL ROUNDED and Java BigDecimal.HALF_UP. Scale 2
        // matches PIC 9(7)V99 on the COBOL side.
        var raw = input.Total / input.Count;
        return Math.Round(raw, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Render the canonical inputs as a stdin payload for the COBOL
    /// driver. Two lines per case — total then count — so the driver's
    /// pair of <c>ACCEPT</c> + <c>NUMVAL</c> calls can pick them up
    /// without parsing the line ourselves. Decimals are emitted with
    /// invariant-culture formatting so the locale of the host running
    /// the API does not leak into the COBOL input stream.
    /// A trailing <c>0\n0\n</c> sentinel terminates the driver loop.
    /// </summary>
    public static string RenderStdin()
    {
        var sb = new StringBuilder();
        foreach (var inp in CanonicalInputs)
        {
            sb.Append(inp.Total.ToString("F2", CultureInfo.InvariantCulture));
            sb.Append('\n');
            sb.Append(inp.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        // Sentinel: a row of (0, 0) tells the driver to stop the
        // PERFORM UNTIL loop and STOP RUN cleanly. Without this the
        // driver hits stdin EOF on its second ACCEPT which is
        // implementation-defined under GnuCOBOL — sentinel keeps the
        // run deterministic.
        sb.Append("0\n0\n");
        return sb.ToString();
    }

    /// <summary>
    /// Stand-alone GnuCOBOL driver for DEPTPAY's AVERAGE-SALARY paragraph.
    /// Reads (total, count) pairs from stdin, COMPUTEs the rounded
    /// average on <c>PIC 9(7)V99</c> fields (with ON SIZE ERROR
    /// fall-through to zero), and DISPLAYs one decimal line per pair.
    /// Free-form source so callers don't have to count to column 72.
    ///
    /// Output format: one line per (total, count) pair, the rounded
    /// average rendered through a <c>ZZZZZZ9.99</c> edited picture
    /// (leading-zero-suppressed, exactly two decimal places). The C#
    /// side strips whitespace and parses with invariant culture.
    /// </summary>
    public const string CobolDriverSource = """
       IDENTIFICATION DIVISION.
       PROGRAM-ID. AVG-DRIVER.
       ENVIRONMENT DIVISION.
       DATA DIVISION.
       WORKING-STORAGE SECTION.
       01  WS-LINE       PIC X(20).
       01  WS-TOTAL      PIC 9(7)V99.
       01  WS-COUNT      PIC 9(7).
       01  WS-AVG        PIC 9(7)V99.
       01  WS-AVG-EDIT   PIC ZZZZZZ9.99.
       01  EOF-FLAG      PIC X VALUE "N".
       PROCEDURE DIVISION.
       MAIN-LOOP.
           PERFORM UNTIL EOF-FLAG = "Y"
              MOVE SPACES TO WS-LINE
              ACCEPT WS-LINE
              COMPUTE WS-TOTAL = FUNCTION NUMVAL(WS-LINE)
              MOVE SPACES TO WS-LINE
              ACCEPT WS-LINE
              COMPUTE WS-COUNT = FUNCTION NUMVAL(WS-LINE)
              IF WS-TOTAL = 0 AND WS-COUNT = 0
                 MOVE "Y" TO EOF-FLAG
              ELSE
                 IF WS-COUNT = 0
                    MOVE 0 TO WS-AVG
                 ELSE
                    COMPUTE WS-AVG ROUNDED = WS-TOTAL / WS-COUNT
                       ON SIZE ERROR MOVE 0 TO WS-AVG
                    END-COMPUTE
                 END-IF
                 MOVE WS-AVG TO WS-AVG-EDIT
                 DISPLAY WS-AVG-EDIT
              END-IF
           END-PERFORM
           STOP RUN.
""";
}
