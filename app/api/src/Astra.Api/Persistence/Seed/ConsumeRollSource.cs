namespace Astra.Api.Persistence.Seed;

/// <summary>
/// Canonical CONSUME_ROLL.FOR synthetic source. Embedded so the demo seeds
/// without any external dependency. All identifiers are generic — no
/// client-specific names appear in the text or in audit payloads.
/// </summary>
public static class ConsumeRollSource
{
    public const string Fortran = """
      SUBROUTINE CONSUME_ROLL(ROLL_ID, USED_LF, OPER_ID, RESULT_CD)
C     ------------------------------------------------------------------
C     CONSUME_ROLL - Posts a stock-consumption event for a single roll.
C     Decrements on-hand linear footage, updates roll status, and
C     emits a downstream inventory-changed notification.
C
C     PARAMS:
C       ROLL_ID    Unique roll identifier (CHAR*12)
C       USED_LF    Linear feet consumed in this event (REAL)
C       OPER_ID    Operator ID for audit (CHAR*8)
C       RESULT_CD  Out: 0=ok, 1=not_found, 2=insufficient, 3=locked
C     ------------------------------------------------------------------
      IMPLICIT NONE
      CHARACTER*12 ROLL_ID
      CHARACTER*8  OPER_ID
      REAL         USED_LF
      INTEGER      RESULT_CD

      INCLUDE 'INVCMN.INC'
      INCLUDE 'AUDMSG.INC'

      REAL         ON_HAND_LF, NEW_LF, MIN_REMAIN
      INTEGER      ROLL_STATUS, IO_STAT
      CHARACTER*4  GRADE_CD
      LOGICAL      LOCKED

      PARAMETER (MIN_REMAIN = 12.0)

C     Read the current roll record from INVMASTR (ISAM keyed on ROLL_ID)
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

C     If remaining stock is below threshold, mark roll as DEPLETED
      IF (NEW_LF .LT. MIN_REMAIN) THEN
         ROLL_STATUS = 9
      END IF

C     Persist update via ISAM rewrite
      CALL INV_WRITE(ROLL_ID, NEW_LF, ROLL_STATUS, OPER_ID, IO_STAT)
      IF (IO_STAT .NE. 0) THEN
         RESULT_CD = 1
         RETURN
      END IF

C     Emit the inventory-changed notification to downstream consumers
      CALL EMIT_EVENT('INV_CHG', ROLL_ID, GRADE_CD, NEW_LF)

      RESULT_CD = 0
      RETURN
      END
""";
}
