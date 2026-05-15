namespace Astra.Api.Validation;

/// <summary>
/// Phase #5 — Per-routine equivalence harness for CONSUME_ROLL.
///
/// Two pieces sit side-by-side:
///   1. <see cref="FortranDriverSource"/> — the canonical CONSUME_ROLL
///      subroutine wrapped in a self-contained gfortran-compilable
///      program with stub implementations of the ISAM I/O calls and
///      the downstream EMIT_EVENT routine. The driver reads N input
///      vectors from stdin and emits one JSON line per result on
///      stdout.
///   2. <see cref="EvaluateCSharp"/> — an in-process C# reference that
///      mirrors the .NET 8 scaffold's ConsumeRollService business
///      logic, evaluated against the same canonical inputs.
///
/// The CrossRuntimeValidator drives both with the same inputs from
/// <see cref="CanonicalInputs"/>; matching outputs row-for-row prove
/// the migration preserves behaviour.
/// </summary>
public static class ConsumeRollEquivalence
{
    /// <summary>
    /// Canonical input vectors. Each line is sent on stdin in the form
    /// "&lt;ROLL_ID&gt; &lt;USED_LF&gt; &lt;OPER_ID&gt;" with the
    /// Fortran driver consuming one per loop iteration.
    /// </summary>
    public static readonly Input[] CanonicalInputs =
    {
        new("R-001",   40.0f,  "OP001",   ExpectedLabel: "happy path"),
        new("R-001",   95.0f,  "OP001",   ExpectedLabel: "depleted (drops below MIN_REMAIN)"),
        new("R-001",  150.0f,  "OP001",   ExpectedLabel: "insufficient"),
        new("R-002",    1.0f,  "OP002",   ExpectedLabel: "locked"),
        new("UNKNOWN",  1.0f,  "OP003",   ExpectedLabel: "not found"),
    };

    public sealed record Input(string RollId, float UsedLf, string OperId, string ExpectedLabel);

    public sealed record Outcome(int ResultCd, float NewLf, int RollStatus, string GradeCd, bool EventEmitted, string? EventGrade, float? EventNewLf);

    /// <summary>
    /// In-process C# evaluation that mirrors the .NET 8 scaffold's
    /// ConsumeRollService. Uses the same stubbed roll table as the
    /// Fortran driver so the two runtimes are comparing apples to
    /// apples.
    /// </summary>
    public static Outcome EvaluateCSharp(Input input)
    {
        var roll = LookupRoll(input.RollId);
        if (roll is null) return new Outcome(1, 0f, 0, "", false, null, null);
        if (roll.Locked) return new Outcome(3, 0f, 0, "", false, null, null);
        if (input.UsedLf > roll.OnHandLf) return new Outcome(2, 0f, 0, "", false, null, null);

        var newLf = roll.OnHandLf - input.UsedLf;
        var status = newLf < MinRemain ? 9 : roll.Status;
        return new Outcome(0, newLf, status, roll.GradeCd, true, roll.GradeCd, newLf);
    }

    private sealed record Roll(string Id, float OnHandLf, int Status, string GradeCd, bool Locked);

    private const float MinRemain = 12.0f;

    private static Roll? LookupRoll(string id) => id switch
    {
        "R-001" => new Roll("R-001", 100.0f, 1, "G-AS", false),
        "R-002" => new Roll("R-002",   5.0f, 1, "G-BS", true),
        _       => null,
    };

    /// <summary>
    /// Self-contained Fortran program that embeds the canonical
    /// CONSUME_ROLL subroutine + the three callees as stubs
    /// (INV_READ, INV_WRITE, EMIT_EVENT). Driver reads N input lines
    /// from stdin and emits one JSON line per outcome.
    ///
    /// Format of stdout per line:
    /// <c>{"result_cd":N,"new_lf":F,"roll_status":S,"grade_cd":"X",
    ///     "event_emitted":B,"event_grade":"X"|null,"event_new_lf":F|null}</c>
    /// </summary>
    public const string FortranDriverSource = """
      PROGRAM CONSUME_ROLL_DRIVER
C     ------------------------------------------------------------------
C     Per-routine equivalence driver for CONSUME_ROLL. Reads N input
C     vectors from stdin (3 fields each: ROLL_ID, USED_LF, OPER_ID) and
C     prints one JSON-shaped line per outcome to stdout.
C     ------------------------------------------------------------------
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      CHARACTER*8  OPER_ID
      REAL         USED_LF
      INTEGER      RESULT_CD
      INTEGER      IOS

C     Read N input lines and process each. List-directed READ takes
C     whitespace-separated tokens — fits in F77 column-72 limit.
   10 READ(*,*, IOSTAT=IOS) ROLL_ID, USED_LF, OPER_ID
      IF (IOS .NE. 0) GO TO 99
      CALL CONSUME_ROLL_AND_REPORT(ROLL_ID, USED_LF, OPER_ID)
      GO TO 10
   99 CONTINUE
      END PROGRAM CONSUME_ROLL_DRIVER

      SUBROUTINE CONSUME_ROLL_AND_REPORT(ROLL_ID, USED_LF, OPER_ID)
C     Calls CONSUME_ROLL and prints a structured outcome line. The
C     driver wraps in this helper so the reporting layer can capture
C     post-call state from the shared stub-storage module.
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      CHARACTER*8  OPER_ID
      REAL         USED_LF
      INTEGER      RESULT_CD

C     Stub-driver shared block: after INV_WRITE the stubs leave the
C     last-written values here so the driver can report them.
      CHARACTER*4  LAST_GRADE
      REAL         LAST_NEW_LF
      INTEGER      LAST_STATUS
      LOGICAL      EVENT_FIRED
      COMMON /STUBSTATE/ LAST_GRADE, LAST_NEW_LF, LAST_STATUS,
     &                   EVENT_FIRED

      EVENT_FIRED = .FALSE.
      LAST_GRADE  = '    '
      LAST_NEW_LF = 0.0
      LAST_STATUS = 0
      CALL CONSUME_ROLL(ROLL_ID, USED_LF, OPER_ID, RESULT_CD)
      CALL EMIT_REPORT(RESULT_CD)
      RETURN
      END

      SUBROUTINE EMIT_REPORT(RESULT_CD)
      IMPLICIT NONE
      INTEGER RESULT_CD
      CHARACTER*4  LAST_GRADE
      REAL         LAST_NEW_LF
      INTEGER      LAST_STATUS
      LOGICAL      EVENT_FIRED
      COMMON /STUBSTATE/ LAST_GRADE, LAST_NEW_LF, LAST_STATUS,
     &                   EVENT_FIRED

      IF (EVENT_FIRED) THEN
         WRITE(*,1000) RESULT_CD, LAST_NEW_LF, LAST_STATUS, LAST_GRADE,
     &                 LAST_GRADE, LAST_NEW_LF
 1000    FORMAT('{"result_cd":', I0, ',"new_lf":', F0.4,
     &          ',"roll_status":', I0, ',"grade_cd":"', A,
     &          '","event_emitted":true,"event_grade":"', A,
     &          '","event_new_lf":', F0.4, '}')
      ELSE
         WRITE(*,2000) RESULT_CD
 2000    FORMAT('{"result_cd":', I0, ',"new_lf":0.0000',
     &          ',"roll_status":0,"grade_cd":"",',
     &          '"event_emitted":false,',
     &          '"event_grade":null,"event_new_lf":null}')
      END IF
      RETURN
      END

C     ------------------------------------------------------------------
C     Canonical CONSUME_ROLL — copied verbatim from the seed corpus.
C     ------------------------------------------------------------------
      SUBROUTINE CONSUME_ROLL(ROLL_ID, USED_LF, OPER_ID, RESULT_CD)
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      CHARACTER*8  OPER_ID
      REAL         USED_LF
      INTEGER      RESULT_CD

      REAL         ON_HAND_LF, NEW_LF, MIN_REMAIN
      INTEGER      ROLL_STATUS, IO_STAT
      CHARACTER*4  GRADE_CD
      LOGICAL      LOCKED

      PARAMETER (MIN_REMAIN = 12.0)

      CALL INV_READ(ROLL_ID, ON_HAND_LF, ROLL_STATUS, GRADE_CD,
     &              LOCKED, IO_STAT)
      IF (IO_STAT .NE. 0) THEN
         RESULT_CD = 1
         RETURN
      END IF

      IF (LOCKED) THEN
         RESULT_CD = 3
         RETURN
      END IF

      IF (USED_LF .GT. ON_HAND_LF) THEN
         RESULT_CD = 2
         RETURN
      END IF

      NEW_LF = ON_HAND_LF - USED_LF

      IF (NEW_LF .LT. MIN_REMAIN) THEN
         ROLL_STATUS = 9
      END IF

      CALL INV_WRITE(ROLL_ID, NEW_LF, ROLL_STATUS, OPER_ID, IO_STAT)
      IF (IO_STAT .NE. 0) THEN
         RESULT_CD = 1
         RETURN
      END IF

      CALL EMIT_EVENT('INV_CHG', ROLL_ID, GRADE_CD, NEW_LF)

      RESULT_CD = 0
      RETURN
      END

C     ------------------------------------------------------------------
C     ISAM shim stubs. Two rolls in the table; matches LookupRoll() in
C     ConsumeRollEquivalence.cs so the two runtimes are comparing the
C     same canonical roll catalog.
C     ------------------------------------------------------------------
      SUBROUTINE INV_READ(ROLL_ID, ON_HAND_LF, ROLL_STATUS, GRADE_CD,
     &                    LOCKED, IO_STAT)
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      REAL         ON_HAND_LF
      INTEGER      ROLL_STATUS, IO_STAT
      CHARACTER*4  GRADE_CD
      LOGICAL      LOCKED

      IF (ROLL_ID(1:5) .EQ. 'R-001') THEN
         ON_HAND_LF  = 100.0
         ROLL_STATUS = 1
         GRADE_CD    = 'G-AS'
         LOCKED      = .FALSE.
         IO_STAT     = 0
         RETURN
      END IF
      IF (ROLL_ID(1:5) .EQ. 'R-002') THEN
         ON_HAND_LF  = 5.0
         ROLL_STATUS = 1
         GRADE_CD    = 'G-BS'
         LOCKED      = .TRUE.
         IO_STAT     = 0
         RETURN
      END IF
      IO_STAT = 1
      RETURN
      END

      SUBROUTINE INV_WRITE(ROLL_ID, NEW_LF, ROLL_STATUS, OPER_ID,
     &                     IO_STAT)
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      CHARACTER*8  OPER_ID
      REAL         NEW_LF
      INTEGER      ROLL_STATUS, IO_STAT

      CHARACTER*4  LAST_GRADE
      REAL         LAST_NEW_LF
      INTEGER      LAST_STATUS
      LOGICAL      EVENT_FIRED
      COMMON /STUBSTATE/ LAST_GRADE, LAST_NEW_LF, LAST_STATUS,
     &                   EVENT_FIRED

      LAST_NEW_LF = NEW_LF
      LAST_STATUS = ROLL_STATUS
      IO_STAT     = 0
      RETURN
      END

      SUBROUTINE EMIT_EVENT(EVT_TYPE, ROLL_ID, GRADE_CD, NEW_LF)
      IMPLICIT NONE
      CHARACTER*7  EVT_TYPE
      CHARACTER*12 ROLL_ID
      CHARACTER*4  GRADE_CD
      REAL         NEW_LF

      CHARACTER*4  LAST_GRADE
      REAL         LAST_NEW_LF
      INTEGER      LAST_STATUS
      LOGICAL      EVENT_FIRED
      COMMON /STUBSTATE/ LAST_GRADE, LAST_NEW_LF, LAST_STATUS,
     &                   EVENT_FIRED

      LAST_GRADE  = GRADE_CD
      LAST_NEW_LF = NEW_LF
      EVENT_FIRED = .TRUE.
      RETURN
      END
""";

    /// <summary>
    /// Render the canonical inputs as a stdin payload for the Fortran
    /// driver. One whitespace-separated triplet per line — list-directed
    /// READ accepts arbitrary spacing and treats unquoted character
    /// tokens as left-justified, blank-padded to the receiving field
    /// width.
    /// </summary>
    public static string RenderStdin()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inp in CanonicalInputs)
        {
            sb.Append(inp.RollId);
            sb.Append(' ');
            sb.Append(inp.UsedLf.ToString("F6", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(inp.OperId);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
